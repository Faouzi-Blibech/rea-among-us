# 04 - Game options and enums

> **Data update:** numeric values the code dump lacks (player speed 2.5, kill cooldown default 15, role cooldowns/durations, task MaxStep, map scale, exile timings) were later recovered from serialized assets: see [data/gameplay](data/gameplay/README.md) and [data/maps](data/maps/README.md) (Observed vent graphs with links, consoles with task types). Still unknown after that pass: reactor/O2 countdowns, door cooldowns, kill-distance floats, option presets.

Gameplay that uses these options: [03](03-gameplay-core.md). Role behaviour: [05](05-roles-and-abilities.md). Wire framing: [02](02-networking.md).

Tags: Observed = Android 2026.9.29 dump (`cs/DiffableCs/Assembly-CSharp/...`); Reference = Impostor server source (`refs/Impostor/src/...`, protocol version 12 parser); Inferred = deduced.

Important limit: the dump has **no field initialisers and no asset data**, so default values and slider min/max/step are *not* in it. Defaults below come from Impostor (Reference) and may differ from the live game. Ranges are marked unknown unless a source gives them.

## 1. Option classes and versioning

| Type | Purpose | Source |
|---|---|---|
| `IGameOptions` | Interface: `GameMode`, `SpecialMode`, `RulesPreset`, `MaxPlayers`, `Keywords`, `MapId`, `NumImpostors`, `IsDefaults`, `RoleOptions`, `Version`, typed getters/setters `Get/Set/TryGet{Bool,Byte,Float,Int,UInt,FloatArray,IntArray}`, `SetRecommendations(numPlayers, isOnline[, rulesPreset])`, `AreInvalid(maxExpectedPlayers)`, `TryClearAprilFoolsMode`, `TotalTaskCount` | Observed, `Assembly-CSharp/Amongus/GameOptions/IGameOptions.cs`, `NormalGameOptionsV12.cs` |
| `NormalGameOptionsV07 ... V12` | Normal and Fools modes. Latest = **V12** | Observed (files V07-V12); Reference `NormalGameOptions.LatestVersion = 12` |
| `HideNSeekGameOptionsV07 ... V12` | Hide and Seek | Observed |
| `RoleOptionsCollectionV07 ... V12` | Per-role rates and role-specific options | Observed |
| `GameOptionsMigratorV06toV07 ... V11toV12`, `IGameOptionsMigrator` | Upgrade older option blobs forward | Observed |
| `GameOptionsFactory`, `GameOptionsJsonConverter`, `LegacyGameOptions`, `LegacyRoleOptionsCollection` | Create/parse by version byte and game mode; JSON for matchmaking filters | Observed |
| `GameOptionsManager`, `LogicOptions` (`LogicOptionsNormal`, `LogicOptionsHnS`) | Runtime holder; `SyncOptions()` pushes via `PlayerControl.RpcSyncSettings(byte[])` (RPC `SyncSettings=2`) | Observed |
| `BaseGameSetting` + `FloatGameSetting`, `Int32GameSetting`/`NumberOption`, `CheckboxGameSetting`, `StringGameSetting` | UI schema for each option (title, type, `OptionName`, `Increment`, `ValidRange`, `ZeroIsInfinity`, `SuffixType`, `FormatString`). Values live in Unity assets, not in the dump | Observed, `Assembly-CSharp/FloatGameSetting.cs` etc. |

Version handling (Observed + Reference): the options blob begins with a version byte; the parser branches per version (Impostor: `Version >= 8` adds `SpecialMode` and `RulesPreset`; `>= 9` adds `Tag`; max supported 12; unknown versions throw). A clone does not need V07-V11; define one schema and a version byte for future migration.

Serialization of V12 Normal (Reference, `Impostor:src/Impostor.Api/Innersloth/GameOptions/NormalGameOptions.cs`; field order is Observed-compatible with the V12 class layout in `NormalGameOptionsV12.cs` but the dump has no bodies, so byte widths are Reference only):

```
byte  SpecialMode, byte RulesPreset, byte MaxPlayers, uint32 Keywords, byte MapId,
f32 PlayerSpeedMod, f32 CrewLightMod, f32 ImpostorLightMod, f32 KillCooldown,
byte NumCommonTasks, byte NumLongTasks, byte NumShortTasks,
i32 NumEmergencyMeetings, byte NumImpostors, byte KillDistance, i32 DiscussionTime, i32 VotingTime,
bool IsDefaults, byte EmergencyCooldown, bool ConfirmImpostor, bool VisualTasks, bool AnonymousVotes,
byte TaskBarMode, byte Tag, RoleOptionsCollection (packed count, then per role:
   i16 RoleType, RoleRate{byte MaxCount, byte Chance}, nested message of role options)
```

Note: the wire stores several numbers as bytes (task counts, impostor count, emergency cooldown); this caps them at 255.

## 2. Enums

### 2.1 Option-name enums (Observed, `Assembly-CSharp/Amongus/GameOptions/*OptionNames.cs`)

Ids are sparse: gaps leave room for role-specific blocks (100s HnS, 1000s role options).

**`Int32OptionNames`**: Invalid=0, NumImpostors=1, KillDistance=2, NumEmergencyMeetings=3, EmergencyCooldown=4, DiscussionTime=5, VotingTime=6, MaxImpostors=7, MinPlayers=8, MaxPlayers=9, NumCommonTasks=10, NumShortTasks=11, NumLongTasks=12, TaskBarMode=13, Tag=14, CrewmatesRemainingForVitals=100, CrewmateVentUses=101, ImpostorPlayerID=102, RulePreset=200.

**`FloatOptionNames`**: Invalid=0, KillCooldown=1, PlayerSpeedMod=2, ImpostorLightMod=3, CrewLightMod=4, CrewmateTimeInVent=100, FinalEscapeTime=101, EscapeTime=102, SeekerFinalSpeed=103, MaxPingTime=104, CrewmateFlashlightSize=105, ImpostorFlashlightSize=106, ShapeshifterCooldown=1000, ShapeshifterDuration=1001, ProtectionDurationSeconds=1100, GuardianAngelCooldown=1101, ScientistCooldown=1200, ScientistBatteryCharge=1201, EngineerCooldown=1300, EngineerInVentMaxTime=1301, PhantomCooldown=1500, PhantomDuration=1501, TrackerCooldown=1550, TrackerDuration=1551, TrackerDelay=1552, NoisemakerAlertDuration=1600, ViperDissolveTime=1601, DetectiveSuspectLimit=1602, JudgeTaskRequirementPercentage=1620, SpiritGuideCooldownSeconds=1640.

**`BoolOptionNames`**: Invalid=0, VisualTasks=1, GhostsDoTasks=2, ConfirmImpostor=3, AnonymousVotes=4, IsDefaults=5, UseFlashlight=6, SeekerFinalVents=7, SeekerFinalMap=8, SeekerPings=9, ShowCrewmateNames=10, Roles=11, ShapeshifterLeaveSkin=1000, ImpostorsCanSeeProtect=1100, NoisemakerImpostorAlert=1300.

**`ByteOptionNames`**: Invalid=0, MapId=1. **`UInt32OptionNames`**: Invalid=0, Keywords=1. **`FloatArrayOptionNames`**: Invalid=0, KillDistances=1. **`Int32ArrayOptionNames`**: Invalid=0, MaxImpostors=1, MinPlayers=2.

Note: `SeekerFinalVents` (Bool 7) exists in the enum but no field of that name is in `HideNSeekGameOptionsV12` (Observed); treat as legacy/unused (Inferred). `MaxImpostors`/`MinPlayers` as Int32 names (7, 8) and as arrays (`Int32ArrayOptionNames`) are lookup tables indexed by player count, not user settings (see static arrays below).

### 2.2 Mode and map enums (Observed)

| Enum | Values | File |
|---|---|---|
| `GameModes` | None=0, Normal=1, HideNSeek=2, NormalFools=3, SeekFools=4 | `Amongus/GameOptions/GameModes.cs` |
| `SpecialGameModes` (flags) | None=0, AprilFools=1 | `SpecialGameModes.cs` |
| `RulesPresets` | Standard=0, StandardRoles=1, Flashlight=2, Custom=100 | `RulesPresets.cs` |
| `MapNames` | Skeld=0, MiraHQ=1, Polus=2, Dleks=3, Airship=4, Fungle=5 | `Assembly-CSharp/MapNames.cs` |
| `TaskBarMode` | Normal=0, MeetingOnly=1, Invisible=2 | `Assembly-CSharp/TaskBarMode.cs` (Reference names: Always/Meetings/Never, same values) |
| `RoleTypes` | Crewmate=0, Impostor=1, Scientist=2, Engineer=3, GuardianAngel=4, Shapeshifter=5, CrewmateGhost=6, ImpostorGhost=7, Noisemaker=8, Phantom=9, Tracker=10, Detective=12, Viper=18, Judge=19, SpiritGuide=21 | `RoleTypes.cs` (ids 11, 13-17, 20 unused/reserved; Inferred) |
| `RoleTeamTypes` | Crewmate=0, Impostor=1 | `RoleTeamTypes.cs` |
| `GameKeywords` (flags, chat language filter) | All=0, Other=1, SpanishLA=2, Korean=4, Russian=8, Portuguese=16, Arabic=32, Filipino=64, Polish=128, English=256, Japanese=512, SpanishEU=1024, Brazilian=2048, Dutch=4096, French=8192, German=16384, Italian=32768, SChinese=65536, TChinese=131072, Irish=262144 | `GameKeywords.cs` |
| `KillDistances` (Reference) | Short=0, Normal=1, Long=2 | `Impostor:src/Impostor.Api/Innersloth/KillDistances.cs` |
| `GameStates` | NotStarted=0, Started=1, Ended=2, Destroyed=3 | `InnerNet/GameStates.cs` |
| `GameOverReason` | see [03](03-gameplay-core.md) section 7 | `GameOverReason.cs` |
| `DeathReason` / `MurderResultFlags` | see [03](03-gameplay-core.md) | |

Role detail ids are for [05](05-roles-and-abilities.md); the dump has role option classes for Scientist, Engineer, GuardianAngel, Shapeshifter, Noisemaker, Phantom, Tracker, Detective, Viper, Judge, SpiritGuide (`*RoleOptionsV12.cs`), and none for Crewmate/Impostor.

### 2.3 Kill distance values

The index is stored as an int (`Int32OptionNames.KillDistance=2`); the actual distances come from a static `Single[] KillDistances` plus `String[] KillDistanceStrings` in the options class (Observed: `NormalGameOptionsV12.KillDistances`, `HideNSeekGameOptionsV12.KillDistances`; the dump does not include their contents). Impostor stores only the enum index and never checks a distance (Reference).
**Actual metre/unit values: unknown from the evidence.** Community figures often quoted are roughly 1.0 / 1.8 / 2.5 world units for Short/Normal/Long, but this is unverified recollection and must be re-measured or chosen by the clone (Inferred, low confidence). Default index is Normal=1 (Reference).

The options class also holds static lookup arrays whose values are unknown: `RecommendedKillCooldown`, `RecommendedImpostors`, `MaxImpostors`, `MinPlayers`, `AvailableRoles` (`RoleTypes[]`). They back `SetRecommendations(numPlayers, ...)` (Observed names). Use the table in 3.3 (Inferred) instead.

## 3. Normal-mode option table (V12)

Defaults: Reference (`Impostor:src/Impostor.Api/Innersloth/GameOptions/NormalGameOptions.cs`) unless marked. "Range" = unknown unless stated. Field offsets and types are Observed from `NormalGameOptionsV12.cs`.

### 3.1 General

| Option (enum / field) | Type | Default | Range / step | Meaning |
|---|---|---|---|---|
| `MaxPlayers` (Int32 9) | int (wire byte) | 10 | unknown; lobby UI caps at the game's player limit (commonly 15; unverified) | Lobby capacity |
| `MapId` (Byte 1) | byte | 0 (Skeld) | `MapNames` 0-5 | Map selection |
| `NumImpostors` (Int32 1) | int (wire byte) | 1 | 1 up to `MaxImpostors[numPlayers]` (table unknown) | Impostor count |
| `Keywords` (UInt32 1) | flags uint32 | English | `GameKeywords` | Matchmaking language |
| `RulesPreset` (Int32 200) | byte | Custom (100) | `RulesPresets` | Standard / Standard+roles / Flashlight / Custom |
| `IsDefaults` (Bool 5) | bool | true | | Set false once any value changed |
| `Tag` (Int32 14) | int (wire byte) | 0 | beginner/intermediate/expert (Reference doc comment; exact ids unknown) | Experience filter |
| `SpecialMode` | flags | None | | April Fools switch |
| `Roles` (Bool 11) | bool | unknown | | Whether the role system is enabled (Inferred; paired with `RulesPresets.StandardRoles`) |

### 3.2 Movement, vision, combat

| Option | Type | Default | Range / step | Meaning |
|---|---|---|---|---|
| `PlayerSpeedMod` (Float 2) | float | 1.0 | unknown (UI slider; likely 0.25-3.0, unverified) | Multiplier on `PlayerPhysics.Speed` (`SpeedMod`) |
| `CrewLightMod` (Float 4) | float | 1.0 | unknown | Crew vision multiplier (against `ShipStatus.MinLightRadius/MaxLightRadius`) |
| `ImpostorLightMod` (Float 3) | float | 1.0 | unknown | Impostor vision multiplier |
| `KillCooldown` (Float 1) | float s | 15 | unknown (likely 10-60 s, step 2.5; unverified) | Time between kills |
| `KillDistance` (Int32 2) | int index | 1 (Normal) | 0-2 | Reach; see 2.3 |
| `UseFlashlight` (Bool 6) | bool | HnS true | | Flashlight cone vision (preset `Flashlight`; field exists in Normal only via preset, Inferred) |

### 3.3 Meetings

| Option | Type | Default | Range / step | Meaning |
|---|---|---|---|---|
| `NumEmergencyMeetings` (Int32 3) | int | 1 | unknown (0..9 plausible) | Emergency button uses per player |
| `EmergencyCooldown` (Int32 4) | int s | 15 | unknown; `EmergencyMinigame.MinEmergencyTime=15` (Observed const) suggests 15 is a floor on button-press delay | Global button lockout between meetings |
| `DiscussionTime` (Int32 5) | int s | 15 | unknown (0 .. ~300) | Chat-only time |
| `VotingTime` (Int32 6) | int s | 120 | unknown; 0 = infinite is the common UI convention (`FloatGameSetting.ZeroIsInfinity` exists, Observed field; applicability to this option is Inferred) | Vote time |
| `AnonymousVotes` (Bool 4) | bool | false | | Hide who voted for whom |
| `ConfirmImpostor` (Bool 3) | bool | true | | Reveal ejected player's team |
| `TaskBarMode` (Int32 13) | enum | Normal (0) | 0-2 | Progress bar: always / meetings only / hidden |

### 3.4 Tasks and ghosts

| Option | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `NumCommonTasks` (Int32 10) | int (wire byte) | 1 | unknown | Common tasks per crew (same for everyone) |
| `NumShortTasks` (11) | int | 2 | unknown | Short tasks |
| `NumLongTasks` (12) | int | 1 | unknown | Long tasks |
| `TotalTaskCount` | derived | sum | | Property on `IGameOptions` (Observed) |
| `VisualTasks` (Bool 1) | bool | true | | Show task animations (scan, trash, shields, asteroids) |
| `GhostsDoTasks` (Bool 2) | bool | true | | Dead crew keep tasking |
| `CrewmatesRemainingForVitals` (Int32 100) | int | unknown | | Threshold for vitals/role-gated rule (Inferred; role-linked, see [05](05-roles-and-abilities.md)) |

Validation: `AreInvalid(maxExpectedPlayers)` exists on the options class (Observed); contents unknown. Recommended-value logic: `SetRecommendations(numPlayers, isOnline[, preset])`.

## 4. Role options (RoleOptionsCollectionV12 and per-role classes)

Structure: `Dictionary<RoleTypes, RoleDataV12>`; each `RoleDataV12{Type, RoleOptions: IRoleOptions, Rate: RoleRate{MaxCount:int, Chance:int}}` (Observed, `RoleOptionsCollectionV12.cs`, `RoleDataV12.cs`, `RoleRate.cs`). `GetNumPerGame(role)` = MaxCount, `GetChancePerGame(role)` = Chance (percent, Inferred). `AnyRolesEnabled()`, `SetRoleRate`, `SetRoleRecommended` (Observed). Role assignment draws `Chance` per role up to `MaxCount` across the team's slots (Inferred from `AssignRolesFromList`).

| Role | Option (enum) | Type | Default (Reference unless noted) |
|---|---|---|---|
| Scientist | `ScientistCooldown` | float s | 15 |
| Scientist | `ScientistBatteryCharge` | float s | 5 |
| Engineer | `EngineerCooldown` | float s | 30 |
| Engineer | `EngineerInVentMaxTime` | float s | 15 |
| Guardian Angel | `GuardianAngelCooldown` | float s | 60 |
| Guardian Angel | `ProtectionDurationSeconds` | float s | 10 |
| Guardian Angel | `ImpostorsCanSeeProtect` (bool) | bool | false |
| Shapeshifter | `ShapeshifterCooldown` | float s | 10 |
| Shapeshifter | `ShapeshifterDuration` | float s | 30 |
| Shapeshifter | `ShapeshifterLeaveSkin` (bool) | bool | false |
| Noisemaker | `NoisemakerAlertDuration` | float s | 10 |
| Noisemaker | `NoisemakerImpostorAlert` (bool) | bool | true |
| Phantom | `PhantomCooldown` / `PhantomDuration` | float s | 15 / 30 |
| Tracker | `TrackerCooldown` / `TrackerDuration` / `TrackerDelay` | float s | 15 / 30 / 1 |
| Detective | `DetectiveSuspectLimit` | float (stored as byte) | 3 |
| Viper | `ViperDissolveTime` | float s | 15 |
| Judge | `JudgeTaskRequirementPercentage` | float % | 50 |
| Spirit Guide | `SpiritGuideCooldownSeconds` | float s | 20 |
| Crewmate / Impostor | none (rate only; Impostor count is `NumImpostors`) | | |

Sources: dump `*RoleOptionsV12.cs` (fields, e.g. `ScientistRoleOptionsV12.ScientistCooldown/ScientistBatteryCharge`, `JudgeRoleOptionsV12.JudgeTaskRequirementPercentage`) for names and types; Impostor `Innersloth/GameOptions/RoleOptions/*.cs` for defaults. Reference defaults for newer roles (Viper, Judge, Spirit Guide, Detective) were taken from Impostor and could be stale. Ranges: unknown. Role behaviour in [05](05-roles-and-abilities.md).

## 5. Hide and Seek option table (HideNSeekGameOptionsV12)

Field offsets/types: Observed (`HideNSeekGameOptionsV12.cs`). Defaults: Reference (`Impostor:.../HideNSeekGameOptions.cs`), whose property names differ from the dump; mapping is shown.

| Dump field / enum | Reference name | Type | Default | Meaning |
|---|---|---|---|---|
| `MaxPlayers` | same | byte | 15 | Capacity |
| `NumImpostors` (private set) | same | int | 1 | Seekers |
| `ImpostorPlayerID` (Int32 102) | `SeekerPlayerId` | uint | 0xFFFFFFFF (random) | Fixed seeker |
| `PlayerSpeedMod`, `CrewLightMod`, `ImpostorLightMod` | same | float | 1.0 | As normal mode |
| `KillCooldown` (field present) | not serialised in Reference | float | unknown | Seeker kill cooldown (Inferred) |
| `NumCommonTasks`, `NumShortTasks`, `NumLongTasks` | same | int | 1 / 2 / 1 | Tasks reduce the hide timer |
| `GhostsDoTasks`, `KillDistance`, `Tag` | | | | As normal mode |
| `CrewmateVentUses` (Int32 101) | same | int | 1 | Vents per hider |
| `CrewmateTimeInVent` (Float 100) | same | float s | 3 | Max time in vent |
| `EscapeTime` (Float 102) | `HidingTime` | float s | 200 | Main hide-phase length |
| `FinalCountdownTime` / `FinalEscapeTime` (Float 101) | `FinalHideTime` | float s | 50 | Final-phase timer |
| `CrewmateFlashlightSize` (Float 105) | same | float | 0.35 | Hider vision cone |
| `ImpostorFlashlightSize` (Float 106) | same | float | 0.25 | Seeker vision cone |
| `useFlashlight` (Bool 6) | `UseFlashlight` | bool | true | Flashlight vision |
| `SeekerFinalMap` (Bool 8) | `FinalHideSeekMap` | bool | true | Seekers get a map in final phase |
| `SeekerFinalSpeed` (Float 103) | `FinalSeekerSpeed` | float | 1.2 | Seeker speed in final phase |
| `SeekerPings` (Bool 9) | `FinalHidePings` | bool | true | Pings hiders' positions in final phase |
| `MaxPingTime` (Float 104) | same | float s | 6 | Ping interval cap |
| `ShowCrewmateNames` (Bool 10) | `ShowNames` | bool | true | Name tags on hiders |

Constants (Observed `const` in `HideNSeekGameOptionsV12`): `VeryScaryMusicDistance=15`, `ScaryMusicDistance=55`, `ImpostorSpeedDifference=0.25` (seekers faster than hiders by this, Inferred), `CrewmateVentCooldown=1`, `ShowPingTime=2`, `CrewmateLeadTime=10`. Mode behaviour: [03](03-gameplay-core.md) section 8.

HnS wire order (Reference): specialMode, rulesPreset, maxPlayers, keywords(i32), map, speed, crewLight, impLight, commonTasks, longTasks, shortTasks, isDefaults, crewVentUses(i32), hidingTime, crewFlash, impFlash, useFlashlight, finalMap, finalHideTime, finalSeekerSpeed, finalPings, showNames, seekerId(u32), maxPingTime, crewVentTime, tag (v9+). No role collection is serialized.

## 6. How options reach gameplay

- `LogicOptions` exposes typed accessors used by game systems: `GetKillCooldown`, `GetKillDistance`, `GetPlayerSpeedMod(pc)`, `GetNumEmergencyMeetings`, `GetEmergencyCooldown`, `GetGhostsDoTasks`, `GetConfirmImpostor`, `GetAnonymousVotes`, `GetVisualTasks`, `GetTaskBarMode`, `GetShowCrewmateNames`, `GetAdjustedNumImpostors(playerCount)`, `GetRoleBool/Float/Int`, `GetEngineerCooldown/GetEngineerInVentTime`, and `LogicOptionsNormal.GetDiscussionTime/GetVotingTime` (Observed, `LogicOptions.cs`, `LogicOptionsNormal.cs`). `GetAdjustedNumImpostors` suggests the impostor count is clamped for small lobbies (Inferred).
- Sync: host edits -> `LogicOptions.SyncOptions` -> `RpcSyncSettings(byte[])`; clients `SetGameOptions`. Late joiners receive options inside the `GameManager`/logic component initial state (`LogicOptions.Serialize/Deserialize`).
- The settings UI is data-driven: `GameManager.AllGameSettingData: Dictionary<StringNames, BaseGameSetting>` and `GetSettingDataByName`; `GameOptionsMenu`, `RolesSettingsMenu`, `RoleOptionSetting`, `RulesCategory` build screens from these assets (Observed). A clone should do the same: one schema table drives UI, validation and serialization.

## 7. Suggested clone schema (Inferred)

Keep the option ids as stable integers, a version byte, and a single typed map per category. Start from section 3 defaults, then tune with playtesting. Mark unknown ranges as configurable constants rather than copying numbers from this document.

## 8. Unknowns

- Every slider min/max/step, `MaxImpostors`/`MinPlayers`/`RecommendedKillCooldown`/`RecommendedImpostors` tables, kill-distance floats, live-game defaults (Impostor may lag), `AvailableRoles` contents, and what exactly `RulesPresets` change.
- Whether Normal V12 uses `UseFlashlight` and `Roles` booleans in its wire format (not in the V12 field list; the Reference parser has no such fields), so they are likely preset-derived or HnS-only.
