#!/usr/bin/env bash
# Clean-machine run of a NativeAOT export (UKIYO-14, docs/export.md): the single binary must work with no .NET SDK or
# runtime reachable and no repository file. Publishes <game>'s Export.Desktop host (or takes --binary), copies ONLY the
# executable into an empty temp folder, and runs it there with an empty environment (env -i PATH=/usr/bin:/bin
# HOME=<temp>) with --capture, which opens a window. Checks: no dotnet resolvable under that environment (command -v,
# DOTNET_ROOT unset), otool -L lists only /System/Library/Frameworks, /usr/lib or bundled dylibs next to the binary
# (the export bundles none), exit 0, GPU frames written, and, where dyld allows it, every dylib dyld loaded came from the
# OS or the binary's own folder. Takes /tmp/ukiyo-gate.lock for the window run and always releases it.
# Usage: scripts/clean-run.sh [Sample] [--binary PATH]   (default RotatingCube; CHECKPOINTS=0,60)
# Evidence: artifacts/evidence-clean-run/<kebab>/ and artifacts/evidence-clean-run/results.md. Exit 1 on any failure.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
SAMPLE="RotatingCube"; BINARY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --binary) BINARY="${2:?--binary needs a path}"; shift 2 ;;
    -*) echo "[clean-run] unknown option $1; usage: scripts/clean-run.sh [Sample] [--binary PATH]" >&2; exit 2 ;;
    *) SAMPLE="$1"; shift ;;
  esac
done
CHECKPOINTS="${CHECKPOINTS:-0,60}"; LOCK="/tmp/ukiyo-gate.lock"
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
failed=0
fail() { echo "[clean-run] $SAMPLE: $1" >&2; failed=1; }

if [[ -z "$BINARY" ]]; then
  # dotnet is only needed to build; the run below never sees it.
  DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
  host="samples/$SAMPLE/Hosts/Export.Desktop"; [[ -d "$host" ]] || host="samples/$SAMPLE/Export.Desktop"
  [[ -d "$host" ]] || { echo "[clean-run] samples/$SAMPLE has no Export.Desktop host" >&2; exit 2; }
  kebab="$(DOTNET_NOLOGO=1 "$DOTNET" msbuild "$host" -getProperty:AssemblyName -nologo | tr -d '[:space:]')"
  step "$SAMPLE: publish $host"
  rm -rf "artifacts/$kebab"
  DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET" publish "$host" -c Release -o "artifacts/$kebab" -nologo -v q
  BINARY="artifacts/$kebab/$kebab"
fi
[[ -x "$BINARY" ]] || { echo "[clean-run] $BINARY is not an executable" >&2; exit 2; }
kebab="$(basename "$BINARY")"
EV="artifacts/evidence-clean-run"; out="$EV/$kebab"
rm -rf "$out" && mkdir -p "$out"

HAVE_LOCK=0
release_lock() { if [[ "$HAVE_LOCK" == 1 ]]; then rmdir "$LOCK" 2>/dev/null || true; HAVE_LOCK=0; fi; }
trap release_lock EXIT
trap 'release_lock; exit 130' INT TERM

# realpath: the default temp dir on macOS is a symlink.
TMP="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/ukiyo-clean-XXXXXX")" && pwd -P)"
trap 'release_lock; rm -rf "$TMP"' EXIT
cp "$BINARY" "$TMP/$kebab"
mkdir -p "$TMP/home"
CLEAN=(env -i PATH=/usr/bin:/bin HOME="$TMP/home")

step "$kebab: nothing to find a .NET installation with"
resolved="$("${CLEAN[@]}" /bin/sh -c 'command -v dotnet || true; echo "DOTNET_ROOT=${DOTNET_ROOT-unset}"')"
echo "$resolved"
grep -q '^/' <<< "$(grep -v '^DOTNET_ROOT=' <<< "$resolved")" && fail "dotnet resolves under PATH=/usr/bin:/bin"
grep -q '^DOTNET_ROOT=unset$' <<< "$resolved" || fail "DOTNET_ROOT is set in the clean environment"
[[ ! -e "$TMP/home/.dotnet" ]] || fail "HOME holds a .dotnet"

step "$kebab: otool -L (system libraries and bundled dylibs only)"
otool -L "$TMP/$kebab" | tee "$out/otool.txt"
bad="$(tail -n +2 "$out/otool.txt" | awk '{print $1}' | grep -vE '^(/System/Library/Frameworks/|/usr/lib/|@loader_path/|@executable_path/)' || true)"
[[ -z "$bad" ]] || fail "otool lists libraries outside the OS: $bad"
bundled="$(tail -n +2 "$out/otool.txt" | awk '{print $1}' | grep -E '^@(loader|executable)_path/' || true)"
for lib in $bundled; do
  [[ -f "$TMP/${lib#@*_path/}" ]] || fail "$lib is bundled by name but the clean folder (the executable alone) has no such file"
done
echo "bundled dylibs referenced: ${bundled:-none}"

step "$kebab: run alone in $TMP, empty environment (opens a window)"
until mkdir "$LOCK" 2>/dev/null; do sleep 10; done; HAVE_LOCK=1
run_exit=0
(cd "$TMP" && "${CLEAN[@]}" DYLD_PRINT_LIBRARIES=1 "./$kebab" --capture "$TMP/capture" --checkpoints "$CHECKPOINTS") > "$out/run.log" 2> "$out/dyld.log" || run_exit=$?
release_lock
grep -v '^snapshot ' "$out/run.log" || true
[[ "$run_exit" == 0 ]] || { fail "the binary exited $run_exit"; tail -5 "$out/dyld.log" >&2; }
grep -q '^\[ukiyo\] target=desktop ' "$out/run.log" || fail "no [ukiyo] target=desktop line in its output"

EXPECTED="$(tr ',' '\n' <<< "$CHECKPOINTS" | grep -c .)"
mkdir -p "$out/capture" && cp "$TMP"/capture/* "$out/capture/" 2>/dev/null || fail "no frames captured"
frames="$(find "$out/capture" -name 'desktop-tick-*.png' | wc -l | tr -d ' ')"
[[ "$frames" == "$EXPECTED" ]] || fail "captured $frames frames, expected $EXPECTED"
for png in "$out"/capture/desktop-tick-*.png; do
  [[ "$(head -c 8 "$png" | xxd -p)" == "89504e470d0a1a0a" && "$(stat -f %z "$png")" -gt 1000 ]] || fail "$(basename "$png") is not a real PNG frame"
done

# dyld lists every image it loads when DYLD_PRINT_LIBRARIES is honored (it is not for hardened or system binaries).
loaded="$(grep -oE '/[^ ]+\.(dylib|framework/[^ ]+)' "$out/dyld.log" | sort -u || true)"
outside="$(grep -vE '^(/System/|/usr/lib/|/Library/Apple/|/System/Volumes/Preboot/)' <<< "$loaded" | grep -v "^$TMP/" | grep . || true)"
if [[ -z "$loaded" ]]; then dyld_result="not reported by dyld (not verified)"
elif [[ -n "$outside" ]]; then dyld_result="loaded from outside the OS: $outside"; fail "$dyld_result"
else dyld_result="$(wc -l <<< "$loaded" | tr -d ' ') images, all OS or the clean folder"; fi
echo "dyld: $dyld_result"

bytes="$(stat -f %z "$TMP/$kebab")"
[[ "$failed" == 0 ]] && verdict=PASS || verdict=FAIL
{
  echo "| Binary | Bytes | No dotnet reachable | otool -L | Run (env -i PATH=/usr/bin:/bin HOME=temp) | Frames | dyld |  Result |"
  echo "|---|---|---|---|---|---|---|---|"
  echo "| \`$kebab\` | $bytes | yes | $(tail -n +2 "$out/otool.txt" | wc -l | tr -d ' ') entries, OS only | exit $run_exit | $frames | $dyld_result | $verdict |"
} > "$EV/results.md"
step "result ($EV/results.md)"; cat "$EV/results.md"
exit $((failed))
