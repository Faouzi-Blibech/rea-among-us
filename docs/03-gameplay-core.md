# 03 - Core gameplay systems

> **Data update:** numeric values the code dump lacks (player speed 2.5, kill cooldown default 15, role cooldowns/durations, task MaxStep, map scale, exile timings) were later recovered from serialized assets: see [data/gameplay](data/gameplay/README.md) and [data/maps](data/maps/README.md) (Observed vent graphs with links, consoles with task types). Those gaps (reactor/O2 countdowns, door cooldowns, kill distances 1.0/1.8/2.5, option presets) and the exact rule logic were then recovered from decompiled functions: see [logic/](logic/). Where this doc and logic/ disagree, logic/ wins.

Scope: game loop, player model, ship systems and sabotage, meetings and voting, win conditions, Hide and Seek.
Roles, tasks and maps are in [05](05-roles-and-abilities.md) and [06](06-tasks-and-maps.md); networking in [02](02-networking.md); option tables in [04](04-game-options.md).

## Source conventions

| Tag | Meaning |
|---|---|
| Observed | Read from the Android 2026.9.29 Cpp2IL dump (types, fields, offsets, enum values, method signatures). No method bodies exist, so Observed never describes how a method works internally. |
| Reference | Read from the Impostor server source (community reimplementation, may lag the live game). |
| Inferred | Conclusion from names, signatures and structure. Treat as a hypothesis. |

Paths: `Assembly-CSharp/X.cs` is `cs/DiffableCs/Assembly-CSharp/X.cs` in the scratchpad dump; `Impostor:src/...` is under `refs/Impostor`.

## 1. Architecture in one page

- **Host-authoritative simulation.** One client (the host) runs the rules; others send "Check*" requests and the host answers with the confirmed RPC. Evidence: `PlayerControl` exposes `CmdCheckMurder` / `RpcMurderPlayer`, `CmdCheckName`, `CmdCheckColor`, `CmdCheckProtect`, `CmdCheckShapeshift`, `CmdCheckVanish`, `CmdReportDeadBody` (Observed, `Assembly-CSharp/PlayerControl.cs`). The RPC enum has paired `CheckMurder=47`/`MurderPlayer=12`, `CheckProtect=48`/`ProtectPlayer=45`, `CheckShapeshift=55`/`Shapeshift=46` (Observed, `Assembly-CSharp/RpcCalls.cs`). Impostor's relay server has an `IsHostAuthoritive` mode keyed on a client-version flag, and in that mode "passes the RPC on unharmed, the client will handle it" (Reference, `Impostor:src/Impostor.Server/Net/State/Game.cs:99`, `.../Objects/InnerPlayerControl.cs` HandleCheckMurder). The Impostor code comments also say the server can instead run the checks itself (older "server authoritative" behaviour).
- **Clone implication (Inferred):** a clone needs exactly one authoritative rules process (dedicated server or host peer). Clients send intents (move, kill-request, report-request, use, vote); the authority validates and broadcasts results.
- **Replicated objects.** Gameplay state lives on network objects that derive from `InnerNetObject` with `Serialize/Deserialize/HandleRpc` and a dirty flag: `PlayerControl`, `PlayerPhysics`, `NetworkedPlayerInfo`, `GameManager`, `ShipStatus`, `MeetingHud`, `VoteBanSystem` (Observed, each file under `Assembly-CSharp/`). Details of the wire format are in [02](02-networking.md).
- **Modular rules via "logic components".** `GameManager` owns five pluggable components, each with a Normal and a Hide-and-Seek implementation (Observed, `Assembly-CSharp/GameManager.cs` fields `LogicFlow`, `LogicMinigame`, `LogicRoleSelection`, `LogicUsables`, `LogicOptions`):

| Slot | Normal | Hide and Seek |
|---|---|---|
| `LogicGameFlow` (end-of-game checks, timers) | `LogicGameFlowNormal` | `LogicGameFlowHnS` |
| `LogicRoleSelection` | `LogicRoleSelectionNormal` | `LogicRoleSelectionHnS` |
| `LogicOptions` | `LogicOptionsNormal` | `LogicOptionsHnS` |
| `LogicUsables` | `LogicUsablesBasic` | `LogicUsablesHnS` |
| `LogicMinigame` | (base) | `LogicMinigameHnS` |

HnS adds `LogicHnSMusic`, `LogicHnSDangerLevel`, `LogicPingsHnS`, `LogicHnSDeathPopup` (Observed, `Assembly-CSharp/HideAndSeekManager.cs`). Every logic component has `OnGameStart/OnGameEnd/FixedUpdate/Serialize/Deserialize` (Observed, `Assembly-CSharp/LogicGameFlowNormal.cs`).

`GameManager` is abstract; `NormalGameManager` (19 lines, only `GetBodyType/GetDeadBody/GetMapOptions/InitComponents/IsNormal`) and `HideAndSeekManager` are the two concrete modes (Observed, `Assembly-CSharp/NormalGameManager.cs`, `HideAndSeekManager.cs`). Key `GameManager` members: `GameHasStarted`, `ShouldCheckForGameEnd`, `StartGame`, `EndGame`, `RpcEndGame(GameOverReason, bool showAd)`, `CheckTaskCompletion`, `CheckEndGameViaTasks`, `DidHumansWin/DidImpostorsWin(GameOverReason)`, `CanReportBodies`, `SabotagesEnabled`, `OnPlayerDeath(player, assignGhostRole)`, `HandleDisconnect` (Observed, `Assembly-CSharp/GameManager.cs`).

## 2. Game loop and phases

```
Lobby -> (host starts) -> Role assignment -> Intro cutscene -> Free roam
   ^                                                  |   ^
   |                              report / emergency |   | exile cutscene done
   |                                                  v   |
   |                               Meeting: Animating -> Discussion -> Voting -> Results -> Proceeding
   |                                                  |
   +-- EndGame screen <-- GameOver (checked after kills, task done, sabotage, exile, disconnect)
```

### 2.1 Phase table

| Phase | Driving classes (all Observed) | Key methods / data |
|---|---|---|
| Connection / session | `AmongUsClient` (`Assembly-CSharp/AmongUsClient.cs`), `InnerNet/GameStates` | `CoCreateOnlineGame`, `CoJoinOnlineGameFromCode`, `CoStartGameHost`, `CoStartGameClient`, `CoEndGame`, `OnBecomeHost`, `OnGameEnd(EndGameResult)`. `GameStates`: NotStarted=0, Started=1, Ended=2, Destroyed=3 (`Assembly-CSharp/InnerNet/GameStates.cs`). |
| Lobby | `LobbyBehaviour`, `PlayerControl.RpcSetStartCounter` | Start countdown synced via RPC `SetStartCounter=18`; `LobbyTimeExpiring=60`, `ExtendLobbyTimer=61` (`RpcCalls.cs`). Lobby layout in [06](06-tasks-and-maps.md). |
| Game start (host) | `AmongUsClient.CoStartGameHost` -> `GameManager.StartGame` -> logic `OnGameStart` | Host builds options, spawns `ShipStatus`, assigns roles and tasks. Exact order is not recoverable (no bodies); see 2.2. |
| Role assignment | `LogicRoleSelection.AssignRolesForTeam(players, opts, team, teamMax, defaultRole)`; `PlayerControl.RpcSetRole(RoleTypes, canOverride)` / `CoSetRole` | Per team (`RoleTeamTypes` Crewmate=0, Impostor=1). `LogicRoleSelectionNormal.AssignRolesFromList(players, teamMax, roleList, ref rolesAssigned)` fills slots from the role list built from `RoleOptionsCollection` rates (see [04](04-game-options.md)). `SetInfected=3` and `SetRole=44` both exist in the RPC enum; which one the current build uses for the initial assignment is Inferred (`SetRole`). |
| Task assignment | `NetworkedPlayerInfo.RpcSetTasks(byte[] taskTypeIds)`, `PlayerControl.SetTasks`, `ShipStatus.AssignTaskIndexes/AddTasksFromList` | `TaskInfo{Id:uint, TypeId:byte, Complete:bool}` per player. Pools: `ShipStatus.CommonTasks / LongTasks / ShortTasks / SpecialTasks`. Counts come from options (`NumCommonTasks`, `NumShortTasks`, `NumLongTasks`). |
| Intro / role reveal | `IntroCutscene` (`Assembly-CSharp/IntroCutscene.cs`) | `CoBegin` -> `ShowRole()` and `ShowTeam(teamToShow, duration)`; `BeginCrewmate(team)` / `BeginImpostor(yourTeam)`; `SelectTeamToShow(roleFilter)` chooses who is shown (impostors see co-impostors; crew see everyone). Players are spawned on a ring via `ShipStatus.SpawnPlayer(player, numPlayers, initialSpawn)` using `InitialSpawnCenter`, `SpawnRadius`. |
| Free roam | `PlayerControl.FixedUpdate`, `PlayerPhysics`, `LogicGameFlow.FixedUpdate`, `ShipStatus.FixedUpdate` | Movement, tasks, kills, vents, sabotage, cooldown ticking. `ShipStatus.Timer` (float) and `ShipStatus.EmergencyCooldown` (float) drive the emergency lock-out. |
| Report / emergency | `PlayerControl.ReportClosest`, `CmdReportDeadBody(NetworkedPlayerInfo)`, `DeadBody.OnClick`, `EmergencyMinigame.CallMeeting`, `ShipStatus.StartMeeting/CoStartMeeting(reporter, target)`, `MeetingRoomManager` | Target is the dead body's player info, or null for the emergency button. See section 4. |
| Meeting | `MeetingHud` (`Assembly-CSharp/MeetingHud.cs`) | States and flow in section 6. |
| Exile | `ExileController` (abstract) with per-map subclasses (e.g. `SkeldExileController`) | `BeginForGameplay(player, voteTie, wasOverruled)`, `Animate`, `WrapUp`, `ReEnableGameplay`. `InitProperties` carries `isImpostor`, `confirmImpostor`, `totalImpostorCount`, `remainingImpostorCount`, `voteTie`, `wasOverruled` (all Observed, `Assembly-CSharp/ExileController.cs`). The "N impostors remain" text is built from these fields. |
| Game over | `GameManager.RpcEndGame(reason, showAd)`, `LogicGameFlow.CheckEndCriteria`, `EndGameManager` | `EndGameManager.Start/SetEverythingUp/ShowButtons` builds the result screen from `CachedPlayerData` (Observed, `Assembly-CSharp/EndGameManager.cs`). Server marks `GameStates.Ended` and despawns all `NetworkedPlayerInfo` (Reference, `Impostor:src/Impostor.Server/Net/State/Game.Incoming.cs` HandleEndGame). |

### 2.2 Order of events at game start

Observed only as method names; the order below is **Inferred**: set options -> `GameManager.InitComponents` -> spawn `ShipStatus` -> `AssignRolesForTeam` (impostors first, then crew roles) -> `RpcSetTasks` -> `IntroCutscene.CoBegin` -> `OnGameStart` on every logic component -> unlock movement. `PlayerControl.OnGameStart/OnGameEnd` and `NetworkedPlayerInfo.OnGameStart/OnGameEnd` exist to reset per-round state (Observed).

### 2.3 Per-game persistent counters

`GameData` (static) tracks `TimeGameStarted`, `TimeLastMeetingStarted`, `MeetingCount`, `RoundsPlayedInSession`, `LastDeathReason`; instance fields `TotalTasks`, `CompletedTasks`, `AllPlayers`. `InvalidPlayerId=255`, `DisconnectedPlayerId=254` (Observed, `Assembly-CSharp/GameData.cs`). Player ids are single bytes, so max 254 addressable; practical player cap is much lower (see `MaxPlayers` in [04](04-game-options.md)).

## 3. Player model

### 3.1 Object split

| Class | Role | Notable fields (Observed) |
|---|---|---|
| `PlayerControl` | The avatar and its commands. `Assembly-CSharp/PlayerControl.cs` | `PlayerId:byte`, `FriendCode`, `Puid`, `MaxReportDistance:float`, `moveable`, `inVent`, `walkingToVent`, `petting`, `inMovingPlat`, `onLadder`, `protectedByGuardianThisRound`, `shapeshifting`, `waitingForShapeshiftResponse`, `isKilling`, `killTimer:float`, `RemainingEmergencies:int`, `myTasks:List<PlayerTask>`, `isTrackingPlayer`, `trackedPlayer`, `Collider`, `MyPhysics`, `NetTransform`, `KillAnimations[]`. Static `LocalPlayer`, `AllPlayerControls`. |
| `PlayerPhysics` | Movement, ladders, vent entry/exit, petting. `Assembly-CSharp/PlayerPhysics.cs` | `Speed`, `GhostSpeed`, `SpeedMod`, `TrueSpeed`, `Velocity`. RPC wrappers `RpcEnterVent(ventId)`, `RpcExitVent`, `RpcBootFromVent`, `RpcClimbLadder`, `RpcPet`, `RpcCancelPet`. |
| `CustomNetworkTransform` | Position sync (`NetTransform`) | `SnapTo=21` RPC for teleports (`RpcCalls.cs`). Interpolation: `PlayerPhysics.EnableInterpolation`. |
| `NetworkedPlayerInfo` ("PlayerInfo", lives under `GameData`) | Persistent per-player record that survives the avatar | `PlayerId`, `ClientId`, `FriendCode`, `Puid`, `RoleType`, `RoleWhenAlive:RoleTypes?`, `Outfits:Dictionary<PlayerOutfitType,PlayerOutfit>`, `PlayerLevel`, `Disconnected`, `Role:RoleBehaviour`, `Tasks:List<TaskInfo>`, `IsDead`, `WasEjected` (`Assembly-CSharp/NetworkedPlayerInfo.cs`). |
| `PlayerOutfit` | Cosmetic set | `ColorId:int`, `HatId`, `PetId`, `SkinId`, `VisorId`, `NamePlateId`, `PlayerName` (strings) plus a `SequenceId:byte` per cosmetic slot used to order out-of-order cosmetic RPCs (`GetNextRpcSequenceId`, `ShouldProcessRpc` in `PlayerControl.cs`). |
| `RoleBehaviour` | Role definition plus runtime behaviour | `Role:RoleTypes`, `TeamType`, `TasksCountTowardProgress`, `CanUseKillButton`, `CanBeKilled`, `CanVent`, `AffectedByLightAffectors`, `MaxCount`, `DefaultGhostRole`, `Ability`/`SecondaryAbility`; hooks `OnMeetingStart`, `OnVotingComplete`, `OnExileComplete`, `OnDeath(DeathReason)`, `UseAbility`, `DidWin(GameOverReason)`, `FindClosestTarget`, `GetAbilityDistance` (`Assembly-CSharp/RoleBehaviour.cs`). Role specifics are in [05](05-roles-and-abilities.md). |

Outfit types (`PlayerOutfitType`) let a player hold several outfits at once (default, shapeshifted, mushroom-mixup); `RawSetOutfit/SetOutfit/MixUpOutfit/FixMixedUpOutfit` switch between them (Observed, `PlayerControl.cs`; enum in `Assembly-CSharp/PlayerOutfitType.cs`). Cosmetic RPCs (`SetColor=8`, `SetHatStr=39`, `SetSkinStr=40`, `SetPetStr=41`, `SetVisorStr=42`, `SetNamePlateStr=43`, `SetName=6`, `SetLevel=38`) all have a `Check*` or retry loop (`CoSetName`, `CoSetColor` retry up to `MAX_NETOBJECT_INIT_ATTEMPTS=60` every 0.1 s, waiting for `NetworkedPlayerInfo` to spawn) (Observed).

### 3.2 Death and ghosts

- `PlayerControl.Die(DeathReason reason, bool assignGhostRole)` is the single death entry. `DeathReason`: Exile=0, Kill=1, Disconnect=2 (Observed, `Assembly-CSharp/DeathReason.cs`). Sets `NetworkedPlayerInfo.IsDead`; `GameManager.OnPlayerDeath(player, assignGhostRole)` then calls `LogicRoleSelection.OnPlayerDeath` which swaps the role for a ghost role (`RoleTypes.CrewmateGhost=6`, `ImpostorGhost=7`, or `GuardianAngel=4` for crew ghosts when enabled) (Observed names; swap logic Inferred). `RoleWhenAlive` remembers the living role. `PlayerControl.Revive()` and `GameManager.ReviveEveryoneFreeplay()` exist (freeplay/tutorial).
- Ghosts: use `PlayerPhysics.GhostSpeed`; `GhostsDoTasks` option decides whether ghost crew keep doing tasks (`BoolOptionNames.GhostsDoTasks=2`). Dead players still count toward `TasksCountTowardProgress` only if the role says so (Inferred).
- `DeadBody` (`Assembly-CSharp/DeadBody.cs`): `Reported:bool`, `ParentId:byte` (the victim's PlayerId), `myCollider`, `bloodSplatter`, `bodyRenderers[]`, `TruePosition`, `OnClick()`. `GameManager.GetDeadBody(RoleBehaviour impostorRole)` picks the prefab (role-specific bodies, e.g. `ViperDeadBody`).
- Kill animation: `KillAnimation[] KillAnimations` on the killer, `KillOverlay`, `OverlayKillAnimation`, `LongModeKillAnimation` (Observed file names). Kill animation locks both parties briefly (`isKilling` flag; Inferred).

### 3.3 Kill flow

Observed signatures: `PlayerControl.CmdCheckMurder(target)` -> `CheckMurder(target)` -> `RpcMurderPlayer(target, didSucceed)` -> `MurderPlayer(target, MurderResultFlags)`; `MurderResultFlags` (bitflags): NULL=0, Succeeded=1, FailedError=2, FailedProtected=4, DecisionByHost=8 (`Assembly-CSharp/MurderResultFlags.cs`). `KillButton.SetTarget/DoClick/CheckClick`, `RoleBehaviour.FindClosestTarget/IsValidTarget/GetAbilityDistance`, option `KillDistance`/`KillDistances[]` select reach (see [04](04-game-options.md)).

```
Impostor presses kill (KillButton.DoClick)
  -> local: target = FindClosestTarget() within kill distance, killTimer <= 0
  -> CmdCheckMurder(target)                    (request to host)
Host/authority:
  validate: killer alive+impostor, target alive+not impostor, cooldown elapsed
  if target protected by Guardian Angel -> result FailedProtected (cooldown still partly consumed)
  else result Succeeded
  -> RpcMurderPlayer(target, result)           (broadcast)
All clients: MurderPlayer(target, flags)
  if Succeeded: target.Die(DeathReason.Kill), spawn DeadBody, play kill animation, teleport killer to body
  killer.SetKillTimer(KillCooldown)  (shortened if the kill failed)
  authority runs CheckEndCriteria
```

Rules in the Reference server (Reference, `Impostor:src/Impostor.Server/Net/Inner/Objects/InnerPlayerControl.Api.cs` `ValidateMurderPlayer`, `InnerPlayerInfo.cs` `CanMurder`):
- killer must be an impostor and alive; target must exist, be alive, and not an impostor;
- direct `MurderPlayer` from a non-host is flagged as cheating; host must have received a matching `CheckMurder`;
- cooldown check uses **half** of the configured kill cooldown, because after a Guardian-Angel-blocked kill the impostor only waits half the cooldown and the server cannot tell whether the kill was blocked. Pseudo: `now - lastMurder >= KillCooldown / 2`;
- a failed-protected result clears the angel's protection.

The server does **not** check distance (no distance code in Impostor; grep of `KillDistance` shows only option storage) (Reference). Distance is therefore client-side plus host `CheckMurder` handling (Inferred). The actual distance floats are not in the dump (static array initialisers are not emitted); see [04](04-game-options.md) section "Kill distance".

Kill timer: `PlayerControl.killTimer` (float, private), `SetKillTimer(float)`, `IsKillTimerEnabled`, `ForceKillTimerContinue` (Observed). It starts at `KillCooldown` (default 15 s in Impostor's `NormalGameOptions`, Reference) and is frozen during meetings and for the initial game start (Inferred from `ForceKillTimerContinue`). `ResetForMeeting()` exists (Observed) so cooldown handling at meeting boundaries is explicit.

### 3.4 Vent usage

- Objects: `Vent` (`Assembly-CSharp/Vent.cs`) with `Id`, `Left`, `Right`, `Center` neighbours (3-way graph), `CanUse(pc, out canUse, out couldUse)`, `EnterVent(pc)`, `ExitVent(pc)`, `TryMoveToVent(other, out error)`, `UpdateArrows(VentilationSystem)`. Enter/exit animation frame counts set when the sprite hides.
- RPCs: `EnterVent=19`, `ExitVent=20`, `BootFromVent=34` (`RpcCalls.cs`); roles opt in via `RoleBehaviour.CanVent`. Role-specific vent time limits: `EngineerInVentMaxTime`, `CrewmateTimeInVent` (HnS), `CrewmateVentUses` (see [04](04-game-options.md)).
- `VentilationSystem` (system type `Ventilation=37`) tracks who is inside which vent and who is cleaning (section 5.3). Its `Operation` enum: StartCleaning=0, StopCleaning=1, Enter=2, Exit=3, Move=4, BootImpostors=5 (Observed, `Assembly-CSharp/VentilationSystem.cs`). Sequence numbers (`SequenceBuffer<VentMoveInfo>`, `opId:ushort`) order operations from each player (Observed). Cleaning a vent (`VentCleaningMinigame`, `VentDirt`) boots impostors out (Observed names; `BootImpostorsFromVent`).
- Light: `PlayerControl.AdjustLighting` and `ShipStatus.CalculateLightRadius(player)` with `MinLightRadius`, `MaxLightRadius` (Observed); light level multiplied by `CrewLightMod`/`ImpostorLightMod` (options); impostors see at the full radius regardless of lights (Inferred from `AffectedByLightAffectors=false`-style flag on `RoleBehaviour`).

### 3.5 Report flow and emergency button

```
Player A near a body (<= PlayerControl.MaxReportDistance) presses Report
  -> ReportClosest() -> CmdReportDeadBody(NetworkedPlayerInfo target)
  authority: GameManager.CanReportBodies()? meeting not already running?
  -> RpcReportDeadBody(target)   [RPC 11]   then StartMeeting [RPC 14]
Emergency button (SystemConsole on ShipStatus.EmergencyButton):
  -> EmergencyMinigame (MinEmergencyTime = 15 s constant), CallMeeting()
  -> allowed if RemainingEmergencies > 0 and ShipStatus.EmergencyCooldown elapsed and no critical sabotage (Inferred)
  -> StartMeeting(target = null)
ShipStatus.StartMeeting(reporter, target): coroutine CoStartMeeting plays EmergencyOverlay/ReportOverlay,
   then MeetingHud is spawned; every player gets ResetForMeeting() and is teleported to MeetingSpawnCenter ring.
```

Evidence: `PlayerControl.MaxReportDistance`, `RemainingEmergencies`, `ReportClosest`, `CmdReportDeadBody`, `StartMeeting`, `RpcStartMeeting`; `ShipStatus.EmergencyCooldown`, `EmergencyButton`, `BreakEmergencyButton`, `MeetingSpawnCenter`, `MeetingSpawnCenter2`, `EmergencyOverlay`, `ReportOverlay`, `CoStartMeeting`, `OnMeetingCalled`, `RepairCriticalSabotages` (the last implies a meeting auto-repairs critical sabotages; Inferred) (all Observed, `PlayerControl.cs`, `ShipStatus.cs`, `EmergencyMinigame.cs`). Options: `NumEmergencyMeetings` (per player), `EmergencyCooldown` (seconds) in [04](04-game-options.md). The Reference server has `MinEmergencyTime`-independent behaviour: it just relays `ReportDeadBody`/`StartMeeting` after ownership/broadcast validation and fires `PlayerStartMeetingEvent` (Reference, `InnerPlayerControl.cs` lines ~255-300, 1217).

Disconnect during game: `PlayerControl/GameManager/MeetingHud/MeetingRoomManager.HandleDisconnect`, with `DisconnectReasons`; disconnects appear in `NetworkedPlayerInfo.Disconnected` and trigger `ImpostorDisconnect`/`CrewmateDisconnect` game-over checks (section 7).

## 4. Cooldown and timer inventory

| Timer | Where | Notes |
|---|---|---|
| Kill cooldown | `PlayerControl.killTimer`, option `KillCooldown` | Server tolerance uses 50% (Reference). |
| Emergency cooldown | `ShipStatus.EmergencyCooldown`, option `EmergencyCooldown` (int seconds) | |
| Emergencies per player | `PlayerControl.RemainingEmergencies`, option `NumEmergencyMeetings` | |
| Discussion / voting time | `MeetingHud.discussionTimer`, options `DiscussionTime`, `VotingTime` (int seconds) | Server total = intro animation + discussion + voting (below). |
| Initial sabotage cooldown | `SabotageSystemType.Timer`, `initialCooldown`, `DoorsSystemType.SetInitialSabotageCooldown` | Duration is an asset value; unknown. |
| Special sabotage spacing | `SabotageSystemType.SpecialSabDelay = 30` (const) | Observed. After a reactor/O2/lights/comms sabotage ends, another cannot start for 30 s (Inferred). |
| Door re-close cooldown | `DoorsSystemType.timers: Dictionary<SystemTypes,float>` | Per-room timer. |
| Role abilities | `ShapeshifterCooldown/Duration`, `EngineerCooldown`, `ScientistCooldown`, ... | [05](05-roles-and-abilities.md), [04](04-game-options.md). |
| Meeting animation | Reference server waits `0.25+0.5+0.4+3+0.75+5 = 9.9 s` before counting down discussion+voting | `Impostor:src/Impostor.Server/Net/Inner/Objects/InnerMeetingHud.cs` constructor. |
| Results display | `MeetingHud.ResultsTime = 5` s (const) | Observed. |

## 5. Ship systems and sabotage

### 5.1 Framework

`ShipStatus` (`Assembly-CSharp/ShipStatus.cs`) owns `Dictionary<SystemTypes, ISystemType> Systems`. Per-map subclasses: `SkeldShipStatus`, `MiraShipStatus`, `PolusShipStatus`, `AirshipStatus`, `FungleShipStatus` (also a Dleks variant of Skeld, see [06](06-tasks-and-maps.md)). `ShipStatus.MapType` enum: Ship=0, Hq=1, Pb=2, Fungle=3 (Observed; Airship has its own class and the enum has no Airship entry, Inferred oddity).

`ISystemType` contract (Observed, `Assembly-CSharp/ISystemType.cs`):

| Member | Meaning |
|---|---|
| `bool IsDirty` / `MarkClean()` | Replication flag; dirty systems are re-serialised. |
| `Serialize(writer, initialState)` / `Deserialize(reader, initialState)` | State sync. |
| `Deteriorate(float deltaTime)` | Per-frame tick on the authority (timers, countdowns). |
| `UpdateSystem(PlayerControl player, MessageReader msg)` | Applies one player action encoded in a compact payload. |

Optional interfaces: `IActivatable{bool IsActive}` (system is currently sabotaged), `ICriticalSabotage{Countdown, IsActive, UserCount, ClearSabotage()}` (reactor-like), `RunTimer`/`IDoorSystem` (doors) (Observed).

**UpdateSystem RPC pattern.** `ShipStatus.RpcUpdateSystem(SystemTypes type, byte amount)` (or with a `MessageWriter` payload) sends RPC `UpdateSystem=35` (Observed, `ShipStatus.cs`, `RpcCalls.cs`). The target system type id selects the `ISystemType`; the payload is a **bit-packed byte** whose layout is defined per system by const masks (below). Clients send it as a command; the authority applies `UpdateSystem(player, reader)`, marks dirty, and re-broadcasts state via `Serialize`. `CloseDoorsOfType=27` is a separate RPC for door closing. Impostor does not parse the payload (Reference, `Impostor:src/Impostor.Server/Net/Inner/Objects/ShipStatus/InnerShipStatus.cs` TODO), so the encodings below come from dump constants only; semantics are Inferred.

### 5.2 `SystemTypes` enum (Observed, `Assembly-CSharp/SystemTypes.cs`, 59 values)

| Id | Name | Id | Name | Id | Name |
|---|---|---|---|---|---|
| 0 | Hallway | 20 | LockerRoom | 40 | Brig |
| 1 | Storage | 21 | Laboratory | 41 | MeetingRoom |
| 2 | Cafeteria | 22 | Balcony | 42 | Records |
| 3 | Reactor | 23 | Office | 43 | Lounge |
| 4 | UpperEngine | 24 | Greenhouse | 44 | GapRoom |
| 5 | Nav | 25 | Dropship | 45 | MainHall |
| 6 | Admin | 26 | Decontamination2 | 46 | Medical |
| 7 | Electrical | 27 | Outside | 47 | Decontamination3 |
| 8 | LifeSupp | 28 | Specimens | 48 | Zipline |
| 9 | Shields | 29 | BoilerRoom | 49 | MiningPit |
| 10 | MedBay | 30 | VaultRoom | 50 | FishingDock |
| 11 | Security | 31 | Cockpit | 51 | RecRoom |
| 12 | Weapons | 32 | Armory | 52 | Lookout |
| 13 | LowerEngine | 33 | Kitchen | 53 | Beach |
| 14 | Comms | 34 | ViewingDeck | 54 | Highlands |
| 15 | ShipTasks | 35 | HallOfPortraits | 55 | Jungle |
| 16 | Doors | 36 | CargoBay | 56 | SleepingQuarters |
| 17 | Sabotage | 37 | Ventilation | 57 | MushroomMixupSabotage |
| 18 | Decontamination | 38 | Showers | 58 | HeliSabotage |
| 19 | Launchpad | 39 | Engine | | |

Mixed use (Inferred): ids double as room identifiers (for minimap, task location, door rooms) AND as keys of interactive systems. Only a subset are keys in `ShipStatus.Systems` (Reference: Electrical, MedBay, Doors, Comms, Security, Reactor, LifeSupp, Laboratory(Polus reactor), Decontamination/2, GapRoom, MushroomMixupSabotage, Sabotage; `Impostor:src/Impostor.Server/Net/Inner/Objects/ShipStatus/Inner*ShipStatus.cs`). Ventilation likely a key on Airship/Polus/Fungle builds (Inferred; Impostor lacks it).

### 5.3 System implementations (Observed fields/constants, `Assembly-CSharp/<Name>.cs`)

| Class | Mapped to | Tracks | Payload constants / behaviour |
|---|---|---|---|
| `SabotageSystemType` : `ISystemType` | `Sabotage` | `Timer` (cooldown), `specials: List<IActivatable>`, `initialCooldown`, `AnyActive`, `PercentCool` | Constructed with the list of all activatable systems; `AnyActive` blocks new sabotages. `SpecialSabDelay=30`. Sabotage button sends a system id to be activated (Inferred). |
| `ReactorSystemType` : `ISystemType, IActivatable, ICriticalSabotage` | `Reactor` (Skeld/Mira), `Laboratory` (Polus) | `ReactorDuration` (ctor arg), `Countdown`, `UserConsolePairs: HashSet<(playerId, consoleId)>`, `timer`, `SyncRate=2` | Ops: `StartCountdown=128`, `AddUserOp=64`, `RemoveUserOp=32`, `ClearCountdown=16`; `ConsoleIdMask=3`; `RequiredUserCount=2`; `CountdownStopped=10000`. Fix = two different consoles held simultaneously. Countdown reaching 0 ends the game (`ImpostorsBySabotage`). |
| `HeliSabotageSystem` (Airship) : `ICriticalSabotage, IActivatable` | `Reactor` on Airship (Reference), enum `HeliSabotage=58` | `Countdown`, `TargetCode:int`, `ActiveConsoles`, `CompletedConsoles`, `codeResetTimer`, `CharlesDuration=90`, `CodeActiveDuration=10`, `SyncRate=1` | Tags: `DamageBit=128`, `ActiveBit=64`, `DeactiveBit=32`, `FixBit=16`, `TagMask=240`, `IdMask=15`. Players enter a code at consoles within 10 s; 90 s fuse. |
| `LifeSuppSystemType` : `ISystemType, IActivatable` | `LifeSupp` (O2) | `LifeSuppDuration`, `Countdown`, `CompletedConsoles: HashSet<int>`, `SyncRate=2` | Ops `StartCountdown=128`, `AddUserOp=64`, `ClearCountdown=16`; `ConsoleIdMask=3`; `RequiredUserCount=2`. Two O2 consoles (keypads) must each be completed. |
| `SwitchSystem` (lights) : `ISystemType, IActivatable` | `Electrical` | `Value:byte` (lights bitmask), `ExpectedSwitches:byte`, `ActualSwitches:byte`, `DetoriorationTime`, `timer`, `Level` | `NumSwitches=5`, `SwitchesMask=31`, `DamageSystem=128`, `MaxValue=255`. Sabotage randomises `ExpectedSwitches`; each player flip toggles one bit of `ActualSwitches`; fixed when equal; `Level` drives vision radius. `Value` moves toward `MaxValue` (fixed) or 0 (dark) over `DetoriorationTime` (Inferred). |
| `HudOverrideSystemType` (comms) : `ISystemType, IActivatable` | `Comms` (Skeld, Polus, Airship, Fungle) | `IsActive` | `DamageBit=128`, `TaskMask=127`. Single repair console; hides task list/map/cams while active (Inferred). |
| `HqHudSystemType` (Mira comms) | `Comms` (Mira) | `ActiveConsoles: HashSet<(byte,byte)>`, `CompletedConsoles`, `Timer`, `TargetNumber:int`, `ActiveTime=10`, `NumComplete`, `PercentActive` | `Tags`: `DamageBit=128`, `ActiveBit=64`, `DeactiveBit=32`, `FixBit=16`; `TagMask=240`, `IdMask=15`. Two consoles must show a matching number within 10 s (Inferred). Impostor's Mira class still uses plain `HudOverrideSystemType` (Reference, older). |
| `DoorsSystemType` : `ISystemType, IActivatable, RunTimer, IDoorSystem` | `Doors` (Polus, Airship, Fungle, Mira none) | `timers: Dictionary<SystemTypes,float>`, `initialCooldown` | Payload: `CloseDoors=128`, `OpenDoor=64`, `ActionMask=192`, `IdMask=31` (door index up to 31). `CloseDoorsOfType(room)`, `GetTimer(room)`. Doors on this type are re-opened by the "door task" minigame per door (`OpenDoorConsole`, `DoorConsole`). |
| `AutoDoorsSystemType` | `Doors` (Skeld/Dleks) and Airship `Decontamination2` (Reference) | `dirtyBits:uint` (one bit per door), `initialCooldown` | Doors auto-reopen after a timer; `SetDoor(door, open)`. `DoorConsole` lists doors (Observed names). |
| `ElectricalDoors` (Airship `Decontamination` key, Reference) | | `Doors[]`, `Rooms[]`, `LeftExits` | Static shuffled door sets (`Initialize` "Shuffle Doors"). |
| `DeconSystem` (decontamination chambers) | `Decontamination`, `2`, `3` | `CurState: States` flags Idle=0, Enter=1, Closed=2, Exit=4, HeadingUp=8; `DoorOpenTime`, `DeconTime`, `timer` | Commands `HeadUpCmd=1`, `HeadDownCmd=2`, `HeadUpInsideCmd=3`, `HeadDownInsideCmd=4`. Used on Mira and Polus. |
| `MedScanSystem` | `MedBay` | `UsersList: List<byte>` queue, `CurrentUser` (`NoPlayer=255`) | Ops `Request=128`, `Release=64`, `NumMask=31`. Queue so only one player scans at a time. |
| `SecurityCameraSystemType` | `Security` | `PlayersUsing: HashSet<byte>`, `ToRemove`, `InUse` | Ops `IncrementOp=1`, `DecrementOp=2`. In-use flag lights the camera indicator (Inferred). |
| `VentilationSystem` | `Ventilation` | `PlayersInsideVents`, `PlayersCleaningVents`, `SeqBuffers`, `ToRemove` | Ops listed in 3.4; `IsImpostorInsideVent`, `IsVentCurrentlyBeingCleaned`. |
| `MushroomMixupSabotageSystem` (Fungle) | `MushroomMixupSabotage` | `State`: Inactive=0, JustTriggered=1, IdleButMixedUp=2; per-player `CondensedOutfit{HatIndex,VisorIndex,SkinIndex,PetIndex,ColorPlayerId}` | `Operation`: None=0, TriggerSabotage=1; `SYNC_RATE=2`. Randomises everyone's look for a duration. |
| `MovingPlatformBehaviour` (Airship `GapRoom`) | `GapRoom` | platform side, current user | Moving platform sync; also an `ISystemType` (Observed list). |

Sync rates and timers as `SyncRate` (2 or 1 per second) throttle `Deteriorate`-driven dirtying (Inferred).

### 5.4 Sabotage state machine (Inferred from members above)

```
Idle: Sabotage.Timer counts down (initial cooldown at game start; unknown length)
Impostor selects sabotage on map -> UpdateSystem(Sabotage, id) -> target system enters damaged state
  Critical (Reactor/HeliSabotage/LifeSupp):  Countdown from X s; fixed when RequiredUserCount(=2) consoles held / codes done
     Countdown <= 0 -> GameOverReason.ImpostorsBySabotage
  Non-critical (Comms/Electrical/Doors/Mushroom): lasts until repaired
Sabotage.Timer restarts (SpecialSabDelay 30 s guards back-to-back specials)
Meeting called -> ShipStatus.RepairCriticalSabotages() (critical ones cleared) ; emergency button disabled while critical active (Inferred)
```

Durations such as `ReactorDuration` and `LifeSuppDuration` are constructor arguments set per map in `OnEnable` (Observed: `PolusShipStatus.OnEnable` etc. reference system constructors, bodies unavailable); values unknown.

`GameManager.SabotagesEnabled()` lets modes (HnS) switch sabotage off (Observed, `HideAndSeekManager.cs`).

## 6. Meetings and voting

### 6.1 Classes

`MeetingHud` (`Assembly-CSharp/MeetingHud.cs`) holds `PlayerVoteArea[] playerStates`, `SkipVoteButton`, `state:MeetingStates`, `discussionTimer`, `exiledPlayer`, `wasTie`, `wasOverruled`, `reporterId`, and a `judgeOverrulesQueue`. `MeetingStates`: Animating=0, Discussion=1, NotVoted=2, Voted=3, Results=4, Proceeding=5. `VoterState{VoterId:byte, VotedForId:byte}` with `SkippedVote` and `AmDead` computed properties. Constants: `ResultsTime=5`, `NumColumns=3`.

### 6.2 Flow

```
ServerStart(reporterId)  (authority creates MeetingHud, PopulateButtons -> one PlayerVoteArea per PlayerInfo)
CoIntro(reporter, reportedBody, deadBodies) + CoStartCutscene   -> state Animating
Discussion: chat enabled, timer = DiscussionTime                -> state Discussion
Voting: timer = VotingTime; players Select()/Confirm() a target  -> NotVoted -> Voted (locks after confirm)
   local: CmdCastVote(playerId, suspectIdx) -> RPC CastVote(24) -> CastVote()/MarkPlayerAsHasVoted()
   authority: CheckForEndVoting(): ends early when every living, connected player has voted
   timeout: ForceSkipAll()-style path counts unvoted players as "Missed"
VotingComplete(VoterState[] states, NetworkedPlayerInfo exiled, bool tie, bool wasOverruled, ushort overruleNonce)
   RPC VotingComplete(23) from authority -> PopulateResults -> BloopAVoteIcon -> state Results (5 s)
Proceeding: HandleProceed()/RpcClose() (CloseMeeting=22) -> exile cutscene -> back to free roam
```

(Observed method names; ordering Inferred.)

### 6.3 Vote encoding

Per-player vote byte (Reference, `Impostor:src/Impostor.Api/Events/Game/Player/IPlayerVotedEvent.cs`): `0..N` = target PlayerId, `Skipped=253`, `Dead=252`, `Missed=254`, `HasNotVoted=255`. The dump confirms `VoterState.VotedForId` is a byte and exposes `SkippedVote`/`AmDead` (Observed), and `GameData.InvalidPlayerId=255`, `DisconnectedPlayerId=254` (Observed).

### 6.4 Tally, tie, ejection (Reference, `InnerMeetingHud.cs` `CalculateVotes`, `MaxPair`, `HandleVotingCompleteAsync`)

1. Count one vote per living, connected player whose vote is not `Missed`. A vote for a player adds 1 to that PlayerId; a **skip is counted under its own key (253)**, i.e. it behaves as a pseudo-candidate (the filter in `CalculateVotes` excludes only Dead and Missed). Disconnected or dead players are pre-marked `Dead` and add nothing.
2. Find the maximum count. If two or more keys share the maximum (this includes skip tying a player), `tie = true` and nobody is exiled.
3. If the winning key is the skip key, no player has that id, so nobody is exiled (the reference looks the id up and gets null). Net rule: plurality wins; ties and skip-plurality both mean no ejection (Reference; the dump's `CalculateVotes() -> Dictionary<byte,int>` plus `max`/`tie` locals in `CheckForEndVoting` match this shape, Observed).
4. Exiled player: `LastDeathReason = Exile`, `PlayerExileEvent`; `NetworkedPlayerInfo.WasEjected` set (Observed field).
5. Judge overrule (new role; client version 2026.7.15+): the first queued overrule whose judge and target are both connected wins. If the overruled target is an impostor they are exiled, otherwise the judge is exiled instead. `wasOverruled` and a 16-bit nonce (never 0) are broadcast in `VotingComplete`. One overrule per judge per meeting, and a judge can only queue for themself (all Reference, same file; Observed: `QueueOverruleVotes=66`, `CmdQueueOverruleVotes`, `JudgeOverrule` class, `judgeUsesRemaining`, `JudgeTaskRequirementPercentage` option). Details in [05](05-roles-and-abilities.md).

### 6.5 Anonymous votes and ejection confirmation

- `AnonymousVotes` (bool option): hides per-voter icons in `PopulateResults` (Inferred from `BloopAVoteIcon(voter, index, parent)` and the option name).
- `ConfirmImpostor` (bool option): `ExileController.InitProperties.confirmImpostor` and `isImpostor` decide whether the ejection text reveals the team; when off, text is "X was ejected" (Inferred). `remainingImpostorCount` feeds the "N impostors remain" line when enabled.

### 6.6 Other meeting facts

- Dead players cannot vote but can see the meeting with a foreground overlay (`SetForegroundForDead`, `hasForegroundForDeadBeenSet`) (Observed).
- The reporter is shown with `PopulateReporter`; the host gets a `HostIcon`.
- Meeting duration on the server is `~9.9 s + DiscussionTime + VotingTime` (Reference), after which it forces `HandleVotingCompleteAsync`.
- Vote-kick is a separate system (`VoteBanSystem`, RPC `AddVote=26`), not part of the in-game vote.

## 7. Win conditions

`GameOverReason` (Observed, `Assembly-CSharp/GameOverReason.cs`):

| Value | Name | Winner | Trigger (source of logic) |
|---|---|---|---|
| 0 | CrewmatesByVote | Crew | Last living impostor exiled. `LogicGameFlowNormal.CheckEndCriteria` (Observed name; logic Inferred). |
| 1 | CrewmatesByTask | Crew | `GameManager.CheckTaskCompletion()` / `CheckEndGameViaTasks()`: `GameData.CompletedTasks >= TotalTasks` over tasks whose role has `TasksCountTowardProgress` (Observed signatures; formula Inferred). The task bar mode (`TaskBarMode`) only changes display. |
| 2 | ImpostorsByVote | Impostors | Impostors become >= half of living players after an exile. `IsGameOverDueToDeath()` (Observed name). |
| 3 | ImpostorsByKill | Impostors | Impostors >= living crew after a kill; `LogicGameFlow.GetPlayerCounts()` returns a `(Int32,Int32,Int32)` tuple of counts (probably total alive, impostors, crew) (Observed signature; meaning Inferred). |
| 4 | ImpostorsBySabotage | Impostors | `LogicGameFlowNormal.EndGameForSabotage()` when a critical countdown (reactor/O2/heli) hits 0 (Observed method). |
| 5 | ImpostorDisconnect | Crew | All impostors disconnected (Inferred from name). |
| 6 | CrewmateDisconnect | Impostors | Enough crew disconnected that impostors >= crew (Inferred from name). |
| 7 | HideAndSeek_CrewmatesByTimer | Crew | HnS hide timers expire (section 8). |
| 8 | HideAndSeek_ImpostorsByKills | Impostors (seekers) | All hiders caught (Inferred). |

Evaluation timing: `GameManager.ShouldCheckForGameEnd` plus `LogicGameFlow.CheckEndCriteria()` run on the authority each fixed update/after state changes (Inferred). Winners are resolved per player with `RoleBehaviour.DidWin(GameOverReason)` and `GameManager.DidHumansWin/DidImpostorsWin(reason)` (Observed signatures), so role-specific winners are possible. End packet: `GameManager.RpcEndGame(reason, showAd)`; client side `AmongUsClient.OnGameEnd(EndGameResult)` -> `EndGameManager` (Observed). The Reference server just rebroadcasts it (`Game.Incoming.cs` HandleEndGame) and fires `GameEndedEvent(reason)`.

Standard crew/impostor parity rule (Inferred from community knowledge, not from the evidence): impostors win when `impostors >= crewmates alive`; crew win when impostors alive = 0 or tasks complete.

## 8. Hide and Seek mode (Observed unless noted)

- Manager: `HideAndSeekManager : GameManager` with `LogicFlowHnS`, `LogicOptionsHnS`, `LogicMusic`, `LogicDangerLevel`, `LogicPing`, `LogicDeathPopup`; overrides `CanReportBodies`, `SabotagesEnabled`, `DidHumansWin/DidImpostorsWin`, `OnPlayerDeath`, `EndGame`, `FinishTask`, `StartGame` (`Assembly-CSharp/HideAndSeekManager.cs`). It tracks stats (`CheckUpdateGameTimeStats`, `CheckUpdateSurvivedStat`). Props: `BrokenEmergencyButton` sprite (emergency button disabled), `horseBody` / `horseWranglerOutfit` (April Fools variant bodies, `GameModes.SeekFools`).
- Roles: seekers are impostor-team players picked by `LogicRoleSelectionHnS.PickSeekersRoundRobin(players, teamMax)`, optionally pre-chosen via option `ImpostorPlayerID` (Observed).
- Flow (`LogicGameFlowHnS`): `totalHideTime/currentHideTime` (seeker blind/waiting period), then a "final hide" phase `totalFinalHideTime/currentFinalHideTime` (`IsFinalCountdown`, `OnFinalCountdownTriggered`), `AdjustEscapeTimer(timeDeduction, forceDirty)` and `OnTaskComplete(timeDeduction)`: finishing tasks reduces remaining hide time. `SeekerAdminMapEnabled(player)` gives seekers a map in the final phase. State is synced about once a second (`SECONDS_TO_SET_DIRTY=1`); countdown beeps for the last `SECONDS_TO_BEEP=10` s. Serialised state = two floats: final-hide time and hide time (Reference, `Impostor:.../Logic/HideAndSeek/LogicGameFlowHnS.cs`).
- End: `HideAndSeek_CrewmatesByTimer=7` (timer reached zero, hiders survive) or `HideAndSeek_ImpostorsByKills=8` (everyone caught) (`GameOverReason.cs`; mapping Inferred). Crew can also finish all tasks to cut the timer.
- Constants in `HideNSeekGameOptionsV12`: `VeryScaryMusicDistance=15`, `ScaryMusicDistance=55`, `ImpostorSpeedDifference=0.25`, `CrewmateVentCooldown=1`, `ShowPingTime=2`, `CrewmateLeadTime=10` (seconds a crewmate head start, Inferred). Full option list in [04](04-game-options.md).
- Hide-and-seek specifics: flashlight vision (`UseFlashlight`, `CrewmateFlashlightSize`, `ImpostorFlashlightSize`), crewmates vent with limited uses/time (`CrewmateVentUses`, `CrewmateTimeInVent`), seeker pings (`SeekerPings`, `MaxPingTime`), final-hide map (`SeekerFinalMap`), seeker speed boost (`SeekerFinalSpeed`), names hidden/shown (`ShowCrewmateNames`). No meetings, no sabotage, no voting.

## 9. Build checklist for a clone (Inferred)

1. Authority process with a fixed tick; clients send intents only.
2. Components: `GameFlow`, `RoleSelection`, `Options`, `Usables` as strategy objects per mode.
3. Data: `PlayerInfo` (persistent), `PlayerAvatar` (transient), `Tasks`, `Systems` map keyed by enum.
4. Systems as small state machines with a bit-packed command byte, a dirty flag and a `tick(dt)`.
5. Meeting as a state machine (6 states) with vote byte codes and a deterministic tally.
6. One `checkEndCriteria()` after every death, exile, task, sabotage tick and disconnect.
7. Do not copy names, art, audio or map layouts; the mechanics above are what to reproduce.

## 10. Unknowns

- Exact kill distance floats, initial sabotage cooldown, reactor/O2 durations, comms/lights deterioration time: not in dump (static initialisers and prefab/asset values are not emitted). See [04](04-game-options.md).
- Whether the live game still treats skip as a pseudo-candidate exactly as the Impostor reference does, exact order at game start, exact cooldown reset on meeting end, and how ghosts treat tasks for the progress bar.
- Method-body-level behaviour everywhere (dump has signatures only).
