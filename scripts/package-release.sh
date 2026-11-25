#!/usr/bin/env bash
# Builds a universal LyricsIsland.app and zips it for a GitHub release.
#   ./scripts/package-release.sh   → build.noindex/LyricsIsland-<version>.zip + its sha256
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)"
./scripts/build-app.sh universal

ZIP="build.noindex/LyricsIsland-$VERSION.zip"
rm -f "$ZIP"
ditto -c -k --sequesterRsrc --keepParent build.noindex/LyricsIsland.app "$ZIP"

echo "✓ $(pwd)/$ZIP"
echo "sha256 $(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
