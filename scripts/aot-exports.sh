#!/usr/bin/env bash
# NativeAOT export checks for the older-layout samples (UKIYO-42, docs/export.md). Per sample: publish
# <game>/Export.Desktop, require only system libraries (otool -L) and no dev bridge (strings), run the binary alone from
# a temporary folder with an empty environment (--capture, opens a window), then compare its snapshot lines and its
# source-hash line with a headless JIT run at the same checkpoints, renderer/backend fields removed.
# The window run takes /tmp/ukiyo-gate.lock (shared with other window-opening checks) and always releases it.
# Usage: scripts/aot-exports.sh [Sample ...]   (default: TexturedQuad LitCube SceneHop; CHECKPOINTS=0,60)
# Evidence: artifacts/evidence-aot-exports/<kebab>/ and artifacts/evidence-aot-exports/results.md.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
EV="artifacts/evidence-aot-exports"; CHECKPOINTS="${CHECKPOINTS:-0,60}"; LOCK="/tmp/ukiyo-gate.lock"
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

kebab_of() {
  case "$1" in
    TexturedQuad) echo textured-quad ;;
    LitCube) echo lit-cube ;;
    SceneHop) echo scene-hop ;;
    *) echo "[aot-exports] unknown sample $1 (TexturedQuad, LitCube, SceneHop)" >&2; return 1 ;;
  esac
}

HAVE_LOCK=0
release_lock() { if [[ "$HAVE_LOCK" == 1 ]]; then rmdir "$LOCK" 2>/dev/null || true; HAVE_LOCK=0; fi; }
take_lock() { until mkdir "$LOCK" 2>/dev/null; do sleep 10; done; HAVE_LOCK=1; }
trap release_lock EXIT
trap 'release_lock; exit 130' INT TERM

# The snapshot JSON keeps a fixed key order (SnapshotWriter): renderer and backend follow tick, so one substitution
# drops exactly the two host-dependent fields and leaves everything else byte for byte.
snapshots() { grep '^snapshot ' "$1" | sed -E 's/"renderer":"[^"]*","backend":"[^"]*",//' || true; }
source_line() { grep -m1 '^\[ukiyo\] target=' "$1" | sed -E 's/^\[ukiyo\] target=[^ ]+ //' || true; }

SAMPLES=("$@"); [[ ${#SAMPLES[@]} -gt 0 ]] || SAMPLES=(TexturedQuad LitCube SceneHop)
for sample in "${SAMPLES[@]}"; do kebab_of "$sample" >/dev/null; done
rm -rf "$EV" && mkdir -p "$EV"
EXPECTED="$(tr ',' '\n' <<< "$CHECKPOINTS" | grep -c .)"
ROWS=(); FAILURES=0

for sample in "${SAMPLES[@]}"; do
  kebab="$(kebab_of "$sample")"; out="$EV/$kebab"; mkdir -p "$out"; failed=0
  fail() { echo "[aot-exports] $sample: $1" >&2; failed=1; }

  step "$sample: publish samples/$sample/Export.Desktop"
  rm -rf "artifacts/$kebab"
  dotnet publish "samples/$sample/Export.Desktop" -c Release -o "artifacts/$kebab" -nologo -v q
  binary="artifacts/$kebab/$kebab"
  bytes="$(stat -f %z "$binary")"
  ls -la "artifacts/$kebab"

  otool -L "$binary" | tee "$out/otool.txt"
  non_system="$(tail -n +2 "$out/otool.txt" | awk '{print $1}' | grep -vE '^/(System/Library/Frameworks|usr/lib)/' || true)"
  [[ -z "$non_system" ]] && otool_result="system only" || { otool_result="non-system: $non_system"; fail "$otool_result"; }
  devbridge="$(strings "$binary" | grep -c DevBridge || true)"
  echo "DevBridge strings: $devbridge"
  [[ "$devbridge" == "0" ]] || fail "the export contains the dev bridge ($devbridge strings)"

  step "$sample: headless JIT run (checkpoints $CHECKPOINTS)"
  dotnet run --project "samples/$sample/Headless" -c Release -- --checkpoints "$CHECKPOINTS" > "$out/headless.log"
  grep '^\[ukiyo\]' "$out/headless.log"

  step "$sample: export alone in a temporary folder, empty environment (opens a window)"
  iso="$(mktemp -d "${TMPDIR:-/tmp}/ukiyo-aot-$kebab-XXXXXX")"; cp "$binary" "$iso/"
  take_lock
  run_exit=0
  (cd "$iso" && env -i HOME="$HOME" PATH=/usr/bin:/bin "./$kebab" --capture "$iso/capture" --checkpoints "$CHECKPOINTS") > "$out/export.log" 2>&1 || run_exit=$?
  release_lock
  grep -v '^snapshot ' "$out/export.log" || true
  [[ "$run_exit" == 0 ]] || fail "isolated run exited $run_exit"
  mkdir -p "$out/capture" && cp "$iso"/capture/* "$out/capture/" 2>/dev/null || fail "no frames captured"
  rm -rf "$iso"
  frames="$(find "$out/capture" -name 'desktop-tick-*.png' | wc -l | tr -d ' ')"
  [[ "$frames" == "$EXPECTED" ]] || fail "captured $frames frames, expected $EXPECTED"
  frame_size="$(grep -m1 -oE 'captured [^ ]+ [0-9]+x[0-9]+' "$out/export.log" | awk '{print $3}' || true)"

  snapshots "$out/headless.log" > "$out/headless.snapshots"
  snapshots "$out/export.log" > "$out/export.snapshots"
  count="$(grep -c . "$out/export.snapshots" || true)"
  if [[ "$count" == "$EXPECTED" ]] && cmp -s "$out/headless.snapshots" "$out/export.snapshots"; then
    snapshot_result="equal ($count lines)"
  else
    snapshot_result="DIFFERENT (export $count lines, expected $EXPECTED)"; fail "snapshot lines differ from headless"
    diff "$out/headless.snapshots" "$out/export.snapshots" | cut -c1-300 >&2 || true
  fi
  headless_source="$(source_line "$out/headless.log")"; export_source="$(source_line "$out/export.log")"
  if [[ -n "$export_source" && "$export_source" == "$headless_source" ]]; then hash_result="equal: \`$export_source\`"
  else hash_result="DIFFERENT: headless \`$headless_source\` export \`$export_source\`"; fail "source hash line differs"; fi

  [[ "$failed" == 0 ]] && verdict=PASS || { verdict=FAIL; FAILURES=$((FAILURES + 1)); }
  ROWS+=("| $sample | \`$kebab\` | $bytes | $otool_result | $devbridge | $snapshot_result | $hash_result | $frames × $frame_size | $verdict |")
done

{
  echo "| Sample | Binary | Bytes | otool -L | DevBridge strings | Snapshots vs headless (ticks $CHECKPOINTS, renderer/backend removed) | Source line vs headless | Frames | Result |"
  echo "|---|---|---|---|---|---|---|---|---|"
  printf '%s\n' "${ROWS[@]}"
} > "$EV/results.md"
step "results ($EV/results.md)"; cat "$EV/results.md"
exit $((FAILURES > 0))
