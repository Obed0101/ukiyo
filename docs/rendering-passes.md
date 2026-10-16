# Multi-camera passes

A frame can be drawn by up to four cameras: a main view plus a minimap, split screen, a picture-in-picture. Every
renderer implements it the same way (wgpu/Metal, three.js/WebGL2, CPU, Null); a game that never adds a pass sends
exactly the bytes it always did. Wire format: [protocol.md](protocol.md) (frame flag `0x4`, 32-byte pass section).

## The model

**One packet per pass.** A multi-camera frame of N passes is N consecutive `RenderPacket`s, each with a `FramePass`
(`Id`, `Count`, `PrimaryId`, `Rect`). Each pass packet is a complete single-camera description of its view: clear color,
camera, meshes, 2D camera and sprites, lighting. Validation stays per packet (the same `RenderValidation.Validate` as
always plus the pass checks), and per-pass capture reuses the single-camera capture path.

| Topic | Rule |
|---|---|
| Pass ids | `0 … Count − 1`, `Count` in `1 … 4` (`RenderValidation.MaxPasses`). Count 1 with a section is a single camera confined to a rect (letterbox). |
| Order | Packets arrive in id order with consecutive sequence numbers and the same tick, count, primary and drawing buffer (`PassSequencer`, mirrored in `protocol.js`). Anything else is `InvalidPacket`. The frame is complete, and presented, when the last pass arrives. |
| Rect | Drawing-buffer pixels, origin top-left, +Y down (the space of screen sprites). Width and height ≥ 1, fully inside the drawing buffer, else `OutOfRange`. With an empty drawing buffer (minimized window) nothing is drawn and the rect is not checked. Rects may overlap. |
| Inside a pass | Exactly a single-camera frame whose drawing buffer is the rect's size: perspective aspect = rect width / height, world sprites through the pass's own `Camera2D` (`ViewHeight` fills the rect's height), screen sprites in pass-local pixels (origin at the rect's top-left), the pass's own lighting. |
| Clear | Before pass 0 the whole drawing buffer is cleared to pass 0's clear color, so pixels no rect covers have it. Each pass clears its own rect (color to its clear color, depth to 1). Clear alpha is ignored; output is opaque. |
| Draw order | Pass 0, then 1, …; inside a pass, the single-camera order (3D, world sprites, screen sprites). A later pass replaces earlier pixels inside its rect; passes never blend with each other and never share depth. |
| Lighting | Per pass. A lit material in pass k needs pass k's lighting section; pass 0's lights do not carry over (`InvalidPacket`, as for a single camera). |
| Primary | `PrimaryId` (< count, the same in every pass) marks the pass tools show as the game view: inspector previews, dev bridge `capture` with `"pass":"primary"`, `GameRuntime.PrimaryPass`. It changes nothing in drawing. A single-camera frame's primary is its only pass, 0. |
| Profiles | Independent of `RenderProfile`; G0Unlit and G1Lit games both use passes. Renderers declare `RenderCapabilities.SupportsPasses`; the runtime refuses passes on a renderer without it (`UnsupportedFeature`) instead of letting every pass draw over the whole buffer. |

### Captures

`IRenderCapture.CaptureAsync(new CaptureRequest(tick) { Pass = … })`:

- `Pass = null`: the whole drawing buffer as presented, every pass composed. For single-camera games this is unchanged.
- `Pass = k`: pass k alone, at its rect size, including the parts a later pass covers in the composed frame. Its pixels
  are the ones a single-camera frame of that size with the same packet produces (`PassTests.A_pass_image_is_the_frame…`
  checks this byte for byte on the CPU).
- A single-camera frame is its own pass 0 (`Pass = 0` returns the whole frame); a pass id ≥ count is `OutOfRange`;
  capturing while a frame's passes are still arriving is `InvalidPacket`.

Hosts write per-pass files next to the whole frame only for multi-camera frames: headless `headless-tick-NNNNNN-pass-K.png`
(and a `capture-pass K primary=true|false <path>` line, which tools reading `capture <path>` lines ignore), desktop
`desktop-tick-NNNNNN-pass-K.png`, browser evidence a `"passes"` array and `"primaryPass"` per capture.

## Game API

```csharp
public void Extract(FrameBuilder frame)
{
    frame.Camera = chase;                       // the builder you get is pass 0
    frame.Lighting = sun;
    DrawWorld(frame);
    frame.PrimaryPass = 0;                      // the default

    var map = frame.AddPass(new PassRect(x, y, side, side));   // drawing-buffer pixels, from frame.DrawingBuffer
    map.Camera = topDown;                       // nothing is inherited: camera, clear color, lighting, draws
    map.ClearColor = dark;
    map.Lighting = overhead;
    DrawWorld(map);
    map.DrawScreenSprite(frameBar, Vector2.Zero, new Vector2(map.Viewport.Width, 3));   // pass-local pixels
}
```

`FrameBuilder.DrawingBuffer` is the whole buffer; `Viewport` is the size the builder draws into (its rect, or the whole
buffer), so UI laid out against `Viewport` lands inside the pass. `Rect` on the first builder confines pass 0 too.
`AddPass` beyond four passes is `OutOfRange`; `AddPass` on a pass builder is `InvalidOperationException`.
`GameRuntime.RenderFrame` returns the primary pass's packet; `LastFrame`, `PassCount`, `PrimaryPass` describe the frame;
`CaptureAsync(pass)` captures one pass. Sample: `samples/Minimap`.

## How each renderer does it

| Renderer | Presented frame | `Pass = k` capture |
|---|---|---|
| CPU (reference) | each pass rasterized into its own image of the rect's size with the single-camera code, then copied opaquely into the frame at its rect | that pass image |
| wgpu/Metal | collects the passes, then one render pass per camera pass straight into the surface with the rect as viewport and scissor; pass 0 clears the whole surface, later passes load it and clear their rect with a quad through the sprite pipeline (white texel × clear color, alpha 1); depth cleared per pass; one present | the pass re-rendered alone into an offscreen target of the rect's size |
| three.js/WebGL2 | each pass drawn into its own sRGB render target of the rect's size with the single-camera code, then blitted 1:1 into the frame target at its rect; the last pass blits the frame to the canvas | `readRenderTargetPixels` of that pass's target |
| Null | validates (including order and the rect against its extent) and counts: `FramesRendered` per complete frame, `PassesRendered` per pass packet | none |

Why the wgpu path draws in place rather than rendering to targets and copying: Metal surfaces in the pinned wgpu-native
29.0.1.1 report usages `0x11` (render attachment, copy source), so a surface texture cannot be a copy destination;
blitting a pass texture back through a shader adds an sRGB decode/encode round trip. Viewport offsets are whole pixels,
so a pass drawn at its rect covers the same pixels as the same pass drawn alone.

## Recipe checklist

- [x] Pass ids, count ≤ 4, primary and rect in every pass packet: frame flag `0x4`, 32-byte section, limits in protocol.md
- [x] Single-camera frames unchanged: no flag, no section; G0 and G1Lit golden fixtures unchanged
- [x] CPU renderer implements passes (reference); Null validates; wgpu and three.js draw them; `SupportsPasses` on all four
- [x] Per-pass captures on every capturing renderer and host
- [x] Golden fixtures shared by C# and JS: `web/three-adapter/fixtures/minimap-resources.bin`, `minimap-frame-tick60-pass0.bin`, `minimap-frame-tick60-pass1.bin`
- [x] Negative tests both sides: count 0 or 5, id or primary ≥ count, empty or out-of-buffer or negative rect → `OutOfRange`;
      unknown frame flag, passes out of order, skipped, repeated, interrupted or inconsistent, capture of an incomplete
      frame → `InvalidPacket`; passes on a renderer without `SupportsPasses` → `UnsupportedFeature`; lit material in a
      pass without its own lighting → `InvalidPacket`
- [x] Two-pass sample `samples/Minimap` captured per pass on CPU, Metal and WebGL2 (`scripts/g1passes.sh`)

## Results · 2026-10-16 · Apple M4 Pro, macOS 27.0, .NET SDK 10.0.401, Bun 1.3.13, Chrome 154.0.8037.95, wgpu-native 29.0.1.1, three 0.186.0

`scripts/g1passes.sh` (Minimap at ticks 0/60/180/600, web canvas 960×640 with a 217×217 minimap, Metal drawing buffer
1920×1280 with a 435×435 minimap): exit 0, 8 per-pass PNGs on every target (headless at both sizes, Metal, WebGL2).
Snapshots and source hashes identical on CPU, Metal and WebGL2 (max Δ = 0). Pixels against the CPU reference at the
same size; "edge" means a color discontinuity in the reference (a silhouette):

| Image | Pair | Interior max Δ | Interior px with Δ = 1 | Edge px (max Δ) |
|---|---|---|---|---|
| whole frame | CPU vs WebGL2 | 0 | 0 | 18–25 (129–144) |
| whole frame | CPU vs Metal | 1 | 529–24 340 | 98–790 (70–144) |
| pass 0 (chase camera) | CPU vs WebGL2 | 0 | 0 | 24–32 (144–151) |
| pass 0 (chase camera) | CPU vs Metal | 1 | 0–23 811 | 2–877 (79–144) |
| pass 1 (minimap) | CPU vs WebGL2 | 0 | 0 | 0–1 (≤ 88) |
| pass 1 (minimap) | CPU vs Metal | 1 | 529 at every tick | 96 (1) |

- **Composition is exact.** The minimap frame, background and position in the whole-frame captures match the CPU
  reference; the only whole-frame differences are the ones each pass has alone (silhouettes, and lit rounding on
  Metal). WebGL2's blit of a pass target into the frame is a 1:1 copy (whole-frame interiors Δ = 0).
- **Metal Δ = 1 is the G1Lit rounding** documented in [rendering-lighting.md](rendering-lighting.md), here on faces
  whose constant lit value sits next to an 8-bit rounding boundary (both cameras use directional light only): in pass 1
  exactly the 25×25 top face of the green pillar (red 68 on CPU, 67 on Metal) at every tick; in pass 0 the rover's
  shaded side at tick 60 (24 244 px, 68,70,77 → 67,70,77) and a green pillar face at tick 600 (19 437 px, green 116 →
  117). No interior pixel differs by 2 or more.
- The minimap's clear color was chosen to encode to whole sRGB steps (33/39/51): a first color 0.04 steps from a
  rounding boundary came out 1/255 apart on Metal across the whole background.

Single-camera games are unchanged: `scripts/g1lit.sh` (LitCube) reproduces the numbers in rendering-lighting.md
exactly, and `scripts/g1.sh` passes all its checks with RotatingCube at Δ ≤ 1. Evidence: `artifacts/evidence-g1lit-minimap/`
(captures, `parity.md`, `lit-pixels.md`).
