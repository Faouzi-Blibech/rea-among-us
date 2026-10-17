# 01 - Overview: How Among Us Mobile Is Built

One-page map of the whole game. Each section links to the deep-dive doc. Evidence tags and sources are in the linked docs; build identity is in [00](00-methodology-and-evidence.md).

## 1. The game in one paragraph

4-15 players on a 2D top-down spaceship map. Most are Crewmates who finish tasks (minigames at consoles); a few are Impostors who kill, vent and sabotage. A dead body report or the emergency button starts a meeting: discussion, then a vote to eject someone. Crewmates win by finishing all tasks or voting out every Impostor; Impostors win by reaching parity, by a critical sabotage timing out, or by votes. Special roles (Engineer, Scientist, Shapeshifter, Phantom, ...) add one ability each.

## 2. System map

```
                    +------------------------------+
                    |  HTTP matchmaker (token,     |
                    |  find server, game list)     |
                    +---------------+--------------+
                                    |
Client (Unity, IL2CPP)              v
+-----------------------------------------------------------+
| Scenes: SplashIntro -> MainMenu -> FindAGame/MatchMaking  |
|         -> OnlineGame (lobby + match) -> EndGame          |
|                                                           |
| AmongUsClient : InnerNetClient   (Hazel UDP + DTLS)       |
|   GameManager (Normal | HideAndSeek)                      |
|     Logic components: GameFlow, RoleSelection, Options,   |
|     Usables, Minigame                                     |
|   Network objects (InnerNetObject, NetId, OwnerId):       |
|     PlayerControl + PlayerPhysics + CustomNetworkTransform|
|     GameData / NetworkedPlayerInfo   (who, role, tasks)   |
|     ShipStatus subclass per map      (ISystemType dict)   |
|     MeetingHud, VoteBanSystem, LobbyBehaviour             |
|   RoleManager -> RoleBehaviour subclasses (15 RoleTypes)  |
|   Tasks: PlayerTask / NormalPlayerTask -> Minigame (~100) |
|   HudManager, chat + QuickChat, TranslationController     |
| Platform: EOS (accounts, inventory, KWS age), Play Billing|
|           LevelPlay rewarded ads, Sentry, Addressables    |
+-----------------------------------------------------------+
                                    |
                    +---------------v--------------+
                    | Game server: validating relay|
                    | (rooms by 6-letter code)     |
                    +------------------------------+
```

The host client (one of the players) runs the rules. Other clients send `Check*` requests to the host, which broadcasts results. The server validates who may send what and relays. Details: [02](02-networking.md).

## 3. Key numbers (Observed in 2026.9.29)

| Thing | Count | Doc |
|---|---|---|
| Game types in `Assembly-CSharp` | 1,636 | [00](00-methodology-and-evidence.md) |
| RPC ids (`RpcCalls`) | 63 values, 0-67 | [02](02-networking.md) |
| Root network tags | 26 | [02](02-networking.md) |
| Roles (`RoleTypes`) | 15 incl. 2 ghost roles and SpiritGuide | [05](05-roles.md) |
| Game over reasons | 9 (2 for Hide and Seek) | [03](03-gameplay-core.md) |
| Maps (`MapNames`) | 6: Skeld, MiraHQ, Polus, Dleks (mirrored Skeld), Airship, Fungle | [06](06-tasks-and-maps.md) |
| `SystemTypes` (rooms + systems) | 59 | [06](06-tasks-and-maps.md) |
| `TaskTypes` | 82 | [06](06-tasks-and-maps.md) |
| Minigame classes | ~100 | [06](06-tasks-and-maps.md) |
| Localized string keys (`StringNames`) | 1,686 | [07](07-client-architecture.md) |
| Scenes | 10 | [07](07-client-architecture.md) |
| Install size | 1.1 GB, ~95% assets (cosmetics, maps) | [08](08-tech-stack-and-assets.md) |
| Game options serialization | version 12 | [04](04-game-options.md) |

## 4. The 10 design patterns worth copying

1. **Request/approve RPCs.** Client sends `CheckMurder`, host validates (cooldown, range, alive), host broadcasts `MurderPlayer`. Same for shapeshift, vanish, protect, name, color. [02](02-networking.md) [05](05-roles.md)
2. **Ship systems as small state machines.** Every sabotage, door set, camera, vent and med scan is an `ISystemType` with `Deteriorate(dt)`, `UpdateSystem(player, opcode)`, dirty flag and `Serialize`. One RPC (`UpdateSystem`, id 35) drives them all. [03](03-gameplay-core.md)
3. **Critical sabotage = two consoles held at once** (`RequiredUserCount = 2` for reactor and O2). [03](03-gameplay-core.md)
4. **Tasks are indexes into per-map Common/Short/Long arrays**; host assigns a byte list; progress counts only roles with `TasksCountTowardProgress`. [06](06-tasks-and-maps.md)
5. **Roles are classes with a small hook surface** (can kill, can vent, ability button, task counting, ghost role on death). [05](05-roles.md)
6. **Vents are a graph** with up to 3 links per vent (Left/Center/Right). [06](06-tasks-and-maps.md), [data/maps](data/maps/README.md)
7. **Meeting vote bytes**: player id, or sentinels Dead=252, Skip=253, Missed=254, NotVoted=255; plurality wins, tie or skip means no eject. [03](03-gameplay-core.md)
8. **Compact movement**: positions quantized to 2 x uint16 over -50..50, uint16 sequence ids, `SnapTo` barrier after teleports, jitter buffer. [02](02-networking.md)
9. **Pluggable game modes**: Normal and Hide and Seek share the engine, differ by Logic components and options class. [03](03-gameplay-core.md)
10. **Quick chat sends string ids, not text**, so it is localized per client and safe for minors. [07](07-client-architecture.md)

## 5. Where to go next

- Building the clone: [09 - Recreation blueprint](09-recreation-blueprint.md)
- Coding agents: [`AGENTS.md`](../AGENTS.md) at repo root
- Raw numbers: [`data/maps`](data/maps/README.md), [`data/gameplay`](data/gameplay/README.md)
