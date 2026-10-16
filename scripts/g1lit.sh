#!/usr/bin/env bash
# G1Lit parity: runs a 3D sample (default LitCube) on headless (CPU capture), desktop (Metal capture) and web (WASM +
# three.js WebGL2) at the same checkpoints, compares snapshots and pixels with scripts/g1-parity.ts and splits pixel
# differences into silhouette edges and interiors with scripts/lit-pixel-report.ts. SAMPLE=RotatingCube gives the
# unlit G0 baseline for the same comparison. Opens a window briefly; it must be visible on the active desktop or
# macOS never presents a frame. Evidence: artifacts/evidence-g1lit (or artifacts/evidence-g1lit-<sample>).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
SAMPLE="${SAMPLE:-LitCube}"; CHECKPOINTS="${CHECKPOINTS:-0,60,180,600}"
EV="artifacts/evidence-g1lit"; [ "$SAMPLE" = LitCube ] || EV="artifacts/evidence-g1lit-$(echo "$SAMPLE" | tr '[:upper:]' '[:lower:]')"
WEB="artifacts/g1lit-web-$(echo "$SAMPLE" | tr '[:upper:]' '[:lower:]')"
PORT="${WEB_PORT:-8768}"
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
# web-evidence.sh would silently read another checkout's server if the port were taken.
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then echo "[g1lit] port $PORT is in use; set WEB_PORT" >&2; exit 1; fi
rm -rf "$EV" && mkdir -p "$EV"

step "$SAMPLE headless (SoftwareRenderer) at the web canvas size"
dotnet run --project "samples/$SAMPLE/Headless" -c Release -- --checkpoints "$CHECKPOINTS" --capture "$EV/headless" --size 960x640 > "$EV/headless.log"
grep -E '^\[ukiyo\]' "$EV/headless.log"

step "$SAMPLE desktop (WgpuRenderer, Metal)"
dotnet build "samples/$SAMPLE/Desktop" -c Release -nologo -v q
dotnet run --project "samples/$SAMPLE/Desktop" -c Release --no-build -- --capture "$EV/desktop" --checkpoints "$CHECKPOINTS" | grep -v '^snapshot' | tee "$EV/desktop.log" \
  || echo "[g1lit] desktop capture failed; the parity step reports it and still compares headless with web"
# The window's drawing buffer follows the display scale, so the CPU reference for its pixels is rendered at that size.
if SIZE="$(grep -om1 '[0-9]*x[0-9]* backend=Metal' "$EV/desktop.log" | cut -d' ' -f1)" && [ -n "$SIZE" ]; then
  dotnet run --project "samples/$SAMPLE/Headless" -c Release --no-build -- --checkpoints "$CHECKPOINTS" --capture "$EV/headless-desktop-size" --size "$SIZE" > /dev/null
  echo "[g1lit] CPU reference at desktop size $SIZE"
fi

step "$SAMPLE web (WASM + three.js, WebGL2)"
rm -rf "$WEB" && dotnet publish "samples/$SAMPLE/Browser" -c Release -o "$WEB" -nologo -v q
scripts/web-evidence.sh "$WEB/wwwroot" "$EV/web-dom.html" "$PORT" "checkpoints=$CHECKPOINTS&fixed=1"

step "parity"
# Snapshots, source hashes and backends must pass in g1-parity.ts. Its whole-image pixel rows are informational here:
# mesh silhouettes rasterize differently on every renderer (the unlit G0 cube too), so lit-pixel-report.ts gates pixels
# on interiors, where lighting math drift would show.
bun scripts/g1-parity.ts "$EV" | tee "$EV/parity.log" || true
if grep -E '\| FAIL \|' "$EV/parity.log" | grep -v ' pixels headless≈'; then PARITY_FAILED=1; fi
bun scripts/lit-pixel-report.ts "$EV" || PARITY_FAILED=1
exit "${PARITY_FAILED:-0}"
