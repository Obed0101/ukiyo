# Decision · WebGPU in the browser path

**Decision: stay on WebGL2 (`WebGLRenderer`) as the only shipped browser renderer. No dual path for now.**
Spike UKIYO-15, measured 2026-10-16.

## Context

The browser host is .NET WebAssembly driving `web/three-adapter/ukiyo-three.js` (three 0.186.0, `WebGLRenderer`,
WebGL2). G0 left two questions open: whether three's `WebGPURenderer` can draw the same frames, and whether the
browser can be captured on a real GPU, since the automated evidence ran on SwiftShader. G1 already fixed two
renderer differences that also apply to WebGPU: blending happens in linear space through an sRGB render target,
and there is no multisampling.

## What was tried

- **Adapter on `WebGPURenderer`.** `web/three-adapter/spike/ukiyo-three-webgpu.js` is a copy of the shipped adapter
  that imports `three/webgpu`. Every difference is marked `WEBGPU:`. No shipped file changed. The spike runner
  remaps the shared host's `./ukiyo-three.js` in the page's import map. Both paths load through
  `spike/timed-adapter.js` and get the same timers.
- **Same capture pipeline.** The C# side, the evidence mode (`PublishEvidence`) and the capture are unchanged:
  `canvas.toDataURL` runs in the same JS task as the render.
- **Runner.** `scripts/spike-webgpu.ts` serves a published `wwwroot` with a local Bun server and starts Chrome on a
  fresh profile with the given flags. The page posts its evidence back to the server, and the runner writes the PNGs
  and snapshots. The same script compares captures (`compare`). `spike/probe.html` reports `navigator.gpu`, the
  adapter, the WebGL2 device and a WebGPU canvas readback of known values.
- **Samples and references.** LanternRun with `samples/LanternRun/parity/input.json` at checkpoints 0–300 (sprites:
  straight alpha blended in linear space, nearest filtering, translucent pause panel). RotatingCube at 0, 60, 180
  and 600 (meshes, depth). Every run uses 960×640 (`fixed=1`). CPU references come from the headless
  `SoftwareRenderer` at 960×640. `scripts/g1.sh` was run unchanged for the Metal (wgpu) reference and to check the
  default path.

## Measured results

Machine: Apple M4 Pro, macOS 27.0 (26A428). Google Chrome 154.0.8037.95 (installed app, no Playwright). .NET SDK
10.0.401, Bun 1.3.13, three 0.186.0. Date: 2026-10-16. Every Chrome run adds `--force-device-scale-factor=1
--window-size=1200,800`, a fresh `--user-data-dir`, and `--headless=new` unless marked headed.

### What Chrome offers per launch (`spike/probe.html`)

| Launch | Extra flags | `navigator.gpu` | WebGPU adapter (`adapter.info`) | WebGL2 device | WebGPU canvas `toDataURL`, expected 64,128,192 |
|---|---|---|---|---|---|
| headless, current evidence flags | `--use-angle=swiftshader --enable-unsafe-swiftshader` | yes | **none** (`requestAdapter` → null) | SwiftShader (Vulkan) | n/a |
| headless, no flags | none | yes | `apple` / `metal-3`, not fallback, 23 features, max 2D texture 16384 | ANGLE Metal, Apple M4 Pro | 64,128,192,255 (same task and next task) |
| headless | `--enable-unsafe-webgpu` (± `--use-angle=metal --enable-gpu`) | yes | `apple` / `metal-3`, 25 features | ANGLE Metal, Apple M4 Pro | exact |
| headless, SwiftShader WebGPU | evidence flags + `--enable-unsafe-webgpu --use-webgpu-adapter=swiftshader` | yes | `google` / `swiftshader`, **fallback adapter**, 21 features, max 2D texture 8192 | SwiftShader | exact |
| headed | none | yes | `apple` / `metal-3`, 23 features | ANGLE Metal, Apple M4 Pro | exact |

Chrome hides `adapter.info.device` and `description` (both empty) in every mode.

The finding behind G0 item 2: on this Mac, headless Chrome 154 already runs on the real GPU (Metal) for both WebGL2
and WebGPU. The evidence used SwiftShader only because `scripts/web-evidence.sh` forces it with flags. Real-GPU
capture through `toDataURL` works headless and headed with no extra flag. The WebGPU path also works under
`--virtual-time-budget=600000`, as the evidence script runs it: frames identical to the real-time run.

### Pixel parity (max RGB channel Δ per checkpoint, 960×640)

| Comparison | LanternRun, 7 checkpoints | RotatingCube, 4 checkpoints |
|---|---|---|
| **WebGPU vs WebGL2**, same launch, Metal headless | **0 at every checkpoint** | **0 at every checkpoint** |
| WebGPU vs WebGL2, Metal headed | 0 at every checkpoint | not run |
| WebGPU vs WebGL2, SwiftShader | 0 at every checkpoint | 0, except tick 0: 152 px on one row (y=231, x 404–555), the cube's top edge |
| WebGPU headless vs WebGPU headed (Metal) | 0 | 0 |
| WebGPU (Metal) vs CPU | 0, except ticks 240/300: **1/255** on 79,362 / 79,262 px inside the pause panel (273,188–686,451); 0 px over 1 | edge pixels only: 2/1/2/0 px (max 186/255) |
| WebGL2 (Metal) vs CPU | identical to the row above | identical to the row above |
| WebGPU (SwiftShader) vs CPU | 0 at every checkpoint | edge pixels only: 23/15/162/17 px |
| WebGL2 (SwiftShader) vs CPU (current gate) | 0 at every checkpoint | edge pixels only: 23/15/10/17 px |
| `WebGPURenderer` with `forceWebGL` (its own WebGL2 backend) vs WebGL2 / CPU | same as Metal WebGL2: 0, and 1/255 in the pause panel | 0 vs WebGL2 |
| `scripts/g1.sh` (default path, unchanged): Metal 1920×1280 vs CPU | 32/32 checks pass, 1/255 at ticks 240/300 | n/a |

- Every WebGPU run's snapshots match the CPU run exactly, apart from the renderer name (28 files compared). The
  pause check (two frames 500 ms apart at the same tick) gave identical frames in every run.
- The 1/255 in the translucent pause panel is GPU blend rounding. It shows on Apple GPUs whatever the API: WebGL2,
  WebGPU and wgpu-Metal desktop all have it. `g1-parity.ts` already allows one step (`PIXEL_TOLERANCE = 1`).
- Cube edge pixels differ by rasterization rules (tie-breaks on edges through pixel centers). G0 compares the cube by
  rotation, not pixels, and the renderer skill says to compare at feature centers.
- The WebGPU capture was not compared pixel for pixel with the wgpu-Metal desktop capture: the evidence mode fixes
  the browser at 960×640 and the desktop window draws 1920×1280. The comparison is transitive through the CPU
  reference, which both match within 1/255.

### Rendering differences found, and their fixes

- **Render targets flip vertically.** In `WebGPURenderer`, render-target textures have v=0 at the top row, on both
  its WebGPU and WebGL2 backends. The first WebGPU run drew every frame upside down (max Δ 225/255 against the
  CPU; kept as `*-v1-flipped`). The blit quad now flips v. This was the only change needed for pixel parity.
- **Opaque canvas.** `WebGPURenderer` defaults to `alpha: true`, which gives a premultiplied canvas. The spike sets
  `alpha: false` to match `WebGLRenderer`'s opaque canvas.
- sRGB target, linear blending, nearest filtering and no MSAA carried over unchanged. The sRGB RGBA8 `RenderTarget`
  becomes `rgba8unorm-srgb`. three requests a `featureLevel: "compatibility"` adapter, but on Metal it got core
  features (`compatibilityMode: false`).

### Costs

| Item | WebGL2 (shipped) | WebGPU spike |
|---|---|---|
| three files loaded | `three.core.js` + `three.module.js` | `three.core.js` + `three.webgpu.js` |
| Raw / gzip -9 / brotli -q11 (bytes) | 2,120,885 / 417,318 / 313,372 | 3,742,936 / 730,381 / 532,262 (**+76 % / +75 % / +70 %**, +219 KB brotli) |
| Dual path | — | ships both builds (+2.28 MB raw files); each session loads one |
| Renderer creation, Metal headless, median of 5 | 10.8 ms (`new WebGLRenderer`, sync) | 9.6 ms (`await renderer.init()`, range 3.6–13.4) |
| Module load incl. three fetch, median of 5 | 21.4 ms (17.6–49.3) | 43.8 ms (38.7–72.7), includes the init above |
| First render, median of 5 | 17.5 ms | 19.0 ms |
| Mean render / capture per frame, median of 5 | 2.6 / 5.2 ms | 3.2 / 3.0 ms |
| Load → evidence published, median of 5 (includes 2 s of fixed waits) | 2428 ms | 2417 ms |
| SwiftShader (CI without GPU), 3 runs | load → evidence 4631–4753 ms; first render 38–73 ms; capture 31–39 ms | device init **765–2843 ms**; load → evidence 4404–5697 ms; capture 51–132 ms |
| `forceWebGL` fallback, Metal, 3 runs | — | first render 21–24 ms, render 3.1–3.5 ms. One earlier cold run took 541 ms / 61 ms per frame; not reproduced and not investigated |

Startup on a real GPU is the same within noise. On SwiftShader, WebGPU costs 0.8–2.8 s of device setup per run.

**Code that would change for a port:**

- **Adapter.** About 25 changed lines of 240: import, async creation, `RenderTarget`, the v flip, backend and device
  reporting. Resource tables, packet application, sprite batching, capture and dispose did not change. A dual path
  could keep one file with a renderer factory instead of a copy.
- **Async initialization.** `ThreeJsRenderer.InitializeAsync` calls a synchronous `JSImport`, but WebGPU device
  creation is async. The spike works around this with a top-level `await` that holds back the host module. A real
  port changes `JsInitialize` to return `Task<string>`; `InitializeAsync` is already async.
- **Build and evidence.** `Directory.Build.targets` copies `three.webgpu.js`. `g0-parity.ts` and `g1-parity.ts`
  accept a `WebGPU` backend (today they require `WebGL*`). `web-evidence.sh` drops the SwiftShader flags or adds
  `--enable-unsafe-webgpu --use-webgpu-adapter=swiftshader`.
- **Honest capabilities.** `WebGPURenderer` silently falls back to its WebGL2 backend without `navigator.gpu`, so the
  adapter must report which backend ran (the spike does).

**Browser support.** Only Chrome 154 on macOS/Metal was measured. Safari, Firefox, Linux, Windows and mobile were not
tested. Without WebGPU, `WebGPURenderer` falls back to WebGL2. That fallback matched `WebGLRenderer` to the pixel on
this machine, but it loads the larger `three.webgpu.js` build.

## Decision and cost

- **Stay on WebGL2.** On today's feature set (unlit meshes, sprites, UI), WebGPU produced the same pixels as WebGL2 in
  every configuration measured. It adds no capability the protocol uses. It adds +219 KB brotli of three, an async
  initialization change and a second evidence leg. On SwiftShader CI it adds 0.8–2.8 s of device setup per run.
  Cost of staying: nothing.
- **Dual: not now.** It would double browser evidence runs and ship both three builds, with nothing to show for it.
- **Migrate: cheap when needed.** About 25 adapter lines, one `JSImport` signature, build-copy and parity-script
  updates. Parity work is already done: the v flip and the opaque canvas.
- **Real-GPU browser capture** needs no WebGPU: headless Chrome on this Mac uses Metal for WebGL2 once the SwiftShader
  flags are dropped. The WebGL2 frames then match the CPU within the same 1/255 as the desktop. Adding that leg to
  `g1.sh` is a separate, small change. It was not made here. (Made later in UKIYO-41: `WEB_GPU=1` adds the leg
  `web-gpu` to `g0.sh`, `g1.sh`, `g1lit.sh` and `g1passes.sh`; results in
  [rendering-lighting.md](../rendering-lighting.md#real-gpu-browser-leg--2026-10-16-ukiyo-41).)

## What would change the decision

- A feature that needs WebGPU: compute shaders, storage buffers, GPU particles or skinning, or a lit profile built on
  three's node or TSL materials that `WebGLRenderer` cannot run.
- A measured WebGL2 performance ceiling on a real ukiyo scene. The current samples render in about 3 ms either way.
- three deprecating `WebGLRenderer`, or new features landing only in `WebGPURenderer`.
- Target browsers or devices where WebGL2 is worse than WebGPU. Measure them with
  `scripts/spike-webgpu.ts probe` first.

## Reproduce

```sh
dotnet publish samples/LanternRun/Browser -c Release -o artifacts/lantern-web && cp samples/LanternRun/parity/input.json artifacts/lantern-web/wwwroot/
dotnet run --project samples/LanternRun/Headless -c Release -- --checkpoints 0,30,60,120,180,240,300 --input samples/LanternRun/parity/input.json --capture artifacts/spike-webgpu/lantern-cpu --size 960x640
bun scripts/spike-webgpu.ts probe --name probe-h-default                       # add --headed or --flags "…"
bun scripts/spike-webgpu.ts run --name lantern-webgpu-headless-gpu --root artifacts/lantern-web/wwwroot --renderer webgpu \
  --query "checkpoints=0,30,60,120,180,240,300&fixed=1&input=input.json"     # --renderer webgl for the baseline
bun scripts/spike-webgpu.ts compare artifacts/spike-webgpu/lantern-cpu artifacts/spike-webgpu/lantern-webgpu-headless-gpu --out artifacts/spike-webgpu/diff/x
```

Raw reports, captures and diff images from this spike are in `artifacts/spike-webgpu/` (not versioned).
