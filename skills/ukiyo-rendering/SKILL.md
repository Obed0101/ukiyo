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

Games run the `G0Unlit` profile unless they declare another: vertex colors × base color, no textures on meshes yet.
Textures exist for the 2D layer (see ukiyo-sprites).

## Meshes from glTF (import them; no parsing at runtime)

Models made in Blender or another tool are glTF 2.0 files in `games/<Game>/art/`; the importer turns them into
`Shared/gen/<Class>.g.cs` with the vertices compiled in. Full rules: docs/assets.md "Meshes". Example: `samples/ImportedMesh`.

```jsonc
// games/MyGame/art/props.import.json (paths relative to this file; .bin buffers relative to each model)
{ "class": "PropMeshes", "namespace": "Ukiyo.Games.MyGame", "output": "../Shared/gen/PropMeshes.g.cs",
  "inputs": ["lamp.gltf", "crate.glb"] }            // optional "normals": "required" | "flat" | "none", "doubleSided": "reject" | "cull"
```

```sh
dotnet run --project tools/Ukiyo.Import -- games/MyGame/art            # regenerate (atlas and mesh specs)
dotnet run --project tools/Ukiyo.Import -- --check games/MyGame/art    # exit 1 if a .g.cs is stale
```

```csharp
_lamp = context.CreateMesh(PropMeshes.Lamp.Create());   // member = PascalCase file name; PropMeshes.Create("lamp") too
_mat = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true) { Lit = true });  // base color is baked in
var lift = -PropMeshes.Lamp.BoundsMin.Y;                // bounds in meters, object space
```

- glTF uses the engine's conventions (meters, right-handed +Y up, CCW front faces, linear colors): data is copied, node
  transforms of the default scene are baked (mirrors flip winding back), `COLOR_0 × baseColorFactor` becomes the vertex
  color. One mesh per file.
- Normals are required by default (`MissingNormals` otherwise): export with normals, or set `"normals": "flat"` /
  `"none"` (unlit only).
- Fails with `[IMPORT:UnsupportedFeature]` rather than dropping: textures, skins, morph targets, lines/points,
  non-opaque or emissive materials, required extensions (Draco…), double-sided materials (turn on backface culling or
  set `"doubleSided": "cull"`). Malformed files are `InvalidMesh`; over 2^20 vertices / 3·2^20 indices `MeshTooLarge`.
- Commit the source model and the `.g.cs` together; the header pins the importer version and lists source SHA-256s.

## Lighting (profile G1Lit)

```csharp
public RenderProfile Profile => RenderProfile.G1Lit;                          // in the IGame; default is G0Unlit

// Initialize: lit meshes need one normal per vertex; lit materials opt in.
_mesh = context.CreateMesh(new MeshData(vertices, indices) { Normals = normals });
_material = context.CreateMaterial(new MaterialData(new Vector4(0.6f, 0.6f, 0.6f, 1), UseVertexColors: false) { Lit = true });

// Extract: ambient + up to 8 lights, every frame that draws a lit material.
frame.Lighting = new SceneLighting(new Vector3(0.03f), [
    Light.Directional(new Vector3(-0.4f, -1f, -0.6f), new Vector3(1f, 0.93f, 0.82f), intensity: 0.85f),
    Light.Point(lampPosition, new Vector3(1f, 0.5f, 0.2f), intensity: 1.8f, range: 4f),
]);
```

Model (docs/rendering-lighting.md): per-pixel Lambert in linear RGB, `albedo × (ambient + Σ color × intensity × max(N·L, 0)
× attenuation)`; directional vectors are the direction the light travels; point lights fade to zero at `range`.
Unlit materials still draw flat in a lit frame. A lit material on a mesh without normals, or in a frame without
`Lighting`, is `InvalidPacket`; more than 8 lights is `OutOfRange`; lighting in a G0Unlit game is `UnsupportedFeature`.
Sample: `samples/LitCube`. Pixel parity: faces with constant lighting match exactly across renderers; light gradients
(point lights) may differ by 1/255 where rounding falls differently.

## Several cameras (minimap, split screen)

```csharp
// Extract: the builder you get is pass 0 (primary by default). Add up to 3 more passes.
frame.Camera = chase; frame.Lighting = sun; DrawWorld(frame);
var map = frame.AddPass(new PassRect(x, y, side, side));       // drawing-buffer pixels, from frame.DrawingBuffer
map.Camera = topDown; map.ClearColor = dark; map.Lighting = overhead; DrawWorld(map);
map.DrawScreenSprite(bar, Vector2.Zero, new Vector2(map.Viewport.Width, 3));   // pass-local pixels
frame.PrimaryPass = 0;                                          // what inspectors and "primary" captures show
```

Each pass is a full view: nothing is inherited (camera, clear color, lighting, draws). Inside its rect a pass draws like
a whole frame of that size (`Viewport` is the rect's size); later passes draw over earlier ones; pixels no rect covers
keep pass 0's clear color. At most 4 passes (`OutOfRange`), rects inside the drawing buffer (`OutOfRange`). Captures:
`runtime.CaptureAsync()` is the composed frame, `runtime.CaptureAsync(pass: k)` pass k alone at its rect size; headless
`--capture` also writes `headless-tick-NNNNNN-pass-K.png` for multi-camera frames. Sample: `samples/Minimap`; model:
docs/rendering-passes.md.

## Renderers

| Renderer | Where | Capture |
|---|---|---|
| `WgpuRenderer` | desktop, Metal | yes (GPU readback) |
| `ThreeJsRenderer` | browser, WebGL2 | yes (canvas) |
| `SoftwareRenderer` | headless `--capture`, tests | yes (CPU, no MSAA) |
| `NullRenderer` | headless default, tests | no |

`renderer.Capabilities` says what a backend supports (`SupportsCapture`, `SupportsSprites`, `SupportsPasses`, `Profile`). The runtime throws
`RenderException(UnsupportedFeature)` instead of silently dropping features a backend lacks.

## Changing the protocol

The wire format is specified in `docs/protocol.md` and implemented twice: `src/Ukiyo.Render/PacketCodec.cs` and
`web/three-adapter/protocol.js`. Any change must update both, `RenderValidation`, the doc, and tests on both sides
(`tests/Ukiyo.Tests/*Protocol*`, `web/three-adapter/*.test.js`). Golden fixtures in `web/three-adapter/fixtures/`
must keep decoding; add a header flag for optional sections rather than changing existing bytes.

Parity: shared math lives in one place per language and mirrors the other (`SpriteGeometry.cs` ↔ `sprite-geometry.js`,
`SoftwareRenderer.ViewProjection` ↔ wgpu). After a rendering change, capture the same tick on two renderers and compare.
