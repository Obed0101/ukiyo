#!/usr/bin/env bash
# G1 parity: runs LanternRun with one input script on headless (CPU capture), desktop (Metal capture) and web
# (WASM + three.js), then compares whole snapshots at the same checkpoints. Opens a window briefly; it must be
# visible on the active desktop or macOS never presents a frame. Evidence goes to artifacts/evidence-g1.
# WEB_GPU=1 adds the target "web-gpu": the same published build in headless Chrome on the real GPU (WebGL2 through
# ANGLE Metal; the leg fails unless the page reports the Apple GPU), compared like "web". Default off (CI has no GPU).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
EV="artifacts/evidence-g1"; INPUT="samples/LanternRun/parity/input.json"; CHECKPOINTS="${CHECKPOINTS:-0,30,60,120,180,240,300}"
PORT="${WEB_PORT:-8767}"
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
# web-evidence.sh would silently read another process's server if the port were taken.
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then echo "[g1] port $PORT is in use; set WEB_PORT" >&2; exit 1; fi
rm -rf "$EV" && mkdir -p "$EV"

step "headless (SoftwareRenderer)"
dotnet run --project samples/LanternRun/Headless -c Release -- --checkpoints "$CHECKPOINTS" --input "$INPUT" \
  --capture "$EV/headless" --size 960x640 --audio "$EV/headless.wav" > "$EV/headless.log"
grep -E '^\[ukiyo\]|^audio' "$EV/headless.log"

step "desktop (WgpuRenderer, Metal)"
dotnet build samples/LanternRun/Desktop -c Release -nologo -v q
dotnet run --project samples/LanternRun/Desktop -c Release --no-build -- --capture "$EV/desktop" --checkpoints "$CHECKPOINTS" --input "$INPUT" | grep -v '^snapshot' | tee "$EV/desktop.log" \
  || echo "[g1] desktop capture failed; the parity step reports it and still compares headless with web"
# The window's drawing buffer follows the display scale, so the CPU reference for its pixels is rendered at that size.
if SIZE="$(grep -om1 '[0-9]*x[0-9]* backend=Metal' "$EV/desktop.log" | cut -d' ' -f1)" && [ -n "$SIZE" ]; then
  dotnet run --project samples/LanternRun/Headless -c Release --no-build -- --checkpoints "$CHECKPOINTS" --input "$INPUT" \
    --capture "$EV/headless-desktop-size" --size "$SIZE" > /dev/null
  echo "[g1] CPU reference at desktop size $SIZE"
fi

step "web (WASM + three.js, WebGL2)"
rm -rf artifacts/lantern-web && dotnet publish samples/LanternRun/Browser -c Release -o artifacts/lantern-web -nologo -v q
cp "$INPUT" artifacts/lantern-web/wwwroot/input.json
scripts/web-evidence.sh artifacts/lantern-web/wwwroot "$EV/web-dom.html" "$PORT" "checkpoints=$CHECKPOINTS&fixed=1&input=input.json"
if [ "${WEB_GPU:-0}" = 1 ]; then
  step "web on the real GPU (WebGL2 through ANGLE Metal)"
  WEB_EVIDENCE_GL=metal scripts/web-evidence.sh artifacts/lantern-web/wwwroot "$EV/web-gpu-dom.html" "$PORT" "checkpoints=$CHECKPOINTS&fixed=1&input=input.json" \
    || { WEB_GPU_FAILED=1; echo "[g1] real-GPU web leg failed; the parity step reports it"; }
fi

step "parity"
bun scripts/g1-parity.ts "$EV"
exit "${WEB_GPU_FAILED:-0}"
