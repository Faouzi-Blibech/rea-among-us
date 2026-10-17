# Logic: movement, network sync and game options

Behaviour spec derived from the decompiled Among Us mobile client (Android 2026.9.29, IL2CPP arm64). It covers (1) how a player's position is replicated (`CustomNetworkTransform`), (2) player speed (`PlayerPhysics`), (3) what each `HandleRpc` does for `PlayerControl`, `MeetingHud` and `ShipStatus`, and (4) game option defaults, recommended presets and impostor-count adjustment. It is written so a C# developer can implement it without seeing the decompiled code.

Evidence tags: **Observed (decompiled)** = read from a function body (cited as `Class.Method @ 0xRVA`); **Observed (dump)** = from signatures/consts/fields; **Observed (disassembly)** = read directly from machine code or a data table when the decompiler output was not enough; **Inferred** = interpretation, reason given. Anything unresolved is in "Open questions".

## 1. Key numbers

| Item | Value | Evidence |
|---|---|---|
| Network send pacing | one pass every `MinSendInterval` = **0.1 s** (10 Hz), driven by the fixed-update timer, only when the client state is non-zero | Observed (disassembly) `InnerNetClient.FixedUpdate @ 0x231f690`, `InnerNetClient..ctor @ 0x2321460` (field default 0x3DCCCCCD). It is a public field, so a prefab could override it (Unknown) |
| Position wire format | 2 x uint16 (x then y), little-endian, 4 bytes | Observed (decompiled) `NetHelpers.WriteVector2 @ 0x213de24` |
| Quantization | `u = trunc(clamp01((v + 50) / 100) * 65535)`; decode `v = -50 + (u / 65535) * 100`; range **[-50, 50]** both axes, step 100/65535 = 0.001526 | Observed (decompiled) `NetHelpers..cctor @ 0x2140094` (range -50..50), `FloatRange.ReverseLerp @ 0x23d4a80`, `FloatRange.Lerp @ 0x23c9a50`; constant 65535.0 Observed (disassembly) |
| Sequence ids | uint16, wrap-aware "newer" = forward distance 1..32767 | Observed (decompiled) `NetHelpers.SidGreaterThan @ 0x213dfd0` |
| Send trigger (owner) | enqueue current position every fixed tick while distance to last *sent* position > **0.0001** | Observed (decompiled) `CustomNetworkTransform.HasMoved @ 0x213e190` |
| Remote queue thresholds | smoothing target switches at queue count > **5**; "extend frame" only if count < **6**; skip-ahead at count >= **12**, second skip when still >= **14** | Observed (decompiled) `SetMovementSmoothingModifier @ 0x213e3fc`, `ShouldExtendCurrentFrame @ 0x213f310`, `SkipExcessiveFrames @ 0x213e2a0` |
| Rubberband modifier | starts 1.0; per fixed tick moves toward target (0.995 if count > 5, else 0.5) by `clamp01(fixedDt * 3)` | Observed (decompiled) `SetMovementSmoothingModifier`; ctor value 1.0 Observed (decompiled) `CustomNetworkTransform..ctor @ 0x213f4e4` |
| Remote velocity cap | speed capped to **10** units/s | Observed (decompiled) `MoveTowardNextPoint @ 0x213e498` |
| Snap sequence bump | `SnapTo(pos)` = `lastSeq + 2`; `Halt` = `lastSeq + 1`; `RpcSnapTo` local `lastSeq + 1`, wire id = `lastSeq(after) + 2` | Observed (decompiled/disassembly) |
| Base speed | `PlayerPhysics.Speed` = **2.5**, `GhostSpeed` = **3.0** (prefab values) | Observed (dump) `docs/data/gameplay/player_tuning.json` |
| Speed formula | alive: `Speed * PlayerSpeedMod`; dead: `GhostSpeed * PlayerSpeedMod` | Observed (decompiled) `PlayerPhysics.get_TrueSpeed @ 0x21e48f0`, `get_SpeedMod @ 0x21e4910` |
| Option defaults (constructor) | MaxPlayers 10, speed 1.0, crew light 1.0, impostor light 1.5, kill cooldown 15 s, tasks 1 common / 1 long / 2 short, meetings 1, emergency cooldown 0, impostors 1, ghosts do tasks true, kill distance index 1, discussion 15 s, voting 120 s, confirm ejects true, visual tasks true, anonymous votes false, task bar Normal | Observed (decompiled) `NormalGameOptionsV12..ctor @ 0x238de40` |
| Recommended impostors / max impostors | tables indexed by player count 4..15 (section 5.2) | Observed (dump contents) global metadata; mapping Inferred |
| Impostor adjustment | `adjusted = (configured < 1) ? 1 : min(configured, MaxImpostors[playerCount])`, default cap 3 if table missing/out of range | Observed (decompiled) `IGameOptionsExtensions.GetAdjustedNumImpostors @ 0x2231d78` |

Assumption used for per-tick numbers: Unity `Time.fixedDeltaTime` = 0.02 s (engine default). It is not visible in the decompiled code (**Unknown**); the queue thresholds (5, 6, 12, 14) fit a 0.1 s send batch carrying about 5 positions at 0.02 s per tick, which supports the assumption (Inferred).

## 2. CustomNetworkTransform (position replication)

### 2.1 State

| Field | Type | Meaning |
|---|---|---|
| `lastSequenceId` | uint16 | Highest accepted sequence id (0x62) |
| `sendQueue` | queue of Vector2 | Owner only: positions waiting to be sent |
| `incomingPosQueue` | queue of Vector2 | Remote only: received positions waiting to be played back (jitter buffer) |
| `rubberbandModifier` | float, starts 1.0 | Speed scale for catching up/slowing down |
| `idealSpeed` | float, starts 0 | Last computed units/s needed to reach the head position in one tick |
| `isPaused` | bool | When true, serialize/deserialize/fixed update do nothing |
| `lastPosition` | Vector2 | Body position stored at the end of the previous playback step |
| `lastPosSent` | Vector2 | Last position actually written to the wire (also initialised in Awake to the transform position) |
| `tempSnapPosition` | optional Vector2 | Snap target held back while the player is "invisible" (see 2.7) |
| dirty counter | uint (InnerNetObject.DirtyBits) | Treated as a small counter, see 2.3 |

Awake (Observed (decompiled) `CustomNetworkTransform.Awake @ 0x213d6ec`): grabs the Rigidbody2D and the owning `PlayerControl`, sets `lastPosSent` to the current transform position and enqueues that position into `incomingPosQueue` so the queue is never empty. OnEnable sets the dirty counter to at least 3 (bitwise OR 3) so an enabled object is sent a few times (Observed (decompiled) `OnEnable @ 0x213da44`); it also disables this component if a certain sibling component (type not resolved) is present and enabled (Inferred: an alternative transform driver).

### 2.2 Wire format

Update (non-initial, "DataFlag"), Observed (decompiled) `Serialize @ 0x213ec10` / `Deserialize @ 0x213ef04`:

1. `firstSequenceId` uint16.
2. `count` packed int.
3. `count` positions, each 4 bytes (quantized as in section 1). Position `i` carries id `firstSequenceId + i` (uint16 arithmetic, wraps at 65536).

Initial state (spawn / late join): `lastSequenceId` uint16, then one quantized position of the body (Serialize with `initialState = true`). On receive: set `lastSequenceId`, set the transform position directly, clear `incomingPosQueue`, enqueue that position.

Rules:
- If `isPaused`, Serialize returns "nothing to send" (false) and Deserialize returns without reading.
- Non-initial Serialize on an inactive/disabled component clears dirty bits and returns false.
- The packer treats a false return as "cancel this sub-message".

### 2.3 Send path

Pacing (Observed (disassembly)): `InnerNetClient.FixedUpdate` adds `fixedDeltaTime` to a timer each fixed tick once the client state is non-zero; when `timer >= MinSendInterval (0.1 s)` it resets to 0 and calls `SendAllStreamedObjects`. That function first flushes the queued reliable and unreliable game-data messages, then packs dirty streamed objects once for the reliable collection and once for the unreliable collection (Observed (decompiled) `InnerNetClient.SendAllStreamedObjects @ 0x231f6f8`).

Packing rule per object (Observed (decompiled) `InnerNetClient.PackAndSendDirtyStreamedObjects @ 0x231fd88`): round-robin from a persistent cursor; an object is sent if it is dirty AND (the local client owns it OR it is host-owned (owner id -2) and the local client is the host). Each becomes a DataFlag sub-message (tag 1, packed netId, `Serialize(false)` body). If a sub-message makes the packet exceed **500 bytes**: if it is not the first in the packet it is cancelled and the cursor steps back (sent next pass), otherwise it is sent anyway with a warning. After a successful write the object's `ClearOrDecrementDirt` runs. Which of the two collections a transform lives in (movement reliability) is **Unknown**.

Dirty counter (Observed (decompiled) `ClearOrDecrementDirt @ 0x213ebfc`): decrement by one if non-zero. Owner sets the counter to 2 whenever it enqueues a position. So after the last movement a trailing update (one duplicate of the last position) is sent on the next pass, then it stops.

Owner tick (Observed (decompiled) `FixedUpdate @ 0x213e018`, owner branch), unless paused or the player object is gone:
1. If distance(current body position, `lastPosSent`) <= 0.0001: do nothing.
2. Else enqueue the current body position into `sendQueue` and set dirty counter to 2.
Note `lastPosSent` only changes when a packet is written, so while the player is more than 0.0001 away from the last sent point **every fixed tick enqueues a point** (about 5 per 0.1 s pass at 0.02 s).

Non-initial Serialize (owner):
1. If `sendQueue` is empty, enqueue `lastPosSent` (guarantees at least one point).
2. `seq = lastSequenceId + 1` (uint16); write `seq`; write `count`; write every queued point and remember the last as `lastPosSent`; clear the queue.
3. `lastSequenceId = seq + count - 1`.

### 2.4 Sequence-number handling

`SidGreaterThan(new, prev)`: let `limit = prev + 0x7FFF` in uint16 arithmetic. If `limit` did not wrap (prev < limit): newer iff `prev < new <= limit`. If it wrapped: newer iff `new > prev OR new <= limit`. Equivalent: `((new - prev) mod 65536)` is in 1..32767. Equal ids are stale. Observed (decompiled) `NetHelpers.SidGreaterThan @ 0x213dfd0`. A byte version exists with a 127 window (Observed (dump) signature; same shape Inferred).

Receive (non-owner, Observed (decompiled) `Deserialize`): ignored entirely for the owner. For each of the `count` points: `id = first + i`; read the point (always read, even if stale); if `SidGreaterThan(id, lastSequenceId)`: `lastSequenceId = id`, enqueue the point. Stale or duplicate points are dropped silently.

After the loop (Inferred partly): if the object is currently "invisible" (2.7) the last point read is stored in `tempSnapPosition`; otherwise `tempSnapPosition` is cleared. When the queue was empty before the batch, the "last known point" used for this comes from the body position.

### 2.5 Remote playback (every fixed tick, non-owner)

Observed (decompiled) `FixedUpdate @ 0x213e018` non-owner branch plus helpers.

1. If `tempSnapPosition` holds a value and the player is not invisible: `SnapTo(value, lastSeq + 2)` and clear it.
2. If `incomingPosQueue` is empty: return (never happens after first point; see 5).
3. `SkipExcessiveFrames()`.
4. `SetMovementSmoothingModifier()`.
5. `MoveTowardNextPoint()`.

`SkipExcessiveFrames` (`@ 0x213e2a0`): if queue count < 12 return. Otherwise put the body exactly on the head point (no dequeue; with no Rigidbody the head is dequeued and the transform set) and run one `MoveTowardNextPoint`. If the count is then still >= 14, put the body on the new head again and run another step. Net effect per tick: initial count 12-14 -> up to 2 pops, initial >= 15 -> 3 pops (two skip steps + the regular step), because any step with count >= 6 pops one point.

`SetMovementSmoothingModifier` (`@ 0x213e3fc`): `target = (count > 5) ? 0.995 : 0.5`; `t = clamp(fixedDeltaTime * 3, 0, 1)`; `rubberband += (target - rubberband) * t`. With 0.02 s ticks `t = 0.06`.

`DidPassPosition(next, last, current)` (`@ 0x213f3a4`): true when `| dist(last,current) - (dist(last,next) + dist(current,next)) | < 0.003`, i.e. `next` lies on the segment last -> current (the body reached or passed the head point this tick).

`ShouldExtendCurrentFrame(next, current)` (`@ 0x213f310`): `!DidPassPosition(next, lastPosition, current) AND count < 6`. "Extend" means keep heading to the same head point instead of consuming it.

`MoveTowardNextPoint` (`@ 0x213e498`):
1. `next = queue.peek()`, `current = body.position`, `extend = ShouldExtend(next, current)`.
2. If **not extend**:
   - If count > 1: `popped = dequeue()`. If distance(body, popped) > **0.05**, set the body position to `popped`. Let `next = queue.peek()` (new head). `idealSpeed = distance(next, body.position) / fixedDeltaTime`. `dir = normalize(next - body.position)`, or zero if the distance <= 1e-5.
   - If count == 1: if distance(body, next) <= **0.01** return unchanged; otherwise set body position to `next`, set velocity to zero, set `lastPosition = body.position`, return.
3. If **extend**: `dir = normalize(next - current)` (zero if distance <= 1e-5); `idealSpeed` is left as computed on the previous step.
4. `velocity = dir * idealSpeed * rubberband`; if `|velocity|^2 > 100` rescale to length **10**. Assign to the body velocity.
5. `lastPosition = body.position` (position at this call, before the physics step).

Consequences: the last point in the queue is never dequeued (count 1 holds position); a healthy queue of 6+ points is consumed one per tick; below 6 the body only advances to the next point after passing the head; the rubberband slows playback to 0.5x when the buffer is thin (<= 5) and 0.995x when it is healthy so the buffer refills.

### 2.6 Snap rules

All snaps share one acceptance rule (Observed (decompiled) `SnapTo(pos, minSid) @ 0x213db54`): accept only if `SidGreaterThan(minSid, lastSequenceId)`; otherwise ignore. If accepted:
1. If the player is "invisible" (2.7): do NOT move; store `tempSnapPosition = pos` and return (the sequence id is not updated).
2. Else clear both position queues, `lastSequenceId = minSid`, clear `tempSnapPosition`, set the Rigidbody and Transform position to `pos`.

Variants:
- `SnapTo(pos)`: `minSid = lastSequenceId + 2` (`@ 0x213df08`; `SNAP_TO_SEQUENCE_ID_BUFFER = 2`).
- `Halt()`: `SnapTo(transform.position, lastSequenceId + 1)` (`@ 0x213db14`): freeze in place and bump the id by 1.
- `RpcSnapTo(pos)` (`@ 0x213dd08`): if the local client is a client (`AmClient`), first `SnapTo(pos, lastSequenceId + 1)`. Then send RPC 21 reliably, broadcast: `Vector2 pos` then `uint16 id` where `id = lastSequenceId (after the local snap) + 2`. So when the local snap applied, the wire id is the old id + 3; otherwise old id + 2.
- `HandleRpc(21)` (`@ 0x213ea50`): only if the component is active and enabled; reads `Vector2`, `uint16 id`; applies the same acceptance rule with `minSid = id`. Any other call id is ignored.

Effect of the buffer: positions already in flight with ids <= the snap id are rejected as stale; the owner's next update starts at `lastSequenceId + 1`, so a couple of immediately following points can also be dropped on peers that adopted the larger id; this is intended hysteresis (Inferred).

### 2.7 Invisible state and misc

- "Invisible" (`IsInMiddleOfAnimationThatMakesPlayerInvisible @ 0x213df7c`): the player is playing the enter-vent animation OR `PlayerControl.walkingToVent` is true. Snaps and fresh positions are deferred through `tempSnapPosition` during this window.
- `SetPaused(b)` simply sets `isPaused`. `ClearPositionQueues` clears whichever queue applies (send queue if owner, incoming if not).
- `HasMoved` uses the Rigidbody position if present, else the transform position.
- `OnDrawGizmos` and the debug ghost/logger hooks are editor/debug only (nothing relevant).

### 2.8 Corrections to `docs/02-networking.md` section 5

- `MinSendInterval` is **0.1 s** (previously "value not visible").
- Dirty counter: set to 2 on movement, decremented per send, giving one trailing update (previously only "decrement").
- `SnapTo` RPC payload is `Vector2` then `uint16` (the sequence id to adopt), not described as "minSid" supplied by the caller; the id written is `lastSeq + 2` after the local snap.
- Quantization clamps to the range (positions outside +-50 saturate); the earlier formula is right otherwise.
- Remote playback thresholds are now exact (5/6/12/14, 0.5/0.995, 3.0 lerp rate, 10 u/s cap, 0.05 and 0.01 distances).
- Who may send RPC 21: the handler accepts it from anyone via the server; the server rule (owner/host) lives outside the client (Reference). The client calls `RpcSnapTo` on other players' transforms too (host teleports).

## 3. PlayerPhysics

- `get_TrueSpeed = Speed * SpeedMod` (`@ 0x21e48f0`).
- `get_SpeedMod` (`@ 0x21e4910`): `mod = GameManager.Instance.LogicOptions.GetPlayerSpeedMod(myPlayer)`. If `GameManager.Instance` is null, `SpeedMod = 1.0`. If the player's data exists and `IsDead`: `SpeedMod = mod * GhostSpeed / Speed`, so a ghost's true speed is `mod * GhostSpeed`. Otherwise `SpeedMod = mod`.
- `LogicOptions.GetPlayerSpeedMod` (normal mode, Observed (disassembly) `@ 0x20d3d58`): reads float option `PlayerSpeedMod` (id 2) from the current game options; if the option is missing returns 1.0. No clamp. (Hide and Seek has its own override, not covered.)
- Resulting speeds with default options: crew/impostor 2.5 units/s, ghost 3.0 units/s. With `PlayerSpeedMod = m`: 2.5m and 3.0m.
- `SetNormalizedVelocity(dir)` (Observed (disassembly) `@ 0x21e5164`): body velocity = `dir * Speed * SpeedMod` (component-wise; despite the name the direction is not renormalized, magnitude comes from the input, normally <= 1). Note it multiplies by `Speed * SpeedMod`, i.e. TrueSpeed.
- `FixedUpdate` (`@ 0x21e4c48`), in order: (1) `HandleAnimation(isDead)`; (2) if owner: if `PlayerControl.CanMove` and the HUD joystick exists, read the joystick direction and call `SetNormalizedVelocity`; (3) owner: `CheckCancelPetting`; (4) if the phantom fade is active, apply the fade alpha to the body, cosmetics, hat, visor and pet sprites (presentation only). When the player cannot move no velocity is written by this function (other code zeroes it, Unknown).
- `HandleAnimation(isDead)` (`@ 0x21e4e5c`) is presentation only: skip while spawn/climb animations play or a custom animation runs; if alive and `|velocity|^2 < 0.05` play idle; else flip X by the sign of velocity.x with a +-0.01 dead zone and play the run animation; if dead play the ghost (or guardian-angel) idle animation and set hat/visor alpha 0.5. Nothing relevant for server logic except the 0.05 and 0.01 thresholds.

## 4. RPC handlers

Direction note: the client-side `HandleRpc` functions do not know the sender. "Who may send" comes from the host-authoritative pattern: a handler marked "host only" in the code is gated by `AmHost` inside the handler; for the rest the relay-server rules in `docs/02-networking.md` (Reference) apply. Where the handler itself performs validation, it is listed in order.

### 4.1 PlayerControl.HandleRpc (`@ 0x21de7dc`)

Unlisted ids fall through to a virtual role handler (`HandleRoleRpc`, e.g. vanish/appear, 62-65) (Observed (decompiled): default case calls a virtual method).

| Id | Name | Payload read | Gating in handler | Effect |
|---|---|---|---|---|
| 0 | PlayAnimation | byte type | none | type 1: play ship shields animation on all shield images; type 6: play next weapon-fire clip (index cycles); types 9 and 10: play hatch animation and particles; other types ignored |
| 1 | CompleteTask | packed uint task id | none; looks up the task on this player's data; if missing logs a warning | If this client owns the player and the role is Scientist (role 2) the role recharges and a stat increments; `GameData.CompleteTask(player, id)`; then notifies the game manager (virtual) |
| 4 | Exiled | none | none | Marks the player dead (`Die`); owner increments a stat. Not only legacy as `docs/02` says |
| 5 | CheckName | string | host runs the dedupe (clients only reach this on the host) | If another player already has that name (ignore case) try `name + <separator> + i` for i = 1..99, then apply the result to the player data (this makes the set-name message) |
| 6 | SetName | uint, string | none | Sets the displayed name |
| 7 | CheckColor | byte color | host | Starting at the requested id, while a different player uses it, advance `(id + 1) mod paletteSize`, at most 100 attempts; then set color and queue the `SetColor` message reliably |
| 8 | SetColor | uint, byte | none | Sets body color |
| 11 | ReportDeadBody | byte target player id (255/unknown = none) | In order: ignore if game over; ignore if a meeting hud already exists; if target is none (emergency) and any ship system is in critical sabotage (list lookup) ignore (Inferred); ignore if reporter is dead; then all clients record reporter/target in `MeetingRoomManager.AssignSelf`; non-hosts stop. Host: if `GameManager.CheckTaskCompletion()` ends the game, stop; else open the meeting room and `RpcStartMeeting(target)` | Starts the meeting flow on the host |
| 12 | MurderPlayer | victim (net object), int `MurderResultFlags` | none | Flags: 1 Succeeded, 2 FailedError, 4 FailedProtected, 8 DecisionByHost. `FailedError` -> nothing. `FailedProtected`, or `DecisionByHost` with victim `protectedByGuardianId >= 0` -> protected-failure path (victim shield flag set, killer owner shows failed-murder UI, **killer cooldown set to KillCooldown x 0.5**; non-owners just remove the shield visual). Succeeded, or DecisionByHost on an unprotected victim -> victim dies via the kill animation, victim's scanner turned off, **owner killer cooldown reset to full `KillCooldown`** (float option id 1), kill sfx, stats |
| 13 | SendChat | string | none | Adds a chat message if the chat controller exists |
| 14 | StartMeeting | byte target player id | none | Runs `ShipStatus.StartMeeting(reporter data, target data)` (opens the meeting on every client); stats if owner |
| 15 | SetScanner | bool on, byte counter | stale if `counter < stored counter` (plain byte compare, no wrap handling) | Starts/stops the Medbay scan animation and visuals |
| 16 | SendChatNote | byte player, note type | none | Adds a system chat note |
| 18 | SetStartCounter | packed int counter, byte seconds | stale if `counter <= stored counter` | Updates the lobby start countdown (255 cancels it) |
| 32 | UsePlatform | none | **host only** and only on the Airship map | Runs the moving-platform use and sets bit 0x1000 on this object's dirty bits |
| 33 | SendQuickChat | quick-chat result | none | Shows the phrase unless chat is disabled in settings |
| 38 | SetLevel | packed uint | none | Sets cosmetic level |
| 39-43 | Set{Hat,Skin,Pet,Visor,NamePlate}Str | string id, byte sequence id | per-rpc sequence check (`ShouldProcessRpc`) | Applies cosmetic via coroutine |
| 44 | SetRole | uint16 role, bool canOverride | none | Starts `CoSetRole(role, canOverride)` |
| 45 | ProtectPlayer | target (net object), byte color | none | Guardian-angel protection on target. If the local role has a per-use hook it fires; the shield duration uses float option `ProtectionDurationSeconds` (id 1100); visibility to impostors from an int/bool role option |
| 46 | Shapeshift | target (net object), bool animate | none | Clears the waiting-for-response flag and applies the shapeshift (target outfit) |
| 47 | CheckMurder | victim (net object) | host only; checks in order: game not over; local client is host; victim non-null; killer not dead; killer role is impostor; killer not disconnected; victim has data and is not dead; victim not in vent; victim not playing enter-vent animation; victim not playing any ladder animation; victim not on a moving platform; no meeting hud open | On pass: set killer `isKilling = true`, `RpcMurderPlayer(victim, Succeeded)`. On fail: logs, `RpcMurderPlayer(victim, 0)`. Kill range and cooldown are NOT checked here (client side) |
| 48 | CheckProtect | target (net object) | host only; game not over; sender role is Guardian Angel (role 4); not impostor; not disconnected; target has data and is alive | `RpcProtectPlayer(target, sender color)` |
| 51 | CheckZipline | bool fromTop | host only; Fungle map only; game not over; sender alive; not on moving platform; no meeting; **distance sender -> zipline entrance <= 3.0** | Calls the zipline use (`RpcUseZipline`) |
| 52 | UseZipline | (read in handler) | Fungle only | Starts the zipline ride for the player |
| 53 | TriggerSpores | packed int mushroom id | Fungle only | Triggers that mushroom if known and not already triggered |
| 54 | CheckSpore | packed int mushroom id | host only; game not over; no meeting | Calls `RpcTriggerSpores` |
| 55 | CheckShapeshift | target (net object), bool animate | host only; game not over; target has data; sender alive; sender role is Shapeshifter (role 5); not disconnected; reject if (target mushroom-mixup active and animate) or (meeting open and animate) | Valid: `RpcShapeshift(target, animate)`; otherwise `RpcRejectShapeshift` |
| 56 | RejectShapeshift | none | none | Clears `waitingForShapeshiftResponse` |
| 3, 9, 10, 17, 36, 37 | legacy / deprecated | n/a | n/a | No case in this handler |

Ids 2, 19-31 (except 32), 34, 35, 49, 50, 60-67 are handled by other objects (`GameData`, `PlayerPhysics`, `MeetingHud`, `ShipStatus`, `LobbyBehaviour`, `VoteBanSystem`) or the role handler.

Corrections to `docs/02-networking.md` 3.3: 4 (Exiled) still kills; 0 is a ship-animation trigger, not a role animation; 32 is processed only by the host (a request); 5 and 7 are processed by the host as dedupe/uniqueness checks; 12 carries `MurderResultFlags`, not a bool, and the failed-protected path halves the killer cooldown; 47 and 55 are validated by the host with the conditions above.

### 4.2 MeetingHud.HandleRpc (`@ 0x2129dbc`)

| Id | Name | Payload | Gating | Effect |
|---|---|---|---|---|
| 22 | CloseMeeting | none | none | Equivalent to `MeetingHud.Close()`: set chat visibility from the local player's dead flag, hide the ban button, start the end cutscene coroutine. `RpcClose` sends it reliably after closing locally |
| 23 | VotingComplete | packed int n, then n `VoterState` (voterId byte, votedForId byte), byte exiled id, bool tie, bool wasOverruled, uint16 overruleNonce | Ignored if state is already Results (4) | Sets exiled player (sets `WasEjected` on it), state = Results, stores tie/overruled; if overruled and the local role is Judge with matching nonce the overrule usage is consumed; hides vote UI, populates results, closes chat/overlay |
| 24 | CastVote | byte voterId, byte suspectId | Ignored if the voter's area is dead (`AmDead`) or already voted (`DidVote`) | Records the vote in the voter's `PlayerVoteArea`, sets dirty bit 1, runs `CheckForEndVoting`, local client adds a "voted" chat note and queues `RpcSendChatNote` |
| 25 | ClearVote | byte voterId | Ignored if the area is dead | If voter is the local player: vote UI reset, state back to NotVoted (2); clears the area's vote and `DidVote` |
| 66 | QueueOverruleVotes | byte judgeId, byte targetId, uint16 nonce | **host only**; judge exists and is connected; target exists and is connected | Stores/updates the judge overrule; marks judge area not voted; sends a chat note; sets dirty bit; if every eligible player has voted, tallies (`CalculateVotes`, max pair, winning overrule) and calls `RpcVotingComplete` |

States: Animating 0, Discussion 1, NotVoted 2, Voted 3, Results 4, Proceeding 5 (Observed (dump) `MeetingStates`).

Corrections: `docs/02` is consistent with the ids; 22 does not "close the UI" only, it triggers the closing cutscene; CastVote has no sender/ownership check on the client (confirms the doc note), only dead/duplicate filtering.

### 4.3 ShipStatus.HandleRpc (`@ 0x2253d2c`)

| Id | Name | Payload | Effect |
|---|---|---|---|
| 27 | CloseDoorsOfType | byte system type | Looks up the doors system (key 16) and closes the doors of that room type (Inferred virtual call on the system) |
| 35 | UpdateSystem | byte system type, net object (player), reader | Looks up the system by type in `Systems`; if found logs and applies `UpdateSystem(player, reader)` (the actual call is hidden behind a no-return path in the decompiled body, Inferred) |
| 67 | SpiritGuideMessage | byte array of image indexes (1-3) | Client-side display: ignored if a meeting hud exists; instantiates the message popup under the HUD and fills it with sprites by index; an index >= sprite-list length is skipped with a warning |
| other | | | Ignored |

## 5. Option data model

`NormalGameOptionsV12` fields (Observed (dump)): GameMode, SpecialMode, RulesPreset, MaxPlayers, Keywords, MapId, IsDefaults, PlayerSpeedMod, CrewLightMod, ImpostorLightMod, KillCooldown, NumCommonTasks, NumLongTasks, NumShortTasks, NumEmergencyMeetings, EmergencyCooldown, NumImpostors, GhostsDoTasks, KillDistance (index), DiscussionTime, VotingTime, ConfirmImpostor, VisualTasks, AnonymousVotes, TaskBarMode, Tag, roleOptions.

`GameModes`: None 0, Normal 1, HideNSeek 2, NormalFools 3, SeekFools 4. `RulesPresets`: Standard 0, StandardRoles 1, Flashlight 2, Custom 100. `TaskBarMode`: Normal 0, MeetingOnly 1, Invisible 2. `GameKeywords` default 256 = English.

Getters: `GetByte(1)` returns MapId, any other id 0; `GetFloat`, `GetBool`, `GetInt` call the corresponding `TryGet*` and return 0/false when the id is unknown (Observed (decompiled) `NormalGameOptionsV12.GetByte/GetFloat/GetBool/GetInt @ 0x2392598..`). The `TryGet*` bodies were not decompiled, so any clamping inside them is **Unknown**; none of the decompiled getters or constructors clamps a value.

### 5.1 Defaults (constructor)

`NormalGameOptionsV12..ctor @ 0x238de40` (values decoded from the constant table):

| Option | Default |
|---|---|
| GameMode | Normal (1) |
| MaxPlayers | 10 |
| Keywords | 256 (English) |
| MapId | 0 (not set) |
| IsDefaults | true |
| PlayerSpeedMod | 1.0 |
| CrewLightMod | 1.0 |
| ImpostorLightMod | 1.5 |
| KillCooldown | 15.0 s |
| NumCommonTasks / NumLongTasks / NumShortTasks | 1 / 1 / 2 |
| NumEmergencyMeetings | 1 |
| EmergencyCooldown | 0 (constructor leaves it unset; online recommendations set 15) |
| NumImpostors | 1 |
| GhostsDoTasks | true |
| KillDistance | 1 (index into `KillDistances`) |
| DiscussionTime | 15 s |
| VotingTime | 120 s |
| ConfirmImpostor | true |
| VisualTasks | true |
| AnonymousVotes | false |
| TaskBarMode | Normal (0) |
| Role options | `RoleOptionsCollectionV12` built with one `RoleDataV12` per entry of `AvailableRoles` |

`LegacyGameOptions..ctor @ 0x23642a0` uses the same numbers (plus version 6 and mode byte 1 in its first word, IsDefaults true) with a `LegacyRoleOptionsCollection`.

### 5.2 Static tables

`NormalGameOptionsV12..cctor @ 0x2393328` creates: `RecommendedKillCooldown` int[16], `RecommendedImpostors` int[16], `MaxImpostors` int[16], `KillDistances` float[3], `KillDistanceStrings` string[3], `MinPlayers` int[4], `AvailableRoles` RoleTypes[11]. The decompiler does not show array contents; they were read from the metadata blob by matching the data (Observed (disassembly)/Inferred mapping):

| Player count (index) | 0-3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `RecommendedKillCooldown` (s) | 0 | 45 | 30 | 15 | 35 | 30 | 25 | 20 | 20 | 20 | 20 | 20 | 20 |
| `RecommendedImpostors` | 0 | 1 | 1 | 1 | 2 | 2 | 2 | 2 | 2 | 3 | 3 | 3 | 3 |
| `MaxImpostors` | 0 | 1 | 1 | 1 | 2 | 2 | 3 | 3 | 3 | 3 | 3 | 3 | 3 |

- `KillDistances` = {1.0, 1.8, 2.5} (index 0 Short, 1 Normal, 2 Long).
- `MinPlayers` = {4, 4, 7, 9}, read as "minimum players for N impostors" with index N (index 0 and 1 both 4; 2 impostors need 7; 3 need 9). Mapping Inferred from the numbers agreeing with `MaxImpostors`.
- `AvailableRoles` has 11 entries; best guess: Scientist, Engineer, GuardianAngel, Shapeshifter, Noisemaker, Phantom, Tracker, Detective, Viper, Judge, SpiritGuide (Inferred from the 11 non-base `RoleTypes` values; not read).

Which of the two near-identical data blocks is `RecommendedImpostors` versus `MaxImpostors` was decided by shape: the block adjacent to the kill-cooldown data and with 2 at 10 players is "recommended"; the other (3 from 9 players) is the maximum. The pair is consistent with `MinPlayers` (Inferred).

### 5.3 SetRecommendations

Wrapper `IGameOptionsExtensions.SetRecommendations(options, numPlayers, mode[, preset]) @ 0x2231ca8 / 0x2231a84`: clamp `numPlayers`: if > 14 then 15; if < 5 then 4 (so valid indexes are 4..15, with 14 and 15 distinct and 15 meaning "15 or more"); `isOnline = (mode == 1)`; forwards to the options object (Observed (decompiled)).

`NormalGameOptionsV12.SetRecommendations(n, isOnline[, preset]) @ 0x2391c54 / 0x2391c60`, in order:
1. If **not online**: `NumImpostors = RecommendedImpostors[n]` and `EmergencyCooldown = 0`. If **online**: NumImpostors is left unchanged and `EmergencyCooldown = 15`.
2. `RulesPreset` = 0 (first overload) or the given preset (second overload).
3. `AnonymousVotes = false`, `ConfirmImpostor = true`, `NumEmergencyMeetings = 1`.
4. `PlayerSpeedMod = 1.0`, `CrewLightMod = 1.0`, `ImpostorLightMod = 1.5`.
5. `DiscussionTime = 15`, `VotingTime = 120`.
6. `KillCooldown = RecommendedKillCooldown[n]` (as float).
7. `NumCommonTasks = 1`, `NumLongTasks = 1`, `NumShortTasks = 2`, `KillDistance = 1`, `VisualTasks = true`, `TaskBarMode = Normal`.
8. Roles: first overload sets every `AvailableRoles` entry to rate (count 0, chance 0). Second overload: if `preset == 1 (StandardRoles)` each role gets rate (count 1, chance 100) and is marked recommended; any other preset sets (0, 0).
GhostsDoTasks, MaxPlayers, Keywords and MapId are not touched.

`LegacyGameOptions.SetRecommendations @ 0x23649a0`: same numeric defaults (speed 1.0, crew light 1.0, impostor light 1.5, kill cooldown from the legacy table, tasks 1/1/2, meetings 1, emergency cooldown 0 or 15 by the same online rule, impostors from the legacy table when offline, voting 120, discussion 15, confirm true, visual true) and resets its role options to: ShapeshifterLeaveSkin false, ShapeshifterCooldown 10, ShapeshifterDuration 30, ScientistCooldown 15, ScientistBatteryCharge 5, GuardianAngelCooldown 60, ProtectionDuration 10, ImpostorsCanSeeProtect false, EngineerCooldown 30, EngineerInVentMaxTime 15 (Observed (decompiled) and decoded constants). The second `SetRecommendations` overload on the legacy class is a no-op.

### 5.4 Impostor count adjustment

`IGameOptionsExtensions.GetAdjustedNumImpostors @ 0x2231d78`:
1. `configured` = `NumImpostors` of the current game options.
2. `table` = `MaxImpostors` int-array option; `players` = `GameData.PlayerCount`.
3. `cap = table[players]` if the table exists and `players < table.Length`, else **3**.
4. Return `1` if `configured < 1`, else `min(configured, cap)`.
Edge: with fewer than 4 players the cap is 0, so a configured value of 1+ returns 0; with 16 or more players the cap falls back to 3.

### 5.5 LogicOptions, GameOptionsManager, role collections

- `LogicOptionsNormal` (`@ 0x20df220`): in the constructor, if the local client is host, it casts the manager's current options to `NormalGameOptionsV12` and keeps it (throws an invalid-cast error if it is not). `SetGameOptions` replaces it with the same cast. Getters return the option fields directly with no transformation: `GetDiscussionTime` (offset 0x50), `GetVotingTime` (0x54), `GetEmergencyCooldown` (0x40), `GetNumEmergencyMeetings` (0x3C), `GetVisualTasks` (0x59), `GetAnonymousVotes` (0x5A), `GetConfirmImpostor` (0x58), `GetTaskBarMode` (0x5C). `OnGameStart`, `OnGameEnd`, `FixedUpdate`, `OnDestroy` do nothing.
- `LogicOptions.GetKillDistance @ 0x20d3bf0`: `distance = KillDistances[clamp(KillDistanceIndex, 0, length-1)]` using the float-array option. With the table above: 1.0 / 1.8 / 2.5.
- `GameOptionsManager` (`@ 0x222cd58` ff.): singleton, built with a logger and a factory. `Initialize` loads or creates host and search options for Normal and Hide and Seek. `LoadOrCreateNormalGameHostOptions`: if saved bytes exist decode them via the factory; if the saved version is older than the newest, run `MigrateNormalGameOptions`; if nothing saved, try a legacy options file; otherwise create `new NormalGameOptionsV12` (the defaults in 5.1); then save. `SwitchGameMode(mode)`: modes 1 and 3 select the normal host/search/current option sets, modes 2 and 4 the Hide and Seek sets; any other id logs an error. `CurrentGameOptions` returns the active set.
- `RoleOptionsCollectionV07..V12`: the only decompiled method is `GetChancePerGame(role)`: returns the stored chance (percent) for the role from its dictionary, 0 if the role is absent. V11 and V12 are identical. `GetNumPerGame` is the stored max count (Observed (dump)).

## 6. Implementation notes for our C# /sim

- Keep the server authoritative for position: the server receives `(startSeq, points[])`, drops points whose sequence id is not newer (wrap-aware, window 32767), and rejects deltas faster than `TrueSpeed` (the original relies on client authority; we should validate). Keep the 16-bit quantization only if bandwidth matters; the range +-50 and 65535 steps give a 0.0015 resolution. Use a monotonic server tick clock and 10 Hz position packets (each carrying about 5 samples at a 50 Hz simulation).
- Teleports: model them as "set position + set minimum sequence id" with the +1/+2 buffer so late movement packets from before the teleport are discarded.
- The remote jitter buffer (thresholds 5/6/12/14, rubberband 0.5/0.995, 10 u/s cap, 0.05/0.01 snap distances) is client-side presentation. A server sim does not need it; a client port should keep the constants.
- Speed: `trueSpeed = (isGhost ? 3.0 : 2.5) * PlayerSpeedMod`. No extra modifiers are visible in the decompiled code (role speed effects may live elsewhere, Unknown).
- Options: keep the defaults table and the recommended tables by player count; implement `GetAdjustedNumImpostors` exactly (including the 0 result below 4 players, or simplify to a minimum of 1). Validate user input ourselves because the original shows no clamping in these functions.
- RPC handlers: only the validation rules matter for a server; client-only effects (animations, sounds, stats, chat UI) can be dropped. Keep these: CheckMurder conditions (4.1 id 47), the half cooldown on a protected kill (id 12), report gating (id 11), CheckShapeshift and CheckProtect role checks, zipline distance 3.0, CastVote dead/duplicate filtering and VotingComplete idempotence by state.
- Name dedupe (suffix 1..99) and color uniqueness (probe up to 100 steps modulo palette size) are lobby rules worth keeping.

## 7. Open questions

1. Real `Time.fixedDeltaTime` (assumed 0.02 s) and whether movement uses the reliable or unreliable collection (`sendMode` of the transform prefab).
2. Whether the prefab overrides `MinSendInterval` (default in code is 0.1 s).
3. The type of the sibling component that makes `CustomNetworkTransform.OnEnable` disable itself.
4. `RecommendedImpostors` versus `MaxImpostors` assignment and the contents/meaning of `MinPlayers` and `AvailableRoles` are inferred from metadata shape, not from the field handles.
5. Value ranges (min/max/step) of options and any validation inside `TryGet*`/`AreInvalid`: not decompiled.
6. What `ShipStatus.HandleRpc` UpdateSystem and CloseDoorsOfType call exactly (hidden behind no-return paths); `GameManager` virtual called after `CompleteTask`.
7. `PlayerPhysics` behaviour when `CanMove` is false (who zeroes the velocity).
8. Exact separator string between name and number in the name dedupe and the exact predicate used to block emergency meetings during critical sabotage.
