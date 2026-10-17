#!/usr/bin/env bash
# Reproduce the static dump used for docs/. Nothing produced here is committed:
# binaries, metadata and the C# signature dump stay in WORK_DIR.
#
# Usage: tools/dump_il2cpp.sh <work_dir>
# Needs: curl, unzip, sha256sum, gh (GitHub CLI). Windows Git Bash or Linux.
set -euo pipefail

WORK_DIR="${1:?usage: dump_il2cpp.sh <work_dir>}"
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
CPP2IL_TAG="2022.1.0-pre-release.21"
UNITY_VERSION="2022.3.62"

mkdir -p "$WORK_DIR"/{apk,bin,tools}
cd "$WORK_DIR"

# 1. Fetch the XAPK (base + arm64 split + Play Asset Delivery pack, ~1.1 GB).
#    The plain user agent gets a 403 from APKPure; the browser one works.
if [ ! -f apk/au.xapk ]; then
  curl -sSL -A "$UA" -o apk/au.xapk \
    "https://d.apkpure.com/b/XAPK/com.innersloth.spacemafia?version=latest"
fi
unzip -o -q apk/au.xapk -d apk/x
grep -o '"version_name":"[^"]*"' apk/x/manifest.json
sha256sum apk/x/*.apk

# 2. Pull the two inputs IL2CPP decompilers need.
unzip -o -q -j apk/x/config.arm64_v8a.apk lib/arm64-v8a/libil2cpp.so -d bin
unzip -o -q -j apk/x/com.innersloth.spacemafia.apk \
  assets/bin/Data/Managed/Metadata/global-metadata.dat -d bin
# Metadata header: magic AF1BB1FA then version (0x1f = 31 for build 2026.9.29).
xxd -l 8 bin/global-metadata.dat

# 3. Cpp2IL handles metadata v31 (upstream Il2CppDumper stops at v29).
gh release download "$CPP2IL_TAG" -R SamboyCoding/Cpp2IL \
  -p "Cpp2IL-$CPP2IL_TAG-Windows.exe" -D tools --clobber
"./tools/Cpp2IL-$CPP2IL_TAG-Windows.exe" \
  --force-binary-path bin/libil2cpp.so \
  --force-metadata-path bin/global-metadata.dat \
  --force-unity-version "$UNITY_VERSION" \
  --output-as diffable-cs --output-to cs

echo "Signatures written to $WORK_DIR/cs/DiffableCs (game code: Assembly-CSharp)"
