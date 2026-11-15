#!/bin/zsh
# Builds the two Mac pieces of displayWindowToMac:
#   1. KeyBridge   - types keys sent from the Windows receiver
#   2. OpenDisplay - the upstream Mac sender, patched to follow the receiver's
#                    refresh rate (e.g. 99/100 Hz instead of a fixed 60 Hz)
#
# Needs: Xcode (full app, opened once to accept the licence) and xcodegen
#   brew install xcodegen
#
# Usage:  zsh mac/build-mac.sh          (from the repo root)
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/mac/build"
UPSTREAM_COMMIT=5366433d9a1e028840c5b77636320fad88bb9330
mkdir -p "$OUT"

echo "==> KeyBridge"
swiftc -O "$ROOT/mac/KeyBridge/main.swift" -o "$OUT/KeyBridge"
echo "    built $OUT/KeyBridge"

echo "==> OpenDisplay (high refresh)"
SRC="$OUT/opendisplay"
if [[ ! -d "$SRC/.git" ]]; then
  git clone https://github.com/peetzweg/opendisplay.git "$SRC"
fi
cd "$SRC"
git fetch -q origin
git checkout -q -f "$UPSTREAM_COMMIT"
git apply "$ROOT/mac/patches/opendisplay-high-refresh.patch"
xcodegen generate
xcodebuild -project OpenSidecar.xcodeproj -scheme OpenSidecarMac -configuration Debug \
  -derivedDataPath build DEVELOPMENT_TEAM= CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual build | tail -5
echo
echo "Done. Start them with:"
echo "  $OUT/KeyBridge"
echo "  $SRC/run.sh        (quit the normal OpenDisplay app first)"
