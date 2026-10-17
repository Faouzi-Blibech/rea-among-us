# Logic: meetings, voting, win conditions, role assignment, task assignment

Behaviour spec derived from decompiled Among Us mobile (Android 2026.9.29, IL2CPP). Written so a C# developer can implement a server-authoritative equivalent without seeing the decompiled code. Class and method names are cited as references only; all prose and pseudocode is original.

Evidence tags: **Observed (decompiled)** = read from a function body, cited as `Class.Method @ 0xRVA`. **Observed (dump)** = from signatures, consts, enums, fields. **Inferred** = interpretation where the body was not available or unclear (reason given). Unresolved items are collected in "Open questions".

## 0. Key numbers

| Item | Value | Tag / source |
|---|---|---|
| Meeting states | Animating 0, Discussion 1, NotVoted 2, Voted 3, Results 4, Proceeding 5 | Observed (dump) `MeetingHud.MeetingStates` |
| Discussion duration | option `DiscussionTime` (int seconds, options offset 0x50) | Observed (decompiled) `LogicOptionsNormal.GetDiscussionTime @ 0x20df40c` |
| Voting duration | option `VotingTime` (int seconds, offset 0x54); `<= 0` means no time limit | Observed (decompiled) `LogicOptionsNormal.GetVotingTime @ 0x20df428`, `MeetingHud.Update @ 0x21244c0` |
| Results display (online games) | 5.0 s fixed, then host auto-proceeds | Observed (decompiled) `MeetingHud.Update @ 0x21244c0`; const `ResultsTime = 5` (dump) |
| Intro animation (state Animating) | not timed in the decompiled code (coroutine not decompiled); the discussion clock does not run while Animating | Observed (decompiled) / Unknown length |
| Emergency button initial lockout | 15 s of `ShipStatus.Timer` since the ship started | Observed (decompiled) `EmergencyMinigame.Update @ 0x224b1ec`, const `MinEmergencyTime = 15` (dump) |
| Emergency cooldown | option `EmergencyCooldown` (int s, offset 0x40); counts down in `ShipStatus.EmergencyCooldown` | Observed (decompiled) `ShipStatus.FixedUpdate @ 0x2252da4`; reset trigger Unknown |
| Emergency meetings per player | option `NumEmergencyMeetings` (offset 0x3c); per-player counter `RemainingEmergencies` | Observed (decompiled) / (dump) |
| Body report distance | `PlayerControl.MaxReportDistance` = 3.0 (asset value) | Observed (dump + asset data) |
| Vote sentinels | 255 = not voted, 254 = missed vote, 253 = skip, 252 = dead | Observed (dump) `PlayerVoteArea` consts |
| Meeting RPC ids (MeetingHud) | 0x16 Close, 0x17 VotingComplete, 0x18 CastVote, 0x19 ClearVote, 0x42 (66) QueueOverruleVotes | Observed (decompiled) `MeetingHud.HandleRpc @ 0x2129dbc` |
| StartMeeting RPC (PlayerControl) | 0x0E, payload one byte: target PlayerId or 0xFF for none (emergency) | Observed (decompiled) `PlayerControl.RpcStartMeeting @ 0x21d6f08`, `PlayerControl.HandleRpc @ 0x21de7dc` |
| VoteBan system RPC | 0x1A; kick when 3 distinct voter slots are filled | Observed (decompiled) `VoteBanSystem.HandleRpc @ 0x20f3fc0` |
| GameOverReason | CrewmatesByVote 0, CrewmatesByTask 1, ImpostorsByVote 2, ImpostorsByKill 3, ImpostorsBySabotage 4, ImpostorDisconnect 5, CrewmateDisconnect 6, HideAndSeek_CrewmatesByTimer 7, HideAndSeek_ImpostorsByKills 8 | Observed (dump) |
| Crew-win reasons | {0, 1, 5} (bit mask 0x23) | Observed (decompiled) `GameManager.DidHumansWin @ 0x20d64ec` |
| Impostor-win reasons | {2, 3, 4, 6} (bit mask 0x17 over reasons 2..6) | Observed (decompiled) `GameManager.DidImpostorsWin @ 0x20d650c` |
| Critical sabotage "stopped" countdown | 10000.0 | Observed (decompiled) `LogicGameFlowNormal.CheckEndCriteria @ 0x20de930`; const `CountdownStopped = 10000` (dump) |
| Task assignment retry guard | at most 1000 loop iterations per call | Observed (decompiled) `ShipStatus.AddTasksFromList @ 0x2252a1c` |
| Scan task cap (visual tasks off) | a scan-type task is removed from the pool once it has been handed out three times | Observed (decompiled) same function; option reading Inferred |
| Role pick RNG | `HashRandom` (hash-based PRNG); role chance roll = `Next(101) < Chance` | Observed (decompiled) |
| Default impostor-count cap | 3 when the player count is outside the lookup table | Observed (decompiled) `IGameOptionsExtensions.GetAdjustedNumImpostors @ 0x2231d78` |

## 1. Scope and structure

Covered: MeetingHud (lifecycle, vote tally, results), EmergencyMinigame, ExileController, report/meeting-start path in PlayerControl and ShipStatus, win checks (LogicGameFlowNormal, GameManager, GameData), NetworkedPlayerInfo task data, VoteBanSystem, role selection (RoleManager, LogicRoleSelectionNormal), task assignment (ShipStatus), and the option getters used by them.

Authority model (Observed): one host runs the rules. Clients send commands (`CmdReportDeadBody`, `CmdCastVote`); the host validates, updates state and broadcasts the result as an RPC. A server-authoritative clone should keep that shape: client intent in, validated result out.

Pure presentation (nothing relevant for a simulation): `MeetingHud.Start` (sets background sprite, recolours player materials, shows chat, stops reactor/oxygen alarms, sets up skip button player id 253, registers controller-navigation targets, enables the dead-player foreground when the local player is dead) @ 0x2123fb4; `MeetingHud.CoStartCutscene` / `Close` (hide/show chat for dead players and run the exile cutscene coroutine) @ 0x2124f64 / 0x2125490; `MeetingHud.SetVoteComplete` (toggles UI buttons on every vote area) @ 0x21266d4; `MeetingHud.PopulateResults` (draws vote icons; see section 3.6 for the one rule it shows) @ 0x2125eec; `EmergencyMinigame.Begin` (logging, minigame bookkeeping) @ 0x224b15c; `ShipStatus.OnMeetingCalled` (destroys the social-feed visuals) @ 0x2251f6c; `ShipStatus.StartMeeting` (starts the coroutine `CoStartMeeting`, whose body was not decompiled) @ 0x2253330; the sound, vibration and animation parts of `EmergencyMinigame.CallMeeting`; `NetworkedPlayerInfo.OnGameStart` (name visibility, pet reset) @ 0x244f9a4.

## 2. Meeting start

### 2.1 Report rules

Two entry points (Observed (decompiled) `EmergencyMinigame.CallMeeting @ 0x224b808`, `PlayerControl.CmdReportDeadBody @ 0x21ca3e4`, `PlayerControl.ReportDeadBody @ 0x21d6bd4`):

- **Dead body report:** target = the victim's player info.
- **Emergency button:** target = none (sent as 0xFF on the wire).

`CmdReportDeadBody` on a non-host client only sends a message containing the target id (0xFF when none) and returns. On the host it runs the same body as `ReportDeadBody`.

Host-side validation, in the order performed:

1. If the game is already over, ignore.
2. If a meeting object already exists (the singleton `MeetingHud.Instance` is alive; identified by its use in the sabotage code as "no sabotage during a meeting"), ignore. Tag: Inferred for which singleton, from usage.
3. If the target is none (emergency call): if the **host's own local player** has any task flagged as an emergency/critical-sabotage task (`PlayerTask.TaskIsEmergency`, i.e. an active sabotage repair task), ignore. Note this reads the local player's task list, not the reporter's; a clone should instead check "is a critical sabotage (reactor, oxygen, seismic, etc.) active" on the server.
4. If the reporter is dead (`IsDead` flag on the reporter's player info), ignore.
5. Register the reporter and target with the meeting-room manager (`MeetingRoomManager.AssignSelf`), which also stores them so the meeting can start.
6. If this process is not the host, stop here.
7. Run the task-completion check (`GameManager.CheckTaskCompletion`, section 5.3). If it returns true (all tasks done; this also ends the game with reason CrewmatesByTask), ignore the report.
8. Open the meeting room on the HUD and broadcast the start-meeting RPC (`RpcStartMeeting`).

What the host does NOT check in these functions (Observed absence): reporter-to-body distance, whether the body was already reported, button cooldown (`ShipStatus.Timer` and `EmergencyCooldown`), and the reporter's remaining emergency count. These are enforced only by client UI (see below). A server-authoritative clone must re-check them (see implementation notes).

Client-side gating of the emergency button (`EmergencyMinigame.Update @ 0x224b1ec`, `CallMeeting @ 0x224b808`):

- State "locked / waiting": if `ShipStatus.Timer < 15.0` or `ShipStatus.EmergencyCooldown > 0`, the button is disabled and the text shows the larger of `trunc(15 - Timer)` and `trunc(EmergencyCooldown)` seconds (both truncated toward zero; the first term goes negative once Timer passes 15 and is then dominated by the second).
- Otherwise, if the local player has an emergency task active: state "blocked by crisis", button disabled.
- Otherwise the button shows the player's name and `RemainingEmergencies`; it is enabled only when `RemainingEmergencies > 0`.
- On press, `CallMeeting` re-checks: no emergency task, `RemainingEmergencies >= 1`, button enabled; then sends `CmdReportDeadBody(none)`, disables the button.

`ShipStatus.FixedUpdate @ 0x2252da4` (every fixed step, on clients with a local player): `Timer += fixedDeltaTime`; `EmergencyCooldown -= fixedDeltaTime`. It also recomputes task counts each step. Where `EmergencyCooldown` is set from the option, and where `RemainingEmergencies` is initialised from `NumEmergencyMeetings`, was not in the decompiled set (Unknown; Inferred: set at game start and after each meeting).

### 2.2 StartMeeting RPC (0x0E)

Handler `PlayerControl.HandleRpc` case 0x0E (@ 0x21de7dc), executed on every client:

1. Read one byte target id; look up the target's player info (0xFF yields none).
2. Fire the HUD "meeting called" overlay (emergency vs body variant).
3. Call `ShipStatus.StartMeeting(reporter, target)`, which starts the meeting coroutine (spawns the MeetingHud, runs the intro, etc.; not decompiled).
4. If this client owns the reporter: if the call was an emergency (target none), decrement the reporter's `RemainingEmergencies` by 1 (client-local copy, no server validation); also record telemetry/stats.

`GameData.OnMeetingStart @ 0x244c558` only increments the static meeting counter (`GameData.MeetingCount`, offset 0x10).

## 3. Meeting state machine and voting

### 3.1 Fields (MeetingHud, from the dump)

| Field | Meaning |
|---|---|
| `state` (0xE0) | `MeetingStates` |
| `discussionTimer` (0x110) | float seconds since the meeting left Animating; it keeps running through voting and results |
| `resultsStartedAt` (0x118) | value of `discussionTimer` when results arrived |
| `lastSecond` (0x11C) | countdown-beep bookkeeping (presentation) |
| `playerStates` (0x88) | array of `PlayerVoteArea`, one per player, plus `SkipVoteButton` (0x80) with player id 253 |
| `exiledPlayer` (0xF8), `wasTie` (0x100), `wasOverruled` (0x101) | result data |

`PlayerVoteArea` (dump): `AmDead` 0x28, `DidReport` 0x29, `DidVote` 0x2A, `PlayerId` 0x2B, `VotedForId` 0x2C, `VoteComplete` 0x2D. Sentinel consts: `HasNotVoted` 255, `MissedVote` 254, `SkippedVote` 253, `DeadVote` 252.

`VoterState` (struct, 2 bytes): `VoterId`, `VotedForId`. `AmDead` is `VotedForId == 252`, `SkippedVote` is `VotedForId == 253` (Inferred from the const names and getters; bodies are tiny and were not decompiled).

### 3.2 Per-frame update (`MeetingHud.Update @ 0x21244c0`)

Let `D` = discussion time, `V` = voting time (both ints from the options).

- State Animating (0): do nothing (no clock advance). The transition to Discussion happens at the end of the intro coroutine `CoIntro` (not decompiled; Inferred).
- In every other state: `discussionTimer += deltaTime`, then refresh foreground.
- **Discussion (1):** if `D <= discussionTimer`: switch to NotVoted (2), enable all vote areas (and hide the timer text if `V <= 0`). Else show "voting begins in" with `trunc(D - discussionTimer)` seconds and keep all vote areas disabled. Because the comparison is `D <= timer`, `D = 0` moves to voting on the first frame after the intro.
- **NotVoted (2) / Voted (3):** only if `V > 0`: `elapsed = discussionTimer - D`; `remaining = max(V - elapsed, 0)`; show "voting ends in" `trunc(remaining)`. In state 2 only, a pulse/beep plays once per whole second at the end (presentation). If `elapsed >= V` and this client is the host, call `ForceSkipAll`. With `V <= 0` there is no timeout and voting ends only when everybody has voted (or after disconnect handling).
- **Results (4):** only when the network mode is OnlineGame (1): `remaining = (resultsStartedAt - discussionTimer) + 5.0`, shown as `trunc(max(remaining, 0))` ("proceeding in"); when `remaining <= 0` and this client is the host, call `HandleProceed`. In LocalGame/FreePlay modes there is no automatic proceed; the host presses a proceed button (`SetupProceedButton`).
- Proceeding (5): nothing in `Update`.

`ForceSkipAll`, `HandleProceed`, `CoIntro`, `ServerStart`, `HandleDisconnect` and `TryGetWinningOverrule` bodies were not in the decompiled set. Inferred behaviour: `ForceSkipAll` marks every player who has not voted as "missed vote" (254, which the tally ignores) and calls `CheckForEndVoting`; `HandleProceed` moves to Proceeding, closes the meeting and starts the exile sequence.

### 3.3 Casting a vote

`CmdCastVote(voterId, suspectId)` (`@ 0x21267ac`): if not host, send RPC 0x18 with two bytes (voter id, suspect id) and return. If host, run `CastVote`. The RPC handler for 0x18 on the host runs the same `CastVote` (Observed, `HandleRpc`).

`CastVote(voter, suspect)` (`@ 0x21274c0`), in order:

1. Look up the voter's vote area by player id.
2. If the area is marked dead **or** has already voted, return silently (votes are final; no change allowed).
3. Play the vote sound (always for the local player's own vote, otherwise only if the network mode is not LocalGame).
4. Record the vote on the area (`PlayerVoteArea.SetVote(suspect)`: sets `VotedForId` and `DidVote`; Inferred, body not decompiled).
5. Mark the meeting object dirty (bit 1) so the full vote state is re-serialized.
6. Call `CheckForEndVoting`.
7. Broadcast a chat-note message for the voter (a "has voted" marker shown to everyone; this reveals who voted but not for whom).

Not checked (Observed absence): the meeting state (a vote sent during Discussion is accepted by the host; the UI merely disables the buttons), that `suspect` is a valid, living player or 253, and that the voter is really the sender. A clone must validate these.

Clearing a vote: RPC 0x19 with one byte (voter id). If it is the local player, the state returns to NotVoted (2) and the buttons are reset; the area's `DidVote` becomes false (Observed, `HandleRpc`). Used by the Judge flow and similar; a clone without it can ignore it.

### 3.4 Ending voting early (`CheckForEndVoting @ 0x2127120`)

Runs after every vote (and after the judge-overrule message, section 3.7).

1. Voting is complete only if **every** vote area satisfies "dead or has voted". Otherwise return. (Disconnected players: their area is handled by `HandleDisconnectedPlayerVoteArea`, not decompiled; Inferred that it marks them dead/finished so they do not block.)
2. Tally (`CalculateVotes`, section 3.5).
3. Find the leading entry and a tie flag (`Extensions.MaxPair`, not decompiled; Inferred behaviour: scan all entries, track the largest count; the tie flag is true when more than one entry holds the largest count, and also true for an empty tally).
4. Exiled player = the player whose id equals the leading key, but only if there is no tie; otherwise nobody. A leading key of 253 (skip) matches no player, so skip wins means nobody is exiled. (Inferred: lambda body not decompiled, but the closure carries exactly the tie flag and the leading pair.)
5. Judge overrule (see 3.7) may replace the exiled player and set `wasOverruled`.
6. Build the `VoterState` array from every vote area (voter id, voted-for value as stored, including 252 for dead and 254 for missed) and broadcast `VotingComplete` with: states, exiled player (or none), tie flag, overruled flag, overrule nonce.

### 3.5 Tally algorithm (`CalculateVotes @ 0x2127ca4`)

Dictionary from voted-for id to count, built by walking every vote area in array order:

- Skip areas whose stored vote is **252** (dead), **255** (not voted) or **254** (missed vote). These are not counted anywhere.
- Every other vote, including **253 (skip)**, increments `counts[vote]` (starting from 0).

So a dead voter, a non-voter and a timed-out voter all contribute nothing; a skip is an ordinary candidate that can win or tie. All votes weigh exactly 1 (no extra weight for any role in this function). Dead players cannot be voted for by the UI, but the tally itself does not filter targets.

Tie handling (Inferred from `MaxPair` usage): two or more candidates with the same highest count, including skip tied with a player, means "tie": nobody is exiled and `tie = true` is announced. A skip win without a tie gives `tie = false`, exiled none; the cutscene text differs for skip versus tie (section 4).

### 3.6 Applying the result (`VotingComplete @ 0x212556c`, also the RPC 0x17 handler)

RPC 0x17 payload (Observed, `HandleRpc`): packed int N; N two-byte `VoterState` entries; one byte exiled player id; bool tie; bool wasOverruled; ushort overrule nonce.

Client behaviour, in order:

1. If state is already Results (4), ignore (idempotent).
2. Store exiled player, set state = Results (4), `resultsStartedAt = discussionTimer`, store tie and overruled flags.
3. If an exiled player exists, set its `WasEjected` flag (player info offset 0x79) to true.
4. If overruled, show the judge-overrule panel on the local client; if the local player is the winning judge, consume one overrule use and bump a stat.
5. Hide the skip button, show the "skipped voting" tally container, tell every living player's role object that voting completed (`RoleBehaviour.OnVotingComplete`), draw results, set up the proceed button (shown only to the host in non-online modes), fire achievement hooks, close chat.
6. The vote icons are shown one per voter under the matching player area; vote icons for skip are shown in the skip container (the `i == 0` rule in `PopulateResults`). **Anonymous votes** are not handled in this function; the option is read elsewhere (Unknown location; likely `BloopAVoteIcon`).

`ExileController` is spawned later by the proceed/close path (not decompiled), see section 4.

### 3.7 Judge overrule (new role; optional for a clone)

Observed: RPC 0x42 (host only): payload judge id, target id, ushort nonce. The host rejects it if the judge or target lookup fails or the judge is flagged `Disconnected` (player info offset 0x64); otherwise queues the overrule, resets the judge's `DidVote` flag to false (so judge keeps voting), sends a chat note, and re-runs `CheckForEndVoting`.

At vote end, `TryGetWinningOverrule` (not decompiled) picks at most one overrule from the queue. If one exists: `wasOverruled = true`, nonce is forwarded; if the overruled target's role has `TeamType == 1` (Impostor), the exiled player becomes that target; otherwise the exiled player stays the judge's own player info (the judge is ejected for a wrong call). The tie flag from the normal tally is forwarded unchanged. Tag: Observed for the branch on `TeamType`; Inferred for the "judge is ejected" interpretation (the code takes the second out-parameter, the judge's info, as the replacement).

### 3.8 Closing the meeting

`RpcClose` (RPC 0x16, no payload) calls `Close` locally on clients then broadcasts. `Close` sets chat visibility to the local player's dead flag, hides the ban button, and runs the cutscene coroutine (exile animation). Observed `MeetingHud.RpcClose @ 0x2128230`, `Close @ 0x2125490`.

### 3.9 VoteBan system (kick votes in the lobby/game)

Observed `VoteBanSystem.*` (all of it):

- State: `Votes: Dictionary<int targetClientId, int[3] voterClientIds>`; a slot value 0 means empty.
- `CmdAddVote(target)`: calls `AddVote(myClientId, target)` locally, then sends the add-vote message (voter client id, target client id).
- RPC 0x1A handler: reads voter id then target id (both Int32) and runs the same `AddVote`.
- `AddVote(voter, target)`: get or create the array for the target (3 zeroed slots). Scan slots in order: if a slot already equals `voter`, return (no double votes); at the first empty slot write `voter`, mark dirty, and if that was the **third** slot (array now full) call `KickPlayer(target, ban = false)`. There is no scaling with lobby size: three distinct voters always kick.
- `HasMyVote(target)`: true if the local client id is present in the target's array.
- `Serialize`: byte entry count, then for each entry Int32 target id and three packed ints. `Deserialize` mirrors it. `ClearOrDecrementDirt` zeroes the dirty bits.
- Client id 0 cannot be a meaningful voter because 0 means empty (Inferred side effect).

## 4. Exile flow

`ExileController.BeginForGameplay @ 0x24097b4` builds the init data, then runs the same display setup as `Begin @ 0x2409110`:

Init data (Observed): exiled outfit and player info (none for skip/tie), `voteTie`, `wasOverruled`, `isImpostor` = exiled role's `IsImpostor`, `confirmImpostor` = the option `ConfirmImpostor` (read via the logic-options object, Inferred slot match), `totalImpostorCount` = count over all players with a first predicate (impostor role; predicate bodies not decompiled), `remainingImpostorCount` = count with a second predicate (Inferred: impostors that are alive and connected), minus 1 if the exiled player is an impostor and not flagged `Disconnected`.

Text selection (string ids are translation keys; only the logic is relevant):

- Nobody exiled: "no one ejected" text, with separate wording for tie (`voteTie` true) versus skip (false). The impostor-count line is hidden.
- Someone exiled and `ConfirmImpostor` false: "X was ejected" (no team reveal).
- Someone exiled and `ConfirmImpostor` true: four variants by (`isImpostor`, `remainingImpostorCount < 2`): "X was not An Impostor / was An Impostor", each with a singular or plural remaining-impostor wording. The extra count line shows "1 impostor remains" when the remaining count is exactly 1, otherwise "N impostors remain". This count line is shown only when confirm is on and someone was exiled.

Cutscene durations are per-map assets: Skeld 5.0 s, Mira 4.0 s, Polus (Pb) 4.0 s, Airship 5.0 s, Fungle 3.0 s (`Duration` field, Observed (dump) asset data). During the cutscene the exiled player is shown moving out; gameplay is frozen.

`ExileController.WrapUp @ 0x2409b48` (after the animation):

1. If an exiled player exists: if its player object exists, call `PlayerControl.Exiled()` (kills it, death reason Exile); in all cases set the info's `IsDead` flag (offset 0x78) to true.
2. If not in freeplay/tutorial, and the game-flow component does not report that it will handle the resume itself (virtual call on `GameManager.LogicFlow`, Inferred), call `ReEnableGameplay` (unfreezes controls, restores HUD; gameplay presentation).
3. Destroy the cutscene object.

Note: win checks (section 5) are not called here; they run from the periodic end-criteria check (see 5.1), so a parity win after an exile is detected on the next check.

## 5. Win conditions and game-over reasons

### 5.1 Trigger

`LogicGameFlowNormal.CheckEndCriteria @ 0x20de930` is the single function that decides game over for normal mode. Its caller was not in the decompiled set (`LogicGameFlowNormal.FixedUpdate` is empty); the `GameManager.ShouldCheckForGameEnd` flag (offset 0x48) gates it and `EndGameForSabotage` clears it (Observed). Inferred: the host calls it every fixed update while the flag is true. Treat as "evaluate after every state change, and at least once per tick".

### 5.2 Exact order of checks in `CheckEndCriteria`

0. If `GameData.Instance` does not exist, return.
1. **Sabotage countdown (life-support/oxygen special case).** Fetch the system with id 8 (`SystemTypes.LifeSupp`) from the ship's system table. If it exists and its `Countdown < 0.0`: call `EndGameForSabotage` and set its `Countdown` to 10000.0 (stopped).
2. **All critical sabotages.** Iterate every system implementing `ICriticalSabotage` (reactor, oxygen, seismic, heli etc.). For each whose `Countdown < 0.0`: call `EndGameForSabotage`, then `ClearSabotage()` on that system. (Interface slot identity Inferred: slot 1 returns the float countdown, slot 3 is the clear call.) `EndGameForSabotage` (non-freeplay) sets `ShouldCheckForGameEnd = false` and ends the game with reason **ImpostorsBySabotage (4)** (`@ 0x20de73c`). Because steps 1 and 2 precede the player-count checks, a simultaneous sabotage expiry beats everything below.
3. **Count players** with `LogicGameFlow.GetPlayerCounts` (not decompiled). Inferred meaning from usage: triple (`crewAlive`, `impostorsAlive`, `extra`), counting only players who are not `Disconnected` and not `IsDead`; `extra` only matters in the tutorial.
4. **No impostors left:** if `impostorsAlive < 1` and (not tutorial, or `extra > 0`): game over, reason **CrewmatesByVote (0)**. This reason is used regardless of how the last impostor was removed (vote, disconnect, or otherwise). Reason ImpostorDisconnect (5) is never emitted by this function (Unknown where it is used).
5. **Crew still outnumbers impostors:** else if `impostorsAlive < crewAlive`: not over by deaths; run `GameManager.CheckEndGameViaTasks` (task check, section 5.3) and return.
6. **Parity or worse:** else (`crewAlive <= impostorsAlive`): impostors win. Reason by the static `GameData.LastDeathReason` (dump enum `DeathReason`: Exile 0, Kill 1, Disconnect 2): Exile -> **ImpostorsByVote (2)**, Kill -> **ImpostorsByKill (3)**, anything else (Disconnect) -> **CrewmateDisconnect (6)**. The end is issued via `RpcEndGame(reason, showAd)`.

`LogicGameFlowNormal.IsGameOverDueToDeath @ 0x20de8b8` is the same death test as a boolean: true if (`impostorsAlive < 1` and (not tutorial or `extra > 0`)) or `crewAlive <= impostorsAlive`.

Win-side helpers: crew win set {0, 1, 5}; impostor win set {2, 3, 4, 6} (section 0). `GameManager.CanReportBodies` always returns true in normal mode.

Hide-and-seek reasons 7 and 8 are outside this area.

### 5.3 Task completion

`GameManager.CheckEndGameViaTasks @ 0x20d59c0` and `CheckTaskCompletion @ 0x20d5750` (the latter adds a tutorial branch):

1. `GameData.RecomputeTaskCounts()`.
2. If `TotalTasks <= CompletedTasks`: end the game with **CrewmatesByTask (1)** and return true; else false. Note: with zero total tasks (e.g. all task counts set to 0, or everyone excluded) the inequality `0 <= 0` is true, so the crew would win as soon as this check runs; a clone should guard `TotalTasks > 0`.
3. Tutorial branch (freeplay only, a tutorial manager exists): if all of the local player's tasks are complete, show a popup and restart the ship tasks (`ShipStatus.Begin`) instead of ending.

`GameData.RecomputeTaskCounts @ 0x244d460` (also run each `ShipStatus.FixedUpdate`, on player-info deserialize and on disconnect):

```
total = 0; completed = 0
for each player info p:
    skip if p.Disconnected
    skip if p has no task list
    skip if p has no live player object
    if p.IsDead and not option GhostsDoTasks: skip     (dead players count only when ghosts do tasks)
    skip if p.Role is missing or Role.TasksCountTowardProgress is false   (impostor roles: false)
    for each task t of p:  total += 1;  if t.Complete: completed += 1
```

Both counters are reset to 0 first. So the formula is the sum over connected, role-eligible players (alive, or dead when ghosts do tasks) of their assigned tasks; there is no percentage scaling and no weighting by task length. `GameData.CompleteTask(player, taskId) @ 0x244da0c`: find the task by id in the player's list; if found and not complete, set complete and `CompletedTasks += 1`; if already complete or not found, log a warning and do nothing (no double count).

The "GhostsDoTasks" getter identity is Inferred from the virtual slot position next to the confirm-impostor getter and from the semantics.

### 5.4 Disconnects

`GameData.HandleDisconnect(player, reason) @ 0x244dca0`:

- Ignore if the player object or info is missing.
- If the game has **not started** (lobby): remove the player info entirely.
- If started: if already `Disconnected`, only recompute counts; else set `Disconnected = true`, make sure the info has a role object (instantiate the default role if missing), set the static `LastDeathReason = 2 (Disconnect)`, show a leave notification.
- Always recompute task counts afterwards.

Disconnected players keep their `IsDead` flag but are excluded from alive counts and task totals (via the `Disconnected` skip). `GameData.HandleDisconnect() @ 0x244e2b8` (no args): in the lobby only, removes player infos whose client no longer exists.

## 6. NetworkedPlayerInfo data used here

Layout (Observed (dump) field offsets): `PlayerId` byte 0x35, `ClientId` 0x38, `RoleType` ushort 0x50, `RoleWhenAlive` nullable 0x52, `Outfits` 0x58, `PlayerLevel` 0x60, `Disconnected` 0x64, `Role` 0x68, `Tasks` 0x70, `IsDead` 0x78, `WasEjected` 0x79. `TaskInfo`: `Id` uint, `TypeId` byte, `Complete` bool.

`Serialize @ 0x244edbc` field order: PlayerId (byte); ClientId (packed); outfit count (byte) then per outfit {type byte, outfit data}; PlayerLevel (packed uint); flags byte (bit 0 = Disconnected, bit 2 = IsDead; other bits unused); role type (ushort); bool hasRoleWhenAlive then ushort; task count (byte) then each TaskInfo; FriendCode string; Puid string. `Deserialize @ 0x244f148` mirrors it and recomputes task counts afterwards (and adds the player to the roster if absent).

`SetTasks(taskTypeBytes) @ 0x244facc`: ignore if `Disconnected`; if no player object, log an error. If the game manager reports Hide-and-Seek (virtual slot on `GameManager`, Inferred) and `RoleType == Impostor (1)`, the incoming list is replaced by an empty list. Then clear the list and create one `TaskInfo(typeId = bytes[i], id = i)` for each byte, so **task ids are simply the list position 0..n-1**; push the list to the player object and set dirty bit 1. `RpcSetTasks @ 0x244fde8`: applies `SetTasks` locally on clients and sends the task-set message (player's net id plus the byte array).

## 7. Role assignment

`RoleManager.SelectRoles @ 0x221b2f8` (host, at game start):

1. Collect all clients; filter (three predicates, bodies not decompiled; Inferred: client has an id, a character, and player info), order by client id ascending, map to player infos, make a list `players`.
2. Append every player info from `GameData.AllPlayers` whose player object is flagged `isDummy` (tutorial/freeplay dummies).
3. `numImpostors = GetAdjustedNumImpostors(options, players.Count)` (7.1).
4. Assign **impostor team first:** `AssignRolesForTeam(players, options, team = Impostor, teamMax = numImpostors, defaultRole = Impostor)`.
5. Then **crew team:** `AssignRolesForTeam(players, options, team = Crewmate, teamMax = int.MaxValue, defaultRole = Crewmate)`.

Assigned players are removed from `players`, so the crew pass sees only the leftovers.

### 7.1 Impostor count adjustment (`GetAdjustedNumImpostors @ 0x2231d78`)

```
n   = currentGameOptions.NumImpostors
cap = MaxImpostors[GameData.PlayerCount]  if PlayerCount is inside the table, else 3
result = min(n, cap)
if n < 1: result = 1
```

The decompiled body reads the global current options for `n`, the table from the options passed in, and the player count from `GameData`; the explicit `playerCount` argument appears unused (Inferred; decompiler shows no use). The `MaxImpostors` table values are static data not present in the dump (Unknown).

### 7.2 `AssignRolesForTeam @ 0x20df624` (normal mode)

Inputs: `players`, `options`, `team`, `teamMax`, optional `defaultRole`. Local `rolesAssigned = 0`, and a list `bag` of role types. Role data per role: `Count` (max per game, `GetNumPerGame`) and `Chance` (percent, `GetChancePerGame`) from the role options.

1. **Guaranteed pass.** From all roles of this team whose chance is exactly 100 (predicate body Inferred), add `Count` copies of each role type to `bag`. Then call `AssignRolesFromList` (7.3).
2. **Chance pass.** Clear `bag`. For each role of this team with count > 0 and chance not 0 and not 100 (static predicate, body Inferred): repeat `Count` times: draw `r = HashRandom.Next(101)` (0..100); if `r < Chance`, add one copy of the role to `bag`. Call `AssignRolesFromList` again.
3. **Default fill.** If `defaultRole` has a value: while `bag.Count < players.Count` and `rolesAssigned + bag.Count < teamMax`, append `defaultRole`. Call `AssignRolesFromList` a final time.

Effects: guaranteed roles are placed before chance roles, so with a tight `teamMax` they win slots first. Chance roles are rolled per copy, independently (not "pick one of N"). Default fill gives every remaining player the default role up to `teamMax`; for the impostor team that tops the team up to the adjusted impostor count with plain Impostors; for crew it turns everyone left into Crewmates. The first guaranteed pass and the chance pass can never exceed `teamMax` because `AssignRolesFromList` stops at it.

Role data and option detail (chance, count ranges per role) are in the options and roles docs.

### 7.3 `AssignRolesFromList @ 0x20e0144`

```
while bag not empty and players not empty and rolesAssigned < teamMax:
    role   = remove bag[HashRandom.FastNext(bag.Count)]
    index  = HashRandom.FastNext(players.Count)
    RpcSetRole(players[index].object, role)
    remove players[index]
    rolesAssigned += 1
```

Randomness: both the role draw and the player draw are uniform via `HashRandom.FastNext` (a hash-based PRNG, `Next(maxInt)` returns 0..maxInt-1, per the dump naming; the exact hash function is Unknown). There is no explicit shuffle and no weighting by previous games, so the client-id ordering of the player list has no effect on who gets a role.

### 7.4 Roles after death

`LogicRoleSelectionNormal.OnPlayerDeath @ 0x20e02f4` (host only, when the death grants a ghost role): if the player info is dead and has a role, try special ghost roles for the role's team (`RoleManager.TryAssignSpecialGhostRoles`); if the role is not already a ghost role, assign the role's `DefaultGhostRole` (role field 0x88): locally start the role-set coroutine on clients, and broadcast the set-role message. Detailed ghost-role selection is outside the decompiled bodies (Unknown).

## 8. Task assignment

Static data per map (`ShipStatus`): arrays `CommonTasks` (0xB0), `LongTasks` (0xB8), `ShortTasks` (0xC0), `SpecialTasks` (0xC8). Options: `NumCommonTasks` (int option id 10), `NumShortTasks` (11), `NumLongTasks` (12), `VisualTasks` (bool).

### 8.1 Index numbering (`ShipStatus.AssignTaskIndexes @ 0x2250fe0`)

Each task asset gets a stable integer `Index` (PlayerTask offset 0x20), numbered consecutively over the concatenation **Common, then Long, then Short**: commons `0..c-1`, longs `c..c+l-1`, shorts `c+l..c+l+s-1`. Special tasks are not numbered here. The per-player task list sent over the wire contains these indices as bytes (so at most 256 distinct tasks per map). The `Length` enum on `NormalPlayerTask` is None 0, Common 1, Short 2, Long 3; three static callbacks in `Begin` are applied with `ForEach` to the common, long and short lists respectively, which most likely stamp that length value on each task (Inferred; bodies not decompiled).

### 8.2 `ShipStatus.Begin @ 0x2252090`

1. Reset `numScans = 0`; run `AssignTaskIndexes`.
2. Create `usedTypes` (set of `TaskTypes`) and `tasks` (byte list).
3. `commonPool` = shuffled copy of `CommonTasks`; `numCommon` = option value. Call `AddTasksFromList(ref start, numCommon, tasks, usedTypes, commonPool)` (8.3). Then run a second loop `numCommon` times that appends a random remaining common task index and removes it from the pool (stops with a warning "not enough common tasks" if the pool is empty). In practice the later trim (step 6) discards this second batch, so it only matters if the first call produced fewer than `numCommon` entries.
4. `longPool` = shuffled copy of `LongTasks`, `shortPool` = shuffled copy of `ShortTasks`, each with its own persistent `start` pointer that is **shared across all players** (so consecutive players take consecutive entries from the shuffled pool). `numShort`, `numLong` read from options. If `numCommon + numShort + numLong == 0`, force `numShort = 1`.
5. For each player in roster order (including impostors and everyone; roles adjust tasks elsewhere):
   1. Clear `usedTypes`.
   2. Trim `tasks` to its first `numCommon` entries (every player therefore shares the same common tasks).
   3. `AddTasksFromList` with `numLong` from the long pool, then with `numShort` from the short pool.
   4. If the player's object exists and its dummy-behaviour component is **not** enabled, send `RpcSetTasks(tasks)` (the byte list of indices). Dummies receive their tasks through another path.
6. Finally the local player's cosmetic/material setup (presentation).

So each player's list order is: common tasks, then long tasks, then short tasks. Task ids in `NetworkedPlayerInfo` are the positions in that list.

### 8.3 `AddTasksFromList @ 0x2252a1c` (duplicate avoidance)

```
added = 0; guard = 1000
while added < count:
    guard -= 1; if guard == 0: return          # silently gives up; list may be short
    if start >= pool.Count:
        start = 0; shuffle pool
        if every task in pool has its type in usedTypes:
            log "ran out of tasks"; usedTypes.Clear()
    t = pool[start]; start += 1
    if usedTypes.Add(t.TaskType):               # first time this type for this player
        tasks.Add(t.Index); added += 1
        if option VisualTasks is false and t.TaskType == SubmitScan (0):
            numScans += 1 (post-increment); if the old value was > 1: remove t from pool
    else:
        pass                                    # same type already used: skip, try next
```

The set `usedTypes` is cleared per player in `Begin`, so duplicates are prevented **within** one player's long list and short list (a player never gets two tasks of the same `TaskTypes` from the same pass sequence), but the common tasks' types are not re-added to the set, so a long or short task can share a type with a common one in principle. The scan rule: while visual tasks are off, the third time a SubmitScan task is handed out (old counter value 2) it is removed from the pool, so at most three players are scheduled with the scan task per game from that pool; with visual tasks on, no cap. The exact option behind the check is Inferred (virtual slot adjacent to the visual-tasks getter).

### 8.4 Task counts for win progress

Section 5.3. Impostor roles have `TasksCountTowardProgress = false` (field 0x68), so their (fake) tasks never count; dead players count only when `GhostsDoTasks` is on.

## 9. Option getters used by this area

| Getter | Source field | Used by |
|---|---|---|
| `GetDiscussionTime` | `DiscussionTime` int @ 0x50 | `MeetingHud.Update` |
| `GetVotingTime` | `VotingTime` int @ 0x54 | `MeetingHud.Update` |
| `GetEmergencyCooldown` | `EmergencyCooldown` int @ 0x40 | emergency button cooldown (setter not seen) |
| `GetNumEmergencyMeetings` | `NumEmergencyMeetings` int @ 0x3C | per-player emergency count |
| `GetConfirmImpostor` | `ConfirmImpostor` bool @ 0x58 | `ExileController` text |
| `GetVisualTasks` | `VisualTasks` bool @ 0x59 | scan-task cap in `AddTasksFromList` (Inferred) |
| `GetAnonymousVotes` | `AnonymousVotes` bool @ 0x5A | vote icon display (consumer not in decompiled set) |
| `GetTaskBarMode` | `TaskBarMode` int @ 0x5C (Normal 0, MeetingOnly 1, Invisible 2) | HUD task bar (presentation) |

All are plain field reads; no clamping happens in the getters. Defaults and ranges: see the options doc (not repeated here).

## 10. Implementation notes for our C# sim

- **Meeting state machine.** Implement states 0 to 5 as the enum above. Clock: start the shared clock when the intro ends; discussion for `D` seconds, voting for `V` seconds if `V > 0` (else unlimited), results for a fixed 5 s, then proceed automatically (always, not only in online mode, since our server drives it). Tick in fixed steps and truncate displayed seconds toward zero.
- **Authoritative vote handling.** Validate on the server: meeting state is NotVoted or Voted, sender equals voter, voter alive and has not voted, target is a living, connected player or the skip value. Keep the four sentinel values (255/254/253/252) in one byte if you want wire parity; otherwise use an enum.
- **Early end and timeout.** End when every living, connected player has a vote recorded. On timeout set every missing vote to "missed" (ignored by the tally) and end.
- **Tally.** Count 1 per vote, include skip as a candidate, ignore dead, not-voted and missed. A strict maximum wins; a shared maximum (including a player tied with skip) is a tie: nobody exits. Skip winning outright also exits nobody. Announce `tie` separately from "skip".
- **Reports.** Add the checks the original leaves to the client: reporter alive, body within `MaxReportDistance` (3.0), body not already reported, no meeting in progress, game not over, tasks not already complete; for the emergency button also `ShipStatus.Timer >= 15` (use game time since start), `EmergencyCooldown <= 0`, `RemainingEmergencies > 0`, and no active critical sabotage. Decrement the emergency count on the server. Keep the order: validate, record meeting info, check task completion (refuse the report if tasks are done), then broadcast.
- **Exile.** Server sets `IsDead` and `WasEjected` at the end of the cutscene delay (3 to 5 s depending on map) or immediately at proceed time; reveal text is purely a function of `ConfirmImpostor`, `isImpostor`, and the remaining impostor count (alive, connected, minus the exiled one).
- **Win checks.** Evaluate in this exact order: critical sabotage countdown below zero, then no impostors alive, then crew strictly outnumbering impostors (go to task check), otherwise impostor victory. Count only connected, alive players. Choose the reason from the last death reason (vote, kill, disconnect). Tasks: sum over connected, role-eligible players, including dead players only when ghosts do tasks; guard `total > 0`.
- **Disconnects.** Flag players disconnected rather than deleting them mid-game; they must not block vote completion and must be excluded from alive counts and task totals; remember the death reason so a parity win caused by a leaver reports the disconnect reason. Also decide how to report an all-impostors-left case (the original maps it to reason 0 here, though reason 5 exists).
- **Roles.** Order the player list, assign impostors first with `teamMax = adjusted impostor count`, then crew with no cap. Use two passes per team (guaranteed 100 percent roles, then per-copy chance rolls) plus a default fill. Use a seeded PRNG you control for replays. Clamp the impostor count to `min(configured, table[playerCount])`, at least 1, and fall back to 3 for unknown player counts.
- **Tasks.** Keep a per-map indexed catalog (common, long, short in that order). For each game: shuffle pools, take the same commons for everyone, then give each player long and short tasks from the shared round-robin pointers with per-player type de-duplication and the wrap/reshuffle/clear-used fallback; give up after 1000 iterations. Number tasks per player by list position. Decide yourself whether to keep the "scan task at most three times" rule (it is a quirk tied to visual tasks being off).
- **Presentation only (omit server-side).** Sounds, vibration, countdown pulses, vote-icon animation, overlay text, chat-note markers, avatar placement; the vote-spread layout and anonymous-vote icon handling belong to the client renderer.
- **Vote-kick.** If you keep a vote-kick, replicate the fixed threshold of three distinct voters per target and per-voter de-duplication, but consider scaling it to the lobby size; the original does not.

## 11. Open questions

1. `ShipStatus.CoStartMeeting`, `MeetingHud.ServerStart`, `CoIntro` and `HandleProceed` bodies were not decompiled, so the intro length, how voting areas are populated (living versus dead players, reporter marking), when exactly state Discussion begins and what proceed does in sequence are Inferred only.
2. `MeetingHud.ForceSkipAll`, `HandleDisconnect` and `HandleDisconnectedPlayerVoteArea`: how a leaver's vote area is finalised is Inferred.
3. `Extensions.MaxPair` and the exile lookup lambda: tie semantics (including the empty tally) are Inferred from the call shape.
4. `LogicGameFlow.GetPlayerCounts` return meaning (especially the third value) is Inferred; it is used only in tutorial logic.
5. What calls `CheckEndCriteria` and how often, and whether it is suppressed during a meeting (`ShouldCheckForGameEnd`), was not visible.
6. Where `ShipStatus.EmergencyCooldown` is reset and where `RemainingEmergencies` is initialised from the options.
7. Who emits GameOverReason 5 (ImpostorDisconnect); `CheckEndCriteria` never does.
8. Contents of the static `MaxImpostors` lookup table; exact `HashRandom` hash and how `FastNext` differs from `Next`.
9. Predicate bodies in role selection (guaranteed versus chance filters) and in `ExileController` (impostor counting) are Inferred from structure; the three `Begin` callbacks on task lists are assumed to stamp the task length.
10. Consumer of the `AnonymousVotes` option (not found in the decompiled functions).
11. The unresolved identity of two virtual getters (ghosts-do-tasks in `RecomputeTaskCounts`, visual-tasks in `AddTasksFromList`) is Inferred from slot position; the Hide-and-Seek check in `SetTasks` is Inferred from the call target.
12. Note for the coordinator: a background shell command from an earlier step was stopped by the system under memory pressure; it was not restarted and nothing depends on it.
