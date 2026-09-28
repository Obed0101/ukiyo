#!/usr/bin/env bash
# Serves a published web export with a plain static server (no Ukiyo daemon), runs the checkpoint evidence in
# headless Chrome and writes the resulting DOM. The server is always stopped on exit.
# Usage: scripts/web-evidence.sh <wwwroot> <out-dom.html> [port]
set -euo pipefail
ROOT="$1"; OUT="$2"; PORT="${3:-8765}"
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$ROOT" >/dev/null 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/index.html" >/dev/null && break; sleep 0.1; done
"$CHROME" --headless=new --use-angle=swiftshader --enable-unsafe-swiftshader --window-size=1200,800 \
  --virtual-time-budget=20000 --dump-dom "http://127.0.0.1:$PORT/index.html?checkpoints=0,60,180,600" > "$OUT" 2>/dev/null
grep -q "ukiyo:evidence-ready" "$OUT" || { echo "[web-evidence] evidence not produced" >&2; exit 1; }
echo "[web-evidence] $OUT"
