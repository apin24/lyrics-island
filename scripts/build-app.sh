#!/usr/bin/env bash
# Build LyricsIsland.app (no Xcode needed — Command Line Tools are enough).
#   ./scripts/build-app.sh             → release build for this Mac
#   ./scripts/build-app.sh debug       → debug build
#   ./scripts/build-app.sh universal   → release build for Apple silicon + Intel
set -euo pipefail
cd "$(dirname "$0")/.."

MODE="${1:-release}"
APP="build.noindex/LyricsIsland.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

if [ "$MODE" = "universal" ]; then
    # Both triples can share one output folder, so copy each binary out before the next build.
    mkdir -p build.noindex/arch
    for ARCH in arm64 x86_64; do
        swift build -c release --triple "$ARCH-apple-macosx14.0"
        cp "$(swift build -c release --triple "$ARCH-apple-macosx14.0" --show-bin-path)/LyricsIsland" "build.noindex/arch/LyricsIsland-$ARCH"
    done
    lipo -create build.noindex/arch/LyricsIsland-arm64 build.noindex/arch/LyricsIsland-x86_64 -output "$APP/Contents/MacOS/LyricsIsland"
    rm -rf build.noindex/arch
else
    swift build -c "$MODE"
    cp "$(swift build -c "$MODE" --show-bin-path)/LyricsIsland" "$APP/Contents/MacOS/LyricsIsland"
fi

cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature so macOS can remember the Automation (Spotify) permission.
codesign --force --sign - "$APP"

echo "✓ $(pwd)/$APP ($(lipo -archs "$APP/Contents/MacOS/LyricsIsland"))"
