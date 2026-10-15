#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${FATTURE_DOTNET:-dotnet}"
if [[ -z "${FATTURE_DOTNET:-}" && -x "$ROOT/.build/dotnet/dotnet" ]]; then DOTNET="$ROOT/.build/dotnet/dotnet"; fi
DOTNET="$(command -v "$DOTNET")"
export DOTNET_HOST_PATH="$(command -v "$DOTNET")"
RID="${1:-osx-$(uname -m)}"
case "$RID" in
    osx-arm64) TARGET=arm64-apple-macosx14.0 ;;
    osx-x86_64|osx-x64) RID=osx-x64; TARGET=x86_64-apple-macosx14.0 ;;
    *) echo 'Usa osx-arm64 oppure osx-x64' >&2; exit 1 ;;
esac
OUTPUT="${FATTURE_OUTPUT_DIR:-$ROOT/artifacts/macos}"
VERSION="${FATTURE_VERSION:-$(cat "$ROOT/VERSION")}"
export FATTURE_VERSION="$VERSION"
APP="$OUTPUT/Lettore Fatture.app"
mkdir -p "$OUTPUT"
STAGE="$(mktemp -d "$OUTPUT/.package.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
BUNDLE="$STAGE/Lettore Fatture.app"
EXTENSION="$BUNDLE/Contents/PlugIns/FatturePreview.appex"
BRIDGE="$BUNDLE/Contents/Resources/bridge"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources" "$BRIDGE"
cp "$ROOT/packaging/macos/Info.plist" "$BUNDLE/Contents/Info.plist"
python3 "$ROOT/packaging/macos/stamp-version.py" "$BUNDLE/Contents/Info.plist"
xcrun swiftc -parse-as-library -O -module-cache-path "${TMPDIR:-/tmp}/lettore-fatture-swift-cache" \
    -target "$TARGET" "$ROOT/src/Fatture.Mac/"*.swift \
    -framework AppKit -framework PDFKit -framework UniformTypeIdentifiers -o "$BUNDLE/Contents/MacOS/LettoreFatture"
xcrun swiftc -O -module-cache-path "${TMPDIR:-/tmp}/lettore-fatture-swift-cache" \
    "$ROOT/packaging/macos/generate-icon.swift" -framework AppKit -o "$STAGE/generate-icon"
"$STAGE/generate-icon" "$STAGE/LettoreFatture.iconset"
iconutil -c icns "$STAGE/LettoreFatture.iconset" -o "$BUNDLE/Contents/Resources/LettoreFatture.icns"
"$DOTNET" msbuild "$ROOT/src/Fatture.Cli/Fatture.Cli.csproj" -t:Publish -restore -m:1 -nodeReuse:false \
    -p:Configuration=Release -p:Version="$VERSION" -p:RuntimeIdentifier="$RID" -p:SelfContained=true \
    -p:_DotNetHostDirectory="$(dirname "$DOTNET")" -p:_DotNetHostFileName="$(basename "$DOTNET")" \
    -p:RuntimeFrameworkVersion=10.0.13 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=true -p:TrimMode=partial -p:ILLinkTreatWarningsAsErrors=true \
    -p:UseSharedCompilation=false -p:DebugType=none -p:DebugSymbols=false \
    -p:PublishDir="$BRIDGE/"
LICENSES="$BUNDLE/Contents/Resources/Licenses"
mkdir -p "$LICENSES"
cp "$ROOT/packaging/licenses/"*.txt "$LICENSES/"
NUGET_ROOT="$("$DOTNET" msbuild "$ROOT/src/Fatture.Cli/Fatture.Cli.csproj" -nologo -getProperty:NuGetPackageRoot)"
RUNTIME_PACKAGE="$NUGET_ROOT/microsoft.netcore.app.runtime.$RID/10.0.13"
cp "$RUNTIME_PACKAGE/LICENSE.TXT" "$LICENSES/Dotnet-LICENSE.txt"
cp "$RUNTIME_PACKAGE/THIRD-PARTY-NOTICES.TXT" "$LICENSES/Dotnet-THIRD-PARTY-NOTICES.txt"
mkdir -p "$EXTENSION/Contents/MacOS" "$EXTENSION/Contents/Frameworks"
"$DOTNET" msbuild "$ROOT/src/Fatture.Preview/Fatture.Preview.csproj" -t:Publish -restore -m:1 -nodeReuse:false \
    -p:Configuration=Release -p:Version="$VERSION" -p:RuntimeIdentifier="$RID" -p:RuntimeFrameworkVersion=10.0.13 -p:SelfContained=true \
    -p:UseSharedCompilation=false -p:DebugType=none -p:DebugSymbols=false -p:StripSymbols=true \
    -p:PublishDir="$STAGE/native/"
cp "$STAGE/native/FatturePreviewCore.dylib" "$EXTENSION/Contents/Frameworks/"
codesign --force --sign - "$EXTENSION/Contents/Frameworks/FatturePreviewCore.dylib"
cp "$ROOT/packaging/macos/quicklook/Info.plist" "$EXTENSION/Contents/Info.plist"
python3 "$ROOT/packaging/macos/stamp-version.py" "$EXTENSION/Contents/Info.plist"
xcrun swiftc -parse-as-library -O -application-extension -module-name FatturePreview \
    -module-cache-path "${TMPDIR:-/tmp}/lettore-fatture-swift-cache" -target "$TARGET" \
    "$ROOT/src/Fatture.Mac/QuickLook/"*.swift \
    -framework AppKit -framework QuickLookUI -framework PDFKit -framework UniformTypeIdentifiers \
    -Xlinker -e -Xlinker _NSExtensionMain -o "$EXTENSION/Contents/MacOS/FatturePreview"
codesign --force --sign - --entitlements "$ROOT/packaging/macos/quicklook/Preview.entitlements" "$EXTENSION"
codesign --force --deep --preserve-metadata=entitlements --sign - "$BUNDLE"
# Replace only this generated bundle, after a successful build.
if [[ -d "$APP" ]]; then mv "$APP" "$STAGE/previous.app"; fi
mv "$BUNDLE" "$APP"
echo "App creata: $APP"
du -sh "$APP"
