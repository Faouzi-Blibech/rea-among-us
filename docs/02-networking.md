# 02 - Networking and multiplayer architecture

Target: Among Us (Innersloth) Android build 2026.9.29 (version code 7492, Unity 2022.3.62f3, IL2CPP).
Purpose: let our team build an original, similar game. Documentation only.

## 0. How to read this document

Evidence tags used on every section and, where it matters, every row:

| Tag | Meaning |
|---|---|
| **Observed** | Present in the 2026.9.29 Cpp2IL dump (types, fields, enums with values, method signatures; no method bodies). |
| **Reference** | Taken from the open-source Impostor server (commit 703915f) or Innersloth's Hazel (commit c6a228c). These may differ from the shipped 2026.9.29 behavior. |
| **Inferred** | Our reasoning from names and structure. Not verified. |

Path shorthand (all read-only):

- `Dump:` = `scratchpad\cs\DiffableCs\` (so `Dump:Assembly-CSharp/InnerNet/Tags.cs`). Hazel sources in the dump are `Dump:Hazel/Hazel/...`.
- `Impostor:` = `scratchpad\refs\Impostor\` (e.g. `Impostor:src/Impostor.Server/Net/State/Game.Data.cs`).
- `HazelRef:` = `scratchpad\refs\Hazel-Networking\` (e.g. `HazelRef:Hazel/Udp/UdpConnection.Reliable.cs`).

Important limitation: the dump has no method bodies. Anything about "what a function does" is Reference or Inferred. Wire formats below come from Impostor and are marked as such.

---

## 1. Transport: Hazel over UDP (with DTLS)

### 1.1 Stack summary

| Layer | Finding | Tag | Source |
|---|---|---|---|
| Library | Hazel (Innersloth), namespaces `Hazel`, `Hazel.Udp`, `Hazel.Dtls`, `Hazel.Crypto`, `Hazel.UPnP` | Observed | `Dump:Hazel/Hazel/` file tree |
| Socket | UDP; client class `UnityUdpClientConnection`, DTLS client class `DtlsUnityConnection` | Observed | `Dump:Hazel/Hazel/Udp/UnityUdpClientConnection.cs`, `Dump:Hazel/Hazel/Dtls/DtlsUnityConnection.cs` |
| DTLS | DTLS 1.2 (`ProtocolVersion.DTLS1_2 = 65277`), single cipher suite `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256` (49199), X25519 key exchange, AES-128-GCM record protection, SHA-256 PRF | Observed | `Dump:Hazel/Hazel/Dtls/CipherSuite.cs`, `ProtocolVersion.cs`, `X25519EcdheRsaSha256.cs`, `Aes128GcmRecordProtection.cs` |
| DTLS usage is per server | `ServerInfo.UseDtls` (default true in ctor), `DnsRegionInfo.UseDtls`, `InnerNetClient.useDtls`, `SetEndpoint(addr, port, dtls)` | Observed | `Dump:Assembly-CSharp/ServerInfo.cs`, `DnsRegionInfo.cs`, `InnerNet/InnerNetClient.cs` |
| Server cert pinning | `AuthManager` embeds a PEM server certificate constant | Observed | `Dump:Assembly-CSharp/AuthManager.cs` (do not reuse; we only note its existence) |
| Nonce handshake | `AuthManager` has `AuthTagNonceMessage = 1`, `LastNonceReceived`, `CreateDtlsConnection(ip, port)`; `DisconnectReasons.InternalNonceFailure`/`ErrorAuthNonceFailure` exist | Observed | `Dump:Assembly-CSharp/AuthManager.cs`, `DisconnectReasons.cs` |
| Nonce meaning | Likely a separate DTLS "auth" connection used to obtain a nonce before the game connection; Impostor says the client's `lastNonceReceived` is "always 0 since 2021.11.9" | Inferred / Reference | `Impostor:src/Impostor.Api/Net/Messages/C2S/HandshakeC2S.cs` |
| Local/LAN | A full embedded server exists in the client: `InnerNetServer` (`StartAsLocalServer`, `StartAsServer`, `LocalGameId = 32`) plus `InnerDiscover` (UDP broadcast discovery, `UdpBroadcaster`/`UdpBroadcastListener`) and `NetworkModes { LocalGame=0, OnlineGame=1, FreePlay=2 }` | Observed | `Dump:Assembly-CSharp/InnerNet/InnerNetServer.cs`, `InnerDiscover.cs`, `NetworkModes.cs`, `Dump:Hazel/Hazel/Udp/` |
| UPnP | Hazel includes UPnP helper classes | Observed | `Dump:Hazel/Hazel/UPnP/` |

### 1.2 Hazel packet layout (Reference, with dump corroboration)

Hazel packets start with a one-byte "send option". Values from `UdpSendOption`:

| Byte | Name | Layout after the byte | Tag | Source |
|---|---|---|---|---|
| 0 | Unreliable data | payload (messages) | Reference | `HazelRef:Hazel/MessageWriter.cs` (Clear), `Dump:Hazel/Hazel/SendOption.cs` (`None=0, Reliable=1`) |
| 1 | Reliable data | `id:uint16 big-endian`, then payload | Reference | `HazelRef:Hazel/Udp/UdpConnection.Reliable.cs` (AttachReliableID) |
| 8 | Hello | `id:uint16 BE`, then handshake bytes (first handshake byte is a Hazel version indicator) | Reference | `HazelRef:Hazel/Udp/UdpConnection.cs` (SendHello), `SendOptionInternal.cs` |
| 9 | Disconnect | optional payload (the game puts a reason message here) | Reference | `HazelRef:Hazel/Udp/SendOptionInternal.cs`, `Impostor:src/Impostor.Api/Net/Messages/S2C/MessageDisconnect.cs` |
| 10 | Acknowledgement | `id:uint16 BE`, `recent:byte` (bit i-1 set if packet id-i was received, for the previous 8 ids) | Reference | `HazelRef:Hazel/Udp/UdpConnection.Reliable.cs` (SendAck) |
| 11 | Fragment | enum value exists; we found no send/receive path for it other than statistics counters | Reference | `HazelRef:Hazel/Udp/SendOptionInternal.cs`; `Dump:Hazel/Hazel/ConnectionStatistics.cs` has `fragmentedMessages*` counters only |
| 12 | Ping | `id:uint16 BE` (quasi-reliable keepalive, answered by an Ack) | Reference | `HazelRef:Hazel/Udp/UdpConnection.KeepAlive.cs` |

Notes:

- Public `SendOption` in the shipped game is just `None=0` and `Reliable=1` (Observed, `Dump:Hazel/Hazel/SendOption.cs`).
- Fragmentation: **no application-level fragmentation was found**. The game instead caps how many messages it packs per datagram: `InnerNetClient.MaxMessageSizeBytes = 500` (private const) and `GetMaxMessagePackingLimit()` (Observed, `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs`). Reading: it keeps each datagram under a safe MTU. (Inferred)
- DTLS handshake fragments (`FragmentRange` in `DtlsUnityConnection`) are a TLS-level concern and unrelated to Hazel app messages (Observed, `Dump:Hazel/Hazel/Dtls/DtlsUnityConnection.cs`).

### 1.3 Reliability, resend and keepalive

| Parameter | Value | Tag | Source |
|---|---|---|---|
| Min resend delay | 50 ms | Observed | `Dump:Hazel/Hazel/Udp/UdpConnection.cs` (`MinResendDelayMs`) |
| Max initial resend delay | 300 ms | Observed | same (`MaxInitialResendDelayMs`) |
| Max additional resend growth | 1000 ms per step | Observed | same (`MaxAdditionalResendDelayMs`) |
| Initial resend delay when `ResendTimeoutMs<=0` | smoothed ping * `ResendPingMultiplier` (default 2), clamped to [50, 300] ms | Reference | `HazelRef:Hazel/Udp/UdpConnection.Reliable.cs` |
| Backoff | each resend adds `min(timeout * multiplier, 1000)` ms | Reference | same |
| Disconnect on no ack | `DisconnectTimeoutMs` default 5000 ms, or `ResendLimit` if non-zero | Reference | same |
| Keepalive interval | 1500 ms | Reference | `HazelRef:Hazel/Udp/UdpConnection.KeepAlive.cs` |
| Missed pings until disconnect | 6 | Reference | same |
| Ack ordering | reliable packets are delivered as they arrive (dedup via received-id tracking), not strictly ordered | Inferred from `reliableDataPacketsMissing` + `ProcessReliableReceive` | `Dump:Hazel/Hazel/Udp/UdpConnection.cs` |
| Ping value exposed to game | `InnerNetClient.Ping`, `Resends`, `BytesSent`, `BytesGot` | Observed | `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs` |

### 1.4 Message framing (MessageWriter / MessageReader)

Hazel payloads are sequences of **sub-messages**; each is length-prefixed and tagged. (Reference: `HazelRef:Hazel/MessageWriter.cs`; API shape Observed in `Dump:Hazel/Hazel/MessageWriter.cs`, `MessageReader.cs`.)

```
sub-message := length:uint16 (little-endian) | tag:byte | payload[length]
              (length counts payload only, not the 3 header bytes)
nested sub-messages are allowed inside payload (StartMessage/EndMessage use a stack)
```

| Primitive | Encoding | Tag | Source |
|---|---|---|---|
| int/uint/short/ushort/float/ulong | little-endian fixed width | Reference | `HazelRef:Hazel/MessageWriter.cs`; methods Observed in dump |
| Packed uint/int ("WritePacked") | 7-bit groups, least significant first, bit 7 = continuation (LEB128/varint). Negative ints are written as their 32-bit two's-complement pattern, so -1 costs 5 bytes | Reference | `HazelRef:Hazel/MessageWriter.cs` (WritePacked) |
| string | packed length + UTF-8 bytes | Inferred from `Write(string)` signature; Impostor uses same | `Dump:Hazel/Hazel/MessageWriter.cs` |
| bytes with size | packed length + raw bytes (`WriteBytesAndSize`) | Observed (API) | `Dump:Hazel/Hazel/MessageWriter.cs` |
| Vector2 (positions) | two ushorts, each `value = lerp(-50, 50, u/65535)`; 4 bytes total | Reference | `Impostor:src/Impostor.Api/Net/MessageReaderExtensions.cs` |
| Writer buffer | pooled; default buffer 64000 bytes | Reference | `HazelRef:Hazel/MessageWriter.cs` |

Datagram-level view (Reference): an outgoing reliable packet is `01 | id(2 BE) | sub-message | sub-message ...`; an unreliable one is `00 | sub-message ...`.

---

## 2. Client-server model

### 2.1 Roles (who is the authority)

The game is a hybrid: a Innersloth-operated **relay server** that knows about rooms and does validation, plus a **host client** (the room creator, or the next player after migration) that runs the game logic.

| Question | Answer | Tag | Source |
|---|---|---|---|
| Is there a dedicated game server? | Yes, regional servers (`ServerInfo`, `DnsRegionInfo`, `StaticHttpRegionInfo`) accept Hazel connections, keep room state (game code, host id, game state, per-client limbo state) and relay messages. | Observed (types) / Reference (behavior) | `Dump:Assembly-CSharp/ServerInfo.cs`; `Impostor:src/Impostor.Server/Net/State/Game.cs` |
| Who runs game logic (kill rules, tasks, meeting resolution, role assignment, win conditions)? | The host client. Host-only entry points exist: `GameManager.CheckEndGameViaTasks`, `ShouldCheckForGameEnd`, `RpcEndGame`; `AmongUsClient.CoStartGameHost` vs `CoStartGameClient`; `InnerNetClient.AmHost`. | Observed (names) | `Dump:Assembly-CSharp/GameManager.cs`, `AmongUsClient.cs`, `InnerNet/InnerNetClient.cs` |
| What does the server enforce? | Anti-cheat ownership/host checks per message, version compatibility, room capacity, bans, which scene a client may announce, spawn timeouts. | Reference | `Impostor:src/Impostor.Server/Net/Inner/InnerNetObject.Anticheat.cs`, `State/Game.Incoming.cs`, `State/Game.Data.cs` |
| Is the server fully authoritative over gameplay? | No in Impostor-style servers: it validates sender permissions and mirrors some state, but trusts the host for outcomes. The client version carries a flag "DisableServerAuthority" (revision >= 25 on the version int) which toggles whether the server or the host spawns per-player info objects. | Reference | `Impostor:src/Impostor.Api/Innersloth/GameVersion.cs`, `Impostor:src/Impostor.Server/Net/State/Game.cs` (`IsHostAuthoritive`), `Game.Data.cs` (`SpawnPlayerInfoAsync`) |
| Which client is host? | The first client to join the room (room creator). Reference server assigns `HostId` to the first player added. | Reference | `Impostor:src/Impostor.Server/Net/State/Game.State.cs` (HostId assignment) |
| Embedded server for local play | The client can also host the whole thing itself (`InnerNetServer`) for LAN/local games. | Observed | `Dump:Assembly-CSharp/InnerNet/InnerNetServer.cs` |

Client-side flags (Observed): `InnerNetClient.AmClient`, `AmConnected`, `AmHost`, `AmLocalHost`, `AmModdedHost`, `HostId`, `ClientId`, `GameId`, `GameState` (`Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs`). `MatchMakerModes { None, Client, HostAndClient }` is passed to `Connect` (`Dump:Assembly-CSharp/InnerNet/MatchMakerModes.cs`).

What host vs server vs clients decide (mix of Reference and Inferred):

| Decision | Decided by | Tag |
|---|---|---|
| Room existence, code assignment, capacity, bans, version check | Server | Reference (`Impostor:.../Net/State/Game.Incoming.cs`, `Game.State.cs`) |
| Role assignment (`SetRole`), who is impostor, kills (`MurderPlayer`), meeting start/close/vote outcome (`VotingComplete`), game end | Host (RPC sent by host, server checks sender is host) | Reference (`InnerPlayerControl.cs`, `InnerMeetingHud.cs`) |
| Own movement, animations, task completion messages, chat, cosmetics | Owner client (server checks sender owns the object) | Reference |
| Requests that need a ruling (kill attempt, name/color change, shapeshift, vanish, zipline, door close, system update) | Client sends a `Check*` or targeted RPC to the host only (`GameDataTo`), host validates and issues the authoritative RPC to everyone | Reference for routing; Observed for `CmdCheck*` method names in `Dump:Assembly-CSharp/PlayerControl.cs` |
| Lobby settings | Host (`SyncSettings` RPC, host-only) | Reference |
| Public/private flag | Host sends root `AlterGame` (tag 10) | Reference (`Impostor:.../C2S/Message10AlterGameC2S.cs`), Observed (`AlterGameTags.ChangePrivacy=1`, `InnerNetClient.ChangeGamePublic`) |

### 2.2 Game codes

Observed: `Dump:Assembly-CSharp/InnerNet/GameCode.cs` has `V2Flag = int.MinValue (-2147483648)`, `MaxGameNumber = 676`, `GameCodeV2MinVersion`, a 26-char private alphabet `V2` and lookup table `V2Map`, and functions `GameNameToInt`, `IntToGameName`, `GameNameToIntV2`, `IntToGameNameV2`, `CreateGameId(sn, gn)`. Wire form is an `int32`.

Reference (`Impostor:src/Impostor.Api/Innersloth/GameCodeParser.cs`):

| Form | Rule |
|---|---|
| V1 (4 letters) | The four ASCII bytes of the code packed little-endian into an int32. Non-negative-ish ints / `>= -1` are treated as V1. |
| V2 (6 letters) | Any int32 `< -1`. Alphabet order is `QWXRTYLPESDFGHUJKZOCVBINMA`; letter i of the code is mapped through it. Low 10 bits = first two letters (`a + 26*b`), the next 20 bits = the other four (base-26), and the sign bit is set (V2 flag). |
| Generation | 6 random letters drawn from that alphabet; invalid if any char is not a letter. `-1` means invalid. |

Takeaway: a 6-letter code carries ~ 26^6 (about 3.1e8) combinations in an int32 and the rearranged alphabet makes it harder to confuse letters. Only the int travels on the wire (Reference).

### 2.3 Matchmaking and discovery (HTTP + game server)

Observed in `Dump:Assembly-CSharp/HttpMatchmakerManager.cs` (HTTP matchmaker, JSON via Newtonsoft):

| Step / method | Meaning | Tag |
|---|---|---|
| `CoGetOrRefreshToken` | Obtain a matchmaker token. `UserTokenRequestData { Puid, Username, ClientVersion, Language }` is sent; `MatchmakerToken { Content: MatchmakerTokenPayload{Puid, ClientVersion, ExpiresAt}, Hash }` is returned as base64 JSON and cached; request context strings: "authenticate" | Observed (`MatchmakerToken.cs`, `MatchmakerTokenPayload.cs`) |
| `CoFindHostServer` | Ask matchmaker which game server to use when creating a room. Returns `HostServer { Ip, Port }` (ip as long in ctor) | Observed |
| `CoFindGameCode(gameId)` | Resolve a game code to a `HostServer` | Observed |
| `CoFindGameInfo(gameId)` -> `FindGameByCodeResponse { Errors[], Game: GameListing, Region, UntranslatedRegion }` | Look up room metadata by code (also tries other regions: `initialRegion`, `foundGame` fields in closure) | Observed |
| `CoRequestGameListFiltered(IGameOptions, GameFilterOptions)` -> `FindGamesListFilteredResponse { games: GameListing[], metadata: { allGamesCount, matchingGamesCount } }` | Public lobby browser | Observed |
| `CoRefreshTags` -> `HashSet<string>` | Download the allowed filter-tag list | Observed |
| `CoRefreshFilters` -> `PermittedFilters` | Download which filters the player may use | Observed |
| Failure model | `MatchmakerFailure { Reason: DisconnectReasons, CustomDisconnect, MatchmakerError{ Reason, SanctionReason, EndsAt }, ShouldGoOffline }`; one retry with a refreshed token (`didRefreshToken`) | Observed |

URL paths and JSON field names beyond `games` and `metadata` are not visible in the dump (only type shapes). (Unknown.)

`GameListing` (Observed, `Dump:Assembly-CSharp/InnerNet/GameListing.cs`): `IP:uint, Port:ushort, GameId:int, PlayerCount:byte, HostName, TrueHostName, HostPlatformName, QuickChat:QuickChatModes, Platform:Platforms, Age:int, MaxPlayers, NumImpostors, MapId:byte, Language:uint, Options:IGameOptions`; there is a `DeserializeV2(MessageReader)` (so a list can also come in a binary V2 form; root tag `GetGameListV2 = 16`).

`GameFilterOptions` (Observed, `Dump:Assembly-CSharp/InnerNet/GameFilterOptions.cs`): `LastRefreshTime`, `FilterTags: HashSet<string>`, `Serialize/Deserialize`, `ResetToDefaults`. Reference wire form: packed count + strings (`Impostor:src/Impostor.Api/Innersloth/GameFilterOptions.cs`; default tag "Beginner").

Filter types under `Dump:Assembly-CSharp/Amongus/Matchmaking/` (Observed): `BoolGameFilter`, `IntGameFilter`, `CategorizedGameFilter`, `MapGameFilter`, `LanguageFilter`, `ChatModeGameFilter`, `PlatformGameFilter`, `ModFilter`, `GameFilterSet`, `GameFiltersList`, `PermittedFilters`; categorized buckets for kill cooldown, discussion/voting time, vision, speed, emergency meetings, vent time, hide-and-seek times (`Categorized*.cs`). Meaning: lobby search filters by bucketed game options. (Inferred)

Other matchmaking roots (Observed tags, unknown payload): `QuickMatch = 18`, `QuickMatchHost = 19`, `ReportPlayer = 17`, `QueryLobbyInfo = 23`, `SetGameSession = 20`, `SetActivePodType = 21`, `QueryPlatformIds = 22`, `HostModdedGame = 25`. `DisconnectReasons.QuickmatchDisabled`, `NoServersAvailable`, `MatchmakerFull`, `TooManyGames`, `Sanctions` exist.

Regions: `IRegionInfo { Name, PingServer, Servers: ServerInfo[], TargetServer, TranslateName }`; `ServerInfo { Name, Ip, Port, UseDtls, Players, ConnectionFailures, HttpUrl }` (Observed, `Dump:Assembly-CSharp/IRegionInfo.cs`, `ServerInfo.cs`). `Redirect = 13` / `ReselectServer = 14` root tags let a server push the client to another IP:port (Reference payload: IPv4 + ushort port; `Impostor:src/Impostor.Api/Net/Messages/S2C/Message13RedirectS2C.cs`).

### 2.4 Identity, auth and crossplay

| Item | Finding | Tag | Source |
|---|---|---|---|
| Account system | `EOSManager` (Epic Online Services): `eosPUID`, `ProductUserId`, `FriendCode`, `UserIDToken`, device-id login variants, temp/offline/full account login flows, exchange token | Observed | `Dump:Assembly-CSharp/EOSManager.cs` |
| Identity on the wire | `ClientData { Id, PlayerName, ProductUserId, FriendCode, PlayerLevel, PlatformData, InScene, IsReady, Character, ... }`; `PlayerControl.FriendCode` and `Puid` are fields on the net object | Observed | `Dump:Assembly-CSharp/InnerNet/ClientData.cs`, `PlayerControl.cs` |
| Server verifies identity | Server sends Puid/FriendCode of each player in the join message; `NetworkedPlayerInfo` deserialization is checked against known identifiers | Reference | `Impostor:.../C2S`/`S2C/Message07JoinedGameS2C.cs`, `InnerPlayerInfo.cs` |
| Connection hello | Hazel Hello payload ("connection data") built by `GetConnectionData(useDtlsLayout, matchmakerToken)`; includes the matchmaker token | Observed (method + parameter), Inferred (contents) | `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs` |
| Hello fields (older, plain-Impostor view) | game version int32, name string, lastNonce uint32, language uint32, quick-chat mode byte, platform-data sub-message, crossplay flags int32, one unknown byte | Reference | `Impostor:src/Impostor.Api/Net/Messages/C2S/HandshakeC2S.cs` |
| Version encoding | `ClientVersion` int = `year*25000 + month*1800 + day*50 + revision` (so 2026.9.29 -> 2026*25000 + 9*1800 + 29*50 + rev) | Reference | `Impostor:src/Impostor.Api/Innersloth/GameVersion.cs` |
| Platforms | `Platforms { Unknown=0, StandaloneEpicPC=1, StandaloneSteamPC=2, StandaloneMac=3, StandaloneWin10=4, StandaloneItch=5, IPhone=6, Android=7, Switch=8, Xbox=9, Playstation=10 }`; `PlatformSpecificData { Platform, PlatformName, XboxPlatformId, PsnPlatformId }` | Observed | `Dump:Assembly-CSharp/Platforms.cs`, `PlatformSpecificData.cs` |
| Crossplay | `CrossplayMode.GetCrossplayFlags()` returns an int mask; Reference flag bits are `1 << platform enum value` (Epic=bit1 ... Playstation=bit10, All=int.Max). The mask is sent with HostGame, GetGameList and Hello. Disconnects `PlatformLock`, `SelfPlatformLock`, `QuickchatLock`, `PlatformParentalControlsBlock`, `PlatformUserBlock` exist. | Observed (function, reasons) / Reference (bit layout) | `Dump:Assembly-CSharp/InnerNet/CrossplayMode.cs`, `Impostor:src/Impostor.Api/Innersloth/CrossplayFlags.cs` |
| Chat policy | `QuickChatModes { FreeChatOrQuickChat=1, QuickChatOnly=2 }` is part of listing, Hello and filters | Observed | `Dump:Assembly-CSharp/InnerNet/QuickChatModes.cs` |
| Reporting | Root `ReportPlayer = 17`; `ReportReasons { None=-1, InappropriateName=0, InappropriateChat=1, Cheating_Hacking=2, Harassment_Misconduct=3 }`; `ReportOutcome { NotReportedUnknown=0, NotReportedNoAccount=1, NotReportedNotFound=2, NotReportedRateLimit=3, Reported=4 }`; `ClientData.HasBeenReported` | Observed | `Dump:Assembly-CSharp/InnerNet/ReportReasons.cs`, `ReportOutcome.cs` |

### 2.5 Lobby lifecycle and states

Observed enums:

| Enum | Values | Source |
|---|---|---|
| `GameStates` (server room state) | `NotStarted=0, Started=1, Ended=2, Destroyed=3` | `Dump:Assembly-CSharp/InnerNet/GameStates.cs` |
| `LimboStates` (per-client gating) | `PreSpawn=0, NotLimbo=1, WaitingForHost=2` | `Dump:Assembly-CSharp/InnerNet/LimboStates.cs` |
| Client-side join status enum (nested in `InnerNetClient`, name not visible) | `NotJoined=0, Joined=1, Started=2, Ended=3` | `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs` |
| Reference server also uses a transient `Starting` state between `StartGame` and all roles being set | | `Impostor:src/Impostor.Server/Net/State/Game.Incoming.cs` |

Limbo meaning (Reference): `PreSpawn` = connected to the room but has not yet spawned a character (gets only a limited set of messages); `NotLimbo` = full participant; `WaitingForHost` = a rejoiner after a game ended who must wait until the host re-enters the lobby. `Impostor:src/Impostor.Server/Net/State/ClientPlayer.cs`, `Game.State.cs` (`CheckLimboPlayers`).

#### Host a game (Reference flow, Observed names)

1. Client asks the HTTP matchmaker for a token, then `CoFindHostServer` for an IP/port (`HttpMatchmakerManager.cs`).
2. Client connects with `Connect(MatchMakerModes.HostAndClient, token)` -> `CoConnect` -> Hazel Hello (`InnerNetClient.cs`).
3. Client sends root `HostGame (0)`: serialized game options + crossplay flags + `GameFilterOptions`. Server creates the room and replies `HostGame` with the game code int. (`Impostor:.../C2S/Message00HostGameC2S.cs`, `Message00HostGameS2C.cs`; client callback `OnGameCreated(gameIdString)` Observed.)
4. Client sends `JoinGame (1)` for its own code. Server replies `JoinedGame (7)`.

#### Join a game

1. Code entered -> `CoFindGameInfoFromCode`/`CoFindGameCode` (matchmaker) gives region+server; or pick from `GetGameListV2` results -> `CoJoinOnlineGameFromListing`. (`AmongUsClient.cs` Observed.)
2. Connect (`MatchMakerModes.Client`), send `JoinGame (1)`: `gameCode:int32` + a bool ("no crossplay" in Reference). (`Impostor:.../C2S/Message01JoinGameC2S.cs`)
3. Server checks (Reference, `Game.Incoming.cs`): IP ban, version compatibility with host, state not Started/Starting, not Destroyed, capacity vs `MaxPlayers`, not already in limbo-conflict. Failure is a Hazel Disconnect carrying a `DisconnectReasons` value.
4. On success server sends the joiner `JoinedGame (7)`: `gameCode:int32, yourClientId:int32, hostId:int32, count:packed, then per other player: clientId(packed), name, platform data, level(packed), friendCode, puid`. Everyone else receives a `JoinGame` S2C announcing the new client. (`Impostor:.../S2C/Message07JoinedGameS2C.cs`; client hooks `OnGameJoined`, `OnPlayerJoined(ClientData)` Observed.)
5. The server also sends `AlterGame` (public/private flag) right after the join (Reference, `Game.Incoming.cs`).
6. Client sends GameData `SceneChange` ("OnlineGame") (see 3.3). Host then performs `SendInitialData(clientId)` for that client: spawns host-owned objects (GameData / VoteBanSystem / GameManager / LobbyBehaviour) and existing player characters at the joiner (Observed method names `SendInitialData`, `SendGameManager`, `sendInitialDataSpawnGameDataMessages`; mechanics Inferred). The joiner then spawns its own `PlayerControl` (Observed `AmongUsClient.CreatePlayer`). If the joiner does not spawn within a timeout the server drops it (Reference `ClientPlayer.InitializeSpawnTimeout`).
7. Client announces `Ready` (GameData sub-tag 7), `ClientData.IsReady` (Observed `SendClientReady`).

#### Start / end / replay

- Host sends root `StartGame (2)`; server sets state to Starting, broadcasts; host assigns roles with `SetRole` RPCs; server flips to Started once all players have a role (Reference `Game.Incoming.cs`, `InnerPlayerControl.cs`). Observed: `InnerNetClient.SendStartGame`, `AmongUsClient.CoStartGameHost/CoStartGameClient`.
- Game over: host sends root `EndGame (8)` with `GameOverReason:byte` + `showAd:bool`; server sets Ended, puts everyone in `PreSpawn` limbo, despawns server-owned player-info objects. Observed: `InnerNetClient.StartEndGame()`, `FinishEndGame`, `GameManager.RpcEndGame(GameOverReason, bool showAd)`, `OnGameEnd(EndGameResult)`. So `RpcEndGame` is sent as the root `EndGame` message rather than a GameData RPC (Inferred from matching shape).
- Replay in same room: after `Ended`, players rejoin via `JoinGame`. The host's rejoin resets the room to `NotStarted` and pulls others out of `WaitingForHost` limbo; other players get `WaitForHost (12)` until then. (Reference `Game.Incoming.cs` `HandleJoinGameNext`; Observed `OnWaitForHost`, `LimboStates.WaitingForHost`.)

#### Leave, kick, ban, host migration

| Event | Mechanism | Tag | Source |
|---|---|---|---|
| Leave | Client sends `RemovePlayer (4)` with a `DisconnectReasons` byte, or its Hazel connection drops. Server broadcasts `RemovePlayer`: `gameCode:int32, playerId:int32, newHostId:int32, reason:byte`. Client callback `OnPlayerLeft(client, reason)` | Reference / Observed | `Impostor:.../S2C/Message04RemovePlayerS2C.cs`; `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs` |
| Kick / ban | Root `KickPlayer (11)` (host only: `CanKick()`, `CanBan()`, `KickPlayer(clientId, ban)`); server broadcasts kick, then removes player. `DisconnectReasons.Kicked=7 / Banned=6`. Server keeps IP bans. VoteBanSystem lets players vote (`AddVote` RPC) | Observed / Reference | `Impostor:.../State/Game.Incoming.cs` (HandleKickPlayer) |
| Host migration (in lobby) | When the host leaves, the room picks another player as host (first remaining in Reference), clears pending name/color requests, and tells clients the new `hostId` inside the `RemovePlayer` message. `OnBecomeHost` fires on the new host | Reference / Observed | `Impostor:.../State/Game.State.cs` (`MigrateHost`); `InnerNetClient.OnBecomeHost` |
| Host migration (post-game) | Root `EndGameHostMigration (24)`: `gameCode:int32, hostClientId:int32` | Reference (payload), Observed (tag) | `Impostor:.../S2C/Message24EndGameHostMigrationS2C.cs` |
| Host leaves mid-match | Shipped client behavior unknown. Inferred: match cannot continue because only the host runs logic; the server hands host to someone else but game state is not transferable. | Inferred | no evidence either way |
| Abort path | Client side `ExitGame(DisconnectReasons)`, `HandleDisconnect`, `EnqueueDisconnect`, suspend handling (`SecondsSuspendedBeforeDisconnect`, default 30 s, `OnApplicationPause`), `FocusLost*`/`FocusLostBackground`/`IntentionalLeaving` reasons | Observed | `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs`, `DisconnectReasons.cs` |

### 2.6 JoinFailureReasons (Observed, `Dump:Assembly-CSharp/InnerNet/JoinFailureReasons.cs`)

| Value | Name |
|---|---|
| 1 | TooManyPlayers |
| 2 | GameStarted |
| 3 | GameNotFound |
| 4 | HostNotReady |
| 5 | IncorrectVersion |
| 6 | Banned |
| 7 | Kicked |

### 2.7 DisconnectReasons (Observed, `Dump:Assembly-CSharp/DisconnectReasons.cs`)

| Value | Name | Value | Name |
|---|---|---|---|
| 0 | ExitGame | 100 | InternalPlayerMissing |
| 1 | GameFull | 101 | InternalNonceFailure |
| 2 | GameStarted | 102 | InternalConnectionToken |
| 3 | GameNotFound | 103 | PlatformLock |
| 5 | IncorrectVersion | 104 | LobbyInactivity |
| 6 | Banned | 105 | MatchmakerInactivity |
| 7 | Kicked | 106 | InvalidGameOptions |
| 8 | Custom (carries a string) | 107 | NoServersAvailable |
| 9 | InvalidName | 108 | QuickmatchDisabled |
| 10 | Hacking | 109 | TooManyGames |
| 11 | NotAuthorized | 110 | QuickchatLock |
| 12 | ConnectionLimit | 111 | MatchmakerFull |
| 16 | Destroy | 112 | Sanctions |
| 17 | Error | 113 | ServerError |
| 18 | IncorrectGame | 114 | SelfPlatformLock |
| 19 | ServerRequest | 115 | DuplicateConnectionDetected |
| 20 | ServerFull | 116 | TooManyRequests |
| 21 | MismatchedVersion | 207 | FocusLostBackground |
| 208 | IntentionalLeaving | 209 | FocusLost |
| 210 | NewConnection | 211 | PlatformParentalControlsBlock |
| 212 | PlatformUserBlock | 213 | PlatformFailedToGetUserBlock |
| 214 | ServerNotFound | 215 | ClientTimeout |
| 216 | ErrorAuthNonceFailure | 255 | Unknown |

Reference server-to-client disconnect payload: `hasReason:bool`, then a sub-message tag 0 with `reason:byte` and, for `Custom`, a string (`Impostor:.../S2C/MessageDisconnect.cs`). Inferred: the lobby/matchmaker inactivity reasons (104, 105) are server-side idle timeouts.

---

## 3. Message catalogue

### 3.1 Root message tags (Observed values; `Dump:Assembly-CSharp/InnerNet/Tags.cs`)

All root messages are sub-messages inside a Hazel packet. "Payload" and "Direction" are Reference unless noted.

| Tag | Name | Direction / purpose | Payload (Reference unless noted) |
|---|---|---|---|
| 0 | HostGame | C2S create room; S2C returns code | C2S: game options, crossplay int32, filter options. S2C: gameCode int32. `Impostor:.../Message00HostGame*.cs` |
| 1 | JoinGame | C2S join; S2C announces a joiner to the room (or a join error) | C2S: gameCode int32 + bool. `Message01JoinGameC2S.cs` |
| 2 | StartGame | host -> server -> all | host announces game start; payload: game code (copied through by the server) |
| 3 | RemoveGame | Unclear. Impostor ignores it | no evidence |
| 4 | RemovePlayer | C2S leave; S2C player removed + new host id | S2C: gameCode, playerId, hostId (int32 each), reason byte |
| 5 | GameData | Broadcast of object-replication sub-messages (see 3.2) | gameCode int32, then GameData sub-messages |
| 6 | GameDataTo | Same, but addressed to one client (also how clients talk only to the host) | gameCode int32, targetClientId packed, sub-messages |
| 7 | JoinedGame | S2C reply to a successful join | see 2.5 |
| 8 | EndGame | host -> all | gameCode, reason byte, showAd bool |
| 10 | AlterGame | host -> all (privacy) | gameCode, alterTag byte (`ChangePrivacy=1`), isPublic byte |
| 11 | KickPlayer | host -> server -> all | gameCode, clientId packed, isBan bool |
| 12 | WaitForHost | S2C rejoiner must wait | gameCode, clientId |
| 13 | Redirect | S2C use another server | IPv4 + ushort port |
| 14 | ReselectServer | S2C: client should pick another server | Observed tag only |
| 16 | GetGameListV2 | C2S public lobby query over the game connection | packed version (=2), options, quick-chat byte, crossplay int32, filter options. `Message16GetGameListC2S.cs`. Note: 2026.9.29 also has the HTTP list API; which path is used is Unknown |
| 17 | ReportPlayer | C2S | Observed tag, `InnerNetClient.ReportPlayer(clientId, reason)`; response `OnReportedPlayer(outcome, ...)` |
| 18 | QuickMatch | C2S | Observed tag only |
| 19 | QuickMatchHost | C2S | Observed tag only |
| 20 | SetGameSession | C2S (`SetGameSession(string session)`) | Observed method; payload Unknown |
| 21 | SetActivePodType | C2S (`SetActivePodType(string podType)`) | Observed method; Impostor has a handler file `Message21SetActivePodType.cs`. Cosmetic/lobby "pod" selection (Inferred) |
| 22 | QueryPlatformIds | C2S request / S2C reply of console platform ids | Observed `QueryPlatformIds`, `OnQueryPlatformData(List<PlatformSpecificData>)` |
| 23 | QueryLobbyInfo | C2S | Observed tag only; Inferred: ask for lobby metadata |
| 24 | EndGameHostMigration | S2C | gameCode int32, hostClientId int32 |
| 25 | HostModdedGame | C2S create room with a mod identifier | like HostGame plus a mod Guid (Reference `Message25HostModdedGameC2S.cs`); `AmModdedHost` Observed |
| 26 | PackedGameDataTo | Targeted GameData with the game code as a packed int | Reference: host-only; `Impostor:src/Impostor.Server/Net/Client.cs` |
| 255 | ServerDebugAlert | S2C debug | Observed tag only |

Gaps in the numbering (9, 15) are unused in this build. (Observed absence in `Tags.cs`.)

### 3.2 GameData sub-tags (inside root 5/6/26)

Observed values from `Dump:Assembly-CSharp/Amongus/InnerNet/GameDataMessages/GameDataTypes.cs`:

| Value | Name | Wire format (Reference: `Impostor:src/Impostor.Server/Net/State/Game.Data.cs`) | Notes |
|---|---|---|---|
| 1 | DataFlag | `netId:packed`, then the object's `Serialize` payload (state update with dirty-bit semantics) | Owner->all for e.g. movement; host->all for host objects |
| 2 | RpcFlag | `netId:packed`, `callId:byte` (= `RpcCalls`), RPC payload | Dispatched to `HandleRpc(callId, reader)` on the object |
| 4 | SpawnFlag | `spawnId:packed`, `ownerId:packed`, `flags:byte` (`SpawnFlags`), `componentCount:packed`, then per component: `netId:packed` + a sub-message holding the full (initial) serialized state | Server accepts it only from the host. Used for all object creation |
| 5 | DespawnFlag | `netId:packed` | Allowed from owner or host |
| 6 | SceneChangeFlag | `clientId:packed`, `sceneName:string` | A client may only announce itself; server only accepts "OnlineGame" |
| 7 | ReadyFlag | `clientId:packed` | Client says it finished loading |
| 8 | ChangeSettingsFlag_Deprecated | (none, deprecated) | Replaced by the `SyncSettings` RPC |
| 207 | XboxDeclareXuid | `clientId:packed`, platform id | Observed value; Impostor calls the nearby tags 205 `ConsoleDeclareClientPlatformFlag` and 206 `PS4RoomRequest`. Value mismatch is Unknown (naming differs between sources) |

Observed class model (`Dump:Assembly-CSharp/Amongus/InnerNet/GameDataMessages/`): `IGameDataMessage { GameDataType; Serialize(MessageWriter) }`, `BaseGameDataMessage`, `BaseRpcMessage(rpcObjectNetId)` with abstract `RpcType` and `SerializeRpcValues`, `SpawnGameDataMessage(parent, ownerId, flags, childNetObjects[])`, `DespawnGameDataMessage(netId)`, and typed RPC messages (`RpcSetRoleMessage(netId, roleType, canOverrideRole)` etc.). So the shipped client now builds outgoing GameData messages as objects, queues them (`reliableMessageQueue`, `unreliableMessageQueue`) and packs several per datagram (`PackAndSendQueuedMessages`, `LateBroadcastReliableMessage/UnreliableMessage`, `FinishRpcImmediately`). (Observed in `InnerNetClient.cs`.)

Rule of thumb (Reference): one GameData root message may contain many sub-messages and the server validates each one in order; any failed check drops the whole remaining packet and the packet is not forwarded.

### 3.3 RpcCalls (Observed values; `Dump:Assembly-CSharp/RpcCalls.cs`)

Columns: **Sender** is who may send it according to the Impostor anti-cheat (Reference: `Impostor:src/Impostor.Server/Net/Inner/Objects/InnerPlayerControl.cs`, `InnerMeetingHud.cs`, `ShipStatus/InnerShipStatus.cs`, `Components/InnerPlayerPhysics.cs`, `Components/InnerCustomNetworkTransform.cs`, `Components/InnerVoteBanSystem.cs`, `InnerPlayerInfo.cs`). "Owner" = the client that owns the target net object. "Host" = host client. **Check?** = "request" RPC that is sent only to the host (`GameDataTo`), host validates, then broadcasts the authoritative twin. Meanings are mostly Inferred from names and dump method names; Reference verifies sender/target rules.

| Val | Name | Target object | Meaning (Inferred unless noted) | Sender | Check? |
|---|---|---|---|---|---|
| 0 | PlayAnimation | PlayerControl | Play a role animation (`RpcPlayAnimation(byte animType)` Observed) | Owner, broadcast | no |
| 1 | CompleteTask | PlayerControl | Player finished task index (`RpcCompleteTask(uint idx)` Observed) | Owner, broadcast | no |
| 2 | SyncSettings | PlayerControl | Host pushes serialized game options (`RpcSyncSettings(byte[])` Observed) | Host | no |
| 3 | SetInfected | (legacy) | Old impostor assignment, replaced by SetRole. Impostor keeps a payload file only | n/a | no |
| 4 | Exiled | (legacy) | Old ejection message (meeting result is now VotingComplete) | n/a | no |
| 5 | CheckName | PlayerControl | Request a name (`CmdCheckName` Observed); host dedups/sanitizes | Owner -> host only | yes |
| 6 | SetName | PlayerControl | Authoritative name | Host, broadcast | no (response to 5) |
| 7 | CheckColor | PlayerControl | Request a body color (`CmdCheckColor`) | Owner -> host only | yes |
| 8 | SetColor | PlayerControl | Authoritative color | Host, broadcast | no |
| 9 | SetHat_Deprecated | | superseded by 39 | n/a | no |
| 10 | SetSkin_Deprecated | | superseded by 40 | n/a | no |
| 11 | ReportDeadBody | PlayerControl | Player reports a body or presses the button (target null = emergency) | Owner, broadcast | no (host then starts the meeting) |
| 12 | MurderPlayer | PlayerControl | Kill resolved (`RpcMurderPlayer(target, didSucceed)` Observed) | Host, broadcast | no (response to 47) |
| 13 | SendChat | PlayerControl | Free-text chat | Owner, broadcast | no |
| 14 | StartMeeting | PlayerControl | Begin a meeting (`RpcStartMeeting(NetworkedPlayerInfo)` Observed) | Host, broadcast | no |
| 15 | SetScanner | PlayerControl | Medbay scan toggle | Owner, broadcast | no |
| 16 | SendChatNote | PlayerControl | System chat note (`srcPlayerId, ChatNoteTypes` Observed) | Owner, broadcast | no |
| 17 | SetPet_Deprecated | | superseded by 41 | n/a | no |
| 18 | SetStartCounter | PlayerControl | Lobby start countdown value (`RpcSetStartCounter(int)`) | Owner, broadcast (in practice the host's own character) | no |
| 19 | EnterVent | PlayerPhysics | Enter vent id | Owner, broadcast (impostor-vent rules enforced by Reference) | no |
| 20 | ExitVent | PlayerPhysics | Exit vent id | Owner, broadcast | no |
| 21 | SnapTo | CustomNetworkTransform | Teleport + sequence barrier (see section 5) | Owner, broadcast | no |
| 22 | CloseMeeting | MeetingHud | Close meeting UI (`RpcClose`) | Host | no |
| 23 | VotingComplete | MeetingHud | Voting result: voter states, exiled player, tie, overruled, nonce (`RpcVotingComplete(VoterState[], exiled, tie, wasOverruled, nonce)` Observed) | Host | no |
| 24 | CastVote | MeetingHud | A player's vote | Voter, broadcast (no ownership check in Reference) | no |
| 25 | ClearVote | MeetingHud | Clear a voter's vote (`RpcClearVote(PlayerId)` Observed) | Host | no |
| 26 | AddVote | VoteBanSystem | Kick-vote from clientId against targetClientId | Any, clientId must equal sender | no |
| 27 | CloseDoorsOfType | ShipStatus | Impostor sabotage-door closure (`RpcCloseDoorsOfType(SystemTypes)`) | Impostor -> host only | yes |
| 29 | SetTasks | NetworkedPlayerInfo | Assign a player's task type ids (`RpcSetTasks(byte[])`) | Host (Reference does not check; role inferred) | no |
| 31 | ClimbLadder | PlayerPhysics | Ladder use with sequence id | Owner | no |
| 32 | UsePlatform | PlayerControl | Airship moving platform | Owner | no |
| 33 | SendQuickChat | PlayerControl | Preset-phrase chat (`QuickChatPhraseBuilderResult`) | Owner | no |
| 34 | BootFromVent | PlayerPhysics | Force a player out of a vent (`RpcBootFromVent(int)`) | Host (inferred; no check in Reference) | no |
| 35 | UpdateSystem | ShipStatus | Task/sabotage/system interaction (`RpcUpdateSystem(SystemTypes, byte or writer)`) | Any client -> host only | yes |
| 36 | SetVisor_Deprecated | | superseded by 42 | n/a | no |
| 37 | SetNamePlate_Deprecated | | superseded by 43 | n/a | no |
| 38 | SetLevel | PlayerControl | Cosmetic player level | Owner | no |
| 39 | SetHatStr | PlayerControl | Cosmetic id (string) + sequence id byte (`CoSetCosmetic(rpc, id, byte seq)` Observed) | Owner | no |
| 40 | SetSkinStr | PlayerControl | same, skin | Owner | no |
| 41 | SetPetStr | PlayerControl | same, pet | Owner | no |
| 42 | SetVisorStr | PlayerControl | same, visor | Owner | no |
| 43 | SetNamePlateStr | PlayerControl | same, nameplate | Owner | no |
| 44 | SetRole | PlayerControl | Authoritative role (`RpcSetRole(RoleTypes, canOverrideRole)` Observed). Includes ghost roles | Host, broadcast | no |
| 45 | ProtectPlayer | PlayerControl | Guardian Angel protection applied | Host | no (response to 48) |
| 46 | Shapeshift | PlayerControl | Shapeshift resolved | Host | no (response to 55) |
| 47 | CheckMurder | PlayerControl | Impostor requests a kill | Owner (impostor) -> host | yes |
| 48 | CheckProtect | PlayerControl | Guardian requests protection | Owner -> host | yes |
| 49 | Pet | PlayerPhysics | Start petting (`Vector2 pos, petPos`) | Owner | no |
| 50 | CancelPet | PlayerPhysics | Stop petting | Owner | no |
| 51 | CheckZipline | PlayerControl | Request zipline (Fungle) | Owner -> host | yes |
| 52 | UseZipline | PlayerControl | Zipline use confirmed | Host | no |
| 53 | TriggerSpores | PlayerControl | Mushroom spores triggered (Fungle) | Host | no |
| 54 | CheckSpore | PlayerControl | Request spore trigger | Owner -> host | yes |
| 55 | CheckShapeshift | PlayerControl | Request shapeshift | Owner -> host | yes |
| 56 | RejectShapeshift | PlayerControl | Host denied a shapeshift request | Host | no |
| 60 | LobbyTimeExpiring | LobbyBehaviour | Lobby inactivity warning (Inferred; not covered by Impostor) | Unknown (probably host) | no |
| 61 | ExtendLobbyTimer | LobbyBehaviour | Extend the lobby idle timer (`RpcExtendLobbyTimer`) | Unknown | no |
| 62 | CheckVanish | PlayerControl | Phantom requests vanish (`CmdCheckVanish(float maxDuration)`) | Owner -> host | yes |
| 63 | StartVanish | PlayerControl | Vanish begins | Host | no |
| 64 | CheckAppear | PlayerControl | Phantom requests reappear (`CmdCheckAppear(bool animate)`) | Owner -> host | yes |
| 65 | StartAppear | PlayerControl | Reappear begins | Host | no |
| 66 | QueueOverruleVotes | MeetingHud | Judge-style role queues an overrule (judge id, target id, nonce) | Judge -> host only | yes |
| 67 | SpiritGuideMessage | ShipStatus | Private message with 1-3 images from the SpiritGuide ghost role | Role-restricted, targeted | no |

Observations:

- Values 28, 30, 57-59 are absent from the 2026.9.29 enum (Observed). Reference contains a legacy `Rpc30UpdateGameData` payload file only.
- The `Check*` / `Cmd*` pattern is the core of the host-authoritative design: client -> host request, host -> everyone result. In the dump, `PlayerControl` has paired `CmdCheckX` (client sender) and `CheckX` (host-side validation) methods (`Dump:Assembly-CSharp/PlayerControl.cs`). (Observed names; split of responsibilities Inferred.)
- Anti-replay: `PlayerControl.GetNextRpcSequenceId(RpcCalls)` and `ShouldProcessRpc(rpc, sequenceId)` imply some RPCs carry an incrementing byte sequence and stale ones are ignored (Observed signatures; used for cosmetic RPCs per `CoSetCosmetic`). Semantics Inferred.
- The `GameManager` has its own logic components; `RpcEndGame` is emitted via root `EndGame`, see 2.5.

---

## 4. Object replication

### 4.1 Base class and addressing (Observed)

`Dump:Assembly-CSharp/InnerNet/InnerNetObject.cs`:

| Member | Meaning |
|---|---|
| `uint SpawnId` | Index into the spawnable-prefab table |
| `uint NetId` | Unique id within the room; every networked *component* has its own NetId |
| `int OwnerId` | ClientId of the owner, or a special value (below) |
| `SpawnFlags SpawnFlags` | Only `None=0, IsClientCharacter=1` exist |
| `uint DirtyBits` | Bit mask of changed state; `SetDirtyBit(uint)`, `UnsetDirtyBit`, `IsDirtyBitSet(int)`, `IsDirty`, `ClearDirtyBits`, `ClearOrDecrementDirt` |
| `SendOption sendMode` | Whether this object's updates go reliable or unreliable |
| `bool DespawnOnDestroy`, `Despawn()` | Lifetime |
| `AmOwner` | Derived: `OwnerId == local client id` (Inferred from getter name) |
| Abstract `Serialize(MessageWriter, bool initialState)` -> bool | Write state; returns whether anything was written |
| Abstract `Deserialize(MessageReader, bool initialState)` | Apply state |
| Abstract `HandleRpc(byte callId, MessageReader)` | RPC dispatch |

Special ids (Observed constants):

| Constant | Value | Where |
|---|---|---|
| `ClientId.INVALID_OWNER_ID_VALUE` | -1 | `Dump:Assembly-CSharp/InnerNet/ClientId.cs` |
| `ClientId.HOST_INHERIT_OWNERSHIP_ID_VALUE` | -2 | same; also `InnerNetClient.HostInherit` and default `Spawn(..., ownerId = -2)` |
| `ClientId.SERVER_OWNER_ID_VALUE` | -4 | same; `InnerNetClient.ServerOwned` |
| `InnerNetClient.CurrentClient` | -3 | `InnerNetClient.cs` |
| `InnerNetClient.BroadcastTargetClientId` / `NoClientId` | -1 | same |
| `InnerNetClient.MinServerID` | 100000 | NetIds >= 100000 are allocated by the server (Reference: `_nextNetId = 100000` in `Impostor:.../Game.Data.cs`) |
| `PlayerId` | byte (0..254; 255 = invalid) | `Dump:Assembly-CSharp/InnerNet/PlayerId.cs`; Reference `PlayerId = byte.MaxValue` default |
| `NetId` struct | uint wrapper | `Dump:Assembly-CSharp/InnerNet/NetId.cs` |

Two distinct ids per player: **ClientId** (int, per connection, the owner id) and **PlayerId** (byte, in-game slot, stable across the match; used by meetings/tasks/voting).

Ownership rules (Reference + Observed constants): the owner id given at spawn can be a specific ClientId, `HostInherit (-2)` meaning "belongs to whoever is host", or `ServerOwned (-4)` meaning created by the server (Reference: the per-player `NetworkedPlayerInfo` when the host has not requested authority). `InnerNetClient.RemoveUnownedObjects()` and `Despawn` handle cleanup when owners leave (Observed names).

### 4.2 Spawning and the prefab table

| Fact | Tag | Source |
|---|---|---|
| `InnerNetClient.SpawnableObjects : AssetReference[]` (Addressables) and `NonAddressableSpawnableObjects : InnerNetObject[]` hold the prefabs; `SpawnId` is the index into them | Observed fields; index semantics Inferred | `Dump:Assembly-CSharp/InnerNet/InnerNetClient.cs` |
| `AmongUsClient` holds the key prefabs: `GameDataPrefab`, `VoteBanPrefab`, `PlayerPrefab`, `ShipPrefabs: List<AssetReference>`, `OnlineScene`, `MainMenuScene` | Observed | `Dump:Assembly-CSharp/AmongUsClient.cs` |
| `Spawn(parent, ownerId=-2, flags=None)` builds a `SpawnGameDataMessage` containing the parent and `childNetObjects` (all `InnerNetObject` components in the prefab) and assigns NetIds from a counter (`NetIdCnt`) | Observed API, Inferred mechanics | `InnerNetClient.cs`, `GameDataMessages/SpawnGameDataMessage.cs` |
| Receiving a spawn is a coroutine (`CoHandleSpawn`) that loads the prefab asynchronously and waits frames; GameData messages arriving meanwhile are queued (`Dispatcher`, `PreSpawnDispatcher`, `DestroyedObjects` set to ignore late data for dead objects) | Observed field names | `InnerNetClient.cs` |

Spawn id table as implemented by the Reference server (may be stale; the shipped table lives in a Unity asset we have not inspected):

| SpawnId | Prefab | Tag |
|---|---|---|
| 0 | SkeldShipStatus | Reference (`Impostor:.../Game.Data.cs`) |
| 1 | MeetingHud | Reference |
| 2 | LobbyBehaviour | Reference |
| 3 | (GameData in older versions; removed from the table in Reference, replaced by 11) | Inferred |
| 4 | PlayerControl (3 components: PlayerControl, PlayerPhysics, CustomNetworkTransform) | Reference (`InnerPlayerControl.cs`) |
| 5 | MiraShipStatus | Reference |
| 6 | PolusShipStatus | Reference |
| 7 | DleksShipStatus (no matching type in the 2026.9.29 dump, so this id is either unused or retired) | Reference / Observed absence (`type-index.txt`) |
| 8 | AirshipStatus | Reference |
| 9 | HideAndSeekManager | Reference |
| 10 | NormalGameManager | Reference |
| 11 | NetworkedPlayerInfo (one per player) | Reference |
| 12 | VoteBanSystem | Reference |
| 13 | FungleShipStatus | Reference |

Spawn payload (Reference, `Impostor:.../State/Game.Outgoing.cs`, `Game.Data.cs`): `spawnId:packed, ownerId:packed, flags:byte, componentCount:packed, then for each component: netId:packed + length-prefixed initial serialization`. Server rejects a spawn whose component count does not match its table. Only the host may spawn.

### 4.3 Network objects in 2026.9.29 (Observed: classes deriving `InnerNetObject`)

| Class | File (`Dump:Assembly-CSharp/`) | Role | Serialization / RPC hints |
|---|---|---|---|
| `PlayerControl` | `PlayerControl.cs` | The player's core entity: PlayerId byte, FriendCode, Puid, name/color/role via RPC, vent/ladder/pet flags, kill timer, emergency count | Spawn state = `isNew:bool, playerId:byte` (Reference); owns many RPCs (section 3.3). `Rpc*` and `CmdCheck*` method families |
| `PlayerPhysics` | `PlayerPhysics.cs` | Vent, ladder, pet RPCs | RPCs 19, 20, 31, 34, 49, 50. No state (Reference) |
| `CustomNetworkTransform` | `CustomNetworkTransform.cs` | Movement sync | Section 5 |
| `NetworkedPlayerInfo` | `NetworkedPlayerInfo.cs` | Public per-player record: name, outfits (`PlayerOutfit`: color, hat, skin, visor, pet, nameplate), role, tasks (`TaskInfo`), dead/disconnected flags, level, friend code | `RpcSetTasks`; serialization via `PlayerOutfit.Serialize` / `TaskInfo.Serialize` (Observed). Accessed through the plain `GameData` MonoBehaviour (not a net object) which holds the player list |
| `GameData` | `GameData.cs` | NOT an `InnerNetObject` anymore: a MonoBehaviour container for `NetworkedPlayerInfo` instances | Observed (`public class GameData : MonoBehaviour, IDisconnectHandler`) |
| `GameManager` (abstract) + `NormalGameManager`, `HideAndSeekManager` | `GameManager.cs`, `NormalGameManager.cs`, `HideAndSeekManager.cs` | Mode rules and game end checks (`CheckEndGameViaTasks`, `CheckTaskCompletion`, `ShouldCheckForGameEnd`) | Serialize aggregates logic components (Reference `InnerGameManager.cs`) |
| `ShipStatus` + `SkeldShipStatus`, `MiraShipStatus`, `PolusShipStatus`, `AirshipStatus`, `FungleShipStatus` | `ShipStatus.cs` etc. | Map instance and "systems" (doors, sabotage, comms, oxygen, ...) | RPCs 27, 35, 67; Serialize = per-system state (`RpcUpdateSystem(SystemTypes, ...)`). The host drives sabotage state |
| `MeetingHud` | `MeetingHud.cs` | Meeting UI state and votes (`PlayerVoteArea.Serialize`, `VoterState`) | RPCs 22-25, 66; host-only deserialization in Reference |
| `VoteBanSystem` | `VoteBanSystem.cs` | Lobby vote-kick tallies | RPC 26; Serialize sends votes per client |
| `LobbyBehaviour` | `LobbyBehaviour.cs` | Lobby dropship/map theme/idle timer | RPCs 60, 61 |

Absent from the dump: any `Dleks` type (see 4.2).

### 4.4 Dirty-bit state sync (Observed API; mechanics Inferred)

- Objects mark fields dirty with `SetDirtyBit(mask)`. The client loop (`FixedUpdate`, paced by `MinSendInterval`) walks "streamed" object collections (`SendAllStreamedObjects`, `PackAndSendDirtyStreamedObjects(collection, sendOption, ref trackingIndex)`) and for each dirty object writes a `DataFlag` sub-message by calling `Serialize(writer, false)`; a return of `false` means "nothing to send". `ClearOrDecrementDirt` clears the mask (or decrements a counter for objects that must repeat for a few ticks). `indexForReliableStreamedUpdates` / `indexForUnreliableStreamedUpdates` are round-robin cursors so large object sets do not starve each other. All Observed names (`InnerNetClient.cs`); interpretation Inferred.
- Initial state: `Serialize(writer, true)` and `Deserialize(reader, true)` are used inside Spawn messages and for late joiners.
- Only the owner (or host, for host-owned) sends `DataFlag`; the Reference server drops others (`ValidateOwnership` / `ValidateHost` in `Impostor:.../InnerNetObject.Anticheat.cs`).
- Reliability is per object (`sendMode`): movement uses unreliable (Inferred, see 5), most state is reliable.

### 4.5 RPC dispatch

Observed: each object implements `HandleRpc(byte callId, MessageReader)`, `PlayerControl` additionally has `HandleRoleRpc` (role-specific RPC handling, e.g. Shapeshifter/Phantom) (`Dump:Assembly-CSharp/PlayerControl.cs`). Outgoing: `InnerNetClient.StartRpcImmediately(targetNetId, callId, SendOption, targetClientId = -1 (broadcast))` returns a writer; `FinishRpcImmediately(writer)` sends it. RPCs to a single client set `targetClientId` which turns the root into `GameDataTo` (Reference).

Server-side routing (Reference, `Impostor:.../Game.Data.cs`): resolve netId -> object -> `HandleRpcAsync(sender, target, call, reader)`. If a handler rejects the call, only that sub-message is removed from the forwarded packet. The target (`GameDataTo`) is what lets `ValidateCmd` demand "this must be addressed to the host".

---

## 5. Movement sync (`CustomNetworkTransform`)

Source: `Dump:Assembly-CSharp/CustomNetworkTransform.cs` (Observed fields/constants) and `Impostor:src/Impostor.Server/Net/Inner/Objects/Components/InnerCustomNetworkTransform.cs` (Reference wire format).

### 5.1 Observed fields and constants

| Name | Value / type | Meaning (Inferred) |
|---|---|---|
| `lastSequenceId` | ushort | Latest accepted movement sequence number |
| `sendQueue` / `incomingPosQueue` | `Queue<Vector2>` | Outgoing positions waiting to be sent; received positions waiting to be played back |
| `lastPosition`, `lastPosSent`, `tempSnapPosition` | Vector2, Vector2, Vector2? | Last applied, last transmitted, pending teleport |
| `SEND_MOVEMENT_THRESHOLD` | 0.0001 | Minimum change before a new sample is queued/sent |
| `REPLAY_POSITION_THRESHOLD` | 0.003 | Distance tolerance when replaying queued positions |
| `NEUTRAL_BAND_MODIFIER` | 0.995 | Speed multiplier applied near neutral queue length |
| `SMOOTHING_BAND_MODIFIER` | 0.5 | Speed adjustment when the queue is building up |
| `SMOOTHING_LERP_RATE` | 3 | Blend rate for speed changes |
| `QUEUE_THRESHOLD_FOR_SMOOTHING` | 5 | Queue length where smoothing kicks in |
| `QUEUE_LENGTH_FOR_SNAPPING` | 12 | Queue length where it skips ahead (drops frames) |
| `QUEUE_LENGTH_FOR_SECOND_SNAP` | 14 | Second, harder skip |
| `SNAP_TO_SEQUENCE_ID_BUFFER` | 2 | Gap added to the sequence id when snapping |
| `rubberbandModifier`, `idealSpeed` | floats (serialized fields) | Tunables for catch-up |
| Methods | `Serialize/Deserialize`, `RpcSnapTo`, `SnapTo(pos)`, `SnapTo(pos, minSid)`, `SetPaused`, `Halt`, `ClearPositionQueues`, `SkipExcessiveFrames`, `MoveTowardNextPoint`, `SetMovementSmoothingModifier`, `ShouldExtendCurrentFrame`, `DidPassPosition`, `HasMoved` | |

Reading: the owner samples its body position each physics tick into a queue; the remote side keeps a jitter buffer of positions and plays them back a little faster or slower to keep the queue at a target depth, dropping frames if it grows too long (rubber-banding avoided by speed modulation first, snap as last resort). (Inferred from constants/names.)

### 5.2 Wire format (Reference)

| Case | Layout |
|---|---|
| Initial state (spawn) | `sequenceId:uint16`, `position:Vector2 (2x uint16 quantized, range +-50)` |
| Update (DataFlag) | `sequenceId:uint16` (id of the first position), `count:packed`, then `count` Vector2 positions. Position i has id `sequenceId + i` (wraps mod 65536) |
| `SnapTo` RPC (21) | `position:Vector2`, `minSid:uint16` |

Sequence handling (Reference): wrap-aware "greater than" using a half-range window (32767); a position with an id not newer than the last accepted id is ignored (stale/out-of-order). `SnapTo` only applies if `minSid` is newer than the last id, then sets last id to `minSid`; this makes old in-flight positions harmless after a teleport (vent, spawn, meeting teleport, ladder). The server mirrors the last position and checks vent entry coordinates (positions within 0.1 of a vent plus a collider offset of (0, -0.4)).

Precision: 100 world units / 65535 = about 0.0015 units per step (derived from the formula).

Rate/reliability: `MinSendInterval` field exists on `InnerNetClient` (value not visible). Whether movement is sent unreliably is Inferred (the object has its own `sendMode` and the client has a separate unreliable queue).

---

## 6. Takeaways and design proposal for our clone

This section is a **proposal** (not an observation). Observations it relies on are linked by section.

### 6.1 What the original does, in one paragraph

Clients talk over Hazel (UDP with a small reliable layer, DTLS 1.2 in front) to a regional relay server found via an HTTP matchmaker. A 6-letter code maps to a room on one server. The host client runs gameplay; every non-trivial action is a `Check*` request to the host, which broadcasts the result as an RPC. The server mostly validates "who is allowed to send what" and relays. State is replicated as network objects with netIds, spawn/despawn messages and dirty-bit updates; movement uses a compact 16-bit-per-axis sequence-numbered stream with a jitter buffer. (Sections 1-5.)

### 6.2 Recommended architecture for us

| Topic | Recommendation | Rationale |
|---|---|---|
| Authority | **Dedicated authoritative game server per room** (headless, same simulation code as clients where possible), not a player-hosted authority | Host-authoritative means the host can cheat and sees hidden info; the original needs a validating relay anyway (2.1) and still leaks roles through broadcast `SetRole`/`NetworkedPlayerInfo` (Inferred). A small server process per lobby costs little for a 4-15 player, low-tick game |
| If we must keep a host model (cheapest MVP) | Keep the original shape: relay validates sender/permissions, host decides outcomes, `Check*` = unicast request to host | It is proven, and the server rules are simple (Reference 2.1, 3.3) |
| Hidden information | Never send roles or task lists to everyone. Send each player only their own role; send impostors their teammates; send ghosts what ghosts may see | Direct fix for the weakness above |
| Transport | UDP-like with two channels: reliable-ordered (state events, RPCs) and unreliable-sequenced (movement). Use an existing library (e.g. ENet, a QUIC library, or WebSocket for MVP) rather than writing Hazel again. Encrypt (DTLS/QUIC/TLS) from day one | Hazel's own reliability is simple but specific (1.3). Any mature lib covers it; DTLS-class security matters for tokens |
| Max packet | Keep datagrams <= ~500 bytes of payload and never rely on fragmentation (1.2) | Matches the original's behavior |
| Framing | Length-prefixed, tagged sub-messages with varints for ids/counts; fixed little-endian for primitives (1.4) | Simple to implement, compact, easy to skip unknown tags |
| Protocol versioning | Put a version int in the first message and reject mismatches with an explicit reason code; use enum-coded failure reasons like `DisconnectReasons` (2.6, 2.7) | Cheap, great UX |
| Room codes | 6 uppercase letters generated server-side; transmit as int32 (2.2). Do NOT copy the original alphabet table; define our own | Short, voice-friendly |
| Discovery | Small HTTP service: token, create-room (returns server address), find-by-code, public list with filters (2.3) | Same split the original uses; HTTP is simple to scale |
| Lobby states | Room: `Lobby -> Starting -> InGame -> Ended -> (Lobby) / Destroyed`. Player: `PreSpawn / Active / WaitingForRoom` (2.5) | Directly reusable |
| Replication | Keep the idea of network entities with ids, owner, spawn/despawn and per-entity RPC dispatch, but trim to what the game needs: Player (identity + role-public state), PlayerMovement, Map/systems state, Meeting, Lobby, Settings. Merge PlayerControl/PlayerPhysics/NetworkTransform into one logical "player entity" with a movement component (4.3) | Fewer moving parts than 3 netIds per player |
| State sync | Server sends deltas on change with a dirty mask per entity; full snapshot on join (4.4) | Matches proven approach, easy to debug |
| Movement | Client-predicted local movement, server validates speed/collision, 16-bit-per-axis quantized positions, 16-bit wrapping sequence ids, `SnapTo`/teleport barrier with a minimum sequence id, and a small jitter buffer on remote players (5) | Proven small bandwidth; the server-authoritative variant simply rejects impossible deltas |
| Check/Result pattern | Keep it as client -> server `Request*`, server -> all `Result*` (3.3) | Natural with a dedicated server; no separate Check/Set pair needed except where cosmetics are echoed |
| Cosmetic sequencing | Per-RPC sequence byte for idempotence of cosmetic changes (3.3 note) | Cheap protection against reordered duplicates |
| Host migration | Only needed in lobby if you keep player-hosted rooms. With dedicated servers it disappears. If kept: new host = first remaining client, state transferable only in lobby (2.5) | Simplify |
| Disconnect handling | Keepalive ~1.5 s, drop after ~6 missed (about 9 s); app-suspend grace (~30 s on mobile) (1.3, 2.5) | Mobile-friendly |
| Anti-cheat | Per-message permission check table (owner / host / broadcast-only / targeted-to-host), report-and-disconnect with a "Hacking" reason, optional shadow-ban list; keep a server-side mirror of positions and roles (2.1, 3.3) | The Impostor ruleset is a ready checklist |
| Identity | Optional account service; room-scoped player id (byte) for gameplay, separate connection id for transport (4.1 "Two distinct ids") | Clean separation |
| Crossplay | Platform enum + per-room platform mask in create/list requests (2.4) | Include only if needed |
| LAN / offline | Same server binary can be launched locally by the host app (the original ships `InnerNetServer`) (1.1) | Gives free LAN/offline play |

### 6.3 What to simplify vs. the original

- Drop: Xbox/PS-specific ids, EOS, quick-match, report relay, mod GUID, legacy deprecated RPCs and SetXxx string cosmetics per-RPC (replace by one `SetOutfit`).
- Drop: separate DTLS auth-nonce connection (not enough evidence to understand it; use token in the first encrypted message).
- Defer: public list filtering with bucketed options (2.3), vote-kick, Hide-and-Seek manager, multiple map prefab ids.
- Keep: game-code format, join failure and disconnect reason enums, lobby/limbo states, sequence-numbered movement, Check/Result for kills/votes, dirty-bit replication.

### 6.4 Minimal message set to start with (proposal)

| Group | Messages |
|---|---|
| Session | Hello(version, token), HostRoom, JoinRoom(code), Joined(you, host, players), PlayerJoined, PlayerLeft(reason), Disconnect(reason), SetPublic |
| Lobby | SyncSettings, SetName/Color/Cosmetics (request + result), StartGame, EndGame(reason) |
| In-game | Spawn/Despawn, State(delta), Move(seq, positions), SnapTo, RequestKill/Kill, ReportBody/StartMeeting, CastVote/VotingComplete, CompleteTask, UseVent, Sabotage/UpdateSystem, Chat |

---

## 7. Open questions and unknowns

1. HTTP matchmaker URL paths, headers and JSON field names beyond `games`/`metadata` are not visible in the dump (only type shapes). (2.3)
2. Exact contents of the Hazel Hello "connection data" and the purpose/protocol of the AuthManager nonce exchange in 2026.9.29. (1.1, 2.4)
3. Whether the shipped build still uses root `GetGameListV2` (16) over the game connection or only the HTTP list. (3.1)
4. Payloads for `QuickMatch`, `QuickMatchHost`, `QueryLobbyInfo`, `SetGameSession`, `ReselectServer`, `ServerDebugAlert`. (3.1)
5. The authoritative spawn-id table (prefab asset order) in 2026.9.29. The Reference table may differ; `Dleks` has no type in the dump. (4.2)
6. Meaning/senders of `LobbyTimeExpiring` (60) and `ExtendLobbyTimer` (61); and the exact serialization of `GameManager`, `ShipStatus` and `MeetingHud`. (3.3, 4.3)
7. `MinSendInterval`, the real send cadence of movement and how unreliable vs reliable is chosen per object. (4.4, 5)
8. Behavior when the host leaves mid-match in the shipped build. (2.5)
9. GameData sub-tag 207 `XboxDeclareXuid` (Observed) versus Impostor's 205/206 numbering. (3.2)
10. Game options serialization is versioned (`FIRST_MODULAR_OPTIONS_DATA_VERSION = 7`, `LatestVersion`) and is handled in the game-options doc/task; here it is treated as an opaque byte blob carried by HostGame, GetGameList and `SyncSettings`. (Observed: `Dump:Assembly-CSharp/Amongus/GameOptions/GameOptionsFactory.cs`; Reference: `Impostor:src/Impostor.Api/Innersloth/GameOptions/GameOptionsFactory.cs`)
