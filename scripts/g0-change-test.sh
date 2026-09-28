#!/usr/bin/env bash
# G0.4 "one change in C# changes every target": edits ONLY samples/RotatingCube/Shared/CubeGame.cs (rotation speed
# and cube size), rebuilds headless, desktop and web, captures tick 60 on each, then always restores the file.
# Evidence lands in artifacts/evidence-change/. Renderer code is untouched (checked with git diff at the end).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_NOLOGO=1
GAME="samples/RotatingCube/Shared/CubeGame.cs"
OUT="artifacts/evidence-change"
BACKUP="$(mktemp)"
cp "$GAME" "$BACKUP"
restore() { cp "$BACKUP" "$GAME"; rm -f "$BACKUP"; }
trap restore EXIT

rm -rf "$OUT" && mkdir -p "$OUT"
renderer_checksum() { find src web/three-adapter -type f \( -name '*.cs' -o -name '*.js' -o -name '*.csproj' \) -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/node_modules/*' | sort | xargs shasum -a 256 | shasum -a 256 | cut -c1-16; }
BEFORE="$(renderer_checksum)"
# The only edit: faster rotation law and a smaller cube (geometry). Nothing in src/ changes.
sed -i '' 's/public const float RadiansPerSecond = 0.9f;/public const float RadiansPerSecond = 2.1f;/' "$GAME"
sed -i '' 's/public const float HalfSize = 0.5f;/public const float HalfSize = 0.3f;/' "$GAME"
grep -q "RadiansPerSecond = 2.1f" "$GAME" && grep -q "HalfSize = 0.3f" "$GAME"
git diff --no-index --stat "$BACKUP" "$GAME" > "$OUT/change.diffstat" || true

dotnet run --project samples/RotatingCube/Headless -- --checkpoints 60 > "$OUT/headless.log"
dotnet build samples/RotatingCube/Desktop -v q -nologo >/dev/null
dotnet run --project samples/RotatingCube/Desktop --no-build -- --capture "$OUT/desktop" --checkpoints 60 | grep -v '^snapshot' || true
dotnet publish samples/RotatingCube/Browser -c Release -o "$OUT/web-export" -v q -nologo >/dev/null
scripts/web-evidence.sh "$OUT/web-export/wwwroot" "$OUT/web-dom.html" 8767

AFTER="$(renderer_checksum)"
echo "renderer+adapter checksum before=$BEFORE after=$AFTER" | tee "$OUT/renderer-checksum.txt"
[[ "$BEFORE" == "$AFTER" ]] || { echo "[change-test] renderer code changed" >&2; exit 1; }
PARITY_TICKS=60 BASELINE_DIR=artifacts/evidence bun scripts/g0-parity.ts "$OUT"
