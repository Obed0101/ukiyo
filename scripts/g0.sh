#!/usr/bin/env bash
# Reproduces every G0 check from a clean checkout on an Apple Silicon Mac and writes evidence to artifacts/.
# Steps: native deps → JS deps → tests → headless → desktop (Metal) → web → NativeAOT single binary →
# web AOT → parity → single-change propagation. Opens a window briefly for the desktop captures.
# WEB_GPU=1 adds the target "web-gpu" for both web builds: the same published build in headless Chrome on the real GPU
# (WebGL2 through ANGLE Metal; the leg fails unless the page reports the Apple GPU). Default off (CI has no GPU).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
EV="artifacts/evidence"; EVA="artifacts/evidence-aot"
HOSTS="samples/RotatingCube/Hosts"   # template layout (docs/game-layout.md): one csproj per host
PORT="${WEB_PORT:-8765}"
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
# The real-GPU browser leg (WEB_GPU=1) on a published wwwroot; a failure is recorded, and the parity step reports it.
web_gpu() {
  [ "${WEB_GPU:-0}" = 1 ] || return 0
  step "web on the real GPU (WebGL2 through ANGLE Metal)"
  WEB_EVIDENCE_GL=metal scripts/web-evidence.sh "$1" "$2" "$3" || { WEB_GPU_FAILED=1; echo "[g0] real-GPU web leg failed"; }
}
# web-evidence.sh would silently read another process's server if the port were taken.
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then echo "[g0] port $PORT is in use; set WEB_PORT" >&2; exit 1; fi

step "native dependencies (wgpu-native, SDL3)"; scripts/fetch-native.sh | tail -1
step "three.js adapter dependencies"; (cd web/three-adapter && bun install --frozen-lockfile >/dev/null && bun test)
step "C# tests"; dotnet test tests/Ukiyo.Tests -nologo -v q
rm -rf "$EV" "$EVA" && mkdir -p "$EV" "$EVA"
step "headless (NullRenderer)"; dotnet run --project "$HOSTS/Headless" > "$EV/headless.log"; cp "$EV/headless.log" "$EVA/"
step "desktop dev host (wgpu/Metal, resize test)"; dotnet build "$HOSTS/Desktop" -nologo -v q
dotnet run --project "$HOSTS/Desktop" --no-build -- --capture "$EV/desktop" --resize-test | grep -v '^snapshot'
step "web export (.NET WASM + Three.js)"; rm -rf artifacts/web && dotnet publish "$HOSTS/Browser" -c Release -o artifacts/web -nologo -v q
scripts/web-evidence.sh artifacts/web/wwwroot "$EV/web-dom.html" "$PORT"
web_gpu artifacts/web/wwwroot "$EV/web-gpu-dom.html" "$PORT"
step "parity (dev builds)"; bun scripts/g0-parity.ts "$EV"
step "NativeAOT single binary, run in isolation"; rm -rf artifacts/desktop && dotnet publish "$HOSTS/Export.Desktop" -c Release -o artifacts/desktop -nologo -v q
ls -la artifacts/desktop; otool -L artifacts/desktop/rotating-cube > "$EVA/otool.txt"
NON_SYSTEM="$(tail -n +2 "$EVA/otool.txt" | awk '{print $1}' | grep -vE '^/(System|usr/lib)/' || true)"
[[ -z "$NON_SYSTEM" ]] || { echo "[g0] non-system dependency in single binary: $NON_SYSTEM" >&2; exit 1; }
ISO="$(mktemp -d)"; cp artifacts/desktop/rotating-cube "$ISO/"
(cd "$ISO" && env -i HOME="$HOME" PATH=/usr/bin:/bin ./rotating-cube --capture "$ISO/capture") | grep -v '^snapshot'
mkdir -p "$EVA/desktop" && cp "$ISO"/capture/* "$EVA/desktop/"
step "web AOT profile (RunAOTCompilation)"; rm -rf artifacts/web-aot && dotnet publish "$HOSTS/Browser" -c Release -p:RunAOTCompilation=true -o artifacts/web-aot -nologo -v q
scripts/web-evidence.sh artifacts/web-aot/wwwroot "$EVA/web-dom.html" 8766
web_gpu artifacts/web-aot/wwwroot "$EVA/web-gpu-dom.html" 8766
step "parity (NativeAOT desktop + AOT web)"; bun scripts/g0-parity.ts "$EVA"
# The change test keeps its one (SwiftShader) browser leg; the real-GPU leg is measured on the two builds above.
step "single C# change reaches every target"; WEB_GPU=0 scripts/g0-change-test.sh
step "sizes"; du -sh artifacts/desktop/rotating-cube artifacts/web/wwwroot artifacts/web-aot/wwwroot
[ -z "${WEB_GPU_FAILED:-}" ] || { echo "[g0] the real-GPU web leg failed (see above)" >&2; exit 1; }
echo; echo "G0 evidence complete: $EV, $EVA, artifacts/evidence-change"
