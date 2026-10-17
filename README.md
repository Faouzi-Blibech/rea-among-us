# rea-among-us

Reverse-engineering study of **Among Us mobile** (Android build 2026.9.29, Unity 2022.3 IL2CPP) to inform an **original** social-deduction game. The repo holds documentation, our own data tables and reproducible extraction scripts, with no game binaries, decompiled code or assets.

## Start here

1. [docs/01-overview.md](docs/01-overview.md): the whole game on one page
2. [docs/09-recreation-blueprint.md](docs/09-recreation-blueprint.md): architecture and milestones for our game
3. [AGENTS.md](AGENTS.md): routing guide for coding agents

## Contents

| Doc | Topic |
|---|---|
| [00](docs/00-methodology-and-evidence.md) | Build identity, pipeline, evidence tags, legal boundary |
| [01](docs/01-overview.md) | System map, key numbers, patterns worth copying |
| [02](docs/02-networking.md) | Hazel transport, matchmaking, message tags, RPC catalogue, replication, movement sync |
| [03](docs/03-gameplay-core.md) | Game loop, kill/report/meeting/vote, ship systems and sabotage, win conditions |
| [04](docs/04-game-options.md) | All game options and related enums |
| [05](docs/05-roles.md) | 15 roles, abilities, role options, assignment |
| [06](docs/06-tasks-and-maps.md) | Task model, 82 task types, minigame catalogue, maps, vents, consoles |
| [07](docs/07-client-architecture.md) | Scenes, HUD, input, chat, localization, accounts, cosmetics, store |
| [08](docs/08-tech-stack-and-assets.md) | Engine, libraries, package layout, assets |
| [09](docs/09-recreation-blueprint.md) | Design proposal for our clone |
| [data/maps](docs/data/maps/README.md) | Rooms, vents (with links), consoles, doors, spawns for 6 maps |
| [data/gameplay](docs/data/gameplay/README.md) | Tuning values from serialized assets |

## Reproduce

```bash
tools/dump_il2cpp.sh /path/outside/repo          # XAPK + Cpp2IL signature dump
uv run --with UnityPy python -I tools/extract_map_layout.py --help
uv run --with UnityPy python -I tools/extract_gameplay_data.py --help
```

## Legal

Among Us is a trademark of Innersloth LLC. This is an independent study for learning game design and architecture; it is not affiliated with or endorsed by Innersloth. No proprietary code, art, audio or text is included. See [docs/00](docs/00-methodology-and-evidence.md#5-legal-and-ethical-boundary).
