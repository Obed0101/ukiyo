#!/usr/bin/env bash
# Fetches and builds the pinned native dependencies of the desktop target into native/<rid>/.
# Pinned versions are the single source of truth for the G0 build; bump them here only.
set -euo pipefail

WGPU_VERSION="v29.0.1.1"
SDL_VERSION="3.4.16"
RID="osx-arm64"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/native/$RID"
WORK="$ROOT/native/.work"
mkdir -p "$OUT/lib" "$OUT/include" "$WORK"

if [[ ! -f "$OUT/lib/libwgpu_native.a" ]]; then
  echo "[fetch-native] wgpu-native $WGPU_VERSION"
  curl -sSfL -o "$WORK/wgpu.zip" "https://github.com/gfx-rs/wgpu-native/releases/download/$WGPU_VERSION/wgpu-macos-aarch64-release.zip"
  rm -rf "$WORK/wgpu" && mkdir -p "$WORK/wgpu" && unzip -q "$WORK/wgpu.zip" -d "$WORK/wgpu"
  cp "$WORK"/wgpu/lib/libwgpu_native.a "$WORK"/wgpu/lib/libwgpu_native.dylib "$OUT/lib/"
  mkdir -p "$OUT/include/webgpu" && cp "$WORK"/wgpu/include/webgpu/*.h "$OUT/include/webgpu/"
  shasum -a 256 "$WORK/wgpu.zip" | tee "$OUT/wgpu-native.sha256"
fi

if [[ ! -f "$OUT/lib/libSDL3.a" ]]; then
  echo "[fetch-native] SDL $SDL_VERSION (static + shared)"
  curl -sSfL -o "$WORK/sdl.tar.gz" "https://github.com/libsdl-org/SDL/releases/download/release-$SDL_VERSION/SDL3-$SDL_VERSION.tar.gz"
  shasum -a 256 "$WORK/sdl.tar.gz" | tee "$OUT/sdl3.sha256"
  rm -rf "$WORK/sdl" && mkdir -p "$WORK/sdl" && tar -xzf "$WORK/sdl.tar.gz" -C "$WORK/sdl" --strip-components=1
  cmake -S "$WORK/sdl" -B "$WORK/sdl-build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 -DSDL_SHARED=ON -DSDL_STATIC=ON -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF >/dev/null
  cmake --build "$WORK/sdl-build" >/dev/null
  cp "$WORK"/sdl-build/libSDL3.a "$OUT/lib/"
  cp "$WORK"/sdl-build/libSDL3.0.dylib "$OUT/lib/libSDL3.dylib"
  install_name_tool -id @rpath/libSDL3.dylib "$OUT/lib/libSDL3.dylib"
fi

echo "[fetch-native] ready: $OUT"
ls -la "$OUT/lib"
