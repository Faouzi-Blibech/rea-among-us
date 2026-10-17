# Logic: Kill, targeting, roles and abilities

Behaviour spec derived from the decompiled Among Us mobile build 2026.9.29 (Unity IL2CPP, arm64). It covers the kill pipeline (client request, host validation, per-client execution), target selection, kill cooldown, Guardian Angel protection, death and ghost-role assignment, the `RoleBehaviour` base class, every concrete role class, and the vent/console usability rules that depend on role.

Source tags used below: **Observed (decompiled)** = read from a function body (cited `Class.Method @ rva`); **Observed (dump)** = signatures, fields, consts, enums; **Observed (binary)** = a constant read directly from `libil2cpp.so` or `global-metadata.dat`; **Inferred** = interpretation, reason given. Several functions had no decompiled body; for those the pseudo-IL listing was used and is tagged **Observed (IL listing)**. Role/team/option values from serialized assets (`docs/data/gameplay/roles.json`) are tagged **Observed (assets)**.

Out of scope here (other specs): role assignment at game start (`RoleManager.SelectRoles`), meeting/voting flow (including the Judge's effect on votes), sabotage systems, task logic.

## 1. Key numbers

| Item | Value | Evidence |
|---|---|---|
| Kill distance, index 0 / 1 / 2 (Short / Medium / Long) | 1.0 / 1.8 / 2.5 world units | Observed (binary): 3-float blob in `global-metadata.dat` used to fill `NormalGameOptionsV12.KillDistances`; the blob-to-field link is Inferred from size and content |
| Default kill distance index | 1 (Medium, 1.8) | Observed (decompiled) `NormalGameOptionsV12..ctor @ 0x238de40` default words |
| Distance lookup | `KillDistances[clamp(KillDistance, 0, len-1)]` | Observed (decompiled) `LogicOptions.GetKillDistance @ 0x20d3bf0` |
| Ability range for every targeted role | = kill distance (Detective overrides: 0.8) | Observed (decompiled) `RoleBehaviour.GetAbilityDistance @ 0x221a444` (slot arithmetic, see 4.1), Detective: Observed (binary) |
| Default kill cooldown | 15.0 s (option `KillCooldown`=1; missing option falls back to 1.0) | Observed (binary) default word; fallback Observed (IL listing) `LogicOptions.GetKillCooldown` |
| Kill cooldown at game start / role init | 10.0 s, clamped to `KillCooldown` | Observed (decompiled) `RoleBehaviour.Initialize @ 0x220ed40`; Observed (IL listing) `HudManager.CoShowIntro` |
| Kill cooldown after a meeting | full `KillCooldown` | Observed (IL listing) `ExileController` |
| Kill cooldown after a successful kill | full `KillCooldown` (killer's client) | Observed (decompiled) `PlayerControl.MurderPlayer @ 0x21d8da0` |
| Kill cooldown after a blocked kill (Guardian Angel shield) | 0.5 x `KillCooldown` | Observed (decompiled) same function |
| Vent use radius | 0.75 (centre of player collider to vent) | Observed (decompiled) `Vent.CanUse @ 0x2257f78` |
| Door console use radius | 1.0 (`DoorConsole`); `OpenDoorConsole.usableDisance` field | Observed (decompiled) `DoorConsole.CanUse @ 0x2248494` |
| Report distance (prefab) | 3.0 (`PlayerControl.MaxReportDistance`) | Observed (assets) |
| Zero-distance direction epsilon in range query | 1e-5 | Observed (binary) |
| Host validation of kill | no distance check, no cooldown check | Observed (decompiled) `CheckMurder @ 0x21d8194` |
| Ghost-role roll | `random(0..100) < chance` assigns | Observed (decompiled) `RoleManager.TryAssignSpecialGhostRoles @ 0x221bc9c` |
| Special crewmate ghost roles | GuardianAngel(4), SpiritGuide(21); impostor list empty | Observed (IL listing) `RoleManager.SpecialCrewmateGhostRoles / SpecialImpostorGhostRoles` |
| Protection removed | at meeting start (not by timer in the code read) | Observed (IL listing) `PlayerControl.ResetForMeeting` |

## 2. State fields (kill-relevant)

`PlayerControl` (offsets from the dump): `killTimer` float (0xB0), `isKilling` bool (0x68), `inVent` (0x60), `walkingToVent` (0x61), `inMovingPlat` (0x63), `onLadder` (0x64), `protectedByGuardianThisRound` bool (0x65), `shapeshifting` (0x66), `waitingForShapeshiftResponse` (0x67), `protectedByGuardianId` int (0x78, negative = not protected), `moveable` (0x4C), `ForceKillTimerContinue` (0x58), `MaxReportDistance` (0x48). Evidence: Observed (dump).

`NetworkedPlayerInfo`: `PlayerId` (0x35), `RoleType` (0x50), `RoleWhenAlive` (nullable, 0x52), `Disconnected` (0x64), `Role` (0x68), `IsDead` (0x78). Observed (dump).

`RoleBehaviour` data (0x20..0xB0): `Role`, `TasksCountTowardProgress`, `CanUseKillButton`, `CanBeKilled`, `CanVent`, `AffectedByLightAffectors`, `MaxCount`, `TeamType` (Crewmate=0, Impostor=1), `Ability`, `SecondaryAbility`, `DefaultGhostRole`, `Player`. Observed (dump). `IsImpostor` is simply `TeamType == Impostor` (Observed (decompiled) `RoleBehaviour.get_IsImpostor @ 0x2213c68`).

`MurderResultFlags`: NULL=0, Succeeded=1, FailedError=2, FailedProtected=4, DecisionByHost=8. Observed (dump).

`DeathReason`: Exile=0, Kill=1, Disconnect=2. Observed (dump).

## 3. The kill pipeline

### 3.1 Client side (the killer's device)

1. The kill button handler (`KillButton.DoClick`, Observed (IL listing)) proceeds only if the button is active, it has a current target, the button is not cooling down, the local player is not dead and `PlayerControl.CanMove` is true.
2. It calls `PlayerControl.CmdCheckMurder(target)` (Observed (decompiled) `@ 0x21de47c`). That function does no validation at all: it sets `isKilling = true`, runs `CheckMurder` directly if this client is the (local or modded) host, and in all cases writes RPC 47 (`CheckMurder`) containing the target's net id. The decompiled code always emits the RPC; whether it is addressed only to the host is **Inferred** (the target-client argument is not visible).

So the "client checks" are: cooldown elapsed (the button's cooling-down flag, driven by `killTimer > 0`), a target found by `FindClosestTarget` (distance + wall test, section 4), not dead, can move. A modified client can skip all of them; the host does not re-check them (3.2).

### 3.2 Host side: exact ordered checks (`PlayerControl.CheckMurder @ 0x21d8194`, and the same logic inlined in `HandleRpc` case 47 @ 0x21de7dc)

Observed (decompiled). Order:

1. Log the request; set the requester's `isKilling = false`.
2. If the game is over: return silently (no reply).
3. If this client is not the host: return silently.
4. Target must be a live object (non-null). If not: reject.
5. Killer `Data.IsDead` must be false.
6. Killer `Data.Role.IsImpostor` (team = Impostor) must be true.
7. Killer `Data.Disconnected` must be false.
8. Target `Data` must exist.
9. Target `Data.IsDead` must be false.
10. Target `inVent` must be false.
11. Target must not be playing the enter-vent animation.
12. Target must not be playing any ladder animation.
13. Target `inMovingPlat` must be false.
14. No meeting may be in progress (the `MeetingHud` singleton must not exist; Inferred identity of the singleton: the same static is used by role code as the "meeting active" guard).

On success: `isKilling = true` on the killer and `RpcMurderPlayer(target, Succeeded)`. On any rejection: a warning is logged and `RpcMurderPlayer(target, not-succeeded)` is sent. `RpcMurderPlayer` (Observed (decompiled) `@ 0x21d8558`) converts the boolean to wire flags: **9 = Succeeded|DecisionByHost**, or **10 = FailedError|DecisionByHost**. It runs `MurderPlayer` locally on the host's client with those flags and writes RPC 12 with the target net id and the flags byte.

Things the host deliberately does NOT check: distance between killer and victim, line of sight, the kill cooldown, whether the killer's role has `CanUseKillButton`, whether the victim `CanBeKilled`, and Guardian Angel protection (protection is applied per client in 3.3). A server-authoritative clone should add the cooldown and distance checks itself.

### 3.3 Per-client execution (`PlayerControl.MurderPlayer(target, flags) @ 0x21d8da0`)

Decision on the flags, evaluated in this order (Observed (decompiled)):

| Step | Condition | Result |
|---|---|---|
| 1 | FailedError bit (2) set | do nothing (return) |
| 2 | FailedProtected bit (4) set | go to the protected path |
| 3 | DecisionByHost bit (8) set | if target `protectedByGuardianId >= 0` go to the protected path, else the success path |
| 4 | DecisionByHost clear and Succeeded bit (1) clear | do nothing |
| 5 | otherwise (Succeeded without DecisionByHost) | success path |

Because the host always sends 9 or 10, in practice each client decides protected-vs-success from its own copy of the target's `protectedByGuardianId` (kept in sync by the protect RPC, section 6).

**Success path**, in order (all Observed (decompiled) unless noted):
1. Clear the killer's `isKilling`.
2. Notify the analytics/observer interface of the death (victim data, killer data). Telemetry only.
3. If the killer is the local player: increment kill statistics, play the kill sound, read `KillCooldown` from the current options and call `SetKillTimer(KillCooldown)` (full cooldown, clamped by `SetKillTimer`).
4. Telemetry `WriteMurder`; move the victim's GameObject to the `Ghost` layer immediately.
5. If the victim is the local player: death statistic; close any open minigame (two close calls); show the kill overlay (killer data, victim data); remove the name mask; `RpcSetScanner(false)`.
6. Achievement hook `OnMurder(killerIsLocal, victimIsLocal, killerIsShapeshifted, shapeshiftTargetId, victimId)`.
7. Pick a kill animation from the killer's `KillAnimations` array and start `KillAnimation.CoPerformKill(killer, victim)` as a coroutine on the killer's physics component.

`KillAnimation.CoPerformKill` (Observed (IL listing), partial): freezes movement of the participants, snaps the killer onto the victim, instantiates the dead body prefab chosen from the victim's role, sets `ParentId` to the victim's player id, colours it with the victim's outfit, places it at victim position plus the animation's `BodyOffset` (x, y) with depth z = y / 1000, lets each role customise it via `RoleBehaviour.KillAnimSpecialSetup(body, killer, victim)`, then enables the body and calls **`victim.Die(DeathReason.Kill, assignGhostRole = true)`**. So the body spawns and the victim becomes a ghost on every client at the end of the kill animation, not at the instant of the RPC. Exact animation duration is presentation and was not extracted.

**Protected path** (shield absorbs the kill), in order:
1. Set the target's `protectedByGuardianThisRound = true`.
2. If the local player's role is Guardian Angel: if the target's `protectedByGuardianId` equals the local player's id, increment the "protected a crewmate" stat and fire `OnProtectACrewmate`. Then (for the local Guardian Angel) always run the failed-murder feedback `ShowFailedMurder(target)` and set the killer's timer to `0.5 x KillCooldown`.
3. If the local player is not a Guardian Angel: when the killer is the local player, same feedback and `SetKillTimer(0.5 x KillCooldown)`; otherwise `RemoveProtection(target)` (the shield visual is dropped on observers).
4. The victim is not killed, no body is spawned. Note the shield is not consumed for the Guardian Angel's own client by this path; removal otherwise happens at meeting start.

The halved cooldown is set via `SetKillTimer(value, killerPlayer)`; it only matters on the killer's own client because only that client ticks the timer.

### 3.4 Exile and generic death

`PlayerControl.Die(reason, assignGhostRole) @ 0x21cb4cc` (Observed (decompiled)), in order:
1. Log; if no HUD exists and this is the local player, apply a small ban-point penalty (-1.0) and record the date (anti-quit housekeeping; not gameplay).
2. Store `reason` into the static `GameData.LastDeathReason`.
3. If riding a zipline on the Fungle map, cancel the zipline use.
4. Pet mourning animation; fix mixed-up outfit.
5. `Data.IsDead = true`.
6. Disable the player's collider; move to `Ghost` layer; remove name mask; stop petting.
7. If the player was walking toward a vent: clear `inVent`, clear the global current vent, re-enable `moveable`, stop physics coroutines.
8. Call `Data.Role.OnDeath(reason)` (role hook, section 7).
9. Call the game manager's `OnPlayerDeath(player, assignGhostRole)`, which delegates to role selection (section 8).
10. If local: chat becomes visible to ghosts, hide the shadow/fog object, adjust lighting, refresh every player's name visibility according to ghost-vision rule.

`PlayerControl.Exiled @ 0x21d5f58` = `Die(DeathReason.Exile, true)` plus an "exiled" statistic increment on the local player. RPC 4 (`Exiled`) in `HandleRpc` also calls `Die(Exile, true)`.

## 4. Targeting

### 4.1 `GetPlayersInAbilityRangeSorted(output, ignoreColliders)` (`RoleBehaviour @ 0x22104b0 / 0x2214d8c`)

Observed (decompiled):
1. Clear the output list.
2. `range = GetAbilityDistance()`. The base implementation tail-calls the options object's method in vtable slot 18, which by slot counting in `LogicOptions` is `GetKillDistance` (Inferred from slot arithmetic: GetKillCooldown is slot 17, GetKillDistance 18, consistent with the other slots decoded). So the range equals the configured kill distance for every role that uses it. The Detective overrides it with 0.8 (Observed (binary), float literal in `DetectiveRole.GetAbilityDistance @ 0x22104b8`).
3. `origin = Player.GetTruePosition()` (collider-centre position, not the sprite transform).
4. For every `NetworkedPlayerInfo` in `GameData.AllPlayers` (list order): keep it only if `IsValidTarget(info)` (virtual, 4.2) is true and the player object exists and its collider component is enabled.
5. `d = |truePosition(target) - origin|`. Keep if `d <= range` (inclusive).
6. Unless `ignoreColliders`: build the unit direction `(target - origin) / d` (if `d <= 1e-5` use a fixed static direction vector instead), then run `PhysicsHelpers.AnyNonTriggersBetween(origin, dir, d, Constants.ShipAndObjectsMask)`, a 2D raycast of length `d` over the `Ship` and `Objects` layers (names inferred from the mask's name); it returns true if any hit collider is not a trigger. If true the target is dropped (wall or closed door in between).
7. Sort the surviving list ascending by distance to `origin` (comparison delegate captured with the origin). Not guaranteed stable.

`ImpostorRole.FindClosestTarget` (Observed (decompiled) `@ 0x2215ea0`) calls the 2-argument form (colliders respected) and returns the first element, or null if empty. There is no angular or facing restriction.

Guardian Angel (`GuardianAngelRole.FindClosestTarget`, Observed (IL listing)) calls it with `ignoreColliders = true` (ghost sees through walls), sorts again with its own comparison, returns the first.

### 4.2 `IsValidTarget`

- `RoleBehaviour.IsValidTarget(info)` (Observed (decompiled) `@ 0x2215d48`): info not null; `!info.Disconnected`; `!info.IsDead`; `info.PlayerId != Player.PlayerId`; `info.Role != null`; the player object exists; object not `inVent`; not `inMovingPlat`; its cosmetics layer is `visible`; and the object's `shouldAppearInvisible` flag (0x84) is false.
- `ImpostorRole.IsValidTarget` (Observed (decompiled) `@ 0x2215d0c`): base check AND `info.Role.CanBeKilled`. Hence impostor-team roles (CanBeKilled = 0) cannot be targeted by the kill button.
- `PhantomRole.IsValidTarget` (Observed (decompiled)): impostor check AND the Phantom itself is not invisible and not mid fade.
- Guardian Angel uses the base check, so it can shield any living non-self player, including impostors (it has no `IsValidTarget` override; Observed (dump) method list).

### 4.3 Dead body and usable proximity (per tick, local player)

`PlayerControl.FixedUpdate @ 0x21d2b6c` (Observed (decompiled)) every fixed tick for the local player:
- Overlap-circle query at the true position with radius `MaxReportDistance` (3.0 prefab) over `Constants.Usables`.
- A body is reportable if it is tagged as a dead body, enabled, not already reported, within `MaxReportDistance` and `!PhysicsHelpers.AnythingBetween(me, body, ShipAndObjectsMask, ...)`, and the local player can move and is alive.
- Each IUsable's `CanUse(data, out canUse, out couldUse)` returns a distance; the usable with the smallest distance among `canUse` becomes `closest`; vents are tracked in a separate list for the vent button.
- If the player cannot move and is not in a vent, `closest` is cleared and the use/vent/report/sabotage/admin buttons are reset.

## 5. Kill cooldown handling

`PlayerControl.SetKillTimer(t) @ 0x21d1d40` (Observed (decompiled)):
1. Return if no `Data` or no `Role`; return if `Role.CanUseKillButton` is false.
2. Read `KillCooldown` from the current options. If it is `<= 0` return without changing anything (cooldown disabled).
3. `killTimer = (t < 0) ? 0 : min(t, KillCooldown)`.
4. Refresh the kill button: cooling-down flag = `killTimer / KillCooldown > 0`; when cooling, show `int(killTimer)` as text.

Per tick (`FixedUpdate`, local player only): if `Role.CanUseKillButton` and the player is alive and (the logic layer says the timer is enabled or `ForceKillTimerContinue`), then `SetKillTimer(killTimer - fixedDeltaTime)` and set the kill button's target to `Role.FindClosestTarget()`. Otherwise the target is cleared and the role is told (via the role target hook) there is none. The rule for "timer enabled" (`get_IsKillTimerEnabled`) delegates to the game logic object; its exact condition (probably false in lobby and while a meeting or intro is running) is not recovered (Unknown, see section 12).

Timeline summary (Observed unless noted): game start 10 s (`min(10, KillCooldown)`), role init 10 s (the same value is set again in `RoleBehaviour.Initialize`), after a meeting full `KillCooldown`, after a kill full `KillCooldown`, after a shield-blocked kill `0.5 x KillCooldown`, minimum value 0 (a request below zero is stored as 0). Only the owning client stores/ticks the timer and gates the button; the host does not.

## 6. Guardian Angel protection

`CmdCheckProtect(target)` (RPC 48) / `CheckProtect(target)` (Observed (decompiled) `@ 0x21de568 / 0x21d996c`; same logic inlined in `HandleRpc` case 48):
1. Game over: return. Not host: return.
2. Target must be a live object.
3. Sender `Data.Role.Role == GuardianAngel`.
4. Sender role is not an impostor.
5. Sender `Data.Disconnected` false.
6. Target `Data` exists and `Data.IsDead` is false.
7. Then `RpcProtectPlayer(target, senderDefaultOutfitColorId)`. Any failure logs a warning and sends nothing.

`ProtectPlayer(target, colorId)` (Observed (IL listing)) stores the guardian's player id into the target's `protectedByGuardianId` (and `protectedByGuardianThisRound` when the kill is absorbed). Whether the shield is visible is decided per local viewer: the Guardian Angel always sees it; others only if the bool option `ImpostorsCanSeeProtect` (1100) is on and the viewer is an impostor (Inferred from the option read in `ProtectPlayer`/`TurnOnProtection`). `RemoveProtection` destroys the shield visuals. `ResetForMeeting` calls `RemoveProtection` (Observed (IL listing)). The `GuardianAngelDuration`/`ProtectionDurationSeconds` option (float 1100, prefab default 10 s, range 5-30) exists, but no reader that expires protection by timer was found in the code read (Unknown; see 12).

Guardian Angel role (`GuardianAngelRole`, Observed (decompiled) `UseAbility @ 0x22145e0`, `SetCooldown @ 0x2214a5c`, `FixedUpdate @ 0x2214acc`):
- Fields: `currentTarget` (0xD0), `cooldownSecondsRemaining` (0xD8).
- `UseAbility` requires: role component enabled, a current target, cooldown `<= 0`, local player dead, `CanMove`, not `IsCoolingDown` (cooldown > 0). Then `CmdCheckProtect(currentTarget)`, a sound, and `SetCooldown()`.
- `SetCooldown`: `cooldown = GuardianAngelCooldown` (float option 1101; asset default 60 s, range 35-120, step 5).
- `FixedUpdate` (owner, no meeting): `cooldown -= deltaTime` while `> 0`.
- `OnVotingComplete` (Observed (IL listing)): owner sets `cooldown = 0`.
- `IsAffectedByComms` = comms sabotage state.
- The initial cooldown on becoming a Guardian Angel is not set in the code read (field default 0, so first use is immediate; Inferred).

## 7. RoleBehaviour base and concrete roles

### 7.1 Base virtuals (`RoleBehaviour`, Observed (decompiled) unless noted)

| Method | Default behaviour |
|---|---|
| `CanUse(usable)` | false (concrete roles override) |
| `DidWin(reason)` | false |
| `Deinitialize`, `SpawnTaskHeader`, `UseAbility`, `UseSecondaryAbility`, `OnVotingComplete`, `OnExileComplete`, `OnDeath`, `OnRoleSet`, `SetUsableTarget`, `SetPlayerTarget`, `SetCooldown`, `AdjustTasks`, `HandleRoleRpc`, `KillAnimSpecialSetup` | no-op |
| `FindClosestTarget`, `FindClosestBody` | null |
| `IsValidTarget` | 4.2 |
| `GetAbilityDistance` | kill distance |
| `Initialize(player)` | store `Player`. For the owner: if team is Impostor and `CanUseKillButton`, reset kill button and `SetKillTimer(10.0)`; refresh role HUD buttons; set HUD active for the role; name colour; set up the primary and secondary ability buttons |
| `OnMeetingStart` | owner with a secondary ability: show the meeting ability button, enabled state off, cooldown readout 0 |
| `IsAffectedByComms` / `CommsSabotaged` | reads whether comms are sabotaged for this player |

### 7.2 Role flag table (Observed (assets), `roles.json`)

| Role (id) | Team | CanUseKill | CanBeKilled | CanVent | Tasks count | Default ghost role | Light affected |
|---|---|---|---|---|---|---|---|
| Crewmate (0) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| Impostor (1) | Imp | 1 | 0 | 1 | 0 | 7 | 0 |
| Scientist (2) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| Engineer (3) | Crew | 0 | 1 | 1 | 1 | 6 | 1 |
| GuardianAngel (4) | Crew | 0 | 0 | 0 | 1 | 4 | 1 |
| Shapeshifter (5) | Imp | 1 | 0 | 1 | 0 | 7 | 0 |
| CrewmateGhost (6) | Crew | 0 | 0 | 0 | 1 | 6 | 1 |
| ImpostorGhost (7) | Imp | 0 | 0 | 0 | 0 | 7 | 1 |
| Noisemaker (8) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| Phantom (9) | Imp | 1 | 0 | 1 | 0 | 7 | 0 |
| Tracker (10) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| Detective (12) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| Viper (18) | Imp | 1 | 0 | 1 | 0 | 7 | 0 |
| Judge (19) | Crew | 0 | 1 | 0 | 1 | 6 | 1 |
| SpiritGuide (21) | Crew | 0 | 0 | 0 | 1 | 21 | 1 |

`RoleManager.IsGhostRole`: {GuardianAngel 4, CrewmateGhost 6, ImpostorGhost 7, SpiritGuide 21} (Observed (IL listing)). `RoleManager.IsImpostorRole(type)` and `GetRole(type)` look the role up in `AllRoles` by `Role` and test `TeamType == Impostor` (Observed (IL listing)). `get_IsDead` is true for the ghost roles and false for living roles (Observed (IL listing) for Engineer/Impostor; ghost roles Inferred from `RoleManager.SetRole`, which uses it to trigger `Die`).

### 7.3 Impostor (`ImpostorRole`)

- `CanUse(usable)` (Observed (decompiled) `@ 0x2215b78`): `LogicUsables.CanUse(usable, player)` (always true in normal games, Observed (IL listing) `LogicUsablesBasic.CanUse`) AND, if the usable is a `Console`, `console.AllowImpostor` must be true. All other usables (vents, doors, etc.) pass.
- `DidWin(reason)`: delegates to the game flow's impostor-win test. `get_IsDead` false. `Deinitialize`/`SpawnTaskHeader`/`AppendTaskHint`: only touch the fake-task header (presentation).
- Kill rules: sections 3 and 4.

### 7.4 Crewmate (`CrewmateRole`) and plain crewmate classes

- `CanUse(usable)` (Observed (decompiled) `@ 0x2209a10`): true only if the usable is one of five specific usable classes (`Console` is one; four others, probably system/map/deconstruction-type consoles, are not resolved). Vents and impostor-only usables are excluded. `Scientist.CanUse` has the identical list.
- `DidWin`: delegates to the game flow's crewmate-win test.
- `Noisemaker`, `Tracker`, `Detective`, `Judge` derive from the crewmate behaviour and inherit this.

### 7.5 Engineer (`EngineerRole`)

Fields: `currentTarget` vent (0xC8), `cooldownSecondsRemaining` (0xD0), `inVentTimeRemaining` (0xD4), `usesRemaining` int (0xD8, constructed as 1), `isExitVentQueued` (0xDC). Observed (dump) + (IL listing).
- Options: cooldown = role float 1300 (`EngineerCooldown`; asset default 30 s, range 5-60), max in-vent time = role float 1301 (`EngineerInVentMaxTime`; asset default 15 s, range 0-60, zero means infinite).
- `CanUse(usable)` (Observed (decompiled) `@ 0x2211954`): for a vent, `if IsCoolingDown and not currently in a vent then false; else usesRemaining > 0`. The other five usable classes (the crewmate list) return true. Everything else false.
- `UseAbility` (Observed (decompiled) `@ 0x2211c98`): needs the targeted vent, `Vent.CanUse` true, role enabled, `cooldown <= 0`. Then `inVentTimeRemaining = GetVentTime()`, `Vent.Use()` (enters or exits). In Hide-and-Seek only, uses are decremented; in normal games `usesRemaining` stays 1.
- `SetCooldown`: `cooldown = EngineerCooldown`.
- `OnVotingComplete` (Observed (IL listing)): owner sets `cooldown = 0`.
- `FixedUpdate` (Observed (decompiled) `@ 0x2212084`; owner, not during a meeting): `cooldown -= deltaTime` while `> 0`. While `inVent`: if `GetVentTime() > 0`, `inVentTimeRemaining -= deltaTime`. If `inVentTimeRemaining < 0` OR comms are sabotaged, and the role is enabled, a vent target exists and no exit is queued: call `Vent.Use()` to force an exit and set `isExitVentQueued`. When not in a vent `isExitVentQueued` resets.
- The cooldown is therefore only started by `SetCooldown`, whose callers were not recovered (Unknown, likely the vent exit RPC path); the 15 s asset value is the in-vent time limit, not a second cooldown.

### 7.6 Scientist (`ScientistRole`)

Fields: `currentCharge` (0xD8), `currentCooldown` (0xDC), `minigame` (0xD0). Options: cooldown role float 1200 (asset default 15), battery charge 1201 (asset default 15 s).
- `Initialize` (Observed (decompiled) `@ 0x221c9ac`): `currentCharge = batteryCharge`; button enabled colouring depends on `charge > 0` and not cooling down.
- `UseAbility` (Observed (decompiled) `@ 0x221cd74`): if `charge > 0`, not cooling down and `CanMove`: halt movement, instantiate the vitals minigame as a child of the camera. If a minigame is already open, a second press closes it.
- `Update` (Observed (IL listing), conditions partly Inferred): owner; while the vitals minigame is open `currentCharge -= deltaTime` and the label shows the remaining seconds; when charge is exhausted the minigame closes and, if the player has finished all tasks, `currentCooldown = cooldown`. While not open: `currentCooldown -= deltaTime` and when it reaches 0 the charge is restored to the full battery (only reachable when all tasks were done).
- `OnVotingComplete` (Observed (IL listing)): owner and all tasks completed: `currentCooldown = 0`, `currentCharge = battery`, refresh button.
- `CompleteTask` (PlayerControl, Observed (decompiled)): for the local Scientist, `Recharge()` sets `currentCharge = battery` (a stat is incremented if it was lower). So each completed task refills the battery.
- `CanUse`: crewmate list. Whether the vitals data itself is blocked by comms: the HUD ability is "affected by comms" (base property), details Unknown.

### 7.7 Noisemaker (`NoisemakerRole`)

Only `OnDeath(reason)` (Observed (decompiled) `@ 0x22183d8`):
1. Act only if `reason == Kill`.
2. On each client, take the local player: if the local team is Impostor and the bool option `NoisemakerImpostorAlert` (1300) is off: stop. If the local player's comms are affected: stop.
3. Otherwise play the alert sound (distance-falloff, min volume 0.5, falloff 7 to 50 units; consts), vibrate, and spawn a death arrow at the body position pointing at it. Arrow lifetime = `NoisemakerAlertDuration` (role float 1600; asset default 10 s, range 1-15). If the Noisemaker was killed by a Viper (dissolving body), lifetime = `min(alertDuration, ViperDissolveTime (1601))`.
4. The dead Noisemaker's own client hides the arrow for itself.
The alert is therefore delivered to everyone alive and non-comms-blind, unless the impostor option disables it for impostors.

### 7.8 Tracker (`TrackerRole`)

Fields: `currentTarget` (0xC8), `cooldown` (0xD0), `duration` (0xD4), `delay` (0xD8), `pauseTrackingTime` (0xDC), `isTrackingActive` (0xE8); const delay 1.0 s. Options: cooldown 1550 (asset 30 s, 10-120), duration 1551 (asset 15 s, 10-120), delay 1552 (asset 1 s, 0-3; the code uses the const 1.0, the option is not read there).
- `UseAbility` (Observed (decompiled) `@ 0x22245dc`): owner, not cooling down, alive, `moveable`, no minigame, `cooldown <= 0`, `delay <= 0`. If tracking is active: cancel tracking (sound) and `SetCooldown`. Else if a target exists: `StartPlayerTracking(target, colorId)`, `isTrackingActive = true`, `duration = TrackerDuration`, `delay = 1.0`, `SetCooldown`.
- `FixedUpdate` (Observed (decompiled) `@ 0x2225278`): owner, no meeting. `delay -= dt` (button disabled while `delay > 0`). While alive: if `cooldown > 0` and not paused, `cooldown -= dt`. While tracking active with `duration > 0` and the cancel-timer condition holds, `duration -= dt`; at 0 tracking ends.
- `OnMeetingStart` (Observed (decompiled) `@ 0x2224efc`): sets `pauseTrackingTime = true`; if tracking is active it is cancelled (sound, button reset).
- `OnDeath`: owner cancels tracking and removes the target highlight.
- The condition that clears `pauseTrackingTime` after a meeting is not recovered (Unknown).

### 7.9 Detective (`DetectiveRole`)

Primary ability = open notes; secondary ability = interrogate a nearby player. Fields: `currentTarget` (0x118), `cooldownSecondsRemaining` (0x148), `interrogateCooldown` (0x14C), `deadPlayers` (0x100). Option: `DetectiveSuspectLimit` 1602 (asset 3, range 2-4).
- Range: `GetAbilityDistance` = **0.8** (Observed (binary), literal at rva 0xC72908).
- `FindClosestTarget` (Observed (IL listing)): `GetPlayersInAbilityRangeSorted(list, ignoreColliders = false)`, first element.
- `SetCooldown`: `cooldownSecondsRemaining = interrogateCooldown` (value of `interrogateCooldown` is a serialized field, not recovered) and refresh the button cooldown.
- `OnMeetingStart`: owner; builds the meeting notes for the dead players (`UpdateDeadPlayers`), adds victims to notes (suspect lists use per-map location tables and per-victim player location history; presentation of evidence). `KillAnimSpecialSetup` records evidence on the body. `OnDeath` clears the target.
- The server-visible effect of the detective (what is validated or broadcast) was not recovered; treat it as a client-side information feature (Inferred).

### 7.10 Shapeshifter (`ShapeshifterRole`)

Fields: `cooldownSecondsRemaining` (0xE8), `durationSecondsRemaining` (0xEC). Options: cooldown 1000 (asset 30 s, 5-90), duration 1001 (asset 0 = infinite, 0-30), `ShapeshifterLeaveSkin` bool (1000).
- `UseAbility` (Observed (decompiled) `@ 0x221e198`): requires alive, `moveable`, no open minigame, `cooldown <= 0`, no running role-effect animation, no mushroom-mixup sabotage active, no match-guide overlay. Halts movement. If currently shapeshifted (outfit type 1): `CmdCheckRevertShapeshift(animate)` (allowed even during a meeting only when the outfit is not shapeshifted, otherwise requires a meeting singleton; sequence per code) and the button resets. Otherwise opens the shapeshift menu and sets `durationSecondsRemaining = ShapeshifterDuration`.
- `SetCooldown`: `cooldown = ShapeshifterCooldown`.
- `FixedUpdate` (Observed (decompiled) `@ 0x221e984`): owner, no meeting. Button disabled if immobile, dead or mushroom-mixup active. During mushroom mixup the cooldown is forced to 0. Else `cooldown -= dt`. While shapeshifted (`CurrentOutfitType == 1`), `duration > 0` and the unshift-timer condition holds: `duration -= dt`; at `<= 0` the player is reverted (`RevertPlayerToNormal`).
- Host validation (`CheckShapeshift @ 0x21d8648`, `CmdCheckShapeshift @ 0x21dc488`, RPC 55): non-host sets `waitingForShapeshiftResponse = true` and sends RPC 55 (target, animate flag) to the host. Host checks in order: game over (return), is host, target object exists with data, requester not dead, requester role is Shapeshifter (5), requester not disconnected, then: reject (`RpcRejectShapeshift`) if mushroom mixup is active on the target and animation was requested, or if a meeting is active and animation was requested; else `RpcShapeshift(target, animate)`. Any failed precondition also rejects. No cooldown or distance check on the host.
- Movement: `CanMove` is false while `shapeshifting` or `waitingForShapeshiftResponse`.

### 7.11 Phantom (`PhantomRole`)

Fields: `cooldown` (0xD0), `duration` (0xD4), `isInvisible` (0xD8), `fading` (0xD9), `serverApproved` (0xDA). Options: cooldown 1500 (asset 15 s, 5-60), duration 1501 (asset 30 s, 10-90). The Phantom also has a normal kill button (CanUseKillButton = 1).
- `UseAbility` (Observed (decompiled) `@ 0x221852c`): owner, alive, `moveable`, no minigame, `cooldown <= 0`, not `fading`, no running role-effect animation, not walking to a vent, not on a moving platform. If already invisible: set `fading`, `CmdCheckAppear(animate)` and `SetCooldown`. If visible: swap the button to the appear state; then the vanish request: host path runs `SetRoleInvisibility(true)` locally and broadcasts RPC 63; non-host path runs it locally first, then sends RPC 62 to the host carrying the duration float (`PhantomDuration`). `CheckVanish @ 0x21df4d8` (host only, no other validation): applies invisibility locally and broadcasts RPC 63. `CheckAppear(animate) @ 0x21df600` (host only): applies visibility locally and broadcasts RPC 65 with the animate bool.
- `SetCooldown`: `cooldown = PhantomCooldown`. `FixedUpdate` (owner, no meeting): button disabled when immobile, dead or fading; `cooldown -= dt`; while invisible and not fading and `duration > 0` and the cancel-timer condition holds: `duration -= dt`; at 0 `MakePlayerVisible`.
- `IsValidTarget`: see 4.2. `CanUse` (Observed (decompiled) `@ 0x2219214`): requires `LogicUsables.CanUse`; vents and three of the crewmate-list usables are allowed only when not `fading`; while invisible nothing else is allowed (no consoles); when visible a Console needs `AllowImpostor`.
- Validation weakness: the host does not check role, cooldown or alive state for vanish/appear (Observed). A server-authoritative clone should.

### 7.12 Viper (`ViperRole`)

Only `Initialize @ 0x2225bc0` is decompiled: base initialize, then for the owner replaces the kill button label and sprite with the Viper variants (presentation). Its kill uses the normal pipeline; the victim's body dissolves over `ViperDissolveTime` (role float 1601, asset 15 s, 5-60), which also caps the Noisemaker arrow. The dissolve itself is in other classes (`ViperDeadBody`), Unknown here.

### 7.13 Judge (`JudgeRole`)

Fields: `OverruledPlayerId` (0xC8), `OverruleNonce` ushort (0xCA), `HasAlreadyOverruledThisMeeting` (0xCC), `HasAnOverruleUse` (0xCD, constructed true), `taskRequirementProportion` (0xD0), `initialTaskCount` (0xD4).
- `Initialize` (Observed (decompiled) `@ 0x22165dc`): starts `CoSetInitialTaskCount` (records the task count once tasks are assigned) and `taskRequirementProportion = role float 1620 / 100` (asset 50%, range 0-100, step 25).
- `TasksRemaining` (Observed (IL listing)): `clamp(ceil(initialTaskCount * proportion) - completedTaskCount, 0, 999)`. `IsBlockedByTasks`: `completedTasks / initialTaskCount < proportion` (comparison direction Inferred).
- `OnMeetingStart` (Observed (decompiled) `@ 0x2216eec`): owner only; reset `HasAlreadyOverruledThisMeeting = false`, `OverruleNonce = 0`, overruled id = none; show the meeting button only if `HasAnOverruleUse`; its label is "ready" when `TasksRemaining < 1`, else "N tasks left".
- `TryOverrule(targetId)` (Observed (IL listing)): local; if already overruled this meeting, warn and return false. Else store the target, mark used, set a random nonce in 1..65535 if zero, mark the vote complete for the local player, clear the "uses" indicator and send `MeetingHud.CmdQueueOverruleVotes(judgeId, nonce, targetId)`.
- `ConsumeOverruleVotesUsage`: `HasAnOverruleUse = false` (one overrule per game). `ClearOverrule` resets the per-meeting state when a different meeting id is seen. How the meeting applies the overrule is in the meeting spec.

### 7.14 Ghost roles

- `CrewmateGhostRole`: `CanUse` true only for `Console` usables (Observed (decompiled) `@ 0x2212468`); `DidWin` as crewmate; `UseAbility` (Observed (decompiled) `@ 0x2212544`) opens or closes the haunt menu (a cosmetic ghost chat/haunt UI; blocked while chat is open). No gameplay effect found.
- `ImpostorGhostRole` (Observed (decompiled) `@ 0x22140c0`): `CanUse` false for vents, true for non-console usables, and for consoles only if `AllowImpostor`; `DidWin` as impostor; `UseAbility` opens the haunt menu (same).
- `GuardianAngelRole`: section 6.
- `SpiritGuideRole` (Observed (decompiled) partial): crewmate-team ghost. Option `SpiritGuideCooldownSeconds` (1640; asset 20 s, range 10-60; code constant `MINIMUM_COOLDOWN_SECONDS = 10`). Secondary ability "send" is a limited-use ability: the ghost picks images from a panel and sends them to a target player; there is a camera/haunt-style UI with flash animation. `FixedUpdate` ticks `cooldownSecondsRemaining`; `OnMeetingStart` re-enables the ability button. Server-visible rules (validation, who receives the image) were not recovered (Unknown).

## 8. Ghost role assignment on death

Called from `Die(..., assignGhostRole = true)` via `GameManager.OnPlayerDeath` -> `LogicRoleSelectionNormal.OnPlayerDeath @ 0x20e02f4` (identical body to `RoleManager.AssignRoleOnDeath @ 0x221bb40`, plus a host gate). Observed (decompiled):

1. Only the host proceeds, and only if `assignGhostRole` is true and the role manager exists.
2. Player must be non-null and `Data.IsDead` true.
3. `TryAssignSpecialGhostRoles(player, isImpostor)` with `isImpostor = Data.Role.TeamType == Impostor`.
4. Re-read the role: if it is now a ghost role (the special roll succeeded, or the player already was a ghost) stop.
5. Otherwise take `Data.Role.DefaultGhostRole` (6 CrewmateGhost for crew roles, 7 ImpostorGhost for impostor roles; 21 SpiritGuide / 4 GuardianAngel are only reached through the special roll), run the role's pre-change hook, then `RpcSetRole(player, defaultGhostRole, canOverride = false)`: locally start `CoSetRole` and queue an `RpcSetRoleMessage` (player net id, role, canOverride flag) in the game-data message list for the other clients.

`TryAssignSpecialGhostRoles(player, impostorRoles)` (Observed (decompiled) `@ 0x221bc9c`):
1. Candidate list: crew -> [GuardianAngel, SpiritGuide] (insertion order of a set, Observed (IL listing)); impostor -> empty (so impostors always get ImpostorGhost).
2. For each candidate in order: `count` = number of players currently holding that role (counted from `GameData.AllPlayers`).
3. In free-play (`NetworkMode == 2`): assign the first candidate immediately with `RpcSetRole` and stop.
4. Otherwise skip the candidate if `numPerGame(role) <= count`; then roll `r = HashRandom.Next(101)`; skip if `chance(role) <= r`; otherwise `RpcSetRole(player, role, false)` and stop. So assignment happens when `chance > r`, giving P = chance/101 for chance 0..100 (a 100% setting fails 1 in 101 if `Next(101)` is `[0,100]`, Inferred).
5. If no candidate is chosen, the caller falls back to step 5 above.

`RoleManager.SetRole(player, type)` (Observed (IL listing)): old role `Deinitialize`d and its object destroyed; the new role prefab (found in `AllRoles` by type) is instantiated under the player, `Initialize(player)` runs, `Data.Role` and `Data.RoleType` are updated and `RoleWhenAlive` is set; if the new role's `IsDead` is true: if the player was alive, call `Die(Kill, assign = false)`; if the player was already dead, `Revive()` then `Die(Kill, false)` to rebuild the ghost state.

`Die(...)` also calls `Data.Role.OnDeath(reason)` before role assignment, so the Noisemaker/Tracker/Detective hooks fire on the living role.

## 9. Usables that depend on role

### 9.1 Vent (`Vent.CanUse @ 0x2257f78`, `Vent.Use @ 0x2258abc`)

Observed (decompiled). Inputs: player data; outputs `canUse`, `couldUse`, distance (FLT_MAX when not usable).
1. `couldUse` starts as: `Data.Role.CanVent` AND `LogicUsables.CanUse(vent, player)` AND `Data.Role.CanUse(vent)` AND not dead AND (`player.CanMove` OR `player.inVent`).
2. If the player "must clean" this vent (`MustCleanVent(ventId)`): they must already be in a vent and it must be the current vent, else false.
3. If a Ventilation system exists (`SystemTypes` id 0x25) and `IsVentCurrentlyBeingCleaned(ventId)`: both flags false.
4. `canUse = couldUse` and then requires the distance from the player's collider centre to the vent position `<= 0.75` and no blocking collider between them on the `ShipOnly` mask (raycast, triggers ignored).
5. `Use()`: re-run `CanUse`; if allowed and not already walking to a vent: if not in a vent, `RpcEnterVent(ventId)`; else `RpcExitVent(ventId)`; update vent arrow buttons.
Who can vent: roles with `CanVent` whose own `CanUse` accepts vents: Impostor-team roles (always, subject to Phantom fading), Engineer (cooldown/in-vent timer, 7.5). Crewmate, Scientist, ghosts: no. Dead players: no (`!dead` required; also ImpostorGhost rejects vents).

### 9.2 Console (`Console.CanUse @ 0x224431c`, `Console.Use @ 0x2244d14`)

Observed (decompiled). Fields: `usableDistance` (0x20), `onlyFromBelow` (0x28), `onlySameRoom` (0x29), `checkWalls` (0x2A), `GhostsIgnored` (0x2B), `AllowImpostor` (0x2C), `Room`, `TaskTypes`, `ValidTasks`.
1. Player object must exist; true position vs console transform.
2. If the player is dead: the option `GhostsDoTasks` (logic-options slot 16, by the same slot arithmetic as 4.1) must be true AND the console must not be `GhostsIgnored`; if the player is alive: `CanMove`. (Both branches then continue with step 3.)
3. `Data.Role.CanUse(console)` must be true (Impostor needs `AllowImpostor`; ghosts only Console types).
4. `onlySameRoom`: player must be inside the console's room polygon.
5. `onlyFromBelow`: player y must not exceed console y (compared via the two positions).
6. A matching task must exist for the player (`FindTask`).
7. `canUse = couldUse AND distance <= usableDistance`; if `checkWalls` also no blocker between them on the `Shadow` mask.
`Use()` finds the task, logs, and opens the task's minigame (not detailed here).
Dead players can therefore use consoles only when `GhostsDoTasks` is on (bool option 2, which the per-tick code also reads when deciding whether a dead player keeps interacting) and the console is not ghost-ignored.

### 9.3 Doors

`DoorConsole.CanUse @ 0x2248494`: alive, the door is not open, `Role.CanUse(door console)` true; `canUse` also needs distance `<= 1.0`. `OpenDoorConsole.CanUse @ 0x224e178`: alive, door not open, distance `<= usableDisance` field. No role-specific logic beyond `Role.CanUse` (crew list vs impostor, ghosts false for non-console).

## 10. Messages (RPC ids) in this area

| Id | Name | Sender -> receivers | Payload |
|---|---|---|---|
| 12 | MurderPlayer | host -> all | target net id, flags byte (9 or 10) |
| 47 | CheckMurder | client -> host | target net id |
| 48 | CheckProtect | client -> host | target net id |
| 55 | CheckShapeshift | client -> host | target net id, animate bool |
| 62 | CheckVanish (client request) | client -> host | duration float |
| 63 | Vanish | host -> all | none |
| 65 | Appear | host -> all | animate bool |
| 4 | Exiled | host -> all | none (calls `Die(Exile, true)`) |

Ids 12, 47, 48, 55, 63, 65 are Observed (decompiled); 62 is Observed on the send side only; the CheckAppear client request id and the exact RPC for protect/shapeshift results (`RpcProtectPlayer`, `RpcShapeshift`, `RpcRejectShapeshift`, `RpcSetRole` message) were not read.

## 11. Implementation notes for our C# /sim

- Server-authoritative kill: validate in this order: game running, killer alive/not disconnected/impostor-team (role with kill ability), cooldown elapsed (authoritative `killTimer`, which the original skips), target alive and not in a vent/ladder/moving platform/spawn animation, no meeting, distance <= kill distance and line of sight clear on walls/doors. Then decide shield: if the target has an active shield, cancel the kill, clear that shield, set the killer's cooldown to 0.5 x KillCooldown; otherwise kill, set the killer's cooldown to KillCooldown.
- Keep the three-way MurderResult semantics (success / failed / blocked by shield) as the broadcast result; the DecisionByHost bit can be dropped because the server is always the decider.
- Targeting query: sample candidates within range (inclusive), drop those failing the validity filter (alive, connected, not self, not in vent, not on moving platform, visible, `CanBeKilled` for kills), raycast against walls/doors, sort ascending by distance, take the first. Use the collider-centre position. Kill ranges 1.0 / 1.8 / 2.5 (default Medium); Detective 0.8.
- Cooldown handling: initial 10 s (clamped to the option) at game start, full cooldown after every kill and every meeting, half cooldown after a shield block, clamp to [0, KillCooldown]; a KillCooldown <= 0 disables the timer.
- Guardian Angel: only a dead player whose role is Guardian Angel; target any living player within kill-distance range ignoring walls; ability cooldown 60 s default (option), reset to 0 after each meeting; the shield persists until the next meeting starts (the duration option was not found being used).
- Ghost roles: on death the host rolls special roles for crew only (Guardian Angel, Spirit Guide, honoring per-game count and percent chance with a roll in 0..100), otherwise gives the default ghost role by team. The victim becomes a ghost after the kill animation; the body appears with it.
- Engineer: vent only when not cooling down (unless already inside), force-exit when the in-vent timer expires or comms are sabotaged, cooldown cleared after meetings.
- Scientist: battery that drains while vitals is open, fully refilled on every task completion; cooldown only matters after all tasks done.
- Shapeshifter/Phantom: do the cooldown, alive and role validation server-side (the original host skips most of it for Phantom). Cancel timers during meetings.
- Presentation only (skip): all HUD button text/fills, sounds, vibration, kill overlay, minigame prefabs, haunt/spirit-guide UI, noisemaker arrow visuals (keep the information rule: who receives the alert), detective notes UI.
- Simplify: collapse the client/host RPC pairs into a single server command per ability; keep one shared "valid target" filter.

## 12. Open questions (Unknown)

- Exact condition of `PlayerControl.get_IsKillTimerEnabled` (likely false outside active play).
- Whether protection expires on a timer (`GuardianAngelDuration`, 1100) or only at meeting start; no reader of the float was found.
- Initial Guardian Angel cooldown and callers of `EngineerRole.SetCooldown` / `TrackerRole.SetCooldown` outside `UseAbility`.
- When `TrackerRole.pauseTrackingTime` is cleared after a meeting.
- Identity of the four non-Console usable classes accepted by `CrewmateRole.CanUse` / `ScientistRole.CanUse` / `EngineerRole.CanUse`.
- Server-visible rules of Detective interrogation, Spirit Guide sending, Viper dissolve, Judge overrule effect (meeting spec).
- Exact kill-animation length and when the victim's collider is disabled relative to the body appearing.
- Association of the 1.0 / 1.8 / 2.5 blob with `NormalGameOptionsV12.KillDistances` is by content and size (only 3-float blob that matches), not by resolving the field handle.
- The `Shapeshifter` revert/meeting branch ordering is described as read; some branch conditions were lost in decompilation.
