# 09 - Recreation Blueprint

**This whole document is a design proposal, not an observation.** It turns docs 02-08 into a build plan for an original social-deduction game. Every "because" points to evidence in the other docs.

## 1. Decisions still open (team must choose)

| Decision | Options | Recommendation | Evidence |
|---|---|---|---|
| Engine | Unity 2022.3 LTS / Unity 6 (same as original), Godot 4 | Unity if parity and C# matter; Godot 4 if size, license and 2D lights for vision matter | [08 s7.2](08-tech-stack-and-assets.md) |
| Authority | Player-hosted (original) vs dedicated server per room | Dedicated headless server running the same simulation code. Host model leaks roles and lets the host cheat | [02 s6.2](02-networking.md) |
| Transport | ENet / QUIC / WebSocket (MVP) | WebSocket for the first playable, then UDP (ENet or QUIC) with reliable + unreliable channels | [02 s1, s6.2](02-networking.md) |
| Accounts | Anonymous device id, platform sign-in, third-party (EOS-like) | Anonymous device id first; age gate before any free chat | [07](07-client-architecture.md) |

## 2. Architecture

```
/sim        engine-agnostic rules (pure code, deterministic, unit-tested)
  state/      GameState, Player, Role, Task, ShipSystem, Meeting
  rules/      kill, report, vote, win checks, task progress, sabotage timers
  roles/      one file per role, small hook surface
  systems/    reactor, o2, lights, comms, doors, vents, cameras (state machines)
/net        protocol: message ids, framing, (de)serializers, version
/server     headless room host: owns GameState, validates requests, sends deltas
/client     engine project: rendering, input, UI, minigames, prediction
/content    map data (rooms, vents, consoles), task definitions, role tuning
```

Rule: `/sim` never imports the engine. The server runs `/sim`; the client runs `/sim` only for prediction and UI hints. This mirrors the original's split between game logic (`GameManager` + `Logic*` components) and presentation (`HudManager`, minigames), but makes it testable without a renderer.

## 3. Mapping original concepts to our modules

| Original (Observed names) | Our module | Notes |
|---|---|---|
| `AmongUsClient`, `InnerNetClient`, Hazel | `/net`, `/client/net` | Length-prefixed tagged messages, varints ([02 s1.4](02-networking.md)) |
| `InnerNetObject`, `NetId`, dirty bits | `/net/entity` | Entity id + owner + dirty mask; full snapshot on join |
| `PlayerControl` + `PlayerPhysics` + `CustomNetworkTransform` | one `Player` entity + movement component | Original uses 3 netIds per player; we merge ([02 s6.2](02-networking.md)) |
| `NetworkedPlayerInfo` (`GameData`) | `sim/state/Player` | Name, color, outfit, role, dead, disconnected, tasks |
| `GameManager` + `LogicGameFlow` / `LogicRoleSelection` / `LogicOptions` | `sim/rules` + `sim/modes/{classic,hideseek}` | Mode = a set of rule components ([03](03-gameplay-core.md)) |
| `RoleBehaviour` + `RoleManager` | `sim/roles` | Hooks: canKill, canVent, countsTasks, ability(cooldown, duration), onDeath -> ghost role ([05](05-roles.md)) |
| `ShipStatus` + `ISystemType` | `sim/systems` | `deteriorate(dt)`, `apply(player, opcode)`, `serialize()` ([03](03-gameplay-core.md)) |
| `NormalPlayerTask`, `TaskTypes`, `Console` | `sim/state/Task`, `/content/tasks` | Task = type + steps + console locations ([06](06-tasks-and-maps.md)) |
| `Minigame` subclasses (~100) | `/client/minigames` | Client-only UI; server only receives `CompleteTask(taskId)` with sanity checks |
| `MeetingHud`, `PlayerVoteArea` | `sim/rules/meeting` + `/client/ui/meeting` | States: Animating, Discussion, Voting, Results, Proceeding |
| `ExileController`, `EndGameManager` | `/client/ui` | Pure presentation |
| `HudManager`, joystick | `/client/ui/hud` | Use, kill, report, vent, sabotage, ability buttons |
| `QuickChat*`, `StringNames` | `/client/chat`, `/content/i18n` | Send phrase ids, not text |

## 4. Core rules to implement (with evidence)

| Rule | Source | Our version |
|---|---|---|
| Kill needs: killer alive + can kill, target alive + not same team, cooldown elapsed, within kill distance, not in meeting/vent | [03](03-gameplay-core.md), [05](05-roles.md); distances in [data/gameplay](data/gameplay/README.md) | Server validates all; client only requests |
| Kill cooldown resets after meetings and on kill; Guardian-Angel-blocked kill costs half cooldown | [03](03-gameplay-core.md), [04](04-game-options.md) | Same |
| Report: body within range; emergency button: uses per player, button cooldown | [03](03-gameplay-core.md) | Same |
| Vote: one vote per living player, skip allowed, plurality ejects, tie/skip-plurality ejects none | [03](03-gameplay-core.md) | Same |
| Crew win: all tasks of task-counting roles done, or all impostors gone | [03](03-gameplay-core.md), [06](06-tasks-and-maps.md) | Same |
| Impostor win: impostors >= crew alive, critical sabotage timer expires, or crew disconnects | [03](03-gameplay-core.md) | Same |
| Critical sabotage: countdown, fixed by 2 players on 2 consoles at once (reactor), or 2 codes (O2) | [03](03-gameplay-core.md) | Same; durations from [data/gameplay](data/gameplay/README.md) |
| Sabotage spacing: special sabotages share a cooldown (`SpecialSabDelay = 30` s) | [03](03-gameplay-core.md) | Same as default |
| Lights: crew vision shrinks; impostor vision unchanged | [03](03-gameplay-core.md), [04](04-game-options.md) | Vision radius option x lights multiplier |
| Comms: hides task list / cameras / admin until fixed | [03](03-gameplay-core.md) | Same |
| Ghosts: dead crew keep doing tasks; dead impostors can sabotage | [05](05-roles.md) | Ghost roles |

## 5. Milestones

Each milestone ends with a verifiable demo. Estimates assume 1-2 developers familiar with the chosen engine.

| # | Milestone | Done when | Estimate |
|---|---|---|---|
| M0 | Repo skeleton, `/sim` with tests, CI | `sim` unit tests run in CI | 2-3 days |
| M1 | Single-player sandbox: one original test map, movement, collisions, vision radius | Walk around with fog of war on desktop and phone | 1 week |
| M2 | Networking: room codes, lobby, 10 clients moving smoothly (quantized positions + jitter buffer) | 10 bots + 1 human, no visible rubber-banding at 150 ms latency | 1-2 weeks |
| M3 | Core loop: roles Crew/Impostor, kill, report, emergency meeting, vote, eject, win checks | Full round playable end to end with 6+ players | 2 weeks |
| M4 | Tasks: 6 simple minigames, task bar, task assignment (common/short/long) | Crew can win by tasks | 1-2 weeks |
| M5 | Sabotage: lights, comms, reactor (2 consoles), doors; vents graph | Impostor can win by sabotage | 1-2 weeks |
| M6 | Two special roles (an Engineer-like vent user and a Shapeshifter-like disguise) | Roles assigned by options, abilities validated server-side | 1 week |
| M7 | Mobile UX: joystick, buttons, quick chat, settings, reconnection | Playtest on 2 Android phones over mobile data | 1 week |
| M8 | Content + polish: second original map, cosmetics slots (no store yet) | External playtest | 2+ weeks |

Defer: store/IAP, ads, accounts beyond device id, Hide and Seek, matchmaking list, crossplay, more roles.

## 6. Data we can start from

- **Map scale and pacing**: [`data/maps`](data/maps/README.md) gives rooms, vents (with links), consoles (with task types), doors, cameras and spawn points for all 6 maps. Use it to size our maps: e.g. Skeld has 21 rooms, 14 vents, 49 consoles. Design our own layouts; do not trace these.
- **Tuning**: [`data/gameplay`](data/gameplay/README.md) for speeds, kill distances, sabotage timers, task lengths. Use as starting defaults, then playtest.
- **Option set**: [04](04-game-options.md) lists every lobby option. A good first subset: impostor count, kill cooldown, kill distance, player speed, crew/impostor vision, emergency meetings, discussion/voting time, confirm ejects, task counts (common/short/long), task bar mode.

## 7. Known weaknesses of the original to fix

| Weakness | Evidence | Our fix |
|---|---|---|
| Host can cheat; roles broadcast to all clients | [02 s6.2](02-networking.md) | Dedicated server; send each client only what it may know |
| Kill distance not checked by community server | [03](03-gameplay-core.md) | Server checks distance and line of sight |
| Weak local obfuscation for entitlements | [08 s7.4](08-tech-stack-and-assets.md) | Server-side inventory only |
| Three netIds per player | [02 s4](02-networking.md) | One entity |

## 8. Testing strategy

- `/sim` rules: table-driven unit tests per rule (kill validation, vote tally incl. ties/skips, win checks, sabotage timers, task progress). These catch real regressions cheaply.
- `/net`: round-trip tests for every message serializer, plus a version-mismatch test.
- Bots: headless clients that join, walk random paths and vote, used for load and soak tests of the server.
- Minigames and UI: verify by playing, not unit tests.
