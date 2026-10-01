---
name: ukiyo-rendering
description: Use when drawing 3D meshes, setting cameras or clear colors, adding a renderer feature, or touching the binary render protocol (C# PacketCodec ↔ web protocol.js). Covers FrameBuilder, conventions, renderer capabilities and parity.
---

# ukiyo rendering

Game code never talks to a GPU. It fills a `FrameBuilder`; the runtime turns that into a `RenderPacket`, validates
it, and every renderer (wgpu/Metal, Three.js/WebGL2, CPU software, null) consumes the same packet.

## Drawing 3D

```csharp
// Initialize
VertexPositionColor[] v = [ new(new Vector3(-0.5f, -0.5f, 0), new Vector3(1, 0, 0)), /* … */ ];
_mesh = context.CreateMesh(new MeshData(v, [0, 1, 2, 0, 2, 3]));            // counter-clockwise = front
_material = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true));

// Extract
frame.ClearColor = new Vector4(0.02f, 0.02f, 0.025f, 1f);                    // linear color
frame.Camera = FrameBuilder.LookAt(new Vector3(0, 1, 3), Vector3.Zero, MathF.PI / 3f, 0.1f, 100f);
frame.Draw(_mesh, _material, pose.ToMatrix());                                // EntityPose → world matrix
```

Conventions (docs/conventions.md): meters, radians, right-handed +Y up, cameras look down −Z, quaternions, row-vector
`Matrix4x4` (`world = scale * rotation * translation`), depth 0..1, CCW front faces, back faces culled, colors in
linear space (each renderer encodes to sRGB).

Current profile is G0 unlit: vertex colors × base color, no lights or textures on meshes yet. Textures exist for the 2D
layer (see ukiyo-sprites).

## Renderers

| Renderer | Where | Capture |
|---|---|---|
| `WgpuRenderer` | desktop, Metal | yes (GPU readback) |
| `ThreeJsRenderer` | browser, WebGL2 | yes (canvas) |
| `SoftwareRenderer` | headless `--capture`, tests | yes (CPU, no MSAA) |
| `NullRenderer` | headless default, tests | no |

`renderer.Capabilities` says what a backend supports (`SupportsCapture`, `SupportsSprites`). The runtime throws
`RenderException(UnsupportedFeature)` instead of silently dropping features a backend lacks.

## Changing the protocol

The wire format is specified in `docs/protocol.md` and implemented twice: `src/Ukiyo.Render/PacketCodec.cs` and
`web/three-adapter/protocol.js`. Any change must update both, `RenderValidation`, the doc, and tests on both sides
(`tests/Ukiyo.Tests/*Protocol*`, `web/three-adapter/*.test.js`). Golden fixtures in `web/three-adapter/fixtures/`
must keep decoding; add a header flag for optional sections rather than changing existing bytes.

Parity: shared math lives in one place per language and mirrors the other (`SpriteGeometry.cs` ↔ `sprite-geometry.js`,
`SoftwareRenderer.ViewProjection` ↔ wgpu). After a rendering change, capture the same tick on two renderers and compare.
