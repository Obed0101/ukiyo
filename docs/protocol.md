# Render protocol v1

Binary format used across the C#↔JavaScript bridge. Encoder: `src/Ukiyo.Render/PacketCodec.cs`.
Decoder: `web/three-adapter/protocol.js`. Both are pinned to the golden files in `web/three-adapter/fixtures/`.
Little-endian, 4-byte aligned. Control data (capabilities, evidence) uses JSON; per-frame data uses this format.

## Header (16 bytes, both packet types)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | magic: `0x42524B55` "UKRB" (resources) or `0x50524B55` "UKRP" (frame) |
| 4 | u16 | version = 1 |
| 6 | u16 | flags = 0 |
| 8 | u32 | command count (resources) or sequence (frame) |
| 12 | u32 | total byte length; must equal the payload length |

## Resource batch

Per command:

| Size | Field |
|---|---|
| u8 | command: 1 CreateMesh, 2 CreateMaterial, 3 Destroy |
| u8 | resource kind: 1 Mesh, 2 Material |
| u16 | padding |
| u32, u32 | handle index, generation (generation 0 is invalid) |

- **CreateMesh:** `u32 vertexCount`, `u32 indexCount`, then `vertexCount × (f32 x, y, z, r, g, b)`, then `indexCount × u32`.
- **CreateMaterial:** `f32 × 4` base color (linear RGBA), `u32` flags (1 = use vertex colors).
- **Destroy:** no payload.

## Frame packet

After the header: `u32 tick lo`, `u32 tick hi`, `u32 viewport width`, `u32 viewport height`, `f32 × 4` clear color,
camera (`f32 × 3` position, `f32 × 4` rotation XYZW, `f32` fovY in radians, `f32` near, `f32` far), `u32` instance count,
then per instance (96 bytes): mesh handle and material handle as `u32 kind, u32 index, u32 generation, u32 pad` each,
followed by the world matrix as 16 `f32` in field order `M11 … M44` (see conventions.md).

## Limits and errors

Vertices ≤ 2^20, indices ≤ 3·2^20 (multiple of 3), instances ≤ 2^16. Any violation fails with a typed error; JS
errors carry the prefix `[RENDER:<Code>]` and are mapped back to `RenderException` in C#:
`InvalidPacket`, `UnsupportedVersion`, `TruncatedPayload`, `StaleHandle`, `UnknownHandle`, `WrongResourceKind`,
`NonFiniteValue`, `OutOfRange`, `DuplicateHandle`, `NotInitialized`, `BackendFailure`. No error is turned into a
silently dropped frame.
