# 06 - Tasks, Minigames and Maps

> **Data update:** numeric values the code dump lacks (player speed 2.5, kill cooldown default 15, role cooldowns/durations, task MaxStep, map scale, exile timings) were later recovered from serialized assets: see [data/gameplay](data/gameplay/README.md) and [data/maps](data/maps/README.md) (Observed vent graphs with links, consoles with task types). Still unknown after that pass: reactor/O2 countdowns, door cooldowns, kill-distance floats, option presets.

Scope: task data model, task catalogue, sabotages, minigame catalogue, maps and map mechanics, vents, consoles, surveillance.
Related docs: roles in `05-roles.md`; network framing / RPC wire format in `02-*`; round flow, meetings, win conditions in `03-*`; game option screens in `04-*`; client UI in `07-*`.

## 0. Conventions and evidence

| Tag | Meaning |
|---|---|
| Observed | Read from the Android 2026.9.29 Cpp2IL dump (types, fields, enum values, signatures; no method bodies) |
| Reference | From Impostor community server data (`Impostor:src/Impostor.Api/Innersloth/Data/...`), may be older |
| Inferred | Deduced from names/fields. Not confirmed. All minigame descriptions are Inferred unless stated. |

`Assembly-CSharp/` in citations = `DiffableCs/Assembly-CSharp/`. Map geometry, tuned timers and prefab-serialized values are not in the dump. The goal of this doc is a vocabulary and catalogue; designers should reinvent concrete rooms, art and puzzles (original game, not a copy).

---

## 1. Task model

### 1.1 Class hierarchy (Observed)

```
PlayerTask (abstract MonoBehaviour)                       Assembly-CSharp/PlayerTask.cs
  NormalPlayerTask                                        NormalPlayerTask.cs
    UploadDataTask, AirshipUploadTask, DivertPowerTask,
    TowelTask, WaterWayTask, WeatherNodeTask              (specialised multi-console tasks)
  SabotageTask (abstract)                                 SabotageTask.cs
    ReactorTask, NoOxyTask, ElectricTask (lights),
    HudOverrideTask, HqHudOverrideTask, HeliCharlesTask
  ImportantTextTask                                       (HUD text line, e.g. status lines)
  MushroomMixupSabotageTask                               (sabotage but derives PlayerTask directly)
```
`ElectricTask.cs` declares class `ElectricTask : SabotageTask` (lights). Interface `IHudOverrideTask` is implemented by both comms tasks.

### 1.2 PlayerTask fields (Observed)
- `StartAt: SystemTypes` (room where the task header says it starts), `TaskType: TaskTypes`, `MinigamePrefab: Minigame`, `HasLocation`, `LocationDirty`.
- `Id: uint` (task instance id, internal set), `Index: int`, `Owner: PlayerControl`, `Locations: List<Vector2>` (arrow/map targets).
- Abstracts: `IsComplete`, `TaskStep`, `AppendTaskText(StringBuilder)`, `Complete()`, `Initialize()`, `ValidConsole(Console)`.
- Helpers: `FindConsoles()`, `FindConsolesPos()`, `FindValidConsolesPositions()`, `GetOrCreateTask<T>`, `PlayerHasTaskOfType<T>`, `TryGetTaskOfType<T>`, `DestroyTasksOfType<T>`, `TaskIsEmergency(task)` (sabotage tasks are "emergency", Inferred).

### 1.3 NormalPlayerTask: the step model (Observed)
`Assembly-CSharp/NormalPlayerTask.cs`

| Field | Meaning |
|---|---|
| Length (TaskLength) | None=0, Common=1, Short=2, Long=3 (internal enum) |
| taskStep / MaxStep | Current stage index and total stages. `NextStep()` advances. Complete when `taskStep >= MaxStep` (Inferred) |
| ShowTaskStep | Show "(1/3)" in the task text |
| ShowTaskTimer, TimerStarted (TimerState NotStarted=0, Started=1, Finished=2), TaskTimer | Tasks with a wait phase (e.g. inspect-sample style) |
| Data: byte[] | Per-instance random payload (e.g. which console ids / which target), serialized to peers (Inferred) |
| Arrow: ArrowBehaviour, arrowSuspended | Direction arrow to next console (suspended while minigame open or timer running) |
| useMultipleText, textStage1/2, maxNumStepsStage1 | Two-phase text (e.g. "Fuel engines: take fuel" then "insert fuel") |

Console binding (Observed `Console.cs`, `TaskSet.cs`): each map `Console` object has `ConsoleId`, `Room (SystemTypes)`, `TaskTypes[]`, `ValidTasks: TaskSet[]` where `TaskSet = { TaskTypes taskType; IntRange taskStep }`. `NormalPlayerTask.ValidConsole(console)` therefore checks "this console serves my type and my current step". `PickRandomConsoles(taskType, consoleIds)` and `PickRandomConsoles(taskStep, taskType)` randomize which of several equivalent consoles a multi-step task uses. This is how fuel/upload/divert-power tasks get different physical locations per assignment.

Console flags (Observed): `usableDistance`, `onlyFromBelow`, `onlySameRoom`, `checkWalls` (line-of-sight to wall), `GhostsIgnored`, `AllowImpostor` (impostors may use for fake tasks).

### 1.4 Network and counting
- Task assignment (Observed): `NetworkedPlayerInfo.Tasks: List<TaskInfo>` where `TaskInfo = { byte typeId; uint id; bool complete }` (Inferred `complete` field; ctor `TaskInfo(byte typeId, uint id)` Observed). Host sends `NetworkedPlayerInfo.RpcSetTasks(byte[] taskTypeIds)`; RPC `SetTasks`=29. `typeId` is an index into the ship's task arrays: `ShipStatus.GetTaskById(byte idx)` (Observed).
- Completion (Observed): `PlayerControl.RpcCompleteTask(uint idx)` -> RPC `CompleteTask`=1 -> `GameData.CompleteTask(PlayerControl pc, uint taskId)`. `GameData.TotalTasks` and `GameData.CompletedTasks` (ints) are the global bar numerators, recomputed by `GameData.RecomputeTaskCounts()` (e.g. after leave/death/role change).
- What counts (Inferred): sum over players whose role has `TasksCountTowardProgress` true (see `05-roles.md`), not counting disconnected players. Impostors get fake task lists (`AdjustTasks`/`SpawnTaskHeader`) that do not count. Dead crew count only if `GhostsDoTasks`.
- End check: `GameManager.CheckTaskCompletion()`, `CheckEndGameViaTasks()`, `FinishTask(PlayerTask)` and `LogicGameFlowNormal.CheckEndCriteria` (Observed names, bodies absent). Flow detail in `03-*`.
- Per-player counts (Observed `NormalGameOptionsV12`): `NumCommonTasks`, `NumShortTasks`, `NumLongTasks`; `TaskBarMode` (below); `VisualTasks` (bool; show animated visual tasks); `GhostsDoTasks`.
- Impostor reference wraps this as `TaskData { Id, Type, Category(CommonTask/LongTask/ShortTask), IsVisual }` and `ITaskInfo.CompleteAsync()` (`Impostor:src/Impostor.Api/Innersloth/Maps/TaskData.cs`, `TaskCategories.cs`) - confirms Id = index into the map's task list. (Reference)

### 1.5 Task selection (Observed signatures, Inferred algorithm)
`ShipStatus` (`ShipStatus.cs`) holds `CommonTasks[]`, `LongTasks[]`, `ShortTasks[]`, `SpecialTasks[]` (sabotage tasks), `AddTasksFromList(ref start, count, List<byte> tasks, HashSet<TaskTypes> usedTaskTypes, List<NormalPlayerTask> unusedTasks)`, `AssignTaskIndexes`, `ValidateAllTasksOnShip`, `HasTaskType`, `HasTaskTypes`. Inferred algorithm: host picks `NumCommonTasks` from the common array (same for everyone), then `NumShortTasks` and `NumLongTasks` per player at random; `usedTaskTypes` prevents the same *type* appearing twice for a player; if the pool is too small, tasks are reused from `unusedTasks`. Map task lists in section 4 show multiple prefab entries with the same `TaskType` (e.g. 5 UploadData), which are per-variant entries.

### 1.6 TaskBarMode (Observed `Amongus/GameOptions/TaskBarMode.cs`)
Normal=0 (bar updates live), MeetingOnly=1 (updates only when a meeting starts), Invisible=2 (no bar).

### 1.7 UI related (Observed names, details in `07-*`)
`TaskPanelBehaviour` (task list), `MapTaskOverlay` (task markers on the map), `ArrowBehaviour`, `TaskAdderGame` + `TaskFolder`/`TaskAddButton`/`TaskSet` (freeplay/dev tool that lets the player pick any task; `TaskFolder.TaskChildren` and `RoleChildren` list tasks and roles), `ImportantTextTask`.

---

## 2. TaskTypes enum (Observed, `Assembly-CSharp/TaskTypes.cs`)

| # | Name | # | Name | # | Name |
|---|---|---|---|---|---|
| 0 | SubmitScan | 28 | RunDiagnostics | 56 | DevelopPhotos |
| 1 | PrimeShields | 29 | WaterPlants | 57 | GetBiggolSword |
| 2 | FuelEngines | 30 | MonitorOxygen | 58 | PutAwayRifles |
| 3 | ChartCourse | 31 | StoreArtifacts | 59 | StopCharles |
| 4 | StartReactor | 32 | FillCanisters | 60 | VentCleaning |
| 5 | SwipeCard | 33 | FixWeatherNode | 61 | None |
| 6 | ClearAsteroids | 34 | InsertKeys | 62 | BuildSandcastle |
| 7 | UploadData | 35 | ResetSeismic | 63 | CatchFish |
| 8 | InspectSample | 36 | ScanBoardingPass | 64 | CollectShells |
| 9 | EmptyChute | 37 | OpenWaterways | 65 | LiftWeights |
| 10 | EmptyGarbage | 38 | ReplaceWaterJug | 66 | RoastMarshmallow |
| 11 | AlignEngineOutput | 39 | RepairDrill | 67 | TestFrisbee |
| 12 | FixWiring | 40 | AlignTelescope | 68 | CollectSamples |
| 13 | CalibrateDistributor | 41 | RecordTemperature | 69 | CollectVegetables |
| 14 | DivertPower | 42 | RebootWifi | 70 | HoistSupplies |
| 15 | UnlockManifolds | 43 | PolishRuby | 71 | MineOres |
| 16 | ResetReactor | 44 | ResetBreakers | 72 | PolishGem |
| 17 | FixLights | 45 | Decontaminate | 73 | ReplaceParts |
| 18 | CleanO2Filter | 46 | MakeBurger | 74 | HelpCritter |
| 19 | FixComms | 47 | UnlockSafe | 75 | CrankGenerator |
| 20 | RestoreOxy | 48 | SortRecords | 76 | FixAntenna |
| 21 | StabilizeSteering | 49 | PutAwayPistols | 77 | TuneRadio |
| 22 | AssembleArtifact | 50 | FixShower | 78 | MushroomMixupSabotage |
| 23 | SortSamples | 51 | CleanToilet | 79 | ExtractFuel |
| 24 | MeasureWeather | 52 | DressMannequin | 80 | MonitorMushroom |
| 25 | EnterIdCode | 53 | PickUpTowels | 81 | PlayVideogame |
| 26 | BuyBeverage | 54 | RewindTapes | | |
| 27 | ProcessData | 55 | StartFans | | |

Sabotage-flavored types: ResetReactor(16), FixLights(17), FixComms(19), RestoreOxy(20), ResetSeismic(35), StopCharles(59), MushroomMixupSabotage(78). `GetBiggolSword`(57) and `None`(61) are not used by any Reference map (a joke/placeholder task, Inferred). `ChartCourse` etc. share the type id across maps; the same TaskType can have different minigames per map (e.g. EmptyGarbage uses `EmptyGarbageMinigame` on Skeld, `AirshipGarbageGame` on Airship, Inferred).

---

## 3. Sabotage system

### 3.1 Sabotage types and the SabotageSystemType
`SabotageSystemType` (Observed, `SabotageSystemType.cs`): constructed with `IActivatable[] specials`; `SpecialSabDelay = 30` (seconds, shared cooldown between special sabotages, Observed const), `SetInitialSabotageCooldown`, `UpdateSystem`, `Deteriorate`. The impostor sabotage menu sends `UpdateSystem(SystemTypes.Sabotage, ...)` with a target system id (RPC `UpdateSystem`=35). `IActivatable` marks systems that are "currently sabotaged" (Observed on Reactor, LifeSupp, Switch, Doors, Hud, Heli, MushroomMixup).

| Sabotage | System class (Observed) | Critical? | Fix mechanic (Inferred) | Task class |
|---|---|---|---|---|
| Reactor (Skeld, Mira) / Seismic (Polus: Laboratory) | `ReactorSystemType` (consts `StartCountdown=128`, `AddUserOp=64`, `RemoveUserOp=32`, `ClearCountdown=16`, `ConsoleIdMask=3`, `RequiredUserCount=2`, `CountdownStopped=10000`, `ReactorDuration`) | Yes (`ICriticalSabotage`) | Two players must simultaneously hold two separate consoles before the countdown ends | `ReactorTask` + `ReactorMinigame` |
| Oxygen (Skeld, Mira) | `LifeSuppSystemType` (consts similar, `LifeSuppDuration`, `Countdown`, `CompletedConsoles`) | Yes | Enter a code at both consoles (two-console) | `NoOxyTask` + `KeypadGame` |
| Lights (all except Fungle, Reference) | `SwitchSystem` (`NumSwitches=5`, `DamageSystem=128`, `SwitchesMask=31`, `ExpectedSwitches`, `ActualSwitches`, `DetoriorationTime`, `Value`) | No | Flip 5 switches to match the expected pattern. Crew vision radius shrinks while broken | `ElectricTask` + `SwitchMinigame` |
| Comms (Skeld, Polus, Airship: `HudOverrideSystemType`; Mira: `HqHudSystemType`) | `DamageBit=128`, `TaskMask=127` | No | Skeld/Polus/Airship: single console fix. Mira: two code-entry consoles (`HqHudSystemType` + `AuthGame`) | `HudOverrideTask`, `HqHudOverrideTask` |
| Doors (Skeld: `AutoDoorsSystemType`; Polus, Airship, Fungle: `DoorsSystemType`) | `CloseDoors=128`, `OpenDoor=64`, `IdMask=31`; per-room timers | No | Doors close by room; reopen by timer; on non-Skeld maps players can open them with a door minigame (`IDoorMinigame`) | none (`OpenableDoor`) |
| Heli / "Stop Charles" (Airship) | `HeliSabotageSystem` (`CharlesDuration=90`, `CodeActiveDuration=10`, tags `DamageBit=128`, `ActiveBit=64`, `DeactiveBit=32`, `FixBit=16`) | Yes | Two consoles each show/enter a code within an active window; both must be completed | `HeliCharlesTask` + `AirshipAuthGame` |
| Mushroom Mixup (Fungle) | `MushroomMixupSabotageSystem` (Operation None=0/TriggerSabotage=1; State Inactive=0/JustTriggered=1/IdleButMixedUp=2; `secondsForAutoHeal`) | No | Players' outfits are scrambled (cosmetic disguise); auto-heals after a time (`secondsForAutoHeal`) | `MushroomMixupSabotageTask` |

Notes:
- `ShipStatus.GetSabotageTask(SystemTypes)`, `RepairCriticalSabotages()` (called on meeting; critical sabotages are cleared when a meeting starts, Inferred) and `OnMeetingCalled` (Observed).
- Sabotage UI: `MapRoom` has `SabotageComms/Doors/Heli/Lights/MushroomMixup/Oxygen/Reactor/Seismic` handlers (Observed `MapRoom.cs`), i.e. the map screen doubles as sabotage menu; `InfectedOverlay` on `MapBehaviour`.
- Which sabotages per map (Reference, from `Data/maps/*/systems.json`): Skeld = Electrical(lights), Comms, Reactor, LifeSupp, Doors(auto). Mira = Electrical, Comms (HQ), Reactor, LifeSupp, no doors. Polus = Electrical, Comms, Doors, Laboratory seismic (ReactorSystemType). Airship = Electrical, Comms, Doors, HeliSabotage, plus gap-room platform and decon door sets. Fungle = Comms (HQ), Reactor, Doors, MushroomMixup. Note Fungle in the reference shows no Electrical system (Reference).

### 3.2 SabotageTask base (Observed)
`SabotageTask`: `didContribute`, `Arrows: ArrowBehaviour[]`, `MarkContributed()`, `SetupArrows()`. Sabotage tasks are added to every non-impostor (and ghost) player's task list via `PlayerControl.AddSystemTask(SystemTypes)` (Observed).

### 3.3 Meetings and vents/interaction
Vent-related and meeting-related rules are in section 5 and `03-*`.

---

## 4. Maps

### 4.1 MapNames (Observed `Assembly-CSharp/MapNames.cs`; same in Reference `Impostor:.../MapTypes.cs`)

| Id | Name | Prefab class | Notes |
|---|---|---|---|
| 0 | Skeld | `SkeldShipStatus` | Empty subclass of ShipStatus (Observed: no members) |
| 1 | MiraHQ | `MiraShipStatus` | Empty subclass |
| 2 | Polus | `PolusShipStatus` | Overrides `OnEnable`, `SpawnPlayer` |
| 3 | Dleks | (no separate class in dump) | Skeld mirrored/flipped (Inferred from name; Impostor ships `IInnerDleksShipStatus` and spawn-object id 7 = SkeldShipStatus, Reference) |
| 4 | Airship | `AirshipStatus` | Many overrides, see 4.4 |
| 5 | Fungle | `FungleShipStatus` | Spores, zipline, mixup |

The "April" folder in Reference is a Skeld clone with different door/vent wiring (Reference; April Fools mode, Inferred). Reference `SpawnableObjects.json`: 0 SkeldShipStatus, 5 Mira, 6 Polus, 7 Skeld(Dleks), 8 Airship, 13 Fungle (`Impostor:src/Impostor.Api/Innersloth/Data/SpawnableObjects.json`). `ShipStatus.MapType` internal enum Ship=0, Hq=1, Pb=2, Fungle=3 (Observed).

### 4.2 ShipStatus base class (Observed, `ShipStatus.cs`)
Networked object (`InnerNetObject`), singleton `ShipStatus.Instance`.
- Lighting: `MaxLightRadius`, `MinLightRadius`, `CalculateLightRadius(NetworkedPlayerInfo)` (virtual; interpolates with lights switch state, impostors unaffected, Inferred).
- Meeting: `EmergencyButton: SystemConsole`, `EmergencyCooldown`, `BreakEmergencyButton()` (used when comms/etc? Inferred), `MeetingBackground`, overlays (`EmergencyOverlay`, `ReportOverlay`), `StartMeeting(reporter, target)` / `CoStartMeeting`.
- Spawn: `InitialSpawnCenter`, `MeetingSpawnCenter`, `MeetingSpawnCenter2`, `SpawnRadius`, `SpawnPlayer(player, numPlayers, initialSpawn)`. Players are placed on a circle around the spawn center (Inferred from `SpawnRadius`).
- Content arrays: `AllRooms: PlainShipRoom[]`, `FastRooms: Dictionary<SystemTypes,PlainShipRoom>`, `AllVents`, `AllDoors: OpenableDoor[]`, `AllConsoles: Console[]`, `AllCameras: SurvCamera[]`, `Ladders`, `AllStepWatchers` (footstep sound surface), `DummyLocations`.
- Systems: `Systems: Dictionary<SystemTypes, ISystemType>`; `UpdateSystem(SystemTypes, PlayerControl, MessageReader|byte)`, `RpcUpdateSystem`, `RpcCloseDoorsOfType(SystemTypes)`, `CloseDoorsOfType`. `ISystemType` contract (Observed): `Deteriorate(float dt)` (timers), `UpdateSystem(PlayerControl, MessageReader)`, `Serialize/Deserialize(…, initialState)`, `IsDirty`/`MarkClean`. This is a clean generic pattern for a clone: a per-map dictionary of server-authoritative state machines driven by small opcode messages.
- Misc: `MedScanner` and `numScans`, `WeaponFires` (asteroid gun fx), `Hatch` (Airship?/Mira? `OpenHatch`), `ShieldsImages` and `StartShields()`, `socialMediumFeedSystem` (a social-media feed prop, `GetSocialMediumSpriteList()`; Mira? unknown), `HideCountdown` (hide-and-seek), `CosmeticsCache`.
- Deserialization: initial state + dirty updates (`Serialize(writer, initialState)`), see `02-*`.

### 4.3 SystemTypes enum = rooms and pseudo-rooms (Observed, `SystemTypes.cs`)

| # | Name | # | Name | # | Name |
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

Room-to-map assignment (Reference, from the console room names in `Data/maps/*/tasks.json`, `vents.json`, `doors.json`):
- Skeld: Cafeteria, Storage, UpperEngine, LowerEngine, Reactor, Security, MedBay, Electrical, Admin, Weapons, Nav, Shields, LifeSupp, Comms, Hallway.
- Mira: Launchpad, Balcony, Hallway, Reactor, Laboratory, LockerRoom, Admin, Office, Greenhouse, MedBay, Decontamination, Cafeteria, Storage.
- Polus: Dropship, Office, Electrical, LifeSupp, Specimens, BoilerRoom, Laboratory, Storage, Comms, Weapons, Hallway, Outside, Decontamination, MedBay.
- Airship: Cockpit, Engine, VaultRoom, Kitchen, ViewingDeck, GapRoom, MainHall, Showers, Records, CargoBay, Lounge, MeetingRoom, Security, Electrical, Brig, Medical, Comms, Armory(not in refs), HallOfPortraits(not in refs).
- Fungle: Dropship, Kitchen, Laboratory, Lookout, MiningPit, Reactor, Storage, Comms, RecRoom, Beach, Jungle, Greenhouse, FishingDock, SleepingQuarters, Highlands(unreferenced).
Some names (ShipTasks, Doors, Sabotage, Ventilation, MushroomMixupSabotage, HeliSabotage, Zipline, GapRoom, Decontamination, Decontamination2/3) double as *system keys* in `ShipStatus.Systems`, not just rooms.

### 4.4 Per-map class summary

| Map | Class | Specific members (Observed) | System types per map (Reference `systems.json`) |
|---|---|---|---|
| Skeld | `SkeldShipStatus` | none | Switch(Electrical), MedScan, AutoDoors, HudOverride(Comms), SecurityCamera, Reactor, LifeSupp, Ventilation, Sabotage |
| Mira | `MiraShipStatus` | none | Switch, MedScan, HqHud(Comms), Reactor, LifeSupp, Ventilation, Sabotage; no doors, no cameras |
| Polus | `PolusShipStatus` | overrides `OnEnable`, `SpawnPlayer` | Switch, MedScan, Doors, HudOverride, SecurityCamera, Ventilation, Laboratory=ReactorSystemType (seismic), Sabotage |
| Airship | `AirshipStatus` | `SpawnInGame: SpawnInMinigame`, `GapPlatform: MovingPlatformBehaviour`, `ShowerParticles`, `LightAffectors: LightAffector[]`, `outOfOrderPlat`; overrides `CalculateLightRadius`, `OnEnable`, `OnMeetingCalled`, `PrespawnStep` (coroutine), `RepairCriticalSabotages`, `SpawnPlayer` | Switch, MedScan, Doors, HudOverride, GapRoom=MovingPlatform, HeliSabotage, Decontamination=ElectricalDoors, Decontamination2=AutoDoors, SecurityCamera, Ventilation, Sabotage |
| Fungle | `FungleShipStatus` | `sporeMushrooms: Dictionary<int,Mushroom>`, `specialSabotage: MushroomMixupSabotageSystem`, `LastBinocularPos`, `Zipline: ZiplineBehaviour`, waves assets; `GetMushroomFromId`, `IsMushroomMixupActive`, `TriggerSporesFromMushroom(id)`, `OnMeetingCalled`, `StartSFX` | Ventilation, HqHud(Comms), Reactor, Doors, MushroomMixupSabotage, Sabotage |

Surprise in the Reference data: Skeld/Mira/Polus/Airship/Fungle all include `Ventilation` (so vents are a system on all maps, Reference).

### 4.5 Map-specific mechanics

#### Ladders (Airship; Observed)
`Ladder : IUsableCoolDown, IUsable` with `Id (byte)`, `IsTop`, `Destination: Ladder`. Using a ladder moves the player to its paired ladder (animated). RPC `ClimbLadder`=31 (Observed `RpcCalls`). `ShipStatus.Ladders[]`. `SetDestinationCooldown` stops immediate re-use.

#### Moving platform / gap room (Airship; Observed)
`MovingPlatformBehaviour : ISystemType` with `LeftPosition`, `RightPosition`, `DisabledPosition`, `LeftUsePosition`, `RightUsePosition`, `IsLeft`, `Target`, `useId`; methods `Use(PlayerControl)`, `MeetingCalled`. `PlatformConsole` triggers it (`usableDistance`, `Platform`). RPC `UsePlatform`=32. The platform carries one player at a time across the gap, can be disabled (`DisabledPosition`, `outOfOrderPlat`), and resets on a meeting (Inferred). SystemTypes key: `GapRoom`.

#### Airship spawn selection (Observed)
`AirshipStatus.SpawnInGame: SpawnInMinigame`: `Locations: SpawnLocation[]` (each has `Name` StringNames, `Location` Vector3, `Rollover` anim), `LocationButtons`, controller selection support. `PrespawnStep()` is a coroutine that shows the spawn chooser before gameplay starts and again after each meeting (Inferred; `SpawnPlayer(player, numPlayers, initialSpawn)` has `initialSpawn` flag). Players choose one of ~several named rooms; a random option is available (Inferred; `previously used` unknown).

#### Decontamination (Mira, Polus, Airship; Observed `DeconSystem`, `DeconControl`)
`DeconSystem` is a two-door airlock: `UpperDoor`, `LowerDoor`, `DoorOpenTime`, `DeconTime`, `TargetSystem`, states Idle=0, Enter=1, Closed=2, Exit=4, HeadingUp=8, commands HeadUpCmd=1/HeadDownCmd=2/inside cmds 3/4. A player walks in; doors close; spray time elapses; the other door opens. `DeconControl` is the usable button. Mira's decontamination and Polus's decon pair are separate instances (`Decontamination`, `Decontamination2`, `Decontamination3` system keys). On Airship `ElectricalDoors` (a `DoorSet`/`StaticDoor` set) is listed under key `Decontamination` (Reference). The Airship "Decontaminate" task (id 45) uses two consoles in MainHall/Hallway (Reference).

#### Doors (Observed)
- `OpenableDoor` (abstract): `Id`, `Room`, `DoUpdate(dt)`. Subtypes: `AutoOpenDoor` (Skeld, auto reopens), `StaticDoor` (fixed door sets used by `ElectricalDoors`), `SomeKindaDoor` (decon airlock doors); roles of these are Inferred.
- `AutoDoorsSystemType` (Skeld, April): `CloseDoorsOfType(room)`, timers per room; doors reopen automatically (`initialCooldown`, `dirtyBits` for replication, 1 bit per door).
- `DoorsSystemType` (Polus, Airship, Fungle): consts `CloseDoors=128`, `OpenDoor=64`, `ActionMask=192`, `IdMask=31` (so up to 32 doors; Airship has 21 per reference); `timers: Dictionary<SystemTypes,float>`.
- Door consoles: `DoorConsole` (player interacts) and `OpenDoorConsole`; `IDoorMinigame` minigames: `DoorBreakerGame` (flip breakers; Polus? Inferred), `DoorCardSwipeGame` (swipe a card; Airship/Polus, Inferred), `MushroomDoorSabotageMinigame` (Fungle; whack mushrooms: 6 total, 3 immediately visible and 3 delayed, `mushroomWhackCount`).
- Reference door counts: Skeld 13, Polus 16, Airship 21, Fungle 8, Mira 0 (`Data/maps/*/doors.json`).

#### Zipline (Fungle; Observed)
`ZiplineBehaviour` (landing, handle positions, `MAX_USE_DISTANCE=3`, SFX, hand pooling, pet hiding), `ZiplineConsole : IUsableCoolDown` (`atTop`, `destination`). RPCs `CheckZipline`=51 (request), `UseZipline`=52 (broadcast). Moves a player between top and bottom; pets are hidden during travel. System key `Zipline`.

#### Spores and mushrooms (Fungle; Observed)
`Mushroom` (id, trigger wait 0.5 s, animations: idle/appear/stepped-on, spore cloud idle/appear/disappear, timings `secondsBetweenSporeReleases`, `secondsSporeIsActive`, `secondsSporeReappearsFor`, `secondsSporeFade`, vibration). RPCs `TriggerSpores`=53 and `CheckSpore`=54. Behavior (Inferred): stepping on a mushroom makes it release a cloud of spores; players inside show an effect; also used for hiding/vision effects (and `FungleShipStatus.sporeMushrooms` dictionary). `SystemTypes.Highlands` etc. are zones.

#### Mushroom Mixup sabotage (Fungle; Observed + Reference)
`MushroomMixupSabotageSystem` stores `currentMixups: Dictionary<byte, CondensedOutfit>` (per player: Hat/Visor/Skin/Pet index and `ColorPlayerId` = whose color to show), `cachedOutfitsByPlayerId`, `deadAndDcPlayerOutfitsByPlayerId`. Configurable empty chances (`skinEmptyChance`, `hatEmptyChance`, `visorEmptyChance`, `petEmptyChance`) and id lists (`hatIds`, `visorIds`, `petIds`, `skinIds`), `secondsForAutoHeal`, screen tint and per-player poof animation. Reference `Data/maps/Fungle/mixup.json` lists the hat/visor/pet cosmetic ids used for the scramble pool (19 hats, 5 visors, 5 pets; `Impostor:src/Impostor.Api/Innersloth/Data/maps/Fungle/mixup.json`). Game effect (Inferred): every player appears disguised with a random outfit/colors; nameplates likely hidden; heals after time or via task `MushroomMixupSabotageTask`. Also `MonitorMushroom` task and `LastBinocularPos` on the ship (Fungle surveillance is a free-pan binocular view).

#### Vision / lighting
`MaxLightRadius`/`MinLightRadius` on ShipStatus, `AffectedByLightAffectors` on roles, `LightAffectors` on Airship (zones that dim, Inferred). Impostors typically ignore the lights sabotage (`ImpostorLightMod` option; Observed option names `CrewLightMod`, `ImpostorLightMod`, `PlayerSpeedMod`, see `04-*`).

---

## 5. Vents, consoles and usable interfaces

### 5.1 Vent (Observed, `Vent.cs`)
`Vent : MonoBehaviour, IUsable` with `Id (int)`, links `Left`, `Right`, `Center` (each a `Vent` reference), `Buttons: ButtonBehavior[]` (arrows in the vent UI, one per link), `CleaningIndicators`, animations `EnterVentAnim`, `ExitVentAnim`, `Offset`, `spreadAmount/spreadShift`, frame delays (`numFramesUntilPlayerDisappearsOnEnter`/`...ReappearsOnExit`). Methods: `CanUse`, `Use`, `EnterVent(pc)`, `ExitVent(pc)` (coroutine), `ClickLeft/Center/Right`, `TryMoveToVent(Vent, out error)`, `UpdateArrows(VentilationSystem)`, `SetButtons(bool)`, `ToggleNeighborVentBeingCleaned`. Neighbor list `NearbyVents`.

Interaction (Inferred): standing near a vent shows a Vent button for roles with `CanVent`; entering hides the player; inside, arrow buttons show Left/Center/Right neighbors (null links are hidden); clicking an arrow moves to that vent; exiting unhides. Vents under cleaning (VentCleaning task) cannot be used and boot inside players.

### 5.2 VentilationSystem (Observed `VentilationSystem.cs`)
Server-authoritative vent state machine, key `SystemTypes.Ventilation`.
- Operations (enum `Operation`): StartCleaning=0, StopCleaning=1, Enter=2, Exit=3, Move=4, BootImpostors=5.
- `VentMoveInfo { Op, VentId, PlayerId }`, per-player `SequenceBuffer<VentMoveInfo>` with `opId: ushort` (op sequence numbers; dedupes and orders per-player messages, `OP_ID_START=0`), `PlayersCleaningVents: Dictionary<byte,byte>`, `PlayersInsideVents`, `ToRemove`.
- `BootImpostorFromVent(ventId, playerId)`, `BootImpostorsFromVent(ventId)`; `IsImpostorInsideVent(ventId)`, `IsVentCurrentlyBeingCleaned(id)`, `IsInvisiblePhantomOnVent(playerId)`. Cleaning a vent with a vent-user inside boots them (RPC `BootFromVent`=34).
- Also still: RPCs `EnterVent`=19, `ExitVent`=20 (legacy and role-based paths), messages `RpcEnterVentMessage`, `RpcExitVentMessage`, `RpcBootFromVentMessage` (Observed `Amongus/InnerNet/GameDataMessages/`).
- Anti-cheat note: sequence buffers + single authoritative system make vent teleports race-free (Inferred).

### 5.3 Vent graph per map (Reference: `Data/maps/<Map>/vents.json`; L/C/R = Left/Center/Right vent id)

Skeld (14 vents; links are mostly L/R pairs):
0 AdminVent (L2,R1), 1 BigYVent (L0,R2), 2 CafeVent (L0,R1) - triangle Admin/Cafe/BigY.
3 ElecVent (L5,R6), 5 SecurityVent (L6,R3), 6 MedVent (L3,R5) - triangle Elec/Security/Med.
4 LEngineVent (L11), 11 UpperReactorVent (R4) - pair.
8 ReactorVent (R9), 9 REngineVent (L8) - pair.
7 WeaponsVent (R12), 12 NavVentNorth (L7) - pair.
10 ShieldsVent (R13), 13 NavVentSouth (L10) - pair.

Mira (11 vents, uses Center): ids 1-11.
1 BalconyVent (L8,R2), 2 YHallRightVent (L6,R1), 3 ReactorVent (L4,C9,R11), 4 LabVent (L3,C9,R5), 5 OfficeVent (L4,C6,R7), 6 AdminVent (L7,C2,R5), 7 AgriVent (L6,R5), 8 MedVent (L1,R10), 9 DeconVent (L3,C10,R4), 10 LockerVent (L8,C11,R9), 11 LaunchVent (L3,R10). Dense, hub-like graph.

Polus (12 vents): 0 ElectricalVent (L2,R1), 1 ElecFenceVent (L2,R0), 2 LifeSuppVent (L1,R0) - triangle; 3 CommsVent (L8,R4), 4 OfficeVent (L3,R8), 8 StorageVent (L3,R4) - triangle; 5 AdminVent (L11,R7), 6 BathroomVent (R7), 7 SubBathroomVent (L6,R5), 11 SouthVent (R5); 9 ScienceBuildingVent (L10), 10 ElectricBuildingVent (L9) - pair (cross-building shortcut).

Airship (12 vents): 0 VaultVent (L1), 1 CockpitVent (L0,R2), 2 EjectionVent (L1) - chain; 3 EngineVent (L4,R5), 4 KitchenVent (L3,R5), 5 HallwayVent1 (L3,R4) - triangle; 6 HallwayVent2 (L8,R7), 7 GaproomVent2 (L8,R6), 8 GaproomVent1 (L7,R6) - triangle; 9 ShowersVent (L10,R11), 10 RecordsVent (L9,R11), 11 StorageVent (L9,R10) - triangle.

Fungle (10 vents): 0 CommunicationsVent (L2,R5), 1 KitchenVent (L6), 2 LookoutVent (L0,R5), 3 StorageVent (L9,R4), 4 NorthWestJungleVent (L8,R3), 5 NorthEastJungleVent (L2,R0), 6 SouthWestJungleVent (L1,R7), 7 SouthEastJungleVent (L6), 8 RecRoomVent (L9,R4), 9 MeetingRoomVent (L8,R3).

Observation (Reference): most vent clusters are 2-3 vent triangles; a few "express" pairs. Mira alone uses Center links.

### 5.4 IUsable and console types (Observed)
`IUsable` (interface): `float CanUse(NetworkedPlayerInfo pc, out bool canUse, out bool couldUse)` (returns distance score), `SetOutline(bool on, bool mainTarget)`, `Use()`. `IUsableCoolDown` adds `IsCoolingDown()` (Ladder, ZiplineConsole). `PlayerControl` finds the nearest usable in range each tick and enables the Use button for it (Inferred from `SetUsableTarget(IUsable)` in `RoleBehaviour`).

Implementers (from grep): `Console` (task console), `SystemConsole` (opens a `MinigamePrefab`: admin/security/vitals/emergency, with `useIcon`, `FreeplayOnly`, `onlyFromBelow`), `DoorConsole`, `OpenDoorConsole`, `DeconControl`, `MapConsole` (admin table), `OptionsConsole` (settings object in lobby), `PlatformConsole`, `Ladder`, `ZiplineConsole`, `Vent`. Role gate: `RoleBehaviour.CanUse(IUsable)`, e.g. Impostor cannot use task consoles with `AllowImpostor` false.

### 5.5 Cameras, admin, vitals, doorlog (Observed)
- Cameras: `SecurityCameraSystemType` (ops `IncrementOp=1`, `DecrementOp=2`; `PlayersUsing: HashSet<byte>`). Players using the console send +1/-1; the camera indicators (`SurvCamera.OnAnim/OffAnim`, red light on cameras) are on while `PlayersUsing` non-empty (Inferred). `SurvCamera` fields: `CamName`, `NewName`, `Images`, `CamSize`, `CamAspect`, `Offset`.
- Minigames: `SurveillanceMinigame` (grid of `ViewPorts` meshes with render textures, `FilteredRooms`; Skeld security), `PlanetSurveillanceMinigame` (single-view with a switching feed; Polus/Airship? Inferred), `FungleSurveillanceMinigame` (binocular free-pan with keyboard/joystick/mobile joystick speeds, `viewport`, arrow buttons).
- Admin: `MapConsole` + `MapBehaviour` countOverlay (`MapCountOverlay`, `CounterArea`) shows per-room occupant counts; `AdminButton` UI. When Comms is sabotaged the overlay is jammed (Inferred; `RoleBehaviour.CommsSabotaged`/`IsAffectedByComms`).
- Vitals: `VitalsMinigame` + `VitalsPanel` per player, `vitalDisconnected` dictionary; shows alive/dead/disconnected; Scientist role can open anywhere (see `05-roles.md`).
- Doorlog: `SecurityLogGame` (scrolling log of doors passed; `SecurityLogBehaviour Logger`, `EntryPool`, `Scroller`). `IStepWatcher` list on `ShipStatus` suggests door/room entry tracking (Inferred).
- Medbay scanner: `MedScanSystem` (ops `Request=128`, `Release=64`, `NumMask=31`, `UsersList`) queues players for the medbay scan platform; `MedScanMinigame` shows blood type/height strings; scanner has visual (Observed names), "Submit Scan" task uses it.
- Emergency button: `EmergencyMinigame` (min emergency time 15 s `MinEmergencyTime`, `ButtonActive`), meeting caps via `NumEmergencyMeetings` and `EmergencyCooldown` options.

---

## 6. Map task lists (Reference)

Source: `Impostor:src/Impostor.Api/Innersloth/Data/maps/<Map>/tasks.json` (per entry: `type` class, `taskType`, `length`, `consoles[] {id, room, position, usableDistance}`). Entries with 0 consoles are multi-stage tasks whose consoles are defined by sub-tasks / special objects (Inferred). Duplicated names (xN) are separate prefab entries (e.g. variants with different console sets). Counts of entries L/S/C: Skeld 8/20/2, Mira 11/14/2, Polus 15/14/4, Airship 17/24/2, Fungle 9/16/4.

| Map | Common | Short | Long |
|---|---|---|---|
| Skeld | SwipeCard, FixWiring | UploadData x5, CalibrateDistributor, ChartCourse, CleanO2Filter, UnlockManifolds, StabilizeSteering, PrimeShields, DivertPower x8, VentCleaning | ClearAsteroids, AlignEngineOutput, SubmitScan, InspectSample, FuelEngines, StartReactor, EmptyChute, EmptyGarbage |
| Mira | FixWiring, EnterIdCode | ChartCourse, CleanO2Filter, FuelEngines, AssembleArtifact, SortSamples, PrimeShields, EmptyGarbage, MeasureWeather, DivertPower, BuyBeverage, ProcessData, RunDiagnostics, UnlockManifolds, VentCleaning | SubmitScan, ClearAsteroids, DivertPower x7, WaterPlants, StartReactor |
| Polus | SwipeCard, InsertKeys, ScanBoardingPass, FixWiring | MonitorOxygen, UnlockManifolds, StoreArtifacts, FillCanisters, EmptyGarbage, ChartCourse, SubmitScan, ClearAsteroids, FixWeatherNode x2, AlignTelescope, RepairDrill, RecordTemperature x2 | UploadData x5, StartReactor, FuelEngines, OpenWaterways, InspectSample, ReplaceWaterJug, FixWeatherNode x4, RebootWifi |
| Airship | FixWiring, EnterIdCode | PolishRuby, StabilizeSteering, UploadData x5, DivertPower x7, PickUpTowels, CleanToilet, DressMannequin, SortRecords, PutAwayPistols, PutAwayRifles, Decontaminate, MakeBurger, FixShower, VentCleaning | CalibrateDistributor, ResetBreakers, UploadData x5, UnlockSafe, StartFans, EmptyGarbage x5, DevelopPhotos, FuelEngines, RewindTapes |
| Fungle | CollectSamples, EnterIdCode, ReplaceParts, RoastMarshmallow | AssembleArtifact, BuildSandcastle, CollectShells, CrankGenerator, EmptyGarbage x2, FixAntenna, FixWiring, LiftWeights, MonitorMushroom, PlayVideogame, RecordTemperature x3, TestFrisbee, TuneRadio | CatchFish, CollectVegetables, ExtractFuel, HelpCritter, HoistSupplies, PolishGem, MineOres, ReplaceWaterJug, WaterPlants |

Observations:
- Several `TaskType`s are shared across maps with different length classes (e.g. `ChartCourse` short on Skeld/Mira/Polus; `UploadData` short on Skeld, long on Polus/Airship; `SubmitScan` long on Skeld/Mira, short on Polus). Length is per-prefab not per-type (Reference).
- Wiring consoles: Skeld 6, Mira 5, Polus 6, Airship 7, Fungle 2 per task entry (Reference). Real wiring tasks use 3 consoles per assignment (Inferred: pick random subset).
- `type` classes in reference: NormalPlayerTask, UploadDataTask (Skeld/Polus), AirshipUploadTask, DivertPowerTask, TowelTask (15 consoles), WaterWayTask, WeatherNodeTask (6 consoles) (Reference matches Observed task subclasses in section 1.1).
- Special task tool: `ImportantTextTask` and `ShipTasks` system key aggregate "specials".
- Spawn data (Reference `spawn.json`): Skeld initial/meeting center (-0.72, 0.62) radius 1.6; Mira initial (-4.4, 2.2), meeting (24.0, 1.7), 1.55; Polus initial (16.6, -2.5), meeting (17.4, -16.3) and second (17.4, -17.5), 1.0; Airship initial (-25, 40), meeting (20, 9), 1.55; Fungle initial (-9.8, 1.0), meeting (-3, -2.1), 1.5. Matches `ShipStatus.InitialSpawnCenter/MeetingSpawnCenter(2)/SpawnRadius` fields (Observed).

---

## 7. Minigame catalogue

Base: `Minigame : MonoBehaviour` (Observed `Minigame.cs`): `Instance` (static), `Depth=-50`, `TransType (TransitionType)`, `TargetPosition`, `MyTask: PlayerTask`, `MyNormTask: NormalPlayerTask`, `Console` property, `Begin(PlayerTask)`, `Close()`, `Close(bool allowMovement)`, `ForceClose()`, `SetupInput(disableCursor, suppressInGamePlayerList)`, `multistageMinigameParent` (Observed). Opening requires a Console or SystemConsole; closing restores movement. Flow (Inferred): Console.Use -> instantiate `MinigamePrefab` -> `Begin(task)` -> user interacts -> on success `MyNormTask.NextStep()` or `Complete()` and `Close()`; server learns via `CompleteTask`.

Composite: `MultistageMinigame` / `AutoMultistageMinigame` (array `Stages: Minigame[]`, `stage`) chain several minigames in one task (fuel: pick-up then insert; Observed fields). Input: every minigame reads `Controller`/`Player` (Rewired) for gamepad parity; mobile uses touch. `ConsoleId` differentiates stages.

All "Inferred" below are from class name + field names only. Maps in brackets are the maps whose Reference task list contains the task type that the minigame most likely serves.

### 7.1 Classic ship tasks (Skeld/Mira/Polus/Airship)

| Minigame class | Task (likely) | Interaction (Inferred) |
|---|---|---|
| WireMinigame | FixWiring | Drag 3-5 colored wires from left nodes to matching right nodes (`LeftNodes`, `RightNodes`, `ExpectedWires`, `ActualWires`) |
| CardSlideGame | SwipeCard | Drag/slide a card through a reader within an accepted time window (`AcceptedTime`, `dragTime`, too fast/slow fails) |
| DoorCardSwipeGame | Door open | Same card swipe, for doors (IDoorMinigame, `minAcceptedTime`) |
| WeaponsMinigame | ClearAsteroids | Tap/shoot asteroids that spawn at random times (`asteroidPool`, `TimeToSpawn`) |
| ShieldMinigame | PrimeShields | Tap highlighted shield hexagons until all are lit (`shields` byte bitmask) |
| NavigationMinigame | ChartCourse (alt) | Move a crosshair on a two-axis image (`TwoAxisImage`, `crossHair`) |
| CourseMinigame | ChartCourse | Drag the ship through a path of waypoint stars (`Stars`, `PathPoints`, `Destination`) |
| AdjustSteeringGame | StabilizeSteering | Keep a crosshair on target by dragging; has thrust and steering gauges (`TargetThrustY`, `TargetSteeringRot`) |
| AlignGame | AlignEngineOutput | Drag engine alignment sliders to the centerline (`YRange`, `centerline`, `guidelines`) |
| SimonSaysGame | StartReactor | Repeat a growing light sequence (`operations` queue, `FlashOp`, `FailOp`) |
| ReactorMinigame | Reactor sabotage | Hold the console handprint while another player holds the second (sweep animation `YSweep`) |
| KeypadGame | O2 sabotage / Enter code | Enter numeric code (`number`, `numString`) tied to `LifeSuppSystemType` and `NoOxyTask` |
| EnterCodeMinigame | EnterIdCode | Read a code from a card and type it (`targetNumber`, `cardOut`) |
| AuthGame / AirshipAuthGame | Comms HQ sabotage / Stop Charles | Two-console code entry (`HqHudSystemType`, `HeliSabotageSystem`; `OtherConsoleId`) |
| SwitchMinigame | Fix Lights | Toggle 5 switches to match the target pattern (`SwitchSystem`) |
| ElecLeverGame | Lever task (Airship?) | Drag a lever handle (`HandleRange`); used in electric-related tasks (task unknown) |
| WifiGame | RebootWifi | Pull a slider, wait (`WaitDuration=60`), reconnect |
| DivertPowerMetagame / DivertPowerMinigame / AcceptDivertPowerGame | DivertPower | Stage 1: move one of several sliders in Electrical to route power to a target room (`SliderOrder`, `Gauges`, `Wires`); stage 2: go to the room and accept (`LeftWires`, `RightWires`) |
| UnlockManifoldsMinigame | UnlockManifolds | Press numbered buttons 1..10 in order (`buttonCounter`, `SystemId`) |
| LeafMinigame | CleanO2Filter | Drag leaves out of the filter (`LeafPrefab`, `ValidArea`) |
| UploadDataGame | UploadData | Download at one console, upload at another; folder animation, gauge (`ConstantTime=3`, `RandomChunks=5`) |
| AirshipUploadGame | UploadData (Airship) | Phone-like device upload with a gauge and beeps (`MaxTimer=20`) |
| SweepMinigame | CalibrateDistributor | Stop each spinner's needle at the right moment (`Gauges`, `SpinRate`) |
| SampleMinigame | InspectSample | Choose the odd-colored vial among several after a wait (`correctTube`, `selectedTube`, `TimePerStep`) |
| MedScanMinigame | SubmitScan | Stand still while the scanner runs (`ScanDuration`, `ScanTimer`, blood-type text) |
| EmptyGarbageMinigame | EmptyGarbage / EmptyChute | Pull a lever to dump objects (`HandleRange`, `NumObjects`) |
| AirshipGarbageGame | EmptyGarbage (Airship) | Drag trash cans toward a hatch (`GarbagePrefabs`, `can`) |
| RefuelStage / WaterStage (via MultistageMinigame) | FuelEngines / ReplaceWaterJug | Hold button to transfer fluid between source and destination gauge (`RefuelDuration`) |
| VentCleaningMinigame | VentCleaning | Wipe dirt off a vent (`dirtPool`, `cleaningDistanceThreshold`, interacts with `VentilationSystem`) |
| WaterWheelGame | OpenWaterways | Spin a wheel to fill the water level gauge (`WheelScale`, `WaterLevel`) |
| WeatherSwitchGame | FixWeatherNode | Flip a set of switches per node (`Controls`, `WeatherNodeTask`) |
| Weather1Game | MeasureWeather? | Grid tilemap; move a control tile around barriers (`BarrierMap`, `controlTile`) |
| WeatherMinigame | MeasureWeather | Press Start, wait for three gauges to fill (`Duration`, `destGauge1..3`) |
| TempMinigame | RecordTemperature | Read a temperature and enter/log it (`LogRange`, `ReadingRange`) |
| TelescopeGame | AlignTelescope | Find a blip in sky view (`BlipDelay`, `grabbed`) |
| DrillMinigame | RepairDrill | Press the lit buttons to fix (`Buttons`, `MaxState`, `states`) |
| MonitorOxyMinigame | MonitorOxygen | Select and adjust gauge fills to ranges (`Fills`, `RandomRanges`) |
| FillCanistersGame | FillCanisters | Drag a canister to the pump, hold to fill, drag away (`FillTime`) |
| SpecimenGame | StoreArtifacts | Pick the right specimen/artifact from a shelf (`selectedSpecimen`) |
| KeyMinigame | InsertKeys | Rotate and insert a key into the right slot (`Slots`, `targetSlotId`) |
| BoardPassGame | ScanBoardingPass | Pull and flip a boarding pass through a scanner (`pullButton`, `flipButton`) |
| DiagnosticGame | RunDiagnostics | Press start, wait through a gauge (`TimePerStep`, `Gauge`) |
| ProcessDataMinigame | ProcessData | Start a scan; scenery parallax and folders animate (`Duration`) |
| VendingMinigame | BuyBeverage | Enter a letter code on a vending machine (`targetCode`, `Slots`) |
| WaterPlantsGame | WaterPlants | Drag a watering can to each plant (`WaterCan`, `waterParticles`) |
| SortMinigame | SortSamples / AssembleArtifact | Drag objects into slots (`currentlyGrabbedObject`); Mira-style sort |
| SortSamplesMinigame | SortSamples (Fungle-style) | Drag each sample to the right bin (`sortableSamples`, `samplesSorted`) |
| PhotosMinigame | DevelopPhotos | Place photos onto polaroid bounds (`PolaroidBounds`, `photos`) |
| EmergencyMinigame | Emergency button | Large "emergency" button after min time |
| SpawnInMinigame | Airship spawn | Choose spawn room (Airship section) |

### 7.2 Airship-exclusive

| Minigame | Task | Interaction (Inferred) |
|---|---|---|
| SafeMinigame | UnlockSafe | Spin a dial to hit a combination with tumbler feel/vibration (`combo`, `latched`) |
| PolishRubyGame | PolishRuby | Swipe/rub smudges off the ruby (`swipesToClean`, `directions`) |
| DressUpMinigame | DressMannequin | Pick cosmetics to dress a mannequin (`DressUpCosmetic[]`) |
| TowelMinigame (TowelTask) | PickUpTowels | Collect scattered towels (15 consoles, `towelsDone`) |
| ToiletMinigame | CleanToilet | Plunge up/down with pressure (`plungeScale`, `pressure`) |
| FixShowerMinigame | FixShower | Hammer a shower to set power in a band (`hammerAngles`, `PowerBar`) |
| ShowerMinigame | Shower | Fill gauge over time (`MaxTime`), shower visuals |
| RecordsMinigame | SortRecords | Slide/sort folders into place (`slideFolderHotkey`) |
| StowArms | PutAwayPistols / PutAwayRifles | Drag guns and rifles into slots (`GunsSlots`, `RifleSlots`) |
| StartFansMinigame | StartFans | Enter a code to start fans (`codeButtons`) |
| RewindTapeMinigame | RewindTapes | Hold a button to rewind a tape to target time (`targetTime`, `currentTime`) |
| BurgerMinigame | MakeBurger | Stack toppings in the displayed order (`ExpectedToppings`, `Toppings`) |
| SecurityLogGame | Doorlog | Scroll the door log (not a task) |
| PlanetSurveillanceMinigame | Security cam | Single viewport with static (`isStatic`) |
| (Decontaminate task) | Decontaminate | Uses DeconSystem rather than a minigame (Inferred) |
| (ResetBreakers task) | ResetBreakers | 7 consoles; minigame unknown (candidates: `ElecLeverGame`, `DoorBreakerGame`) |

### 7.3 Fungle-exclusive

| Minigame | Task | Interaction (Inferred) |
|---|---|---|
| CollectSamplesMinigame | CollectSamples | Pick samples (`SAMPLE_COUNT=3`) |
| PlacePartsMinigame / TakePartsMinigame | ReplaceParts | Take parts from a box (`partsTaken`) then place at slots (`placedParts`) |
| RoastMarshmallowFireMinigame / RoastMarshmallowStickMinigame | RoastMarshmallow | 5 consoles on the Beach: get stick, get marshmallow, hold over fire, take it (`FIRE_CONSOLE_ID=0`, states) |
| BuildSandcastleMinigame | BuildSandcastle | Fill and flip buckets with timing/gravity (`fallGravity`, `targetBucketY`) |
| CollectShellsMinigame | CollectShells | Wipe sand to uncover shells (`shells`, `wipeDistance`) |
| LiftWeightsMinigame | LiftWeights | Hold to lift bar into a valid percent band (`validFillPercentRange`) |
| TestFrisbeeMinigame | TestFrisbee | Pick up frisbee and throw (`thrown`, `pickedUp`) |
| CrankGeneratorMinigame | CrankGenerator | Circular crank motion; progress decays (`progress`, `progressDecay`) |
| TuneRadioMinigame | TuneRadio | Rotate dial until signal wave matches (`Tolerance`, `steadyTimer`) |
| CatchFishMinigame | CatchFish | Cast, wait ~5 s, reel (3 fish; 2 steps each: `NUM_FISH_TO_CATCH=3`) |
| CookFishMinigame | (CatchFish stage 2) | Cook fish with timer (`cookingTimeSeconds`, `FungleTimer`) |
| CollectVegetablesMinigame | CollectVegetables | Select/pick vegetables (`collected`) |
| CutVegetablesMinigame | (CollectVegetables stage) | Slice veggies on a board (`numVeggiesCut`) |
| HoistSuppliesMinigame | HoistSupplies | Pull rope to raise a basket (`ropeLength`) |
| MineOreMinigame | MineOres | Time a power bar and swing a pickaxe on rocks (`ORE_NUMBER=3`, good/perfect thresholds) |
| CrystalMinigame | PolishGem | Pick crystal pieces from a tray (`CrystalPieces`, `Shuffed`) |
| FindCritterEggMinigame | HelpCritter | Find an egg under leaves (`leaves`, `eggButton`) |
| IncubateEggMinigame | HelpCritter | Feed/water an egg until hatch (`secondsToHatchEgg`, emotes: happy, thirsty, hungry) |
| MushroomDoorSabotageMinigame | Mushroom door | Whack mushrooms to open a door |
| FungleSurveillanceMinigame | Binoculars | Free-pan viewport |
| (FixAntenna, MonitorMushroom, ExtractFuel, PlayVideogame) | various | Minigame class not identified (may reuse Leaf/Switch-style classes; Unknown) |

### 7.4 Role / meta minigames (not tasks)

| Minigame | Purpose |
|---|---|
| ShapeshifterMinigame | Target picker (panel grid) for Shapeshifter |
| VitalsMinigame | Alive/dead panels, used by vitals console and Scientist |
| HauntMenuMinigame | Ghost haunt/spectate picker |
| SpiritGuidePanelMinigame | Spirit Guide picture-message panel |
| DetectiveNotesMinigame | Detective's notes about a victim |
| TaskAdderGame | Freeplay/debug task picker |
| LogicMinigameHnS (`LogicMinigame` family) | Hide-and-seek rule component (not a UI minigame; derives from `LogicMinigame`) |
| PlaceholderMinigame | Stub for unimplemented tasks (Inferred) |
| DoorBreakerGame | Door fix minigame (breaker switches) |

### 7.5 Design reading
- About 90 classes; roughly 14 reusable *input patterns*: drag-to-target, hold button, tap sequence, slider/gauge, swipe, rotate dial/crank, code entry, drag-sort, timed-stop, wait-then-confirm, find-hidden, toggle-grid, pan-view, whack-a-mole.
- Mobile-friendly: all minigames read gamepad (`Controller`, `Player`) and touch; layouts stay inside a small modal. Keep this property.
- Task-length rule of thumb (Reference lists): common = identical across all players on the map, one console-set per assignment; short = 1-2 stages; long = 2-3+ stages or wait timers. Each map has 2-4 common, 14-24 short, 8-17 long prefab entries to choose from.

---

## 8. Unknowns / to verify
- Exact mapping of each TaskType to Minigame class (inferred above; prefab refs live in Unity assets, not the dump).
- Per-task timer/step counts (`MaxStep`), `TaskTimer` values, sabotage durations (`ReactorDuration`, `LifeSuppDuration`), door timers, emergency defaults.
- Dleks details (class not present; likely a flag on the Skeld prefab).
- Airship `Armory`, `HallOfPortraits`, Fungle `Highlands` rooms: only enum evidence.
- Whether Fungle really has no lights sabotage (Reference shows none; verify).
- GetBiggolSword / None task types: unused leftovers (Inferred).
- Server-side authority: which of these systems run on the host client vs a dedicated server in 2026.9.29 (see `02-*`).
