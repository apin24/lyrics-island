#!/usr/bin/env bash
# Build LyricsIsland.app (no Xcode needed — Command Line Tools are enough).
#   ./scripts/build-app.sh            → release build
#   ./scripts/build-app.sh debug      → debug build
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-release}"
swift build -c "$CONFIG"
BIN="$(swift build -c "$CONFIG" --show-bin-path)/LyricsIsland"

APP="build/LyricsIsland.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/LyricsIsland"
cp Resources/Info.plist "$APP/Contents/Info.plist"

# Ad-hoc signature so macOS can remember the Automation (Spotify) permission.
codesign --force --sign - "$APP"

echo "✓ $(pwd)/$APP"
