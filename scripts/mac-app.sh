#!/usr/bin/env bash
# Builds Ukiyo.app (the Studio as a native Mac app: Swift + AppKit + WKWebView, system swiftc only) and installs it.
# Usage: scripts/mac-app.sh [--no-install]
#   default        build artifacts/mac/Ukiyo.app, quit a running copy, replace /Applications/Ukiyo.app
#   --no-install   build artifacts/mac/Ukiyo.app only
# The checkout this script lives in is baked into the app as the Studio's repo; ~/.config/ukiyo/app.json
# {"repo": "..."} overrides it at run time. Output is ad-hoc signed (codesign -s -).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/apps/mac"
OUT="$ROOT/artifacts/mac"
APP="$OUT/Ukiyo.app"
INSTALL=1
[[ "${1:-}" == "--no-install" ]] && INSTALL=0

[[ -f "$ROOT/studio/src/server.ts" ]] || { echo "[mac-app] studio/src/server.ts not found under $ROOT" >&2; exit 1; }
command -v swiftc >/dev/null || { echo "[mac-app] swiftc not found (install the Xcode command line tools)" >&2; exit 1; }

rm -rf "$APP" "$OUT/icon.iconset" "$OUT/icon-maker"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "== compile"
swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos13.0" \
  -framework AppKit -framework WebKit \
  "$SRC"/Sources/*.swift -o "$APP/Contents/MacOS/Ukiyo"

echo "== icon"
swiftc -O -swift-version 5 "$SRC/IconMaker.swift" -o "$OUT/icon-maker"
"$OUT/icon-maker" "$ROOT/.github/assets/brand/ukiyo-app-icon.svg" "$OUT/icon.iconset"
iconutil -c icns "$OUT/icon.iconset" -o "$APP/Contents/Resources/Ukiyo.icns"
rm -rf "$OUT/icon.iconset" "$OUT/icon-maker"

echo "== bundle"
cp "$SRC/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :UkiyoRepo $ROOT" "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null
codesign --force --sign - "$APP"
codesign --verify --strict "$APP"
echo "built $APP (repo: $ROOT)"

if [[ "$INSTALL" == 1 ]]; then
  echo "== install"
  if pgrep -x Ukiyo >/dev/null; then
    osascript -e 'tell application id "dev.ukiyo.studio" to quit' >/dev/null 2>&1 || true
    for _ in $(seq 1 40); do pgrep -x Ukiyo >/dev/null || break; sleep 0.25; done
  fi
  rm -rf /Applications/Ukiyo.app
  cp -R "$APP" /Applications/Ukiyo.app
  echo "installed /Applications/Ukiyo.app"
fi
