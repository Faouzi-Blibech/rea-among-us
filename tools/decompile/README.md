# tools/decompile

Recovers the logic of selected game functions with Ghidra so we can write behaviour specs (`docs/logic/`).
The decompiled output is study material only: it stays in your work directory and is **never committed**
(AGENTS.md rule 2). Specs describe behaviour in our own words; `/sim` is written from the specs.

## Run

```bash
tools/dump_il2cpp.sh /path/outside/repo            # once: XAPK, libil2cpp.so, metadata, Cpp2IL
tools/decompile/run.sh /path/outside/repo /path/to/ghidra_12.1.4_PUBLIC
```

Needs Java 21 and Ghidra 12.x. Takes about 15 minutes: the ISIL dump takes 30 s, and each Ghidra pass
takes 5-10 minutes. Output: `re/out/<Class>.c` and `re/out2/<Class>.c`, plus `re/methods.tsv`, which maps
123,575 method addresses to names.

## Files

| File | Purpose |
|---|---|
| `isil_map.py` | Builds `methods.tsv` (rva, assembly, type, method, signature) from the Cpp2IL ISIL dump |
| `noreturn.py` | Finds throw helpers that never return, so Ghidra stops each function at the right place |
| `select_targets.py` | Picks methods whose `Type<TAB>Method` matches a regex in a patterns file |
| `targets.txt`, `targets2.txt` | Patterns for the 498 functions used for the `docs/logic/` specs |
| `DecompTargets.java` | Ghidra headless script: label every method, mark noreturn helpers, decompile targets |
| `run.sh` | Runs the whole pipeline |

## Lessons

- Cpp2IL 2022.1 `isil` output on arm64 prints the wrong bytes in its disassembly text, but its method
  addresses and lifted ISIL are correct. We only use the addresses.
- Without the noreturn list, every function body runs on into the next method after a null-check throw.
- Ghidra loads the `.so` at image base 0x100000, so a Ghidra address is the rva plus 0x100000.
- Float constants show up as hex integers (0x41f00000 = 30.0).
- To add functions, append a pattern, rerun `select_targets.py` with the already-done list, and use
  `-process` on the existing project. You don't need to import it again.
