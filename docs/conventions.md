# Data conventions

Every renderer consumes the same data. These rules are what makes `Program.cs` produce the same image on
wgpu/Metal and on Three.js.

| Topic | Convention |
|---|---|
| Units | Meters, seconds, radians. |
| Handedness | Right-handed, +Y up. Cameras look down their local −Z. |
| Rotation | Quaternions, XYZW order. |
| Matrices | `System.Numerics.Matrix4x4`, row vectors (`v' = v * M`). Serialized in field order `M11 … M44`. That array is exactly the column-major layout of the equivalent column-vector matrix, which is what WGSL (`mvp * v`) and Three.js (`Matrix4.fromArray`) expect. Translation lands at indices 12–14 (tested in `BridgeTests`). |
| Composition | `world = scale * rotation * translation`; clip = `world * view * projection`. |
| Projection | Right-handed perspective with depth 0..1 (WebGPU). Three.js builds its own projection from the camera fields. |
| Winding | Counter-clockwise front faces; back faces culled in both renderers. |
| Color | Linear working space. Each renderer encodes to its sRGB output (Metal `*Srgb` surface; Three `SRGBColorSpace`). |
| Depth | 24-bit, compare `less`, cleared to 1. |
| Lighting (G1Lit) | Per-pixel Lambert in linear RGB, world space: albedo × (ambient + Σ lights). Directional vectors are the direction light travels; point lights fall off to 0 at their range. Defined once in [rendering-lighting.md](rendering-lighting.md). |
| Passes (multi-camera) | A frame is 1 pass (single camera, no pass section) or up to 4 passes, one packet each, drawn in id order. Pass rects are drawing-buffer pixels, top-left origin, +Y down (like screen sprites). Inside its rect a pass behaves exactly like a single-camera frame of the rect's size: aspect = rect width / height, world sprites through its own `Camera2D`, screen sprites in pass-local pixels, its own clear, depth and lighting. Defined once in [rendering-passes.md](rendering-passes.md). |
| Ticks | Fixed 60 Hz. After N updates the world is at tick N. 64-bit ticks cross to JS as two uint32 (lo, hi). |
| Memory | Renderers consume packets before returning; nothing keeps a borrowed buffer. The browser bridge copies (`byte[]` → `Uint8Array`) in G0; there is no zero-copy claim. |

## Determinism

Same code and same input give byte-identical snapshots and render packets on every host: CoreCLR and NativeAOT on x64
and arm64, and WebAssembly. Floating point is the part that breaks this quietly, so simulation and packet paths
(game state, `Snapshot`, `Extract`, `PacketCodec`) follow three rules:

- No SIMD reductions or fused paths. `Vector2/3/4.Dot/Length/Normalize`, `Vector4.Transform`, `Matrix4x4` products,
  `Invert`, `CreateFromQuaternion`, `CreateLookAt`, `CreatePerspective…` and the `Quaternion` helpers sum lanes in an
  order of their own and use fused multiply-adds where the CPU has them. Element-wise `+ - * /` is exact and fine.
- No libm. `MathF`/`Math` `Sin`, `Cos`, `Tan`, `Atan2`, `Exp`, `Log`, `Pow` differ by an ulp between macOS, glibc and
  the WebAssembly runtime; `FusedMultiplyAdd` and the `*Estimate` functions differ by design. `Sqrt`, `Abs`, `Floor`,
  `Round`, `Min`, `Max` are exact.
- Zeros are normalized: `SnapshotWriter` and `PacketCodec` write −0 as +0, because two machines can agree on a value
  and still disagree on the sign of a zero.

Use `DetMath` (`src/Ukiyo.Render/DetMath.cs`, namespace `Ukiyo`): `SinCos`, `Pow`, `AxisAngle`, `Dot`, `Length`,
`Normalize`, `Cross`, `Compose` (scale · rotation · translation), `Rotation`, `Multiply`, `PositiveZero`. They use only
IEEE operations evaluated one at a time. CPU-renderer pixel math may use System.Numerics where its output is compared
within a tolerance (`SoftwareRenderer` transforms, `LightingMath`); sRGB encode and decode, which a byte-exact CPU frame
goes through, use `DetMath.Pow`. `SnapshotGoldenTests` pins every sample's snapshots and packet digests on both CI
runners, also with `DOTNET_EnableHWIntrinsic=0`.

## Detecting inversions

`CubeGame` gives each face its own color (+X red, −X cyan, +Y green, −Y magenta, +Z blue, −Z yellow) and puts an
off-center white marker near the top-left of the +Z face. A mirrored axis, flipped winding, swapped color channel
or transposed matrix produces a visibly different image, so a uniform cube cannot hide it.
