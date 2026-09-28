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
| Ticks | Fixed 60 Hz. After N updates the world is at tick N. 64-bit ticks cross to JS as two uint32 (lo, hi). |
| Memory | Renderers consume packets before returning; nothing keeps a borrowed buffer. The browser bridge copies (`byte[]` → `Uint8Array`) in G0; there is no zero-copy claim. |

## Detecting inversions

`CubeGame` gives each face its own color (+X red, −X cyan, +Y green, −Y magenta, +Z blue, −Z yellow) and puts an
off-center white marker near the top-left of the +Z face. A mirrored axis, flipped winding, swapped color channel
or transposed matrix produces a visibly different image, so a uniform cube cannot hide it.
