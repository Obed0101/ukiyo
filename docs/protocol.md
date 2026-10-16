# Render protocol v1

Binary format used across the C#↔JavaScript bridge. Encoder: `src/Ukiyo.Render/PacketCodec.cs`.
Decoder: `web/three-adapter/protocol.js`. Both are pinned to the golden files in `web/three-adapter/fixtures/`.
Little-endian, 4-byte aligned. Control data (capabilities, evidence) uses JSON; per-frame data uses this format.

## Header (16 bytes, both packet types)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | magic: `0x42524B55` "UKRB" (resources) or `0x50524B55` "UKRP" (frame) |
| 4 | u16 | version = 1 |
| 6 | u16 | flags: resources 0; frame `0x1` = 2D section present, `0x2` = lighting section present (G1Lit), `0x4` = pass section present (multi-camera). Unknown bits fail with `InvalidPacket`. |
| 8 | u32 | command count (resources) or sequence (frame) |
| 12 | u32 | total byte length; must equal the payload length |

## Resource batch

Per command:

| Size | Field |
|---|---|
| u8 | command: 1 CreateMesh, 2 CreateMaterial, 3 Destroy, 4 CreateTexture, 5 CreateMesh with normals (G1Lit) |
| u8 | resource kind: 1 Mesh, 2 Material, 3 Texture |
| u16 | padding |
| u32, u32 | handle index, generation (generation 0 is invalid) |

- **CreateMesh:** `u32 vertexCount`, `u32 indexCount`, then `vertexCount × (f32 x, y, z, r, g, b)`, then `indexCount × u32`.
- **CreateMesh with normals (5, G1Lit):** same as CreateMesh, but each vertex is 36 bytes:
  `f32 x, y, z, r, g, b, nx, ny, nz`. Normals are object space, finite and non-zero (not necessarily unit length;
  renderers normalize per pixel). Meshes without normals keep command 1 and its exact bytes.
- **CreateMaterial:** `f32 × 4` base color (linear RGBA), `u32` flags: `0x1` use vertex colors, `0x2` lit (G1Lit).
  Unknown bits fail with `InvalidPacket`.
- **CreateTexture:** `u32 width`, `u32 height`, `u32 filter` (1 Nearest, 2 Linear), then `width × height × 4` bytes of
  RGBA8, rows top to bottom, sRGB-encoded color with straight (not premultiplied) alpha. Width and height are 1..4096.
  The pixel bytes are not padded; the next command starts right after them.
- **Destroy:** no payload.

## Frame packet

After the header: `u32 tick lo`, `u32 tick hi`, `u32 viewport width`, `u32 viewport height`, `f32 × 4` clear color,
camera (`f32 × 3` position, `f32 × 4` rotation XYZW, `f32` fovY in radians, `f32` near, `f32` far), `u32` instance count,
then per instance (96 bytes): mesh handle and material handle as `u32 kind, u32 index, u32 generation, u32 pad` each,
followed by the world matrix as 16 `f32` in field order `M11 … M44` (see conventions.md).

### 2D section (flag `0x1`)

Present only when the frame has sprites, so frames without sprites are byte-identical to the G0 format and the golden
fixtures stay valid. Appended after the instances:

- 2D camera (16 bytes): `f32 center x`, `f32 center y`, `f32 view height` in world units (> 0), `f32` padding.
- `u32` sprite count, then per sprite (84 bytes):

| Size | Field |
|---|---|
| 16 | texture handle: `u32 kind (3), u32 index, u32 generation, u32 pad` |
| u32 | space: 1 World (units, +Y up, through the 2D camera), 2 Screen (drawing-buffer pixels, +Y down) |
| i32 | layer |
| f32 × 2 | position |
| f32 × 2 | size |
| f32 × 2 | pivot, normalized in image space (0,0 top-left, 1,1 bottom-right) |
| f32 | rotation, radians, counter-clockwise on screen |
| f32 × 4 | uv rect `u0, v0, u1, v1` |
| f32 × 4 | color (linear RGBA tint) |

Sprites draw after meshes, without depth: World before Screen, then by layer, then in submission order. Quad corners
come from `SpriteGeometry` (C#) / `sprite-geometry.js` (JS), which must stay identical. Blending is straight alpha in
linear space.

### Lighting section (flag `0x2`, profile G1Lit)

Present only when the game set `FrameBuilder.Lighting`, so unlit frames stay byte-identical to G0. Appended after the
2D section when both are present (sections follow flag-bit order), otherwise after the instances:

- `f32 × 3` ambient (linear RGB, ≥ 0), `u32` light count (0..8), then per light (40 bytes):

| Size | Field |
|---|---|
| u32 | kind: 1 Directional, 2 Point; anything else fails with `InvalidPacket` |
| f32 × 3 | directional: direction the light travels (world, non-zero); point: position (world, meters) |
| f32 × 3 | color (linear RGB, ≥ 0) |
| f32 | intensity (≥ 0); the light delivers color × intensity |
| f32 | range in meters: 0 for directional, > 0 for point |
| u32 | padding |

Lights apply in packet order. The shading model is specified once in [rendering-lighting.md](rendering-lighting.md).
A lit material drawn on a mesh without normals, or in a frame without this section, fails with `InvalidPacket`.

### Pass section (flag `0x4`, multi-camera)

Present only on the packets of a multi-camera frame, so single-camera frames stay byte-identical. Appended last, after
every other section that is present (32 bytes, all `u32`):

| Offset in section | Field |
|---|---|
| 0 | pass id, `0 … count − 1` |
| 4 | pass count of the frame, `1 … 4` |
| 8 | primary pass id, `< count`, the same in every pass of the frame |
| 12 | padding |
| 16, 20 | rect x, y: drawing-buffer pixels, origin top-left, +Y down |
| 24, 28 | rect width, height: ≥ 1, and x + width ≤ viewport width, y + height ≤ viewport height |

A frame of N passes is N consecutive packets: sequence s, s+1, …, ids 0, 1, … in order, same tick, count, primary and
viewport (the drawing buffer). Anything else (a pass before pass 0, a skipped or repeated pass, a single-camera packet
between passes) fails with `InvalidPacket`. Each pass packet is a complete frame description for its own camera:
clear color, camera, instances, its own 2D section and its own lighting section. The model (rects, clears, draw order,
captures) is specified in [rendering-passes.md](rendering-passes.md).

## Profiles

`RenderProfile` is cumulative: `G0Unlit` (1) is everything above except command 5, material flag `0x2` and frame flag
`0x2`; `G1Lit` (2) adds them. A game declares its profile (`IGame.Profile`, default `G0Unlit`) and the runtime
initializes the renderer with it; `RenderCapabilities.Profile` reports the profile the renderer runs. G1Lit data sent
to a renderer running G0Unlit, or a profile a renderer does not implement, fails with `UnsupportedFeature`.
Multi-camera passes (frame flag `0x4`) are not tied to a profile: G0Unlit and G1Lit games can both use them; a renderer
that does not declare `RenderCapabilities.SupportsPasses` gets `UnsupportedFeature` from the runtime instead.

## Limits and errors

Vertices ≤ 2^20, indices ≤ 3·2^20 (multiple of 3), instances ≤ 2^16, sprites ≤ 2^16, texture side ≤ 4096, lights ≤ 8
per frame (`RenderValidation.MaxLights`; more fail with `OutOfRange`, non-finite light values with `NonFiniteValue`),
passes ≤ 4 per frame (`RenderValidation.MaxPasses`; a count of 0 or 5+, an id or primary ≥ count, or a rect that is
empty or leaves the drawing buffer fail with `OutOfRange`; with an empty drawing buffer the rect is not checked). Any
violation fails with a typed error; JS errors carry the prefix `[RENDER:<Code>]` and are mapped back to
`RenderException` in C#:
`InvalidPacket`, `UnsupportedVersion`, `TruncatedPayload`, `StaleHandle`, `UnknownHandle`, `WrongResourceKind`,
`NonFiniteValue`, `OutOfRange`, `DuplicateHandle`, `NotInitialized`, `BackendFailure`, `UnsupportedFeature`
(a packet uses something the renderer did not declare in its capabilities, e.g. sprites, or lighting on a G0Unlit
renderer). No error is turned into a
silently dropped frame.
