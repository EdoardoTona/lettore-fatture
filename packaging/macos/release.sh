#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RID="${1:-osx-$(uname -m)}"
case "$RID" in osx-arm64|osx-x64) ;; *) echo 'Usa osx-arm64 oppure osx-x64' >&2; exit 1 ;; esac
bash "$ROOT/packaging/macos/package.sh" "$RID"
APP="${FATTURE_OUTPUT_DIR:-$ROOT/artifacts/macos}/Lettore Fatture.app"
DESTINATION="$ROOT/artifacts/releases/$RID"
STAGE="$(mktemp -d "$ROOT/artifacts/.mac-release.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/dmg" "$DESTINATION"
ditto "$APP" "$STAGE/dmg/Lettore Fatture.app"
ln -s /Applications "$STAGE/dmg/Applications"
hdiutil create -quiet -volname 'Lettore Fatture' -srcfolder "$STAGE/dmg" \
    -format UDZO -ov "$STAGE/LettoreFatture-$RID.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$STAGE/LettoreFatture-$RID.zip"
mv "$STAGE/LettoreFatture-$RID.dmg" "$DESTINATION/"
mv "$STAGE/LettoreFatture-$RID.zip" "$DESTINATION/"
echo "Release macOS: $DESTINATION"
