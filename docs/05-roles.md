# 05 - Roles

> **Data update:** numeric values the code dump lacks (player speed 2.5, kill cooldown default 15, role cooldowns/durations, task MaxStep, map scale, exile timings) were later recovered from serialized assets: see [data/gameplay](data/gameplay/README.md) and [data/maps](data/maps/README.md) (Observed vent graphs with links, consoles with task types). Still unknown after that pass: reactor/O2 countdowns, door cooldowns, kill-distance floats, option presets.

Scope: role enum, role class model, every role, role options, role assignment, role RPCs.
Related docs: networking and RPC framing in `02-*`, gameplay loop in `03-*`, room/lobby options in `04-*`, client UI in `07-*`. This file does not duplicate them.

## 0. Conventions and evidence

| Tag | Meaning |
|---|---|
| Observed | Read directly from the Android 2026.9.29 Cpp2IL dump (types, fields, enum values, signatures; no method bodies) |
| Reference | From the Impostor community server (`refs/Impostor`), may be older than 2026.9.29 |
| Inferred | Deduced from names, field sets, and common-sense game behavior. Not confirmed. |

Dump root used in citations: `Assembly-CSharp/` = `DiffableCs/Assembly-CSharp/`. Because the dump has no method bodies, anything about numeric defaults, formulas, and exact behavior is Inferred unless stated. Prefab-serialized values (e.g. `CanVent` on a given role) are NOT in the dump; they live in Unity assets.

---

## 1. RoleTypes enum (Observed)

Source: `Assembly-CSharp/Amongus/GameOptions/RoleTypes.cs`

| Value | Name | Kind (Inferred from class parentage) |
|---|---|---|
| 0 | Crewmate | base crew role |
| 1 | Impostor | base impostor role |
| 2 | Scientist | crew, active ability (vitals) |
| 3 | Engineer | crew, active ability (vent) |
| 4 | GuardianAngel | crew ghost role |
| 5 | Shapeshifter | impostor, active ability |
| 6 | CrewmateGhost | base crew ghost role |
| 7 | ImpostorGhost | base impostor ghost role |
| 8 | Noisemaker | crew, passive (alert on death) |
| 9 | Phantom | impostor, active ability (invisibility) |
| 10 | Tracker | crew, active ability |
| 12 | Detective | crew, active ability + notes |
| 18 | Viper | impostor, special kill |
| 19 | Judge | crew, meeting ability |
| 21 | SpiritGuide | crew ghost role |

Notes:
- Values 11, 13-17, 20 are absent from the dump. Likely removed, unreleased, or enum gaps for other modes (Unknown). Do not assume them reserved; just do not reuse them if wire compatibility with the original ever mattered (it should not for a clone).
- Ghost-ness is a role, not a flag: a dead player's role is *replaced* by a ghost role (see 5.3). Observed: `RoleManager.IsGhostRole(RoleTypes)`, `RoleManager.AssignRoleOnDeath` (`Assembly-CSharp/RoleManager.cs`).
- `RoleTypes` is in namespace `AmongUs.GameOptions`; `RoleTeamTypes` is global.

### RoleTeamTypes (Observed, `Assembly-CSharp/RoleTeamTypes.cs`)

| Value | Name |
|---|---|
| 0 | Crewmate |
| 1 | Impostor |

Only two teams exist at the team enum level. No neutral team. Hide and Seek reuses the same teams (Inferred, see 6).

---

## 2. RoleBehaviour base class (Observed)

Source: `Assembly-CSharp/RoleBehaviour.cs`. It is an abstract MonoBehaviour. One prefab instance per role lives in `RoleManager.AllRoles`; each player gets their own instance attached when assigned (Inferred from `Initialize(PlayerControl)` / `Deinitialize` and the protected `Player` field).

### 2.1 Data fields

| Field | Type | Meaning |
|---|---|---|
| Role | RoleTypes | Which role this is |
| StringName, BlurbName, BlurbNameMed, BlurbNameLong | StringNames | Localization keys for name and 3 lengths of blurb |
| RoleIconSolid/White/Color, RoleScreenshot | Sprite | UI art (how-to-play, role panels) |
| NameColor | Color | Name tag color. `TeamColor` getter derives from it (Inferred) |
| TasksCountTowardProgress | bool | Whether this player's tasks count toward the global task bar |
| CanUseKillButton | bool | Shows/uses kill button |
| CanBeKilled | bool | Can be murdered |
| CanVent | bool | Can enter vents |
| AffectedByLightAffectors | bool | Reduced vision by lights sabotage/affectors |
| MaxCount | int | Hard cap of players with this role |
| TeamType | RoleTeamTypes | Crewmate or Impostor |
| Ability | AbilityButtonSettings | Primary ability button config (text, font material, `AbilityTypes`) |
| SecondaryAbility | AbilityButtonSettings | Second button (Detective, SpiritGuide use it, see sections) |
| DefaultGhostRole | RoleTypes | Ghost role used on death (Crewmate -> CrewmateGhost etc., Inferred) |
| UseSound, IntroSound | AudioClip | Ability use SFX, intro cutscene sting |
| AllGameSettings | List<BaseGameSetting> | The role's settings UI definitions |
| CustomKillAnimations | OverlayKillAnimation[] | Role-specific kill cutscenes (Viper uses KillAnimSpecialSetup) |

`AbilityTypes` (Observed, grep `enum AbilityTypes`): GameOnly=0, MeetingOnly=1, Both=2, Limited=3. Controls when the ability button is shown (Inferred: Limited = finite uses).

### 2.2 Computed properties
`Blurb`, `BlurbMed`, `BlurbLong`, `NiceName`, `TeamColor`, `IsImpostor` (TeamType == Impostor, Inferred), `IsSimpleRole` (true for plain Crewmate/Impostor, Inferred), `IsDead` (abstract: ghost roles return true), `IsAffectedByComms`, `CommsSabotaged`.

### 2.3 Virtual hooks (the extension surface)
All Observed as signatures only.

| Hook | Purpose (Inferred) |
|---|---|
| Initialize(PlayerControl) / Deinitialize(PlayerControl) | Attach/detach to a player; ability button created via private `InitializeAbilityButton` and `InitializeSecondaryAbilityButton` |
| OnRoleSet() | After server confirms role |
| AdjustTasks(PlayerControl), SpawnTaskHeader, AppendTaskHint(StringBuilder) | Customize task list header/hint text (Impostor shows "fake tasks") |
| CanUse(IUsable) | Gate interaction with consoles/vents (e.g. Impostor cannot use task consoles; Phantom restricted while invisible) |
| UseAbility(), UseSecondaryAbility() | Ability button press |
| SetCooldown() | Reset ability cooldown (called after meetings via `OnVotingComplete`) |
| FindClosestTarget(), IsValidTarget(NetworkedPlayerInfo), SetPlayerTarget(PlayerControl), SetUsableTarget(IUsable), GetAbilityDistance() | Targeting for kill/ability buttons |
| FindClosestBody(), GetBodiesInAbilityRangeSorted, GetPlayersInAbilityRangeSorted | Range queries for report/ability |
| OnMeetingStart(), OnVotingComplete(), OnExileComplete() | Meeting lifecycle |
| OnDeath(DeathReason) | Death callback. `DeathReason`: Exile=0, Kill=1, Disconnect=2 (Observed, `Assembly-CSharp/DeathReason.cs`) |
| DidWin(GameOverReason) | Per-role win evaluation |
| KillAnimSpecialSetup(DeadBody, killer, victim) | Per-role kill animation hook |
| HandleRoleRpc(byte callId, MessageReader) | Role-owned RPC channel (Phantom uses it) |

Design takeaway (Inferred): a clone needs a role interface with: static flags (team, can-kill, can-vent, can-be-killed, counts-tasks), an optional ability slot with cooldown/duration/uses, targeting rules, and meeting hooks.

---

## 3. Role classes (Observed unless tagged)

Class tree (Observed from `Assembly-CSharp/*Role.cs`):

```
RoleBehaviour
  CrewmateRole -> NoisemakerRole, TrackerRole, DetectiveRole, JudgeRole
  EngineerRole, ScientistRole      (direct children of RoleBehaviour)
  ImpostorRole -> ShapeshifterRole, PhantomRole, ViperRole
  CrewmateGhostRole -> GuardianAngelRole, SpiritGuideRole
  ImpostorGhostRole
```
Note: `EngineerRole` and `ScientistRole` derive directly from `RoleBehaviour`, not `CrewmateRole` (`Assembly-CSharp/EngineerRole.cs`, `ScientistRole.cs`). The other crew specials derive from `CrewmateRole`.

Per-role numeric flags (CanVent etc.) are prefab data, not in the dump. The flag values below are Inferred from known gameplay and Impostor.

### 3.1 CrewmateRole (value 0)
- Observed: overrides `IsDead`, `CanUse`, `DidWin`, `AppendTaskHint` (`CrewmateRole.cs`). No fields.
- Inferred: TeamType Crewmate, TasksCountTowardProgress true, CanUseKillButton false, CanVent false, CanBeKilled true.
- Win: tasks complete or all impostors out (see `03-*`).

### 3.2 ImpostorRole (value 1)
- Observed: overrides `FindClosestTarget`, `IsValidTarget`, `CanUse`, `DidWin`, `Deinitialize`, `SpawnTaskHeader` (`ImpostorRole.cs`). `Deinitialize` has a PlayerTask predicate lambda (clears fake tasks, Inferred).
- Inferred: Impostor team; CanUseKillButton true; CanVent true; TasksCountTowardProgress false; gets sabotage and vent buttons.
- Kill: server-validated through `CheckMurder` then `MurderPlayer` RPC (Observed in `RpcCalls`, section 7).

### 3.3 EngineerRole (value 3)
- Observed fields: `currentTarget: Vent`, `cooldownSecondsRemaining`, `inVentTimeRemaining`, `usesRemaining: int`, `isExitVentQueued` (`EngineerRole.cs`).
- Ability: crew member may use vents for a limited time per charge, with cooldown (Inferred from fields + options).
- Options (Observed `EngineerRoleOptionsV12`): `EngineerCooldown` (float), `EngineerInVentMaxTime` (float).
- RPCs: reuses vent RPCs (`EnterVent` 19, `ExitVent` 20, `BootFromVent` 34) and `VentilationSystem` ops (see `06-tasks-and-maps.md` section 5). Forced exit when time ends (Inferred).
- UI: ability button, vent button.

### 3.4 ScientistRole (value 2)
- Observed fields: `VitalsPrefab: VitalsMinigame`, `minigame`, `currentCharge`, `currentCooldown`; methods `Recharge()`, `RefreshAbilityButton` (`ScientistRole.cs`).
- Ability: remote vitals panel with a battery that drains while open and recharges (Inferred from `ScientistBatteryCharge` option + `Recharge`).
- Options (Observed `ScientistRoleOptionsV12`): `ScientistCooldown`, `ScientistBatteryCharge`.
- Uses `VitalsMinigame` (list of player status panels with `vitalDisconnected` map, `VitalsMinigame.cs`).

### 3.5 GuardianAngelRole (value 4, ghost)
- Parent `CrewmateGhostRole`. Fields: `currentTarget: PlayerControl`, `cooldownSecondsRemaining` (`GuardianAngelRole.cs`).
- Ability: a dead crewmate protects a living player for a duration; impostor kill attempt on a protected player fails (Inferred from `ProtectPlayer` / `CheckProtect` RPCs and options).
- Options (Observed `GuardianAngelRoleOptionsV12`): `GuardianAngelCooldown`, `ProtectionDurationSeconds`, `ImpostorsCanSeeProtect` (bool).
- RPCs (Observed `RpcCalls`): `CheckProtect`=48 (client asks), `ProtectPlayer`=45 (server broadcast, `PlayerControl.ProtectPlayer(PlayerControl target, int colorId)`). `RoleManager.protectAnim` and `protectLoopAnim` are the visual effects (`RoleManager.cs`).
- Helper methods on PlayerControl: `TurnOnProtection(bool visible, int colorId, int guardianPlayerId)`, `RemoveProtection` (Observed). `visible` plus `ImpostorsCanSeeProtect` imply the shield may be hidden from impostors (Inferred).

### 3.6 ShapeshifterRole (value 5)
- Parent `ImpostorRole`. Fields: `EvidencePrefab: ShapeshifterEvidence`, `EvidenceOffset`, `ShapeshifterMenu: ShapeshifterMinigame`, `cooldownSecondsRemaining`, `durationSecondsRemaining`. Methods `RevertPlayerToNormal(shouldAnimate, setCooldown)`, `SetEvidence()` (`ShapeshifterRole.cs`).
- Ability: pick a living player from a menu and copy their appearance for a duration. `SetEvidence` plus option `ShapeshifterLeaveSkin` (Observed) imply an optional evidence marker is left at the shift location (Inferred).
- Options (Observed `ShapeshifterRoleOptionsV12`): `ShapeshifterLeaveSkin` (bool), `ShapeshifterCooldown`, `ShapeshifterDuration`.
- RPCs (Observed): `CheckShapeshift`=55 (client request), `Shapeshift`=46 (server broadcast), `RejectShapeshift`=56. PlayerControl: `CmdCheckShapeshift(target, shouldAnimate)`, `CmdCheckRevertShapeshift(shouldAnimate)`, `waitingForShapeshiftResponse` flag, `ShapeshiftedPlayerID()`.
- UI: `ShapeshifterMinigame` fields `bodies`, `PanelPrefab: ShapeshifterPanel`, grid offsets (`XStart,YStart,XOffset,YOffset`) (`ShapeshifterMinigame.cs`). `RoleManager.shapeshiftAnim`.
- Reverting to own look = shapeshift to self (`CmdCheckRevertShapeshift`, Inferred).
- Detective interacts: `DetectiveSuspect.shapeshifterPlayerInfo` (Observed) - shapeshifters can be confused with their target in suspect lists.

### 3.7 PhantomRole (value 9)
- Parent `ImpostorRole`. Fields: `cooldownSecondsRemaining`, `durationSecondsRemaining`, `isInvisible`, `fading`, `serverApproved`, appear sound; constants `SFX_VANISH_NAME="PhantomVanish"`, `SFX_APPEAR_NAME="PhantomAppear"` (`PhantomRole.cs`).
- Ability: become invisible for a duration, then reappear (cooldown). Overrides `CanUse`/`IsValidTarget` so interaction while invisible is limited (Inferred). Invisible phantom can sit on a vent (`VentilationSystem.IsInvisiblePhantomOnVent`, Observed).
- Options (Observed `PhantomRoleOptionsV12`): `PhantomCooldown`, `PhantomDuration`.
- RPCs (Observed `RpcCalls`): `CheckVanish`=62, `StartVanish`=63, `CheckAppear`=64, `StartAppear`=65. PlayerControl: `CmdCheckVanish(float maxDuration)`, `CmdCheckAppear(bool shouldAnimate)`, `RpcVanish`, `RpcAppear`, `HandleServerVanish`, `HandleServerAppear`, `ForcePhantomVisible`, `PhantomFadeActive`. `serverApproved` shows the same client-request / server-approve pattern as Shapeshifter (Inferred).
- Role uses `HandleRoleRpc` and coroutine `CoAppearClients(callback)` for other-client fade-in (Observed).
- Animations: `RoleManager.vanish_ChargeAnim`, `vanish_PoofAnim`, `appear_PoofAnim`.
- Phantom remains killable? Unknown (flag in prefab).

### 3.8 NoisemakerRole (value 8)
- Parent `CrewmateRole`. Fields: `deathSound`, `deathArrowPrefab`, `deathArrow: NoisemakerArrow`; constants `FALLOFF_START_DISTANCE=7`, `FALLOFF_MAX_DISTANCE=50`, `MIN_VOL_ALERT=0.5`, `VIBRATION_INTENSITY=1`, `VIBRATION_DURATION=1.2` (`NoisemakerRole.cs`).
- Behavior (Inferred): when killed, plays a loud alert audible with distance falloff and (optionally) points an arrow to the body for a duration. `KilledByViper()` check (Observed) suggests the alert is suppressed when the body dissolves (Viper).
- Options (Observed `NoisemakerRoleOptionsV12`): `NoisemakerImpostorAlert` (bool: alert reaches impostors too, Inferred), `NoisemakerAlertDuration`.
- Method: `NotifyOfDeath()`, `OnDeath(DeathReason)`.
- No active ability button.

### 3.9 TrackerRole (value 10)
- Parent `CrewmateRole`. Fields: `currentTarget`, `cooldownSecondsRemaining`, `durationSecondsRemaining`, `delaySecondsRemaining`, `pauseTrackingTime`, `isTrackingActive`, `cancelSound`; const `DelaySeconds=1` (`TrackerRole.cs`).
- Ability: pick a nearby player; for a duration their position is shown to the tracker (pinged on map). Tracking is cancelled on tracker death/meeting. Target also is notified? Unknown. `PlayerControl.StartPlayerTracking(player, colorID)`, `CancelPlayerTracking`, `isTrackingPlayer`, `DisableCurrentTrackers` (Observed). `MapBehaviour.SetTrackedHerePointColor`, `trackedPreMeetingPosition` show the position displayed on the map (Observed `MapBehaviour.cs`).
- Options (Observed `TrackerRoleOptionsV12`): `TrackerCooldown`, `TrackerDuration`, `TrackerDelay` (delay between pings, Inferred).
- RPC: none named in `RpcCalls`; tracking appears to be client-side after the target is picked, or goes via `HandleRoleRpc` (Unknown).

### 3.10 DetectiveRole (value 12)
- Parent `CrewmateRole`. Has primary and secondary ability (`UseAbility`, `UseSecondaryAbility`). Fields: `notesPrefab`, `inspectionGraphic`, `abilityInfoPrefab`, `interrogateSFX`, `notesPageInfos: List<DetectiveNotesPageInfo>`, `notesMinigame: DetectiveNotesMinigame`, `deadPlayers`, `abilityInfo`, `meetingAbilityInfo`, `currentTarget`, `interrogateCooldown`, five per-map `*ShipLocationsPrefab` fields (Skeld, Mira, Polus, Airship, Fungle) (`DetectiveRole.cs`).
- Behavior (Inferred): when a body is found the detective gets a notes page for that victim listing suspects and where each player was at the time of death (`DetectiveDeadBodyInfo.PlayerLocationsAtDeath: Dictionary<byte,string>`, `GetPlayerLocation(playerId, victimId)`). Detective can interrogate a nearby player (`Interrogate`, `interrogateCooldown`), and the suspect list size is capped (`DetectiveSuspectLimit`).
- Notes UI: `DetectiveNotesMinigame` with victim name/location, preposition popup, impostor-type popup, `suspectContainers` (`DetectiveNotesMinigame.cs`). The map overlay `MapBehaviour.detectiveMapOpen` plus `CreateMapLocations`, `CreatePlayerLocations` shows who was where per map.
- Options (Observed `DetectiveRoleOptionsV12`): `DetectiveSuspectLimit` (float).
- RPC: none dedicated seen.
- Server must store a room-history snapshot at the time of each kill to support this (Inferred design requirement).

### 3.11 JudgeRole (value 19)
- Parent `CrewmateRole`. Properties: `OverruledPlayerId`, `OverruleNonce` (ushort, consts min 1 / max 65535), `HasAlreadyOverruledThisMeeting`, `HasAnOverruleUse`; fields `taskRequirementProportion`, `initialTaskCount`; methods `TryOverrule(PlayerId)`, `ConsumeOverruleVotesUsage`, `ClearOverrule`, `IsBlockedByTasks`, `TasksRemaining`, coroutine `CoSetInitialTaskCount` (`JudgeRole.cs`).
- Behavior (Inferred): in a meeting, the judge may overrule the vote to force a target ejected/skipped, once per meeting, unlocked only after completing a percentage of their tasks. The nonce lets the server dedupe retries.
- Options (Observed `JudgeRoleOptionsV12`): `JudgeTaskRequirementPercentage`.
- RPC (Observed): `QueueOverruleVotes`=66.
- Tag: this role is not in the public Impostor reference.

### 3.12 ViperRole (value 18)
- Parent `ImpostorRole`. Fields: `killSprite`, property `dissolveMaxTime` (`ViperRole.cs`). Overrides `Initialize`, `KillAnimSpecialSetup`.
- Behavior (Inferred): kills leave a `ViperDeadBody` that dissolves over time (`ViperDeadBody`: `dissolveAnims`, `dissolveCurrentTime`, `maxDissolveTime`, `victimDissolving`, `dissolveStage`, `myKiller`). After dissolution the body can no longer be reported. `NoisemakerRole.KilledByViper` references it.
- Options (Observed `ViperRoleOptionsV12`): `ViperDissolveTime`.
- RPC: standard kill.

### 3.13 CrewmateGhostRole / ImpostorGhostRole (values 6, 7)
- Observed: both have `HauntMenu: Minigame` and `IsDead = true`; override `CanUse`, `DidWin`, `SpawnTaskHeader`, `UseAbility`. `ImpostorGhostRole.WasManuallyPicked` (bool, Observed; true when the host/dev chose it vs random).
- `HauntMenuMinigame` (`HauntMenuMinigame.cs`): ghost picks a player to "haunt"; filters enum `HauntFilters` None=0, Impostor=1, Crewmate=2, Ghost=3; `HauntTarget`, `players[]`. Inferred: ghosts can spectate by cycling players, with a team filter for ghost-only views.
- Ghost tasks: option `GhostsDoTasks` (Observed `NormalGameOptionsV12`), so crew ghosts may still do tasks.

### 3.14 SpiritGuideRole (value 21, ghost)
- Parent `CrewmateGhostRole`. Fields: `spiritGuidePanel`, `spiritGuideCamera`, `possibleSprites: List<Sprite>`, `imageButtons`, `selectedImageButtons`, `currentTarget`, `hauntTarget`, `cooldownSecondsRemaining`, `sendingImage`, const `MINIMUM_COOLDOWN_SECONDS=10` (`SpiritGuideRole.cs`). Methods: `OpenSpiritGuidePanel`, `GenerateRandomImages`, `RefreshImages`, `SelectImage`, `SendMessageToSelectedPlayer`, coroutines `CoSendPhotoAnim`, `CoAnimateSendImage`.
- Behavior (Inferred): a dead crewmate opens a panel showing a random set of icon "photos" (a camera and a horse - `horseCameraOffsetPos`), selects some, and sends them as a message to a living player. This is a restricted "ghost hint" channel in place of free chat.
- Options (Observed `SpiritGuideRoleOptionsV12`): `SpiritGuideCooldownSeconds`.
- RPCs (Observed): `SpiritGuideMessage`=67; `ShipStatus.SendSpiritGuideMessage(byte[] spriteIndices, ClientId targetId)` - message is an array of sprite indices to one client. UI `SpiritGuidePanelMinigame`.

---

## 4. Role summary matrix

| Role | Team | Kill | Vent | Tasks count | Active ability | Options (V12) | Source of flags |
|---|---|---|---|---|---|---|---|
| Crewmate | Crew | no | no | yes | none | - | Inferred |
| Impostor | Imp | yes | yes | no | sabotage, vent | - | Inferred |
| Engineer | Crew | no | limited | yes | timed vent use | Cooldown, InVentMaxTime | Observed fields, Inferred flags |
| Scientist | Crew | no | no | yes | remote vitals, battery | Cooldown, BatteryCharge | Observed fields, Inferred flags |
| Noisemaker | Crew | no | no | yes | passive death alert | ImpostorAlert, AlertDuration | Observed |
| Tracker | Crew | no | no | yes | track a player | Cooldown, Duration, Delay | Observed |
| Detective | Crew | no | no | yes | interrogate, notes | SuspectLimit | Observed |
| Judge | Crew | no | no | yes | overrule vote | TaskRequirementPercentage | Observed |
| Shapeshifter | Imp | yes | yes | no | morph | LeaveSkin, Cooldown, Duration | Observed |
| Phantom | Imp | yes (Inferred) | yes | no | invisibility | Cooldown, Duration | Observed |
| Viper | Imp | yes | yes | no | dissolving kills | DissolveTime | Observed |
| CrewmateGhost | Crew | no | no | optional | haunt menu | GhostsDoTasks (global) | Observed |
| ImpostorGhost | Imp | no | no | no | haunt menu | - | Observed |
| GuardianAngel | Crew ghost | no | no | - | protect | Cooldown, ProtectionDuration, ImpostorsCanSeeProtect | Observed |
| SpiritGuide | Crew ghost | no | no | - | send picture hint | CooldownSeconds | Observed |

---

## 5. Role options and assignment

### 5.1 Option model (Observed)
Sources: `Assembly-CSharp/Amongus/GameOptions/*.cs`

- `IRoleOptions` (interface): each role's settings class returns its `RoleTypes Type`.
- `RoleDataV12` = `{ RoleTypes Type; IRoleOptions RoleOptions; RoleRate Rate }`.
- `RoleRate` (struct) = `{ int MaxCount; int Chance }`. MaxCount = how many players may get the role; Chance = percent probability (0 to 100, Inferred) a given slot rolls it.
- `RoleOptionsCollectionV12 : IRoleOptionsCollection`: `AddOrUpdateRole`, `RemoveRole`, `AnyRolesEnabled()`, `GetNumPerGame(RoleTypes)`, `GetChancePerGame(RoleTypes)`, `TryGetRoleOptions<T>(type, out T)`, `TryGetRoleRates(type, out RoleRate)`, `UpdateFrom`.
- `NormalGameOptionsV12` holds a `roleOptions` field of that collection, plus global values (see `04-*`): `NumImpostors`, `KillCooldown`, `KillDistance`, `GhostsDoTasks`, `NumCommonTasks/NumShortTasks/NumLongTasks`, `TaskBarMode`, etc. Also static `AvailableRoles: RoleTypes[]` (contents not in dump) listing which roles the settings screen offers.
- Versioning: each role has option classes V07 to V12 (e.g. `EngineerRoleOptionsV07`...`V12`); newer roles appear later (Noisemaker/Phantom/Tracker from V08, Detective/Viper from V10, Judge/SpiritGuide from V11). Observed by file presence: Detective V10-V12, Viper V10-V12, Judge V11-V12, SpiritGuide V11-V12, Noisemaker/Phantom/Tracker V08-V12. Guardian/Engineer/Scientist/Shapeshifter from V07. For a clone: one options schema with a version number is enough.

Per-role option fields (V12, Observed):

| Role | Fields |
|---|---|
| Engineer | EngineerCooldown, EngineerInVentMaxTime |
| Scientist | ScientistCooldown, ScientistBatteryCharge |
| GuardianAngel | GuardianAngelCooldown, ProtectionDurationSeconds, ImpostorsCanSeeProtect |
| Shapeshifter | ShapeshifterLeaveSkin, ShapeshifterCooldown, ShapeshifterDuration |
| Phantom | PhantomCooldown, PhantomDuration |
| Noisemaker | NoisemakerImpostorAlert, NoisemakerAlertDuration |
| Tracker | TrackerCooldown, TrackerDuration, TrackerDelay |
| Detective | DetectiveSuspectLimit |
| Viper | ViperDissolveTime |
| Judge | JudgeTaskRequirementPercentage |
| SpiritGuide | SpiritGuideCooldownSeconds |

Default values are not in the dump. Reference defaults (`Impostor:src/Impostor.Api/Innersloth/GameOptions/RoleOptions/*.cs`, seconds unless noted):

| Role | Defaults (Reference) |
|---|---|
| Engineer | Cooldown 30, InVentMaxTime 15 |
| Scientist | Cooldown 15, BatteryCharge 5 |
| GuardianAngel | Cooldown 60, ProtectionDuration 10 |
| Shapeshifter | Cooldown 10, Duration 30 |
| Phantom | Cooldown 15, Duration 30 |
| Tracker | Cooldown 15, Duration 30, Delay 1 |
| Noisemaker | AlertDuration 10 |
| Detective | SuspectLimit 3 |
| Viper | DissolveTime 15 |
| Judge | TaskRequirementPercentage 50 (float) |
| SpiritGuide | Cooldown 20 (float) |

Wire format (Reference, `Impostor:.../RoleOptionsCollection.cs`, `RoleRate.cs`): packed-int count, then per role: int16 role type, RoleRate = 2 bytes (MaxCount, Chance), then a sub-message holding the role's option fields (mostly single bytes in the reference; the 2026 dump types them as float). The reference supports all 11 non-base roles above, so the same role set existed in a recent version. Network details belong in `02-*`.

Settings UI classes (Observed, for `07-*`): `RolesSettingsMenu`, `RoleOptionSetting`, `RoleSettingsTabButton`, `AdvancedRoleViewPanel`, `ViewSettingsInfoPanelRoleVariant`, `CategoryHeaderRoleVariant`, `CategoryHeaderEditRole`, `HowToPlayRolePage/RoleButton/SceneRoleTitle`, `MatchInfoRolePanel`, `RoleStat`, `RoleEffectAnimation`. Quick chat has role rules: `QuickChatRoleRules`, `QuickChatRolePhraseCollector` (Observed).

### 5.2 Assignment at game start (Observed signatures, Inferred algorithm)
Sources: `RoleManager.cs`, `LogicRoleSelection.cs`, `LogicRoleSelectionNormal.cs`, `LogicRoleSelectionHnS.cs`.

- `RoleManager.SelectRoles()` runs on the host at game start. Its lambdas filter `ClientData` (connected, not disconnected, not spectator-like, Inferred) and order them randomly (`<SelectRoles>b__11_3` returns an Int32 key).
- It delegates to `LogicRoleSelection.AssignRolesForTeam(List<NetworkedPlayerInfo> players, IGameOptions opts, RoleTeamTypes team, int teamMax, RoleTypes? defaultRole)`, called once per team (impostors first, Inferred). The concrete `LogicRoleSelectionNormal` has `AssignRolesFromList(players, teamMax, List<RoleTypes> roleList, ref rolesAssigned)`. Its helper type is `RoleManager.RoleAssignmentData { Role, Count, Chance }`, built from each `RoleBehaviour` where `team == teamType` and `roleOptions` rate is set.
- `teamMax` = number of impostors for the impostor team (from `NumImpostors`, clamped by `MaxImpostors` per player count, Inferred from static tables in options) and the remaining player count for crew. `defaultRole` = Crewmate/Impostor fallback for unassigned slots.
- Inferred algorithm (typical): (1) pick the impostor players randomly; (2) for each special role with `Chance == 100` assign `MaxCount` first, then roll roles with `Chance < 100`, each success consuming a random unassigned player of that team until `teamMax` is hit; (3) leftover players get `defaultRole`. Reference unavailable; Impostor does not implement role selection (host-client does, the server only relays `SetRole`).
- Result broadcast: `RoleManager.SetRole(PlayerControl, RoleTypes)` creates the `RoleBehaviour` from `AllRoles` and the sequence goes through RPC `SetRole`=44 (and legacy `SetInfected`=3), message type `RpcSetRoleMessage` (Observed in `Amongus/InnerNet/GameDataMessages/RpcSetRoleMessage.cs`). The Impostor reference only relays these; it does not run role selection (Reference).
- Debug path: `DebugRoleAssignments(players, ref numImpostors)`.
- Hide and Seek: `LogicRoleSelectionHnS.PickSeekersRoundRobin(players, teamMax)` (Observed) - seekers rotate across rounds instead of random; uses `HideAndSeekManager`.
- `MaxCount` on a role prefab and `RoleRate.MaxCount` both cap counts (Inferred: rate cap wins when lower).

### 5.3 Role change on death (Observed signatures)
- `RoleManager.AssignRoleOnDeath(PlayerControl player, bool specialRolesAllowed)` -> `LogicRoleSelection.OnPlayerDeath(PlayerControl, bool assignGhostRole)`.
- `TryAssignSpecialGhostRoles(player, impostorRoles)` uses `SpecialCrewmateGhostRoles()` and `SpecialImpostorGhostRoles()` lists and the `GhostRoles` HashSet. Inferred: if a special ghost role (GuardianAngel or SpiritGuide for crew; none or custom for impostor) is enabled with remaining quota and `specialRolesAllowed` (false e.g. when ejected? unknown), the dying player gets it; otherwise `DefaultGhostRole` (CrewmateGhost/ImpostorGhost).
- `ImpostorGhostRole.WasManuallyPicked` is set when the host picks it (Observed).

### 5.4 Role-specific game over considerations
- `RoleBehaviour.DidWin(GameOverReason)` is per-role, allowing special winners (Inferred); `GameOverReason` enum is documented in `03-*`.
- Tasks: `TasksCountTowardProgress` false for impostors; Judge unlocks after a percentage of tasks.

---

## 6. Hide and Seek (Observed, brief)
`GameModes`: None=0, Normal=1, HideNSeek=2, NormalFools=3, SeekFools=4 (`Amongus/GameOptions/GameModes.cs`). Hide and Seek has its own role logic `LogicRoleSelectionHnS` and options `HideNSeekGameOptionsV07..V12`. April Fools modes map to `NormalFools` / `SeekFools` (Inferred; Impostor also ships an `April` map folder, mirrored/flipped Skeld, Reference). Full detail in `03-*` / `04-*`.

---

## 7. Role-related RPC index

All Observed from `Assembly-CSharp/RpcCalls.cs`. Framing in `02-*`.

| RPC | Id | Role relation | Direction (Inferred) |
|---|---|---|---|
| SetInfected | 3 | legacy impostor assignment | host -> all |
| SetRole | 44 | assign role (`RpcSetRoleMessage`) | host -> all |
| MurderPlayer | 12 | kill result | server/host -> all |
| CheckMurder | 47 | kill request | client -> host |
| ProtectPlayer | 45 | apply guardian shield | host -> all |
| CheckProtect | 48 | request protect | client -> host |
| Shapeshift | 46 | morph broadcast | host -> all |
| CheckShapeshift | 55 | morph request | client -> host |
| RejectShapeshift | 56 | refuse request | host -> client |
| CheckVanish | 62 | phantom vanish request | client -> host |
| StartVanish | 63 | vanish begins | host -> all |
| CheckAppear | 64 | reappear request | client -> host |
| StartAppear | 65 | reappear begins | host -> all |
| QueueOverruleVotes | 66 | judge overrule | client -> host |
| SpiritGuideMessage | 67 | spirit guide hint | client -> host -> target |
| EnterVent / ExitVent / BootFromVent | 19 / 20 / 34 | vent use (Engineer, Impostor) | see `06` |
| SetTasks / CompleteTask | 29 / 1 | tasks | see `06` |

Pattern worth copying (Inferred from naming): "Check*" = untrusted client request, authoritative host validates, then emits the effect RPC (or `RejectShapeshift`). This keeps cheating down and resolves races (e.g. kill vs protect). Impostor Reference confirms `CheckMurder`-style events exist server-side (`Impostor:src/Impostor.Api/Events/Game/Player/IPlayerCheckMurderEvent.cs`).

---

## 8. Unknowns / to verify
- Prefab flag values (CanVent, CanBeKilled, MaxCount, TasksCountTowardProgress per role).
- Default option values and valid ranges (not in dump).
- Exact assignment algorithm and ordering (bodies stripped).
- `AvailableRoles` array contents; which ghost roles are "special".
- Tracker RPC (if any) and exact tracker visibility rules.
- Role enum gaps (11, 13-17, 20).
- Whether Phantom can be killed while invisible; Engineer vent time reset rules; Detective data replication.
- Win conditions unique to roles (none seen in `DidWin` signatures; bodies unavailable).
