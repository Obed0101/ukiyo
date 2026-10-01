---
name: ukiyo-renderer-dev
description: Use when implementing or changing a ukiyo renderer or a rendering feature inside the engine — IRenderer/IRenderCapture, resource handles, RenderValidation, the binary protocol (PacketCodec.cs ↔ protocol.js), WGSL in WgpuRenderer, the three.js adapter, the CPU SoftwareRenderer — and keeping all of them pixel-consistent. Not for drawing in a game (that is ukiyo-rendering / ukiyo-sprites).
---

# Renderer development

Four renderers consume the same `RenderPacket`. A feature exists only when every renderer that claims it draws it
the same way, or explicitly refuses it through capabilities.

| Renderer | Files | Draws with | Capture |
|---|---|---|---|
| `NullRenderer` | `src/Ukiyo.Render.Null` | nothing; validates and counts | no |
| `SoftwareRenderer` | `src/Ukiyo.Render.Software` | CPU rasterizer, no MSAA | PNG from its buffer |
| `WgpuRenderer` | `src/Ukiyo.Render.Wgpu` (`WgpuNative.cs` LibraryImports, WGSL inline in `WgpuRenderer.cs`) | wgpu-native → Metal | GPU readback → `PngEncoder` |
| `ThreeJsRenderer` | `src/Ukiyo.Render.Three` (JSImports) + `web/three-adapter/ukiyo-three.js` | three.js WebGL2 | canvas readback |

## The contract (src/Ukiyo.Render/Contracts.cs)

- `InitializeAsync(config)` → `Capabilities` must then name the **effective** backend and device, never a guess.
- `ApplyResources(batch)`: create/destroy meshes, materials, textures by generational `ResourceHandle`. Use
  `ResourceTable` semantics: stale generation → `StaleHandle`, unknown index → `UnknownHandle`, wrong kind →
  `WrongResourceKind`, re-create of a live handle → `DuplicateHandle`.
- `Render(packet)`: order is 3D instances, then world sprites (through `Camera2D`), then screen sprites; within a
  space by `Layer`, then submission order. Colors are linear; encode to sRGB at output. Textures are sRGB straight
  alpha (decode before tinting/blending).
- `IRenderCapture.CaptureAsync`: the image the renderer actually presented. Never synthesize one.
- Anything unsupported is `RenderException(UnsupportedFeature)`; set `SupportsSprites` (or the new capability flag)
  honestly — `GameRuntime.RenderFrame` checks it before submitting.

Call `RenderValidation.Validate(...)` at the boundary (packets, batches, textures, sprites, cameras). Validation lives
there once; renderers do not reimplement it differently.

## Adding a rendering feature, in order

1. **Contract**: data in `Contracts.cs` (records, linear units, documented conventions), validation in
   `RenderValidation.cs`, capability flag if not every renderer will support it immediately.
2. **Protocol**: `PacketCodec.cs` and `web/three-adapter/protocol.js` together. Keep v1 bytes stable: optional data is
   a new header flag (`FlagSprites = 0x1` is the pattern) and a trailing section; older fixtures in
   `web/three-adapter/fixtures/` must still decode. Update `docs/protocol.md` with offsets and sizes.
3. **Shared math once per language**: e.g. `SpriteGeometry.cs` ↔ `sprite-geometry.js`. The CPU renderer and the
   shaders follow the same formulas (view-projection, pivot, UV flip, rounding).
4. **Renderers**: Software first (easiest to test exactly), then Wgpu (WGSL + pipeline + bind groups), then three.js,
   then Null (validation and counters only).
5. **Tests**: C# protocol round trip and validation errors (`SpriteProtocolTests` pattern), JS decode tests
   (`sprites.test.js` pattern), SoftwareRenderer pixel asserts at known coordinates (`SoftwareRendererTests`).
6. **Evidence**: same game, same tick, captured on Software (headless `--capture`), Wgpu (dev bridge capture) and
   three.js (browser evidence mode); compare pixels at feature centers, not whole-image hashes (MSAA and
   rasterization rules differ at edges).

## wgpu specifics

- Bindings in `WgpuNative.cs` follow `webgpu.h` of the pinned wgpu-native (`scripts/fetch-native.sh`). Struct layout
  must match the header exactly: field order, `nint` for pointers, `uint` enums, explicit padding. Check against the
  header, not memory, and say so when you could not.
- Every created object has one owner and is released in `Dispose`; readback buffers are mapped, copied, unmapped.
- Texture rows for `wgpuQueueWriteTexture` / readback copies use `bytesPerRow` aligned to 256.
- Shaders are WGSL strings in `WgpuRenderer.cs`. Geometry that the CPU can compute (sprite corners, UVs) is computed
  in `SpriteGeometry` and uploaded, so the shader and the SoftwareRenderer cannot drift apart.

## three.js specifics

- C# sends encoded bytes through `JSImport` (`initialize`, `applyResources`, `render`, `capture`, `resize`, `dispose`
  in module `ukiyo-three`). The adapter decodes with `protocol.js` and keeps **no game rules** and no animation of
  its own: it draws exactly the packet it received.
- Errors thrown in JS start with `[RENDER:<Code>]` so the C# facade maps them back to `RenderException`.
- `three` is pinned in `web/three-adapter/package.json`; the browser host copies the adapter into `wwwroot/lib`
  (`Directory.Build.targets`, `UkiyoBrowserHost`).

## Don'ts

- No renderer-side animation, interpolation or time: a paused game must produce identical frames.
- No silent clamping of invalid data; validation throws.
- No feature on one renderer only without a capability flag and a typed refusal on the others.

Related: ukiyo-engine (rules, evidence), ukiyo-rendering (game-facing API), ukiyo-testing.
