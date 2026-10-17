#!/usr/bin/env bash
# Decompile selected game functions with Ghidra, for study only. Output stays in WORK (outside the repo).
# Needs: tools/dump_il2cpp.sh already run into WORK (bin/libil2cpp.so, bin/global-metadata.dat, tools/Cpp2IL*.exe),
#        Java 21, Ghidra 12.x unpacked at $GHIDRA, Python 3.
# Usage: tools/decompile/run.sh <WORK> <GHIDRA_DIR>
set -euo pipefail
WORK=$(cd "$1" && pwd); GHIDRA=$2; HERE=$(cd "$(dirname "$0")" && pwd)
cd "$WORK"
# 1. Cpp2IL ISIL dump: gives every method's address (its disassembly text is unreliable, the addresses are fine).
./tools/Cpp2IL-*-Windows.exe --force-binary-path bin/libil2cpp.so --force-metadata-path bin/global-metadata.dat \
  --force-unity-version 2022.3.62 --output-as isil --output-to isil
mkdir -p re
python -I "$HERE/isil_map.py" isil/IsilDump re/methods.tsv
# 2. Throw helpers that never return (otherwise Ghidra runs into the next method).
python -I "$HERE/noreturn.py" bin/libil2cpp.so re/methods.tsv re/noreturn.txt
# 3. Targets, then headless Ghidra: import without auto-analysis, label all methods, decompile targets.
python -I "$HERE/select_targets.py" re/methods.tsv "$HERE/targets.txt" re/targets.tsv
python -I "$HERE/select_targets.py" re/methods.tsv "$HERE/targets2.txt" re/targets2.tsv re/targets.tsv
"$GHIDRA/support/analyzeHeadless" re/proj au -import bin/libil2cpp.so -noanalysis -scriptPath "$HERE" \
  -postScript DecompTargets.java re/methods.tsv re/targets.tsv re/out re/noreturn.txt
"$GHIDRA/support/analyzeHeadless" re/proj au -process libil2cpp.so -noanalysis -scriptPath "$HERE" \
  -postScript DecompTargets.java re/methods.tsv re/targets2.tsv re/out2 re/noreturn.txt
