#!/usr/bin/env bash
# Multi-camera parity (docs/rendering-passes.md): the Minimap sample (main view + minimap, two passes) on headless (CPU),
# desktop (Metal) and web (WASM + three.js WebGL2) through scripts/g1lit.sh, which compares snapshots and, with
# lit-pixel-report.ts, every whole frame and every per-pass capture (edges from the CPU reference's geometric masks, also per pass; interiors
# Δ ≤ 1 + floor(gradient/16), edges reported). This wrapper
# also fails when a host wrote no per-pass captures. Evidence: artifacts/evidence-g1lit-minimap. Set WEB_PORT if
# 8768 is taken; opens a window briefly. WEB_GPU=1 passes through to g1lit.sh (real-GPU browser leg "web-gpu") and
# also requires its per-pass captures.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
EV="artifacts/evidence-g1lit-minimap"
STATUS=0
SAMPLE=Minimap scripts/g1lit.sh || STATUS=$?
TARGETS="headless headless-desktop-size web desktop"; [ "${WEB_GPU:-0}" = 1 ] && TARGETS="$TARGETS web-gpu"
for target in $TARGETS; do
  count="$({ find "$EV/$target" -name '*-pass-*.png' 2>/dev/null || true; } | wc -l | tr -d ' ')"
  echo "[g1passes] $target: $count per-pass captures"
  [ "$count" -gt 0 ] || STATUS=1
done
exit "$STATUS"
