#!/usr/bin/env bash
# Publish a self-contained single-file build for one runtime.
# Usage: scripts/publish.sh [rid]   (default: current OS/arch; e.g. osx-arm64, osx-x64, win-x64, win-arm64)
# Output: artifacts/<rid>/ and, for osx-*, artifacts/<rid>/QuotaTray.app
set -euo pipefail
cd "$(dirname "$0")/.."

RID="${1:-}"
if [[ -z "$RID" ]]; then
  ARCH="$(uname -m)"; [[ "$ARCH" == "x86_64" ]] && ARCH=x64 || ARCH=arm64
  case "$(uname -s)" in
    Darwin) RID="osx-$ARCH" ;;
    Linux)  RID="linux-$ARCH" ;;
    *)      RID="win-$ARCH" ;;
  esac
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
OUT="artifacts/$RID"
rm -rf "$OUT"
dotnet publish src/QuotaTray.App/QuotaTray.App.csproj -c Release -r "$RID" -o "$OUT/publish"

if [[ "$RID" == osx-* ]]; then
  APP="$OUT/QuotaTray.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
  sed "s/__VERSION__/$VERSION/g" src/QuotaTray.App/macos/Info.plist > "$APP/Contents/Info.plist"
  # Ad-hoc signature so Gatekeeper lets a right-click → Open through. Replace '-' with a Developer ID for notarisation.
  codesign --force --deep --sign - "$APP"
  echo "Bundle: $APP"
fi

echo "Published $RID v$VERSION to $OUT"
