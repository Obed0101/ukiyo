# Render protocol v1

Binary format used across the C#↔JavaScript bridge. Encoder: `src/Ukiyo.Render/PacketCodec.cs`.
Decoder: `web/three-adapter/protocol.js`. Both are pinned to the golden files in `web/three-adapter/fixtures/`.
Little-endian, 4-byte aligned. Control data (capabilities, evidence) uses JSON; per-frame data uses this format.

## Header (16 bytes, both packet types)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | magic: `0x42524B55` "UKRB" (resources) or `0x50524B55` "UKRP" (frame) |
| 4 | u16 | version = 1 |
| 6 | u16 | flags: resources 0; frame `0x1` = 2D section present. Unknown bits fail with `InvalidPacket`. |
| 8 | u32 | command count (resources) or sequence (frame) |
| 12 | u32 | total byte length; must equal the payload length |

## Resource batch

Per command:

| Size | Field |
|---|---|
| u8 | command: 1 CreateMesh, 2 CreateMaterial, 3 Destroy, 4 CreateTexture |
| u8 | resource kind: 1 Mesh, 2 Material, 3 Texture |
| u16 | padding |
| u32, u32 | handle index, generation (generation 0 is invalid) |

- **CreateMesh:** `u32 vertexCount`, `u32 indexCount`, then `vertexCount × (f32 x, y, z, r, g, b)`, then `indexCount × u32`.
- **CreateMaterial:** `f32 × 4` base color (linear RGBA), `u32` flags (1 = use vertex colors).
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

## Limits and errors

Vertices ≤ 2^20, indices ≤ 3·2^20 (multiple of 3), instances ≤ 2^16, sprites ≤ 2^16, texture side ≤ 4096. Any
violation fails with a typed error; JS errors carry the prefix `[RENDER:<Code>]` and are mapped back to
`RenderException` in C#:
`InvalidPacket`, `UnsupportedVersion`, `TruncatedPayload`, `StaleHandle`, `UnknownHandle`, `WrongResourceKind`,
`NonFiniteValue`, `OutOfRange`, `DuplicateHandle`, `NotInitialized`, `BackendFailure`, `UnsupportedFeature`
(a packet uses something the renderer did not declare in its capabilities, e.g. sprites). No error is turned into a
silently dropped frame.
