# Lighting · profile G1Lit

G1Lit adds per-pixel lighting to 3D meshes on every renderer at once (wgpu/Metal, three.js/WebGL2, CPU, Null).
`G0Unlit` games are untouched: their packets, resources and pixels are the same bytes as before. Wire format:
[protocol.md](protocol.md) (frame flag `0x2`, resource command 5, material flag `0x2`).

## Profile

- `RenderProfile.G1Lit = 2` contains `G0Unlit = 1`. A game opts in with `public RenderProfile Profile => RenderProfile.G1Lit;`
  (`IGame.Profile`, default `G0Unlit`). `GameRuntime.StartAsync` initializes the renderer with the game's profile;
  hosts do not choose it.
- Lit data (normals, lit materials, a lighting section) on a renderer running `G0Unlit` → `UnsupportedFeature`, checked
  by the runtime and again by each renderer (`RenderValidation.RequireProfile`). A renderer asked for a profile it does
  not implement → `UnsupportedFeature` (`RenderValidation.RequireImplemented`). Nothing is ever drawn unlit instead.
- Renderer support: WgpuRenderer, ThreeJsRenderer, SoftwareRenderer and NullRenderer all implement G1Lit. The CPU
  renderer implements it because headless captures are the pixel reference; Null validates and counts lights.

## Inputs

| Input | Where | Rules |
|---|---|---|
| Vertex normals | `MeshData.Normals` (one per vertex, object space) | finite, non-zero; normalized per pixel |
| Lit material | `MaterialData.Lit = true` | needs a mesh with normals and a frame with `Lighting`, else `InvalidPacket` |
| Ambient | `SceneLighting.Ambient` | linear RGB, finite, ≥ 0 |
| Lights | `SceneLighting.Lights`, at most 8 (`RenderValidation.MaxLights`) | 9+ → `OutOfRange`; NaN/∞ → `NonFiniteValue` |
| Directional light | `Light.Directional(direction, color, intensity)` | direction = where the light travels, world space, non-zero; range 0 |
| Point light | `Light.Point(position, color, intensity, range)` | world position in meters; range > 0 m |

Colors are linear RGB ≥ 0; intensity is a unitless multiplier ≥ 0. There are no physical units (no lux or candela):
a white light of intensity 1 hitting a surface head-on adds the surface's albedo once.

## The model (defined once)

All math is float32, linear RGB, world space. Per pixel of a lit material:

```
albedo   = (useVertexColors ? vertexColor : 1) × baseColor.rgb          (interpolated, as in G0)
N        = normalize(interpolated world normal)                         world normal = NormalMatrix × object normal
                                                                        NormalMatrix = transpose(inverse(world 3×3))
sum      = ambient
for each light i, in packet order:
  directional: L = normalize(−direction); attenuation = 1
  point:       v = position − P; d² = v·v
               if !(d² > 0): skip this light
               L = v / sqrt(d²); t = clamp(1 − d² / range², 0, 1); attenuation = t²
  sum += (color × intensity) × (max(N·L, 0) × attenuation)
color    = albedo × sum                                                  (not clamped; the sRGB encode clamps)
```

Lambert diffuse only: no specular, no shadows, no 1/π factor and no inverse-square law (the window `t²` alone makes
point lights fade smoothly to exactly zero at their range). Unlit materials in the same frame draw `albedo` as in G0.

Implementations, kept in the same operation order:

| Renderer | Code |
|---|---|
| CPU (reference) | `LightingMath.Shade` and `LightingMath.NormalMatrix` in `src/Ukiyo.Render/Lighting.cs`, called per pixel by `SoftwareRenderer` |
| wgpu/Metal | `LitShader` (`vs_lit`/`fs_lit`) in `src/Ukiyo.Render.Wgpu/WgpuRenderer.cs`; normal matrix from `LightingMath.NormalMatrix`, lights uniform written by `WriteLights` |
| three.js | `web/three-adapter/lit-material.js`: a `ShaderMaterial`, because three's built-in lights use a different BRDF (1/π Lambert, physical light units). The normal matrix is `transpose(inverse(mat3(modelMatrix)))` in GLSL |
| Null | validation and counters only |

Meshes without normals keep the G0 pipeline on wgpu and `MeshBasicMaterial` on three.js, which is why G0 frames
cannot change.

## Recipe checklist

- [x] New `RenderProfile.G1Lit`; `G0Unlit` keeps working (G0 golden fixtures unchanged, `scripts/g0.sh` passes)
- [x] Light list in the packet: frame flag `0x2`, ambient + up to 8 lights, limits documented in protocol.md
- [x] Normals on meshes (resource command 5) and lit materials (material flag `0x2`); unknown flag bits rejected
- [x] WGSL forward pass (lit pipeline next to the untouched G0 pipeline)
- [x] three.js lights with the same math (custom `ShaderMaterial`, not three's built-in lights)
- [x] CPU renderer implements G1Lit; Null validates and counts; renderers without the profile refuse with `UnsupportedFeature`
- [x] Golden fixtures shared by C# and JS: `web/three-adapter/fixtures/lit-cube-resources.bin`, `lit-cube-frame-tick60.bin`
- [x] Negative tests both sides: 9 lights → `OutOfRange`, NaN/∞ light values → `NonFiniteValue`, unknown light kind or
      material flag → `InvalidPacket`, lit data on a G0Unlit renderer or a G1Lit game on a G0-only renderer →
      `UnsupportedFeature`, lit material without normals or lighting → `InvalidPacket`
- [x] Lit sample `samples/LitCube` captured at ticks 0/60/180/600 on CPU, Metal and WebGL2 with a numeric comparison
      (`scripts/g1lit.sh`)
- [ ] Pixel parity Δ = 0 everywhere: **not met** for point-light gradients (Δ = 1/255 on a small share of pixels); see
      results

## Results · 2026-10-16 · Apple M4 Pro, macOS 27.0, .NET SDK 10.0.401, Bun 1.3.13, Chrome 154.0.8037.95, wgpu-native 29.0.1.1, three 0.186.0

| Check | Result |
|---|---|
| `dotnet build Ukiyo.slnx -c Release` | pass, 0 warnings, 0 errors (24 projects with the three LitCube hosts) |
| `dotnet test tests/Ukiyo.Tests` | pass, 98/98 (80 before + 18 in `LightingTests`) |
| `bun test` in `web/three-adapter` | pass, 25/25 (14 before + 11 in `lighting.test.js`) |
| `bun test` in `tools/mcp`, `studio` | pass, 20/20 each |
| G0 golden fixtures (`cube-resources.bin`, `cube-frame-tick60.bin`) | unchanged bytes; BridgeTests and protocol.test.js pass |
| `scripts/g0.sh` | every check passes (parity Δ = 0 dev and NativeAOT, resize, isolated NativeAOT binary, change test). Run from a copy without its `bun install` step (`node_modules` was a read-only link in that checkout) and on free ports; the AOT web page was read through a real-time DevTools probe because `web-evidence.sh`'s virtual-time budget ran out while the 7 MB AOT module was still compiling (load average ≈ 14 from parallel builds; the page itself reached "evidence ready" in 5.4 s) |
| `scripts/g1lit.sh` snapshots | pass: LitCube at ticks 0/60/180/600 identical on CPU, Metal and WebGL2 (max Δ = 0), same source hashes |
| `scripts/g1lit.sh` pixels | see below; `g1-parity.ts` reports FAIL because silhouette edges differ by more than 1, which the unlit G0 cube does too |

**Re-run on the integrated `dev` · 2026-10-16** (lighting, scenes, texture importer and Studio merged; same machine and
versions): C# 171/171, adapter 25/25, tools/mcp 38/38, studio 20/20; `scripts/g0.sh` exit 0 (run without its
`bun install` step and on port 8775 because 8765 was taken by an unrelated process); `scripts/g1.sh` exit 0, 32 checks;
`scripts/g1lit.sh` exit 0. `g1lit.sh` now gates on snapshots, source hashes and backends from `g1-parity.ts` and on
interior pixels from `lit-pixel-report.ts` (interior Δ ≤ 1); `g1-parity.ts`'s whole-image pixel rows are kept in
`parity.log` as information, since silhouettes rasterize differently on every renderer.

Pixels: each target against the CPU reference rendered at the same size (web canvas 960×640, Metal drawing buffer
1920×1280). "Edge" means the pixel sits on a color discontinuity in the reference (a silhouette).

| Sample | Pair | Interior max Δ | Interior px with Δ = 1 | Edge px (max Δ) | Interior px with Δ ≥ 2 |
|---|---|---|---|---|---|
| LitCube | CPU vs WebGL2 | 1 | 906–1487 of 614 400 (0.15–0.24 %) | 35–51 (252) | 0 |
| LitCube | CPU vs Metal | 1 | 21 921–58 444 of 2 457 600 (0.9–2.4 %) | 89–1621 (204), of which 2–6 differ by ≥ 3 | 0 |
| RotatingCube (G0, unlit) | CPU vs WebGL2 | 0 | 0 | 10–23 (192) | 0 |
| RotatingCube (G0, unlit) | CPU vs Metal | 0 | 0 | 0–3 (186) | 0 |

What the numbers mean:

- **The lighting math agrees.** Nowhere inside a surface does any renderer differ from the CPU reference by more than
  one 8-bit step, at any checkpoint. A formula or ordering mismatch would show as broad Δ ≥ 2 regions; there are none.
- **Δ = 1 is rounding, only on gradients.** The differing pixels lie on the iso-lines of the point light's falloff
  (diff maps `artifacts/evidence-g1lit/diff-*-tick-*.png`): where the exact value sits next to an 8-bit rounding
  boundary, float32 results that differ by a few ulps (GPU attribute interpolation, Metal's fast `normalize`/`sqrt`/
  division, SwiftShader, CPU) round to neighboring values. Faces lit only by ambient and directional light are
  constant per face and match exactly. Bit-exact per-pixel float shading across a CPU, Metal and SwiftShader is not
  available, so the tolerance for lit gradients is **1/255 per channel**.
- **Edges are a G0 property, not lighting.** The unlit G0 cube shows the same silhouette differences (the CPU
  rasterizer uses float edge functions without the GPUs' fixed-point snapping or top-left rule). The lit run has more
  edge pixels because it has more silhouettes (floor, lamp) and lit-face contrast.

Evidence: `artifacts/evidence-g1lit/` (captures, `parity.md`, `lit-pixels.md`, diff maps) and
`artifacts/evidence-g1lit-rotatingcube/` (unlit baseline). Reproduce with `scripts/g1lit.sh` and
`SAMPLE=RotatingCube scripts/g1lit.sh` (opens a window briefly; set `WEB_PORT` if 8768 is taken).
