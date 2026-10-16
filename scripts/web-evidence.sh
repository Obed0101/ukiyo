#!/usr/bin/env bash
# Serves a published web export with a plain static server (no Ukiyo daemon), runs the checkpoint evidence in
# headless Chrome and writes the resulting DOM. The server is always stopped on exit.
# Usage: scripts/web-evidence.sh <wwwroot> <out-dom.html> [port] [query]
#   query defaults to checkpoints=0,60,180,600; e.g. "checkpoints=0,60&fixed=1&input=input.json" (input under wwwroot)
#   WEB_EVIDENCE_GL=swiftshader (default): WebGL2 on SwiftShader, the CPU implementation, so it runs on machines
#     without a GPU (CI).
#   WEB_EVIDENCE_GL=metal: WebGL2 on the real GPU through ANGLE's Metal backend, still headless, waiting in real time
#     (not virtual time) for the evidence. Fails unless the page's evidence reports an Apple GPU
#     ("ANGLE Metal Renderer: Apple …"), never SwiftShader.
set -euo pipefail
ROOT="$1"; OUT="$2"; PORT="${3:-8765}"; QUERY="${4:-checkpoints=0,60,180,600}"
GL="${WEB_EVIDENCE_GL:-swiftshader}"
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
case "$GL" in
  swiftshader) GL_FLAGS=(--use-angle=swiftshader --enable-unsafe-swiftshader) ;;
  metal) GL_FLAGS=(--use-angle=metal) ;;
  *) echo "[web-evidence] WEB_EVIDENCE_GL must be swiftshader or metal, got '$GL'" >&2; exit 2 ;;
esac
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$ROOT" >/dev/null 2>&1 &
SERVER=$!
# Waiting for the server to exit frees the port before a following run (the real-GPU leg reuses it right away).
trap 'kill $SERVER 2>/dev/null; wait $SERVER 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/index.html" >/dev/null && break; sleep 0.1; done
# A server that could not bind exits at once; the probe above may then have reached another process on the port.
kill -0 "$SERVER" 2>/dev/null || { echo "[web-evidence] static server on port $PORT did not start" >&2; exit 1; }
URL="http://127.0.0.1:$PORT/index.html?$QUERY"
if [ "$GL" = metal ]; then
  # On the GPU, virtual time outran the AOT WebAssembly compile in 4 of 5 runs (the dump showed "loading .NET
  # WebAssembly…"), so this leg waits in real time for the page's evidence (web-evidence-realtime.ts).
  bun "$(dirname "$0")/web-evidence-realtime.ts" "$URL" "$OUT" "${GL_FLAGS[@]}" --window-size=1200,800
else
  # Virtual time runs ahead while the page idles on the WebAssembly compile (seconds of real time for an AOT build),
  # so the budget is far larger than the run itself; it costs nothing once the evidence is published.
  "$CHROME" --headless=new "${GL_FLAGS[@]}" --window-size=1200,800 \
    --virtual-time-budget=600000 --dump-dom "$URL" > "$OUT" 2>/dev/null
fi
grep -q "ukiyo:evidence-ready" "$OUT" || { echo "[web-evidence] evidence not produced" >&2; exit 1; }
# The WebGL device string the adapter read (UNMASKED_RENDERER_WEBGL), as the page published it.
DEVICE="$(grep -om1 '"device":"[^"]*"' "$OUT" | cut -d'"' -f4 || true)"
echo "[web-evidence] gl=$GL device=\"$DEVICE\""
if [ "$GL" = metal ]; then
  if [[ "$DEVICE" == *SwiftShader* || "$DEVICE" != *"Metal Renderer: Apple"* ]]; then
    echo "[web-evidence] real-GPU leg did not run on the Apple GPU: device=\"$DEVICE\"" >&2; exit 1
  fi
fi
echo "[web-evidence] $OUT"
