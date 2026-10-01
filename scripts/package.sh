#!/usr/bin/env bash
# Build the installer for one runtime from a previous scripts/publish.sh run.
#   osx-*  → artifacts/<rid>/quota-tray-<rid>.dmg        (needs macOS: hdiutil)
#   win-*  → artifacts/<rid>/quota-tray-<rid>-setup.exe  (needs Windows: Inno Setup 6, ISCC on PATH or default install dir)
# Usage: scripts/package.sh [rid]   (default: current OS/arch, same rule as publish.sh)
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

case "$RID" in
  osx-*)
    APP="$OUT/QuotaTray.app"
    [[ -d "$APP" ]] || { echo "missing $APP — run scripts/publish.sh $RID first" >&2; exit 1; }
    DMG="$OUT/quota-tray-$RID.dmg"
    STAGE="$(mktemp -d)"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    rm -f "$DMG"
    hdiutil create -quiet -volname "QuotaTray $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
    rm -rf "$STAGE"
    echo "Installer: $DMG"
    ;;
  win-*)
    [[ -d "$OUT/publish" ]] || { echo "missing $OUT/publish — run scripts/publish.sh $RID first" >&2; exit 1; }
    ARCH="${RID#win-}"; [[ "$ARCH" == "x64" ]] && ISARCH="x64compatible" || ISARCH="arm64"
    ISCC="$(command -v ISCC.exe || command -v iscc || true)"
    [[ -n "$ISCC" ]] || ISCC="/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
    [[ -x "$ISCC" ]] || { echo "Inno Setup 6 not found (ISCC.exe). Install from https://jrsoftware.org/isinfo.php or 'choco install innosetup'." >&2; exit 1; }
    "$ISCC" "/DRid=$RID" "/DArch=$ISARCH" "/DVersion=$VERSION" "/Q" "installer/windows/quota-tray.iss"
    echo "Installer: $OUT/quota-tray-$RID-setup.exe"
    ;;
  *)
    echo "No installer defined for $RID" >&2; exit 1 ;;
esac
