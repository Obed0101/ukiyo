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

With several cameras ([rendering-passes.md](rendering-passes.md)) lighting is per pass: each pass packet carries its
own lighting section, and a lit material in a pass without one is `InvalidPacket` even when another pass has lights.

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

**Geometric edges · 2026-10-16 (UKIYO-45)** · same machine and versions, on `dev` with multi-camera passes merged.
The color rule ("edge" = some neighbor in the reference differs by more than 24 levels) let low-contrast silhouettes
through on `samples/ImportedMesh` (Suzanne's ear rim and ear-to-head seam, the brow over the eye sockets, the chin):
on the same captures it counted pixels with Δ = 13, 39, 15 and 14 against WebGL2 as interior, so `SAMPLE=ImportedMesh
scripts/g1lit.sh` exited 1. Edges now come from geometry:

- **Edge mask from the CPU reference.** `SoftwareRenderer.Surfaces` (a debug `SurfaceMap`, null by default; it never
  changes a frame, and while null the renderer only pays a null check per covered pixel) records which draw and
  triangle covers each pixel, of the frame and of each pass. Headless `--edge-masks <dir>` (needs `--capture`) writes
  `edges-tick-NNNNNN.png` and `edges-tick-NNNNNN-pass-K.png` next to the captures: black = background, gray = interior,
  white = edge. No packet, protocol, fixture or capture changes; `g1lit.sh` passes the flag to both CPU runs.
- **What counts as one surface.** A pixel is an edge when any two pixels of its 3×3 window lie on different surfaces:
  the clear color and a draw, two draws, a sprite, two passes, or two triangles of one draw that are not neighbors on
  one smooth sheet. Vertices are welded by position, color and normal. Two triangles that share an edge are one
  surface (attributes interpolate continuously across it). Two that share only a vertex are one surface only when that
  vertex's fan is closed and entirely front-facing in this frame; otherwise the vertex is on a contour, an open border
  or a flat-shaded crease. So the inside of smooth Suzanne stays interior across her ~1000 triangles, while folds seen
  edge-on, the separate eyeballs, the pedestal's flat-shaded creases and every silhouette are edges. All pairs in the
  window are checked because this continuity is not transitive: a sliver across a fold touches both sides.
- **Interior tolerance follows the surface's gradient.** With geometric edges, 4–18 interior pixels per frame still
  differed by 2–3 on WebGL2 (SwiftShader), never on Metal. All of them sit on steep, continuous shading (curved
  surfaces turning away from the view: the reference changes by 33–134 levels per pixel there), where a sample
  position off by a fraction of a pixel moves the value by more than one step. The gate is now Δ ≤ 1 + floor(G/16),
  G = the largest difference between the pixel and its 8 neighbors in the reference (all on the same surface for an
  interior pixel), 1/16 px being one subpixel step at the GL ES 3.0 minimum of `SUBPIXEL_BITS` = 4 that WebGL2
  inherits (SwiftShader's actual subpixel precision was not checked). Where the reference changes by less than 16
  levels per pixel (every LitCube and Minimap interior), the gate is exactly Δ ≤ 1 as before. `lit-pixels.md` reports
  both the interior pixels with Δ > 1 that the gradient allows and those over tolerance.
- **Checks.** `dotnet build Ukiyo.slnx -c Release` 0 warnings; `dotnet test tests/Ukiyo.Tests` 245/245 (9 in
  `SurfaceMapTests`); adapter `bun test` 37/37; `tools/mcp` 89/89. Gates re-run after merging dev at `8a53c21` (game
  layout): same numbers. `scripts/g1lit.sh` exit 0 for LitCube, `SAMPLE=RotatingCube` and
  `SAMPLE=ImportedMesh`; `scripts/g1passes.sh` exit 0 (masks per pass; its numbers in
  [rendering-passes.md](rendering-passes.md) reproduce exactly). Several runs first failed for the environment only
  (desktop window never became visible, or headless Chrome's virtual-time budget ran out at load average ≈ 20–36)
  and passed on retry. **Negative control:** with the square dropped from the point-light attenuation in
  `web/three-adapter/lit-material.js` (`attenuation = t`), ImportedMesh failed on WebGL2 with 17 601–46 952 interior
  pixels over tolerance per frame (max interior Δ 14–24) while Metal stayed at Δ ≤ 1; after the revert the file had no
  diff and the rerun reproduced the passing numbers exactly.

Pixels: each target against the CPU reference rendered at the same size (web canvas 960×640, Metal drawing buffer
1920×1280). "Edge" means the CPU reference's geometric edge mask marks the pixel (above); the LitCube and RotatingCube
rows are the same under the old color rule and the mask.

| Sample | Pair | Interior max Δ | Interior px with Δ = 1 | Edge px (max Δ) | Interior px with Δ ≥ 2 |
|---|---|---|---|---|---|
| LitCube | CPU vs WebGL2 | 1 | 905–1487 of 614 400 (0.15–0.24 %) | 35–52 (252) | 0 |
| LitCube | CPU vs Metal | 1 | 21 919–58 442 of 2 457 600 (0.9–2.4 %) | 89–1623 (204) | 0 |
| RotatingCube (G0, unlit) | CPU vs WebGL2 | 0 | 0 | 10–23 (192) | 0 |
| RotatingCube (G0, unlit) | CPU vs Metal | 0 | 0 | 0–3 (186) | 0 |
| ImportedMesh | CPU vs WebGL2 | 2–3 | 2014–2631 of 614 400 (0.33–0.43 %) | 387–839 (226); mask 5820–8015 px (0.9–1.3 %) | 4–18, all on steep gradients (within 1 + floor(G/16)) |
| ImportedMesh | CPU vs Metal | 1 | 8334–12 021 of 2 457 600 (0.34–0.49 %) | 507–1149 (221); mask 11 530–15 968 px | 0 |

What the numbers mean:

- **The lighting math agrees.** Nowhere inside a surface does any renderer differ from the CPU reference by more than
  one 8-bit step, at any checkpoint. A formula or ordering mismatch would show as broad Δ ≥ 2 regions; there are none.
- **Δ = 1 is rounding, only on gradients.** The differing pixels lie on the iso-lines of the point light's falloff
  (diff maps `artifacts/evidence-g1lit/diff-*-tick-*.png`): where the exact value sits next to an 8-bit rounding
  boundary, float32 results that differ by a few ulps (GPU attribute interpolation, Metal's fast `normalize`/`sqrt`/
  division, SwiftShader, CPU) round to neighboring values. Faces lit only by ambient and directional light are
  constant per face and match exactly. Bit-exact per-pixel float shading across a CPU, Metal and SwiftShader is not
  available, so the tolerance for lit gradients is **1/255 per channel**, plus one step per 16 levels of the
  reference's local gradient on steep curved shading (ImportedMesh on WebGL2; see above).
- **Edges are a G0 property, not lighting.** The unlit G0 cube shows the same silhouette differences (the CPU
  rasterizer uses float edge functions without the GPUs' fixed-point snapping or top-left rule). The lit run has more
  edge pixels because it has more silhouettes (floor, lamp) and lit-face contrast.

### Real-GPU browser leg · 2026-10-16 (UKIYO-41)

Apple M4 Pro, macOS 27.0 (26A428), Google Chrome 154.0.8037.95, three 0.186.0. WebGL device reported by the page:
`ANGLE (Apple, ANGLE Metal Renderer: Apple M4 Pro, Unspecified Version)`.

Every WebGL2 row above ran on SwiftShader: `scripts/web-evidence.sh` forces it with `--use-angle=swiftshader`, so
"WebGL2 parity" had not been measured on a GPU. `WEB_GPU=1` adds an opt-in target `web-gpu` to `g0.sh`, `g1.sh`,
`g1lit.sh` and `g1passes.sh`: the same published build, run again in headless Chrome with `--use-angle=metal` and
compared with the same scripts, tolerances and edge masks as `web`. Default off, so CI (no GPU) and existing runs keep
their single SwiftShader leg.

- **Proof of the GPU.** The page's evidence JSON now carries `backend` and `device`: the string the adapter reads from
  `UNMASKED_RENDERER_WEBGL` (`PlatformBootstrap.PublishEvidence`). The leg fails unless the device contains
  `Metal Renderer: Apple` and not `SwiftShader`. It is checked in `web-evidence.sh` (`WEB_EVIDENCE_GL=metal`) and again
  in `g1-parity.ts`/`g0-parity.ts` (row "web-gpu ran on the Apple GPU"). The default leg reports
  `ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (LLVM 10.0.0) (0x0000C0DE)), SwiftShader driver)`. Negative
  control: a SwiftShader dump fed in as `web-gpu` fails that row, and a missing one fails "web-gpu evidence present"
  plus every `web-gpu` row in `lit-pixel-report.ts`.
- **How it runs (decided by experiment).** `--headless=new` with `--use-angle=metal` (and with no ANGLE flag at all)
  reports the Apple GPU, so no visible window is needed. The leg waits for the evidence in real time
  (`scripts/web-evidence-realtime.ts`: DevTools port, poll the title, read the DOM) instead of `--dump-dom` with
  virtual time. Under virtual time, the GPU leg on the AOT RotatingCube build dumped the page at "loading .NET
  WebAssembly…" in 4 of 5 runs, against 5 of 5 good runs in real time. Real-time and virtual-time captures of LitCube
  gave identical numbers. Running two legs back to back on one port also exposed a race in `web-evidence.sh`: the
  first server was killed without waiting, and 1 of 3 following runs lost its evidence. The script now waits for the
  server to exit (6 of 6 good runs after the fix).

Same table as above, real-GPU WebGL2 against the CPU reference at 960×640, ticks 0/60/180/600 (snapshots identical,
max Δ = 0, on every sample):

| Sample | Pair | Interior max Δ | Interior px with Δ = 1 | Edge px (max Δ) | Interior px with Δ ≥ 2 |
|---|---|---|---|---|---|
| LitCube | CPU vs WebGL2 (Apple GPU) | 1 | 5395–14 099 of 614 400 (0.88–2.29 %) | 52–807 (226) | 0 |
| RotatingCube (G0, unlit) | CPU vs WebGL2 (Apple GPU) | 0 | 0 | 0–2 (186) | 0 |
| ImportedMesh | CPU vs WebGL2 (Apple GPU) | 1 | 1951–2829 (0.32–0.46 %) | 245–565 (211) | 0 |
| Minimap, whole frame | CPU vs WebGL2 (Apple GPU) | 1 | 100–5836 | 45–389 (144) | 0 |
| Minimap, pass 0 (960×640) | CPU vs WebGL2 (Apple GPU) | 1 | 0–5736 | 1–438 (144) | 0 |
| Minimap, pass 1 (217×217) | CPU vs WebGL2 (Apple GPU) | 1 | 100 at every tick | 44 (1) | 0 |

- **Every gate passes on the real GPU**: `WEB_GPU=1` `g1lit.sh` (LitCube, `SAMPLE=RotatingCube`,
  `SAMPLE=ImportedMesh`) and `g1passes.sh` exit 0, with no tolerance changed.
- **The Apple GPU behaves like the desktop Metal leg, not like SwiftShader.** LitCube's interior Δ = 1 share
  (0.88–2.29 %) matches CPU vs Metal (0.9–2.4 %) and is 3.6–11× SwiftShader's at the same tick. That is float rounding on the Apple GPU,
  whichever API drives it. ImportedMesh stays at Δ ≤ 1 everywhere. The gradient allowance (1 + floor(G/16)) that
  SwiftShader needed on 4–18 steep pixels per frame is not used on the GPU. Unlit cube edges differ on 0–2 px
  (SwiftShader 10–23, desktop Metal 0–3).
- **Minimap pass 1:** the 144 differing pixels per tick (100 interior, 44 on the mask's edge) are one 12×12 flat face,
  the green cube's top, with red 68 vs 67. The desktop Metal leg shows the same kind of one-step difference in that
  pass (529 px).
- `g1-parity.ts`'s whole-image pixel rows fail for `web-gpu` on 1–5 silhouette pixels per frame (2 of 16 frames pass),
  as they do for `web` and `desktop`. `g1lit.sh` keeps those rows informational, as before.

Evidence: `artifacts/evidence-g1lit/` (captures, `parity.md`, `lit-pixels.md`, diff maps, edge masks in
`headless-edges/` and `headless-desktop-size-edges/`), `artifacts/evidence-g1lit-rotatingcube/` (unlit baseline) and
`artifacts/evidence-g1lit-importedmesh/`. Reproduce with `scripts/g1lit.sh`, `SAMPLE=RotatingCube scripts/g1lit.sh`
and `SAMPLE=ImportedMesh scripts/g1lit.sh` (opens a window briefly; set `WEB_PORT` if 8768 is taken). Prefix any of
them with `WEB_GPU=1` for the real-GPU leg (`web-gpu-dom.html` and `web-gpu/` in the same evidence folder; needs a Mac
with an Apple GPU and Chrome).
