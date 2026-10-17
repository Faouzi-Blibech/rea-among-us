# AGENTS.md - Guide for Coding Agents

This repo holds reverse-engineering knowledge about Among Us mobile (build 2026.9.29) for building an **original** social-deduction game. Read this first, then open only the doc you need.

## Where to look

| You need | Read |
|---|---|
| Whole-game picture in 2 minutes | `docs/01-overview.md` |
| What to build and in which order | `docs/09-recreation-blueprint.md` |
| Network protocol, RPC ids, replication, movement sync | `docs/02-networking.md` |
| Game loop, kill/report/meeting/vote, sabotage systems, win conditions | `docs/03-gameplay-core.md` |
| Lobby options, enums (maps, modes, kill distances) | `docs/04-game-options.md` |
| Roles and abilities, role assignment | `docs/05-roles.md` |
| Tasks, minigame catalogue, maps, vents, consoles | `docs/06-tasks-and-maps.md` |
| Scenes, HUD, chat, accounts, cosmetics, store | `docs/07-client-architecture.md` |
| Engine, libraries, package layout, assets | `docs/08-tech-stack-and-assets.md` |
| Map layout numbers (rooms, vents + links, consoles, doors, spawns) | `docs/data/maps/<map>.json` + `README.md` |
| Tuning numbers (speeds, timers, distances, task lengths) | `docs/data/gameplay/*.json` + `README.md` |
| How the evidence was made, trust levels, legal boundary | `docs/00-methodology-and-evidence.md` |

Search tip: grep `docs/` for the original class name (e.g. `ShipStatus`, `CheckMurder`, `ReactorSystemType`); every doc cites them.

## Trust levels

Every claim is tagged:
- **Observed**: read from the shipped build. Structure is reliable; behavior is not proven.
- **Reference**: from the Impostor community server; may be older than the live game.
- **Inferred**: a guess from names and signatures. Treat as a hypothesis; do not build hard logic on it without a playtest or new evidence.

The dump has **no method bodies**. Algorithms (role assignment, vote tally details, win check order) are Reference or Inferred.

## Hard rules

1. **Original work only.** Never copy Innersloth code, art, audio, text, names, map layouts, or trademarks into the product. Use the data to understand scale, pacing and structure, then design our own.
2. **Never commit** APKs, `.so`, `global-metadata.dat`, decompiled code, or extracted assets. Regenerate locally with `tools/dump_il2cpp.sh` and the extract scripts into a scratch directory outside the repo.
3. **Server-authoritative rules.** Clients send requests; the server validates and broadcasts results. Never send a player hidden info (other roles, impostor teammates to crew) they may not know. See `docs/09-recreation-blueprint.md` section 7.
4. **Keep rules engine-agnostic** in `/sim` (see blueprint section 2) and cover them with table-driven unit tests.
5. When you add new reverse-engineering findings, keep the Observed/Reference/Inferred tags and cite a source.

## Tools

| Script | Purpose |
|---|---|
| `tools/dump_il2cpp.sh <work_dir>` | Download the XAPK and produce the Cpp2IL C# signature dump |
| `tools/extract_map_layout.py` | Map layout JSON from the maps Addressables bundle (UnityPy) |
| `tools/extract_gameplay_data.py` | Tuning data JSON from serialized assets (UnityPy) |

Run Python tools with `uv run --with UnityPy python -I tools/<script>.py --help`.
