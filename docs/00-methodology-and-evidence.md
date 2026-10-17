# 00 - Methodology, Evidence and Legal Boundary

How the knowledge in `docs/` was produced, how much to trust each claim, and how to regenerate it.

## 1. Target identity

| Item | Value |
|---|---|
| Game | Among Us (Innersloth), Android package `com.innersloth.spacemafia` |
| Version | 2026.9.29, version code 7492, minSdk 26, targetSdk 36 |
| Source | APKPure XAPK (base + `config.arm64_v8a` + `UnityDataAssetPack`), downloaded 2026-10-17 |
| Base APK SHA-256 | `f4d98769a49e876694654c17f372895074f78aadc27ebdd260622272cbbc2430` |
| arm64 split SHA-256 | `46f65048cdd8c295369294b58d298d9d81ab62e843dd7769c1527833baecf17c` |
| Asset pack SHA-256 | `273b284a9b9ed66749da69dada3df475b2b53572ad374645256cb2ff8f1a74ef` |
| Engine | Unity 2022.3.62f3, IL2CPP, arm64-v8a only |
| IL2CPP metadata | `global-metadata.dat` version 31 (header `AF 1B B1 FA 1F 00 00 00`) |
| Symbols | Game types are not obfuscated: `PlayerControl`, `ShipStatus`, `MeetingHud` etc. are readable |

## 2. Pipeline

| Step | Tool | Output | Committed? |
|---|---|---|---|
| Fetch XAPK | curl (browser user agent; plain UA gets HTTP 403) | 1.1 GB XAPK | No |
| Package inventory | REA `open_binary` + `inspect_artifact` on base APK | 557 ZIP entries, 513 content nodes, 0 integrity contradictions (Evidence `ev_cdf723a9...`, summary view `ev_b6dff28c...`) | No |
| Manifest decode | androguard `axml` (apktool not installed) | `AndroidManifest.xml` text | No |
| Type/signature recovery | Cpp2IL 2022.1.0-pre-release.21, `diffable-cs` output | 1,636 game types in `Assembly-CSharp` + all third-party assemblies; fields with offsets, enums with values, `const` values, method signatures. **No method bodies.** | No |
| Map layout | `tools/extract_map_layout.py` (UnityPy) on `initialmaps_assets_all.bundle` | `docs/data/maps/*.json` | Yes (numbers and names only) |
| Tuning data | `tools/extract_gameplay_data.py` (UnityPy) on serialized assets | `docs/data/gameplay/*.json` | Yes (numbers and names only) |
| Method addresses | Cpp2IL `isil` output + `tools/decompile/isil_map.py` | 123,575 method addresses mapped to names | No |
| Function logic | Ghidra 12.1.4 headless, no auto-analysis; every method labelled, throw helpers marked noreturn (`tools/decompile/`) | 498 decompiled functions (ship systems, kill, roles, meetings, win checks, assignment, movement, options) | No (study only) |
| Behaviour specs | 4 analysts paraphrasing the decompiled functions; numbers spot-checked against the source functions | `docs/logic/*.md` | Yes (our own words) |
| Public cross-reference | Impostor server (commit `703915f`), Hazel-Networking (commit `c6a228c`) | Wire format, option defaults, task/vent tables | No (cited only) |

Regenerate the signature dump with `tools/dump_il2cpp.sh <work_dir>`. Regenerate data with the commands in `docs/data/*/README.md`.

Note: the map bundle ships with Unity typetrees, so serialized MonoBehaviour fields (vent links, console task types, room ids) were readable without generating typetrees from dummy DLLs.

## 3. Evidence tags used in every doc

| Tag | Meaning | Trust |
|---|---|---|
| **Observed** | Read directly from the 2026.9.29 build: dump enum/const/field/signature, manifest, catalog, serialized asset | High for structure; it does not prove runtime behavior |
| **Observed (decompiled)** | Read from a Ghidra-decompiled function body; cites `Class.Method @ 0xRVA` | High for logic; still static, not a runtime capture |
| **Reference** | From Impostor/Hazel public source; may lag the live game | Medium; verify against Observed where possible |
| **Inferred** | Our reading of names, fields and signatures without method bodies | Low to medium; treat as a hypothesis |

Rules for anyone extending these docs:
- Cite a source for every factual row: dump path (`Assembly-CSharp/ShipStatus.cs`), `Impostor:<path>`, or data file.
- Never promote Inferred to Observed without new evidence (a body, a serialized value, or a runtime capture).
- Static analysis did not run the game. Nothing here is a runtime observation.

## 4. What the evidence cannot tell us

- Method bodies outside the 498 decompiled functions. Add patterns to `tools/decompile/targets*.txt` to recover more; each spec in `docs/logic/` lists its open questions.
- Server-side code (matchmaker, EOS backend). Only the client side and the community reimplementation are visible.
- Runtime tuning that comes from remote config or the CDN catalog.

## 5. Legal and ethical boundary

- Purpose: learn the structure and design of a genre-defining game to build an **original** game. Game mechanics and genre conventions are not protected; code, art, audio, text, names, characters and trademarks are.
- Decompiled functions were read only to learn rules; specs restate behaviour in our own words, and `sim/` is written from the specs, not from the decompiled code.
- This repo contains **no** Innersloth binaries, decompiled code, sprites, audio, localized text, or cosmetic art. It contains our own descriptions, enum/constant tables, counts, and numeric layout data.
- Map layout JSON is reference material for understanding scale and pacing (room counts, vent graph shape, task density). Our own maps must be original designs, not traced copies.
- Do not reuse the names "Among Us", "Innersloth", role names styled as theirs, or any collab IP in our product.
