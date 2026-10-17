# Logic: Sabotage and ship systems

This document specifies how the ship "systems" of Among Us mobile (Android 2026.9.29, IL2CPP) behave: critical sabotages (reactor, oxygen, helicopter), lights, comms, the Fungle mushroom mix-up, doors, the shared sabotage cooldown, and the utility systems (ventilation, cameras, medbay scanner). It is written so a C# developer can implement an equivalent server-authoritative simulation without reading the decompiled code.

Conventions used below:

- Evidence tags: **Observed (decompiled)** = read from a function body (cited as `Class.Method @ 0xRVA`), **Observed (dump)** = from signatures, constants, field offsets or exported asset data, **Inferred** = interpretation, reason given.
- "Tick" means one call of `ShipStatus.FixedUpdate`, which hands `Time.fixedDeltaTime` to every system's `Deteriorate`. The project's fixedDeltaTime value is not in the decompiled code (Unity default would be 0.02 s). Anything below that depends on it is flagged.
- `SystemTypes` ids used as dictionary keys: Reactor 3, Electrical 7, LifeSupp 8, MedBay 10, Security 11, Comms 14, Doors 16, Sabotage 17, Decontamination 18, Laboratory 21, Decontamination2 26, GapRoom 44, Ventilation 37, MushroomMixupSabotage 57, HeliSabotage 58 (Observed (dump): `SystemTypes` enum).

## Summary of key numbers

| Item | Value | Source |
|---|---|---|
| Reactor duration | Skeld 30 s, Mira 45 s, Polus Laboratory (system 21) 60 s, Fungle 60 s, Airship none | `ShipStatus.OnEnable @ 0x225029c`, `PolusShipStatus.OnEnable @ 0x21bb548`, `FungleShipStatus.OnEnable @ 0x2428a80` |
| Oxygen (LifeSupp) duration | Skeld 30 s, Mira 45 s, others none | `ShipStatus.OnEnable @ 0x225029c` |
| Helicopter ("Charles", Airship) | 90 s | `HeliSabotageSystem.UpdateSystem @ 0x21ac4e8` |
| Countdown "stopped" sentinel | 10000.0 | `ReactorSystemType.ClearSabotage @ 0x22054d8` |
| Critical sabotage needs | 2 distinct consoles (reactor: held at the same time; O2/Mira comms/heli: completed within a window or sticky) | sections below |
| Countdown sync rate | Reactor/O2 every 2.0 s, Heli every 1.0 s, Mushroom every 2.0 s | `Deteriorate` bodies |
| Shared sabotage cooldown | 30 s after any special sabotage; 10 s initial (after `SetInitialSabotageCooldown`) | `SabotageSystemType.UpdateSystem @ 0x224f794`, `SetInitialSabotageCooldown @ 0x224f780` |
| Cooldown only counts down while | no special sabotage is active | `SabotageSystemType.Deteriorate @ 0x224f728` |
| Door cooldowns | per room 30 s after closing; initial 10 s | `DoorsSystemType.CloseDoorsOfType @ 0x2249d50`, `SetInitialSabotageCooldown @ 0x2249ed8` |
| Autodoor (Skeld) | cooldown 30 s, closed duration 10 s, initial 10 s | `AutoOpenDoor` consts (dump), `AutoDoorsSystemType.SetInitialSabotageCooldown @ 0x22494b8` |
| Lights level | byte 0..255, step 3 per 0.03 s of accumulated tick time; level = value/255 | `SwitchSystem.Deteriorate @ 0x241a33c`, `get_Level @ 0x241a244` |
| Vision radius, crew | (Min + level x (Max - Min)) x CrewLightMod; Min 1.0, Max 5.0 on every map | `ShipStatus.CalculateLightRadius @ 0x2253068`, `ship_tuning.json` |
| Vision radius, impostor | Max x ImpostorLightMod (lights ignored) | same |
| Vision radius, dead or no player | Max (no multiplier) | same |
| Mira comms code window | 10 s, code 0..99999 | `HqHudSystemType.Deteriorate @ 0x224c0a8` |
| Mushroom mix-up auto-heal | 10 s | `secondsForAutoHeal` in `ship_tuning.json` (dump) |
| Sabotage game over | `GameOverReason.ImpostorsBySabotage` = 4 | `LogicGameFlowNormal.EndGameForSabotage @ 0x20de73c` |

## 1. Which systems exist per map

`ShipStatus.OnEnable` builds a dictionary `Systems` (SystemTypes to system object) once. The base version switches on the ship `Type` field (Ship = 0, Hq = 1, Pb = 2, Fungle = 3); Polus, Airship and Fungle replace it with their own `OnEnable`; Skeld and Mira only have a constructor (nothing relevant: it sets default light radii and a logger). Airship's prefab `Type` is `Ship`, but `AirshipStatus.OnEnable` overrides everything.

| System (key) | Skeld | Mira | Polus | Airship | Fungle |
|---|---|---|---|---|---|
| Electrical lights `SwitchSystem` (7) | yes | yes | yes | yes | no |
| `MedScanSystem` (10) | yes | yes | yes | yes | no |
| `SecurityCameraSystemType` (11) | yes | no | yes | yes | no |
| Comms (14) | `HudOverrideSystemType` | `HqHudSystemType` | `HudOverrideSystemType` | `HudOverrideSystemType` | `HqHudSystemType` |
| Doors (16) | `AutoDoorsSystemType` | none | `DoorsSystemType` | `DoorsSystemType` | `DoorsSystemType` |
| `ReactorSystemType` (3) | 30 s | 45 s | none | none | 60 s |
| `ReactorSystemType` as Laboratory (21) | no | no | 60 s | no | no |
| `LifeSuppSystemType` (8) | 30 s | 45 s | no | no | no |
| `VentilationSystem` (37) | yes | yes | yes | yes | yes |
| `HeliSabotageSystem` (58) | no | no | no | yes | no |
| `MushroomMixupSabotageSystem` (57) | no | no | no | no | yes |
| `ElectricalDoors` (18, "Decontamination" key) | no | no | no | yes | no |
| `AutoDoorsSystemType` (26, "Decontamination2" key) | no | no | no | yes | no |
| Moving platform (44, "GapRoom" key) | no | no | no | yes | no |
| `SabotageSystemType` (17) | yes | yes | yes | yes | yes |

Evidence: **Observed (decompiled)** for every row, from `ShipStatus.OnEnable @ 0x225029c` (Skeld branch registers doors/comms/camera/reactor 30/O2 30/ventilation; Mira branch registers HqHud/reactor 45/O2 45/ventilation; always registers Switch and MedScan first), `PolusShipStatus.OnEnable @ 0x21bb548`, `AirshipStatus.OnEnable @ 0x20d1a8c`, `FungleShipStatus.OnEnable @ 0x2428a80`. Notes:

- The Skeld doors system is the autodoor class because the allocated class pointer is the same one `AirshipStatus.OnEnable` uses for `AutoDoorsSystemType` (**Inferred** from matching class pointers).
- The base `OnEnable` also contains a Polus branch (Type = 2) that duplicates the Polus override minus ventilation. It is unreachable for the real Polus prefab because `PolusShipStatus` overrides `OnEnable` (**Inferred**).
- The `SabotageSystemType` is always created last. Its `specials` array is every system in the dictionary that implements `IActivatable` (a LINQ chain over `Systems.Values`; **Inferred** from the chain shape and the `OnEnable` delegate name). Implementers (Observed (dump) interface lists): `SwitchSystem`, `ReactorSystemType`, `LifeSuppSystemType`, `HudOverrideSystemType`, `HqHudSystemType`, `DoorsSystemType`, `AutoDoorsSystemType`, `HeliSabotageSystem`, `MushroomMixupSabotageSystem`. The constructor drops null entries.
- Airship additionally calls `ElectricalDoors.Initialize` first when the local client is host.
- Light radius defaults: `MaxLightRadius` 5.0, `MinLightRadius` 1.0 on all six ship prefabs (Skeld, April Skeld, Mira, Polus, Airship, Fungle) per `docs/data/gameplay/ship_tuning.json` (Observed (dump)).

### Implementation notes for our C# /sim
- Model the per-map system set as data (a list of system kinds + parameters), not as code branches.
- Keep the key ids only if you want wire compatibility; otherwise use your own enum.
- The "specials" set drives only one thing: whether the shared sabotage cooldown is paused (section 3). Doors never pause it on the door-dictionary variant (always inactive), but the autodoor variant may (section 7).

## 2. Common framework

### 2.1 Interface surface (Observed (dump))
Every system implements: `IsDirty`, `Deteriorate(dt)`, `UpdateSystem(player, reader)`, `Serialize(writer, initialState)`, `Deserialize(reader, initialState)`, `MarkClean()`. `IActivatable` adds `IsActive`. `ICriticalSabotage` (reactor, heli) adds `Countdown`, `UserCount`, `ClearSabotage()`. `IDoorSystem` adds `CloseDoorsOfType(room)`, `SetInitialSabotageCooldown()`.

### 2.2 Per tick
`ShipStatus.FixedUpdate @ 0x2252da4` (**Observed (decompiled)**), in order:

1. Do nothing unless the client object and the local player object both exist.
2. `Timer += fixedDt` (ship uptime), `EmergencyCooldown -= fixedDt`.
3. If game data exists, recompute task counts.
4. If this process is a connected client (which includes the host), walk the global ordered list of all system ids; for each id present in `Systems`, call `Deteriorate(fixedDt)`. The list is `SystemTypeHelpers.AllTypes`; ascending enum order is assumed (**Inferred**). Order only matters for the cooldown rule in section 3: systems with lower ids (reactor 3, lights 7, O2 8, comms 14, doors 16) update before the sabotage system (17); heli (58) and mushroom (57) update after.

Important: `Deteriorate` runs on every client, not only the host. Each client counts countdowns down locally; the host marks the system dirty periodically and the next received `Deserialize` overwrites local values. The game-over check that reads the countdowns is separate (section 4.5).

### 2.3 Message entry points (`ShipStatus.HandleRpc @ 0x2253d2c`, `ShipStatus.UpdateSystem` overloads @ 0x224fa1c and @ 0x2251dd0)
- RPC id 35 (0x23) "update system": reads a system id byte and the sending player object, then passes the remaining bytes to `Systems[id].UpdateSystem(player, reader)`. Unknown ids are ignored. (**Observed (decompiled)**; the host-only gating is not visible in the body, **Unknown**.)
- RPC id 27 (0x1b) "close doors of type": reads one room byte and calls `CloseDoorsOfType(room)` on `Systems[16]` through the door interface. (**Observed (decompiled)**.)
- RPC id 67 ('C'): spirit-guide message UI (cosmetic; suppressed while a meeting hud exists). Nothing relevant.
- `ShipStatus.UpdateSystem(id, player, byte)` (local helper): looks up the system, wraps the single byte into a reader and calls `UpdateSystem`. If the id is not in `Systems` it silently returns. `RpcUpdateSystem` itself (the sender) is not in the decompiled set.

### Implementation notes
- Replace "every client ticks and the host syncs" with: server ticks; clients get countdown snapshots (a few Hz) and interpolate locally. Do not copy the 2 s sync period, it exists to save bandwidth.
- Every `UpdateSystem` payload below is a single opcode byte unless stated; keep them as typed commands in our own protocol.

## 3. Shared sabotage cooldown: `SabotageSystemType`

Fields: `Timer` (float, seconds left), `specials` (array of `IActivatable`), `initialCooldown` (bool), dirty flag. **Observed (dump)** field list.

State and per-tick (`Deteriorate @ 0x224f728`, **Observed (decompiled)**):
1. If `Timer > 0` and no special is active (`AnyActive` = any element of `specials` has `IsActive` true): mark dirty, `Timer -= dt`. If `Timer <= 0` and `initialCooldown` is set, clear the flag.
2. Otherwise nothing (the timer is frozen while a sabotage is active, and stays at 0 when ready).

`SetInitialSabotageCooldown @ 0x224f780`: `Timer = 10.0`, `initialCooldown = true`. Used by the game flow to start the "first 10 seconds" lockout. The callers are not in the decompiled set (not in `ShipStatus.Begin`, which is only task assignment) so when it is invoked is **Unknown**; a natural rule is game start and after each meeting (**Inferred**, see Open questions).

`PercentCool @ 0x224f48c` = `Timer / 10` while `initialCooldown` else `Timer / 30` (UI fill).

`UpdateSystem @ 0x224f794` (a sabotage request). Order of checks (**Observed (decompiled)**):
1. Mark dirty.
2. If `Timer > 0`: stop (cooldown not ready). Nothing is applied.
3. If a meeting-hud singleton exists: stop (**Inferred** to be `MeetingHud.Instance`; the same singleton is used by engineer, mushroom and RPC code to suppress effects during meetings).
4. Read one byte: the sabotage target `SystemTypes`.
5. Only if the local process is host, dispatch by target:

| Target byte | Meaning | Effect |
|---|---|---|
| 3 | Reactor | `UpdateSystem(3, caller, 0x80)` |
| 8 | Oxygen | `UpdateSystem(8, caller, 0x80)` |
| 7 | Lights | build a 5-bit mask, then `RpcUpdateSystem(7, mask or 0x80)` (below) |
| 14 | Comms | `UpdateSystem(14, caller, 0x80)` |
| 21 | Polus Laboratory reactor | `UpdateSystem(21, caller, 0x80)` |
| 57 | Mushroom mix-up | `UpdateSystem(57, caller, 1)` |
| 58 | Helicopter | `UpdateSystem(58, caller, 0x80)` |
| other | ignored (door closing is not a sabotage-system opcode) | none |

6. In every case that reaches this point, including non-host callers and unknown targets, set `Timer = 30.0` and dirty.

Lights mask: start with bit 2 (value 4) already set; for each of the 5 switch bits, set it with probability 0.5 (a boolean draw at 0.5). So at least one switch is always wrong. The final byte is `mask | 0x80`.

There is no check of the caller's role, liveness or position in this method (**Observed (decompiled)**); the original relies on the UI. A server must validate: caller is an alive impostor-team player allowed to sabotage, no meeting, cooldown 0, and the chosen sabotage exists on this map.

Serialize: one float `Timer` (4 bytes little endian). Deserialize: read it. `initialCooldown` is not on the wire (**Observed (decompiled)**).

### Implementation notes
- Keep exactly: single shared timer, frozen while any critical sabotage, lights, or comms are active; 30 s after a trigger; 10 s initial lock.
- The original dirties this system every tick while counting; send it only on start/stop events and periodic snapshots.
- Decide whether doors count as "active" (section 7); simplest faithful choice: dictionary-door maps never block, Skeld autodoors block while a door is closed.

## 4. Critical sabotages

### 4.1 Reactor: `ReactorSystemType`

Constants (Observed (dump)): `StartCountdown 0x80`, `AddUserOp 0x40`, `RemoveUserOp 0x20`, `ClearCountdown 0x10`, `CountdownStopped 10000`, `ConsoleIdMask 3`, `RequiredUserCount 2`, `SyncRate 2`.

State: `Countdown` (float, 10000 when idle; constructor sets it to 10000), `ReactorDuration`, `UserConsolePairs` (set of (playerId, consoleId)), `timer` (sync accumulator), dirty, own system id.
`IsActive` = `Countdown < 10000`. `UserCount` = number of **distinct console ids** among the pairs (**Observed (decompiled)** `get_UserCount @ 0x2205280`: a bitmask over the console id). `GetConsoleComplete(id)` = any pair has that console id.

`UpdateSystem @ 0x22054e8`, opcode byte `op`:
1. If `op == 0x10`: `Countdown = 10000` (cleared, repaired).
2. Else if `op == 0x80` and the system is not active: `Countdown = duration`, clear all pairs.
3. Else if `op` has bit 0x40: add (callerId, op & 3); then if distinct console count >= 2: `Countdown = 10000` (repaired).
4. Else if `op` has bit 0x20: remove (callerId, op & 3).
5. Always mark dirty at the end.

A start request while already active is ignored (falls through the bit checks and only dirties). The console id is only 2 bits, so 0..3, practically 0 and 1.

`Deteriorate @ 0x22056f4`:
- Inactive: presentation only (stops the red flash coroutine, hides the overlay).
- Active: if the local player lacks the fix task for this system, add it (UI). Then `Countdown -= dt`, `timer += dt`; if `timer > 2.0`: `timer = 0` and mark dirty.

Serialize: `float Countdown`, packed int N, N x (byte playerId, byte consoleId). Deserialize: read float, clear pairs, read packed N and N pairs. (**Observed (decompiled)**.)

Fix procedure: two different players must stand at the two different consoles at once; each presses and holds; the instant two distinct console ids are held, the sabotage ends. Releasing sends the remove op. Because the set is not cleared on repair, a later start clears it.

### 4.2 Oxygen: `LifeSuppSystemType`

Constants: same op bytes (0x80 start, 0x40 add, 0x10 clear), `ConsoleIdMask 3`, `RequiredUserCount 2`, `SyncRate 2`.

State: `Countdown`, `LifeSuppDuration`, `CompletedConsoles` (set of int), `timer`, dirty. `IsActive` = `Countdown < 10000`. `UserCount` = size of `CompletedConsoles`.

`UpdateSystem @ 0x2204340`:
1. `op == 0x10`: `Countdown = 10000`.
2. Else if `op != 0x80` or the system is already active: if `op` has bit 0x40, add (op & 3) to `CompletedConsoles`.
3. Else (`op == 0x80` and idle): `Countdown = duration`, clear `CompletedConsoles`.
4. Mark dirty.

`Deteriorate @ 0x2204458` when active: add the fix task to the local player if missing (UI); `Countdown -= dt`; if `UserCount < 2`: `timer += dt`, and when `timer > 2.0` reset and mark dirty; else (two consoles completed) `Countdown = 10000` and mark dirty (repaired).

Unlike the reactor, completed consoles persist: two different consoles each entered once (the keypad code is checked by the client minigame) is enough, no simultaneous holding. The server must verify codes itself since the system accepts any add op.

Serialize: `float Countdown`, packed int N, N x packed int consoleId. Deserialize: read float; **only if more bytes remain** clear the set and read packed N and N packed ints (**Observed (decompiled)** `Deserialize @ 0x2204714`), which makes an older shorter payload valid.

### 4.3 Helicopter ("Charles", Airship): `HeliSabotageSystem`

Constants (dump): `CountdownStopped 10000`, `TagMask 0xF0`, `IdMask 0x0F`, `CharlesDuration 90`, `CodeActiveDuration 10`, `SyncRate 1`.

State: `Countdown` (10000 initially), `codeResetTimer`, `syncTimer`, `wasActive`, `TargetCode` (int), `ActiveConsoles` (set of (playerId, consoleId)), `CompletedConsoles` (set of byte, constructor and `ClearSabotage` fill it with {0, 1}), dirty. `IsActive` = `CompletedConsoles.Count < 2`. `UserCount` always 0. (**Observed (decompiled)** `.ctor @ 0x21ac004`, `ClearSabotage @ 0x21ac0f4`.)

`UpdateSystem @ 0x21ac4e8`, tag = `op & 0xF0`, id = `op & 0x0F`:
- 0x80: `codeResetTimer = -1`, `Countdown = 90`, clear completed, clear active. (No "already active" guard.)
- 0x10: `codeResetTimer = 10`, add id to `CompletedConsoles`.
- 0x40: add (callerId, id) to `ActiveConsoles`.
- 0x20: remove (callerId, id) from `ActiveConsoles`.
- Mark dirty.

`Deteriorate @ 0x21ac174`:
- Inactive: `wasActive = false` and hide UI.
- Active: on the first active tick set `codeResetTimer = 0` and `wasActive = true`. Update the helicopter sprite scale. Then `codeResetTimer -= dt`, `Countdown -= dt`. If `codeResetTimer <= 0`: `TargetCode = random int 0..99999`, `codeResetTimer = 10`, and clear `CompletedConsoles` (the half-entered code expires). Ensure the local player has the system task (id 58). `syncTimer -= dt`; when below 0, reset it to 1.0 and mark dirty.

So both consoles must be completed within one 10 s code window; the code changes every 10 s regardless. Note: when both are completed the system becomes inactive but `Countdown` is **not** reset to 10000 by the fix itself (the countdown just stops ticking). `ClearSabotage` (used by `AirshipStatus.RepairCriticalSabotages`) resets it.

Serialize: `float Countdown`, `float codeResetTimer`, packed N, N x (byte playerId, byte consoleId), packed M, M x byte completed id. `TargetCode` is not serialized (**Observed (decompiled)** `Serialize @ 0x21ac68c`). Deserialize mirrors it.

### 4.4 Comms

#### Skeld, Polus, Airship: `HudOverrideSystemType`
State: `IsActive` (bool), dirty. `UpdateSystem @ 0x224cae4`: `IsActive = (op >> 7) & 1`; dirty. So 0x80 starts comms and any byte without bit 7 (the fix task sends 0) ends it. `Deteriorate`: while active the local player gets the "fix comms" task (system id 14) if missing (UI). Serialize: one byte (0 or 1). Deserialize: read bool. No timer, no countdown, no game-over.

Constants: `DamageBit 0x80`, `TaskMask 0x7F` (dump), unused in the body.

#### Mira and Fungle: `HqHudSystemType`
Constants: `TagMask 0xF0`, `IdMask 0x0F`, `ActiveTime 10`.
State: `ActiveConsoles` (set of (playerId, consoleId)), `CompletedConsoles` (set of byte; constructor adds 0 and 1 so the system starts inactive), `Timer` (float), `TargetNumber` (int), dirty. `IsActive` = `CompletedConsoles.Count < 2`. `NumComplete` = completed count, `PercentActive` = `Timer / 10`.

`UpdateSystem @ 0x224c304`, tag = `op & 0xF0`, id = `op & 0x0F`:
- 0x80: `Timer = -1`, clear completed, clear active.
- 0x10: `Timer = 10`, add id to completed.
- 0x40: add (callerId, id) to active.
- 0x20: remove (callerId, id) from active.
- other tags only mark dirty. Mark dirty.

`Deteriorate @ 0x224c0a8` when active: `Timer -= dt`; if `Timer <= 0`: `TargetNumber = random 0..99999` (an integer range helper, upper bound inclusiveness **Unknown**), `Timer = 10`, clear completed (the partially entered code expires). Ensure the local player has the comms fix task (system id 14).

So, after the first tick of a sabotage the code window starts at 10 s and cycles; both consoles must be completed within one window. Serialize: packed N, N x (byte playerId, byte consoleId), packed M, M x byte. `Timer` and `TargetNumber` are **not** serialized (**Observed (decompiled)** `Serialize @ 0x224c500`), so in the original each client generates its own code locally. Our server must generate the code and send it. Helper queries: `IsConsoleActive(id)` = any active pair with that console; `IsConsoleOkay(id)` = completed contains id.

#### Comms effects (what depends on it)
- `RoleBehaviour.get_CommsSabotaged @ 0x2212300` is true when the local player owns the comms fix task; `IsAffectedByComms @ 0x2219f64` additionally requires the player's role team type is not impostor. So crew-side roles are affected, impostors are not (**Observed (decompiled)**).
- `EngineerRole.FixedUpdate @ 0x2212084`: if comms is sabotaged and the engineer is inside a vent, the engineer is forced out (same as running out of vent time), unless a meeting is open.
- The standard consequences (task list hidden, admin map/vitals/security disabled) are consumers outside this document's functions (**Inferred**, see `docs/03-gameplay-core.md`).

### 4.5 What happens at zero

`LogicGameFlowNormal.CheckEndCriteria @ 0x20de930` (**Observed (decompiled)**), run when game data exists:
1. Look up `Systems[8]` (oxygen). If its `Countdown < 0`: call `EndGameForSabotage` and reset that countdown to 10000.
2. Enumerate all `Systems.Values` that implement `ICriticalSabotage` (reactor on every map that has one, Polus lab reactor, heli). For each whose `Countdown < 0`: call `EndGameForSabotage`, then call that system's `ClearSabotage()`.
3. Then the normal alive-count checks run (crew win when no impostors remain, etc.).

`EndGameForSabotage @ 0x20de73c`: in a real match it sends the end-game message with reason byte 4, `GameOverReason.ImpostorsBySabotage`, and a boolean flag derived from a local data-manager setting. In the tutorial/hud-singleton case it only shows a "sabotage failed" message and freezes the local player instead. Crewmate vote/task wins use other reasons (0 and 1). Nothing else (kills, tasks) is consulted: reaching `Countdown < 0` is an immediate impostor win.

Order inside the game-flow tick therefore: oxygen first, then the other critical sabotages, then deaths/tasks.

### Implementation notes for our C# /sim
- Make one `CriticalSabotage` base: `Countdown`, `Active`, `Start()`, `Clear()`, `Tick(dt)`; subclasses define the fix condition (reactor: 2 simultaneous holders; O2: 2 distinct codes; heli and Mira comms: 2 distinct codes inside a 10 s sliding window).
- Server-authoritative: validate who may start (section 3), validate who may hold a console (alive, in range of that console), validate codes. The original trusts any add/complete opcode.
- At `Countdown <= 0` end the game with reason "impostors by sabotage" before any other win check; reset or discard the system afterwards.
- Reactor and O2 have no per-player "user count" beyond distinct consoles; do not let one player count as two.
- Heli leaves a stale countdown after being fixed; in our sim reset it to idle on fix.
- Dead players: the original does not remove dead players from reactor pairs. Decide: a holder who dies releases the console.

## 5. Meetings and active sabotages

- `ShipStatus.RepairCriticalSabotages @ 0x2250a38` (**Observed (decompiled)**): sends the clear opcode 0x10 for the Polus lab reactor (21), the reactor (3) and oxygen (8), each as the local player, then toggles a boolean on a UI singleton (presentation, **Unknown** which). It does not touch lights, comms, doors, mushroom, or the cooldown. Systems that are not on the map are skipped silently.
- `AirshipStatus.RepairCriticalSabotages @ 0x20d202c` (**Observed (decompiled)**): overrides it to call `ClearSabotage` on the helicopter: `Countdown = 10000`, `CompletedConsoles = {0, 1}`, dirty. (It does not clear reactor/oxygen, which Airship lacks.)
- `ShipStatus.OnMeetingCalled @ 0x2251f6c` (**Observed (decompiled)**): only destroys the spawned social-feed UI objects and clears that list. Nothing about sabotage.
- Where `RepairCriticalSabotages` is called from is **not in the decompiled set**. From the original game's structure it is called when a meeting starts or its results are applied (**Inferred**); the practical rule is that critical sabotages (reactor, O2, heli) are cleared by meetings, lights and comms are not, and the countdown stops during the meeting because the sabotage request itself is refused while the meeting hud exists. Door states and the sabotage cooldown after a meeting are likewise not visible; see Open questions.
- A sabotage start request is rejected while a meeting hud exists (section 3, step 3).

### Implementation notes
- At meeting start: call "clear critical sabotages" (reactor, O2, heli, Polus lab). Keep lights and comms as they are. Pause all countdowns while the meeting runs.
- After the meeting set the shared cooldown to the 10 s initial lockout (our design choice, consistent with `SetInitialSabotageCooldown`).

## 6. Lights: `SwitchSystem` and `ElectricTask`

Constants (dump): `NumSwitches 5`, `SwitchesMask 0x1F`, `DamageSystem 0x80`, `MaxValue 255`.
State: `Value` (byte, starts at 255 = full light), `ExpectedSwitches` and `ActualSwitches` (5-bit masks), `DetoriorationTime` (0.03), `timer`, dirty. The constructor draws one random 5-bit value (via a `System.Random` byte masked with 0x1F) and uses it for both Expected and Actual, so lights start fixed. `IsActive` = `Expected != Actual`. `Level` = `Value / 255`.

`UpdateSystem @ 0x241a488`: read `op`. If bit 0x80 is set (sabotage), the mask is `op & 0x1F`; otherwise it is a single bit `1 << (op & 0x1F)` (a player flipping switch index `op`, index 0..4). `Actual ^= mask`; dirty. So the sabotage flips a random subset of the switches (section 3) and repairing means flipping the exact same set back, in any order.

`Deteriorate @ 0x241a33c`:
1. `timer += dt`.
2. When `timer >= 0.03`: `timer = 0` (the remainder is dropped, not carried). If `Expected == Actual`: `Value = min(Value + 3, 255)`; else `Value = max(Value - 3, 0)`, and the local player gets the fix-lights task if it lacks one (UI).

Rate: 3 of 255 per step is 85 steps from full to dark or back. With a 0.03 s step that would be 2.55 s; because `timer` is reset instead of reduced and steps only happen on ticks, the effective step is the first tick boundary at or after 0.03 s (for a 0.02 s tick that is 0.04 s, so 3.4 s end to end). The exact tick length is **Unknown**.

`ElectricTask` (the fix task): `Initialize @ 0x2273a78` binds to `Systems[7]` and positions the task consoles; `FixedUpdate @ 0x2273d50`: if the task is not complete and `Expected == Actual`, complete it. So the task ends automatically once the switches match; no explicit "done" message.

Serialize: byte Expected, byte Actual, byte Value (**Observed (decompiled)** `Serialize @ 0x241a534`). Deserialize reads the same three bytes.

### 6.1 Vision radius: `ShipStatus.CalculateLightRadius @ 0x2253068`

Inputs: the player info, ship `MinLightRadius`/`MaxLightRadius` (1.0 / 5.0), options `CrewLightMod` (FloatOptionNames 4) and `ImpostorLightMod` (3), both default 1.0 per `docs/04-game-options.md`.

Steps in order (**Observed (decompiled)**):
1. If the player is null: return `MaxLightRadius`.
2. If the player is dead (`IsDead`): return `MaxLightRadius` (no option multiplier).
3. If the player's role is an impostor role (`RoleBehaviour.IsImpostor`): return `MaxLightRadius * ImpostorLightMod`. Lights have no effect on impostors.
4. Otherwise (crew): `level` = 1 if the map has no `Systems[7]`, else `Value / 255`, clamped to at most 1. Return `(Min + level * (Max - Min)) * CrewLightMod`.

Airship adds a step after the base calculation (`AirshipStatus.CalculateLightRadius @ 0x20d20ec`, Observed (decompiled)): if the player's role has `AffectedByLightAffectors`, then for each `LightAffector` zone the player's collider is touching, multiply the result by that zone's `Multiplier`. Roles with the flag false: Impostor, Shapeshifter, Phantom, Viper; all crew and ghost roles have it true (`roles.json`, Observed (dump)). The zone positions and multipliers are scene data not exported here (**Unknown**).

Skeld, Mira, Polus, Fungle use the base version unchanged (their overrides in the decompiled set are constructors only). Fungle has no lights system, so crew always see `Max * CrewLightMod` there (the Fungle flashlight modes are separate options, not in these functions).

### Implementation notes for our C# /sim
- Server only needs: Expected, Actual, a lights level 0..1, and the sabotage/flip commands. The step (3/255 per ~0.03-0.04 s) is presentation-adjacent: sending target state + start time is enough; but keep level rules server-side if vision is simulated server-side.
- Reproduce: sabotage mask always contains switch 2; repair = flip the same set; lights restore automatically when masks match.
- Vision: `radius = lerp(1, 5, level) * crewMod` for living crew; `5 * impostorMod` for impostors; `5` for ghosts.
- The fix-lights/fix-comms "system tasks" are UI entry points; you only need the commands.

## 7. Doors

### 7.1 Per-room door system: `DoorsSystemType` (Polus, Airship, Fungle)

Constants (dump): `CloseDoors 0x80`, `OpenDoor 0x40`, `ActionMask 0xC0`, `IdMask 0x1F`.
State: `timers` (SystemTypes room to float seconds of per-room cooldown), `initialCooldown` (float), dirty. `IsActive` is always false (**Observed (decompiled)** `get_IsActive @ 0x224965c`), so doors never pause the sabotage cooldown on these maps.

- `CloseDoorsOfType(room)` (@ 0x2249d50): `timers[room] = 30.0`; every door whose `Room` equals `room` gets `SetDoorway(false)` (closed); dirty. No cooldown check inside; the UI/caller must enforce `timers[room] == 0` and `initialCooldown <= 0`. Closing is requested through RPC 27, **not** through this system's `UpdateSystem` (opcode 0x80 is defined but not handled).
- `UpdateSystem @ 0x22497e0`: `door id = op & 0x1F`; if `(op & 0xC0) == 0x40` (open): find the door whose `Id` equals the id in `ShipStatus.AllDoors` and call `SetDoorway(true)`; a missing door logs a warning. Dirty. Open requests need no cooldown and no check of any kind in the system.
- `Deteriorate @ 0x2249678`: for every known system id that has a timer: `timers[id] = clamp(timers[id] - dt, 0, 30)`; then if `initialCooldown > 0`: `initialCooldown -= dt`.
- `SetInitialSabotageCooldown @ 0x2249ed8`: `initialCooldown = 10.0`.
- `GetTimer(room)` (UI): if `initialCooldown > 0` return `initialCooldown / 10`; else return `timers[room] / 30`, or 0 if no entry.
- Serialize: byte count of timers; per entry byte room + float seconds; then, for each door in `AllDoors` order, the door's own `Serialize` (a single open/closed flag for `PlainDoor`, **Inferred** from the `Open` field; `PlainDoor.Serialize` is not in the decompiled set). Deserialize mirrors it.

### 7.2 Skeld auto-doors: `AutoDoorsSystemType` + `AutoOpenDoor` (a `PlainDoor`)

- `AutoOpenDoor` constants (dump): `CooldownDuration 30`, `ClosedDuration 10`, fields `ClosedTimer`, `CooldownTimer`. Its `DoUpdate`/`SetDoorway` bodies are not decompiled, so the door-local behaviour is **Inferred**: closing sets `ClosedTimer = 10` and `CooldownTimer = 30`; the door reopens by itself when `ClosedTimer` runs out; `CooldownTimer` counts down until the room can be closed again.
- System state: `dirtyBits` (uint, one bit per door index), `initialCooldown` (float). `IsDirty` = `dirtyBits != 0`; `MarkClean` clears it.
- `Deteriorate @ 0x2248e88`: call each door's `DoUpdate(dt)`; if it returns true set that door's dirty bit; afterwards `initialCooldown -= dt` while it is positive.
- `CloseDoorsOfType(room)` (@ 0x22492e0): every door with that `Room` gets `SetDoorway(false)` and its dirty bit set.
- `SetDoor(door, open)` (@ 0x2249204): `SetDoorway(open)` and set its dirty bit.
- `UpdateSystem` is an empty function. Opening is never player-initiated on Skeld (doors reopen themselves).
- `GetTimer(room)` (@ 0x22493b4): `initialCooldown / 10` if positive, else the first door of the room's `CooldownTimer / 30`.
- `SetInitialSabotageCooldown`: `initialCooldown = 10.0`.
- `IsActive @ 0x2248d58`: any door in `AllDoors` satisfies a delegate named `<get_IsActive>b__3_0` whose body is not in the decompiled set. **Inferred**: "door is not open". If so, one closed door pauses the shared sabotage cooldown for its 10 s.
- Serialize: when `initialState` is false: packed uint `dirtyBits`, then each flagged door's `Serialize`; when true: every door in order. Deserialize reads the same (packed uint then the flagged doors, or all doors).

### 7.3 Door consoles (Polus/Airship/Fungle)
- `DoorConsole` (closed-door unlock station with a minigame): usable only if the player is alive, the door is closed, the role allows using this console, and the player's true position is within 1.0 world unit of the console (`CanUse @ 0x2248494`). `Use` halts the player's movement transform and opens the `MinigamePrefab` bound to the door. The minigame, when finished, sends the open command (`Doors`, `id | 0x40`).
- `OpenDoorConsole` (direct open panel): usable if alive, the door is closed, and distance <= its `usableDisance` field (`CanUse @ 0x224e178`). `Use @ 0x224e408` sends `RpcUpdateSystem(16, doorId | 0x40)` and locally opens the door.
- `PlainDoor.SetDoorway @ 0x2248978`: sets the `Open` flag (no-op if unchanged), switches the collider to a trigger when open (players can pass) and solid when closed, updates the shadow collider, plays open/close animation and sound; on close also camera shake (presentation). `DoUpdate` returns false (nothing). Door ids are limited to 5 bits (0..31).

### 7.4 Airship electrical door maze: `ElectricalDoors` (system key 18)
- `Initialize @ 0x21abab8` (host only, on `AirshipStatus.OnEnable`): close all doors in `Doors`, then build a random spanning tree over the `Rooms` door sets: keep a visited set, start from the first room; repeat (capped at 10000 iterations) until all rooms are visited: take a random door of the current room, find the room on its other side; if that room is unvisited, mark it visited and open that door; then with probability 0.5 move the cursor to that other room. After that, `LeftExits` holds two doors: a random boolean decides which of the two is opened and the other is closed. Mark dirty.
- `Deteriorate` and `UpdateSystem` do nothing; the serialized form is a 32-bit little-endian bitmask, bit i = `Doors[i]` is open. Deserialize applies it. (Players toggling these doors is not part of this class; **Unknown**.)

### Implementation notes for our C# /sim
- Two door styles: (a) command-based close with a 30 s per-room cooldown and player-opened doors (unlock minigame), (b) self-reopening 10 s doors with a 30 s cooldown. Implement both as `DoorSystem` strategies.
- Enforce the cooldown on the server: the original `CloseDoorsOfType` itself does not check it.
- Validate open requests (alive, near the door, door closed); the original accepts any id.
- Doors do not block the sabotage cooldown on the command-based style.

## 8. Fungle mushroom mix-up: `MushroomMixupSabotageSystem`

Not a countdown sabotage: a timed cosmetic scramble.

Constants (dump): `SYNC_RATE 2`, operation enum `None 0`, `TriggerSabotage 1`; state enum `Inactive 0`, `JustTriggered 1`, `IdleButMixedUp 2`. Asset tuning (`ship_tuning.json`): `secondsForAutoHeal 10.0`, empty chances `hat 0.1`, `visor 0.25`, `skin 0.1`, `pet 0.5`, 19 hat ids, 5 visor ids, 5 pet ids; skin ids come from the players' current skins at `Start`.

State: `currentState`, `currentSecondsUntilHeal` (float), `syncTimer`, per-player `currentMixups` (playerId to condensed outfit: hat, visor, skin, pet indices, and a "color player id"), outfit caches, dirty. `IsActive` = `currentSecondsUntilHeal > 0`.

`UpdateSystem @ 0x242b944`: read opcode byte; if it equals 1: (host-side generation) for every living player generate a random outfit (`GenerateRandomOutfit @ 0x242c478`: for each of hat, visor, skin, pet draw a uniform random in [0,1); if it is below that slot's empty chance the index is 255 (none), otherwise a uniformly random index from the list; the "color player id" is a random other player id, never the player's own), apply it locally (screen tint, vibration, sound, and the system task id 57 for the local living player), set `currentState = JustTriggered`, `currentSecondsUntilHeal = 10`, dirty. Any other opcode only logs an error.

`Deteriorate @ 0x242aff0`: first, players in the mix-up map that are dead or disconnected are moved out of it (their outfit restored/cached); then, while `currentSecondsUntilHeal > 0`: subtract dt, accumulate `syncTimer`, and when it exceeds 2.0 reset and mark dirty. When it reaches 0 or below: run the heal animation for every mixed-up player, play the end effects (not during a meeting), and set the state to 0. There is no repair action; it only heals itself after 10 s (**Observed (decompiled)** and dump).

Serialize (`@ 0x242c964`): byte state; float `currentSecondsUntilHeal`; packed count of mixups; per mixup: byte playerId then the 5-byte condensed outfit (hat, visor, skin, pet, color player id; field order from the struct, the struct's own serializer body is not decompiled, **Inferred**). After writing, a `JustTriggered` state becomes `IdleButMixedUp` (so the "trigger" is only sent once). Deserialize: read state, seconds, count (as a byte), outfits; if the new state is `JustTriggered` and the old one was not, play the mix-up locally and set the state to `IdleButMixedUp`.

### Implementation notes
- Server: store `healTimer`, per-player scramble map; apply to rendering only. Gameplay effect is that players cannot trust names/colors/cosmetics for 10 s. Keep names unscrambled only if you want less confusion; the original also swaps the color/name source.

## 9. Utility systems

### 9.1 `VentilationSystem` (key 37)
State: `SeqBuffers` (per player sequence buffers of pending ops), `PlayersCleaningVents` (playerId to ventId), `PlayersInsideVents` (playerId to ventId), `ToRemove`, dirty.
Message (`UpdateSystem @ 0x2287238`): ushort `opId` (per-player monotonically increasing sequence, starts at 0), byte `op`, byte `ventId`. Ops (enum): 0 StartCleaning, 1 StopCleaning, 2 Enter, 3 Exit, 4 Move, 5 BootImpostors. The system buffers per player: an op already processed is dropped, the next expected `opId` is applied immediately, and any later ones are kept until the gap fills; then every ready op is performed (**Inferred** from the buffer calls; the buffer class is not decompiled).
`PerformVentOp` (@ 0x2287954): StartCleaning boots impostors inside that vent first, then records (player, vent) as cleaning; StopCleaning removes every cleaner of that vent; Enter and Move record (player, vent) as inside; Exit removes the player from inside; BootImpostors boots impostors inside the vent. Always dirty and refreshes vent arrow UI.
`BootImpostorsFromVent` (@ 0x2287b18): for each player inside that vent, except an impostor with an active invisible Phantom state, remove them and send a boot-from-vent command to that player.
`Deteriorate @ 0x2286898`: any cleaning player whose data is missing or marked disconnected is removed from both maps; dirty only if something was removed.
Serialize: packed N, N x (byte player, byte vent) for cleaning; packed M, M x (byte player, byte vent) for inside. Deserialize mirrors it (sequence buffers are not serialized).
Implementation note: vents are impostor/engineer hiding; cleaning (by crew) kicks the occupants out. Keep the two maps; sequence numbers are only needed on an unreliable transport (skip them over a reliable stream).

### 9.2 `SecurityCameraSystemType` (key 11)
State: `PlayersUsing` (set of playerId), dirty. `UpdateSystem @ 0x22270e4`: opcode 1 adds the caller, any other value removes the caller; dirty; each `SurvCamera` is told whether any user exists (blink the red light). `InUse` = set not empty. `Deteriorate`: any user whose player info is missing or disconnected is removed; refreshes camera lights and dirty if any removed. Serialize: packed N, N x byte. Constants: `IncrementOp 1`, `DecrementOp 2`. No effect on gameplay beyond the visible light.

### 9.3 `MedScanSystem` (key 10)
Constants: `Request 0x80`, `Release 0x40`, `NumMask 0x1F`, `NoPlayer 255`. State: `UsersList` (queue of playerIds), `CurrentUser` (byte, 255 = none), dirty.
`UpdateSystem @ 0x21a40d0`: `playerId = op & 0x1F`. If bit 0x80 (request): append the id to the queue if not already queued; else if bit 0x40 (release): remove the id from the queue; always dirty. `Deteriorate @ 0x21a3edc`: `CurrentUser` follows the head of the queue (255 when empty); mark dirty when it changes. Serialize: packed N, N x byte (the queue); `CurrentUser` is derived on each side.
Note the 5-bit mask caps the id at 31.

### 9.4 `ElectricTask`
Presentation/task logic only; see section 6.

### Implementation notes
- Medscan is a FIFO lock: one scanning player at a time.
- Cameras/vents/medscan do not influence sabotage logic.

## 10. Serialization summary (field order)

All numeric widths little endian; "packed" is the Hazel variable-length integer.

| System | Layout |
|---|---|
| Reactor | float Countdown; packed N; N x (u8 playerId, u8 consoleId) |
| Oxygen | float Countdown; packed N; N x packed consoleId (list optional on read) |
| Heli | float Countdown; float codeResetTimer; packed N; N x (u8 playerId, u8 consoleId); packed M; M x u8 |
| Lights | u8 Expected; u8 Actual; u8 Value |
| HudOverride comms | u8 bool |
| HqHud comms | packed N; N x (u8 playerId, u8 consoleId); packed M; M x u8 |
| Sabotage cooldown | float Timer |
| Doors (per-room) | u8 K; K x (u8 room, float timer); then per door its own state |
| AutoDoors | delta: packed uint dirtyBits + flagged doors; initial: all doors |
| ElectricalDoors | u32 open bitmask |
| Mushroom | u8 state; float healTimer; packed N; N x (u8 playerId, 5 x u8 outfit) |
| Ventilation | packed N cleaning pairs; packed M inside pairs (each pair u8, u8) |
| Security cameras | packed N; N x u8 |
| MedScan | packed N; N x u8 |

Initial vs delta state: only `AutoDoorsSystemType` distinguishes it. The overall `ShipStatus.Serialize`/`Deserialize` (the envelope that carries these per system) is not in the decompiled set.

## Open questions

- **Unknown:** the callers of `ShipStatus.RepairCriticalSabotages` and `SetInitialSabotageCooldown` (meeting start/end, game start). The rules in sections 3 and 5 are inferred from their effect only.
- **Unknown:** `Time.fixedDeltaTime` of the project; it fixes the exact lights fade time (2.55 s to 3.4 s) and the granularity of countdown syncs.
- **Unknown:** `RpcUpdateSystem` sender logic and whether the host-only gate sits before `Systems[id].UpdateSystem` for RPC 35.
- **Unknown:** body of `AutoOpenDoor.DoUpdate`/`SetDoorway`, the `AutoDoorsSystemType.IsActive` predicate, `PlainDoor.Serialize`, `CondensedOutfit` serializer, the vent sequence buffer class. All flagged Inferred above.
- **Unknown:** whether `IntRange.Next(0, 99999)` is inclusive of 99999.
- **Unknown:** Airship `LightAffector` zones' positions and multipliers (scene data).
- **Unknown:** what the UI singleton flag cleared by `RepairCriticalSabotages` controls.
- **Inferred, unverified:** the meeting-hud singleton identity in `SabotageSystemType.UpdateSystem` (based on usage by other callers).
