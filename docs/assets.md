# Asset import: textures, sprite atlases and meshes

Games ship without image decoders, model parsers or file access. Source art (PNG, plus the atlas JSON the MCP sprite
tools write; glTF 2.0 models for meshes, see [Meshes](#meshes)) is imported on a development machine into C# that the
game compiles: pixels, frame rectangles, animations, vertices and a content hash. At runtime the game uploads those
bytes with the existing `CreateTexture` / `CreateMesh` commands; nothing is parsed, downloaded or read from disk.

```
games/<Name>/art/hero_run.png          source art (not under Shared/: never embedded, never compiled)
games/<Name>/art/hero_run.json         optional atlas: frames + animations (ukiyo_sprite_generate/_process/_sheet)
games/<Name>/art/hero.import.json      import spec: what goes into one atlas
games/<Name>/Shared/gen/HeroArt.g.cs   generated; compiled into every host and hashed into its source identity
```

## Use

```jsonc
// art/hero.import.json — paths are relative to this file
{
  "class": "HeroArt",                       // PascalCase class name
  "namespace": "Ukiyo.Games.MyGame",        // the game's namespace
  "output": "../Shared/gen/HeroArt.g.cs",   // must end in .g.cs
  "inputs": ["hero_run.png", "hero_idle.png", "stone.png"],
  "extrude": 1,                             // optional, 0..8, default 1
  "maxWidth": 1024                          // optional, 1..4096, default 1024
}
```

```sh
dotnet run --project tools/Ukiyo.Import -- games/MyGame/art               # every *.import.json under the folder
dotnet run --project tools/Ukiyo.Import -- games/MyGame/art/hero.import.json
dotnet run --project tools/Ukiyo.Import -- --check games/MyGame/art       # exit 1 if a generated file is stale
```

```csharp
// Initialize: one texture upload, every frame and animation by name
_sheet = HeroArt.Load(context);                       // or HeroArt.Load(context, TextureFilter.Linear)
_run = _sheet.Animation("hero_run");
// Extract
frame.DrawSprite(_run.At(_tick - _runStart), position, new Vector2(1, 1));
// Lower level: HeroArt.Texture(filter) is the TextureData; HeroArt.Frames / Animations are the tables.
```

The generated class exposes `Width`, `Height`, `Extrude`, `ContentHash`, `Frames` (`AtlasFrame`: name, x, y, width,
height in pixels, y down), `Animations` (`AtlasAnimation`: name, frame names, fps, loop), `Rgba`, `Texture(filter)` and
`Load(context, filter)`. `Load` calls `SpriteSheet.FromAtlas`, which applies the same validation as
`SpriteSheet.Parse` for JSON.

The host csproj must compile `Shared/**/*.cs` (the template layout of `templates/game` and `ukiyo_new_game`, and
`samples/TexturedQuad` and `samples/ImportedMesh`, do; LanternRun, SceneHop, LitCube and Minimap glob `Shared/*.cs`). In the template layout
(docs/game-layout.md) `Shared/gen/` is the layout's generated-code folder and specs live in `<game>/art/`, so
`"output": "../Shared/gen/<Class>.g.cs"` lands it there; it is hashed as `gen/<Class>.g.cs`. `samples/TexturedQuad` is
the working example.

## Inputs

- Each input is a PNG the engine's `PngDecoder` accepts (non-interlaced; 8-bit RGBA, RGB, gray, gray+alpha or
  palette; 1/2/4-bit palette and gray). Anything else fails with `[IMPORT:InvalidPng]`.
- A PNG with a sibling `.json` of the same name (the `ukiyo_sprite_*` format,
  `{"frames":{"name":{"x","y","w","h"}},"animations":{"name":{"frames":[…],"fps":8,"loop":true}}}`) contributes those
  frames and animations. `meta` and other keys are ignored. `fps` defaults to 8 and `loop` to true, as in
  `SpriteSheet.Parse`.
- A PNG without JSON is one frame named after the file (`stone.png` → `stone`).

## Atlas rules

All inputs of one spec are packed into one texture (one texture = one draw batch).

1. **Names.** Frame and animation names keep their source names; they must be 1–64 characters of letters, digits,
   `_`, `.`, `-`, starting with a letter or `_`. A name used twice (across inputs too) is an error
   (`DuplicateName`), never a silent rename. An animation naming a missing frame is `UnknownFrame`.
2. **Cells.** Every frame becomes a cell `extrude` pixels larger on each side.
3. **Order.** Cells are placed tallest first, then widest, then by frame name (ordinal, culture-independent).
4. **Shelves.** Cells go left to right on shelves; a cell that would cross the shelf limit starts a new shelf below
   the tallest cell of the current one. The shelf limit is the smallest power of two ≥ √(total cell area), at least the
   widest cell and at most `maxWidth`. The image is then trimmed to the cells placed (width = right-most cell edge,
   height = bottom of the last shelf), so one frame with `extrude: 0` keeps its exact size and bytes.
5. **Extrusion.** The ring around each frame repeats the frame's nearest edge pixel (corners repeat the corner pixel).
   A sample that lands just outside a frame — float error at a fractional position or scale with `Nearest`, or the
   half-texel footprint of `Linear` — reads a copy of the frame's own edge instead of the neighbor's pixels.
   `extrude: 0` turns it off; keep it on for anything drawn at non-integer scale or with `Linear`.
6. **Everything else** in the atlas is transparent black `(0,0,0,0)`.
7. **Rectangles** in `Frames` are the frame itself, without extrusion. `Frames` and `Animations` are sorted by name;
   an animation's frame list keeps playback order.
8. **Limits.** A cell wider than `maxWidth`, or an atlas taller than 4096 px, fails with `AtlasTooLarge`: split the
   inputs into several specs.

## Pixels: what the importer writes, and why it is sRGB straight alpha

The generated `Rgba` is exactly what `CreateTexture` takes ([protocol](protocol.md)): RGBA8, rows top to bottom,
**sRGB-encoded color with straight (not premultiplied) alpha** — the same bytes a runtime `PngDecoder.Decode` of the
source would produce (tests compare them). Renderers decode sRGB to linear (`Rgba8UnormSrgb` on wgpu, `SRGBColorSpace`
on three.js, once per texture to floats on the CPU), then tint and blend in linear space with straight-alpha blending.

The UKIYO-30 task text asked for "linear RGBA, premultiply". That is deliberately **not** what the importer emits:

- **Parity.** G1 proved this exact contract pixel for pixel on CPU, Metal and WebGL2 (`docs/G1.md`). Premultiplied
  alpha changes the blend state and the tint math in every renderer, and the golden protocol fixtures; it would be a
  protocol v2 change, not an importer feature.
- **Precision.** Linear values stored in 8 bits band visibly in dark tones: sRGB encoding spends its 256 codes where
  the eye needs them. Storing linear without banding needs 16-bit channels, twice the memory and upload size.
- **Premultiplying sRGB bytes is wrong.** Premultiplication must happen in linear space; the GPU decodes sRGB after
  sampling, and `decode(a·c) ≠ a·decode(c)`. Correct premultiplied textures therefore also need linear (16-bit)
  storage or shader-side decoding.
- **What premultiplied alpha would buy** is fringe-free `Linear` filtering at transparent edges and one blend state for
  additive and alpha blending. With `Nearest` (the default for pixel art) there is no visible difference. With `Linear`,
  fully transparent texels still contribute their RGB to edge samples (usually black, so a dark rim): if that shows,
  give the transparent pixels next to the edge the edge's color with alpha 0 in the source art.

## Sampler

`CreateTexture` carries one sampler field, `filter` (1 Nearest, 2 Linear), chosen per texture by `Texture(filter)` /
`Load(context, filter)`. Addressing is clamp-to-edge on every renderer (CPU clamps, wgpu `ClampToEdge`, three.js
`ClampToEdgeWrapping`), which is what atlas sprites need. There is no repeat/mirror wrap mode in protocol v1; a
repeating texture would need a new protocol field.

## Determinism and hashes

- Same spec and same source bytes → byte-identical `.g.cs`: no timestamps, no absolute paths, `\n` line endings,
  ordinal sorting, invariant number formatting. The header's "Regenerate" path is relative to the repository root.
- The header lists the SHA-256 of every source file (PNGs and atlas JSONs).
- `ContentHash` is the SHA-256 of what the game receives — format version, size, extrusion, frame table, animations and
  pixels — so re-exporting identical art (new `meta` timestamps in the JSON) keeps it.
- Hosts hash every compiled shared file into their source identity (`ukiyo.source:*`), so `[ukiyo] target=…` lines and
  parity reports show the imported file's hash next to the game code's.
- The importer rewrites a file only when its content changed; `--check` regenerates in memory and fails with
  `[IMPORT:Stale]` if the file on disk differs.

## Errors

Every failure is an `ImportException` with an `ImportErrorCode` and the prefix `[IMPORT:<Code>]`: `InvalidSpec`,
`MissingFile`, `InvalidPng`, `InvalidAtlas`, `FrameOutOfBounds`, `DuplicateName`, `InvalidName`, `AtlasTooLarge`,
`UnknownFrame`, `Stale`, and for meshes `InvalidMesh`, `UnsupportedFeature`, `MissingNormals`, `MeshTooLarge`. The CLI
prints the message and exits 1 (2 for usage errors).

## Runtime decoding still exists, for prototypes

`context.LoadTexture(Assets.Read("x.png"))` and `SpriteSheet.Load(context, "sprites/x")` decode embedded PNGs at
startup. They are fine while iterating on art straight out of `ukiyo_sprite_generate`; import before shipping.

## Meshes

The same tool imports glTF 2.0 models into `MeshData` literals. A spec whose `inputs` are all `.gltf`/`.glb` files is
a mesh spec (one spec imports either PNGs or models, never both):

```
games/<Name>/art/lamp.gltf + lamp.bin       source models (not under Shared/: never embedded, never compiled)
games/<Name>/art/crate.glb
games/<Name>/art/props.import.json          mesh import spec
games/<Name>/Shared/gen/PropMeshes.g.cs     generated; compiled into every host and hashed into its source identity
```

```jsonc
// art/props.import.json — paths are relative to this file; a model's .bin files are relative to the model
{
  "class": "PropMeshes",                    // PascalCase class name
  "namespace": "Ukiyo.Games.MyGame",
  "output": "../Shared/gen/PropMeshes.g.cs",
  "inputs": ["lamp.gltf", "crate.glb"],     // one mesh per file, named after the file: "lamp", "crate"
  "normals": "required",                    // optional: "required" (default) | "flat" | "none"
  "doubleSided": "reject"                   // optional: "reject" (default) | "cull"
}
```

```sh
dotnet run --project tools/Ukiyo.Import -- games/MyGame/art            # atlas and mesh specs alike
dotnet run --project tools/Ukiyo.Import -- --check games/MyGame/art    # exit 1 if a generated file is stale
```

```csharp
// Initialize: the generated class rebuilds MeshData from compiled spans. No parsing, no files.
_lamp = context.CreateMesh(PropMeshes.Lamp.Create());                 // or PropMeshes.Create("lamp")
_material = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true) { Lit = true });
// Extract
frame.Draw(_lamp, _material, pose.ToMatrix());
// Also: PropMeshes.Count / VertexCount / TriangleCount / ContentHash / Names; per mesh Name, VertexCount,
// TriangleCount, HasNormals, ContentHash, BoundsMin, BoundsMax (object space, meters).
```

`samples/ImportedMesh` is the working example: Blender's Suzanne (`.gltf` + `.bin`) on a pedestal (`.glb`), both
exported by Blender's glTF exporter from `art/export_models.py`, drawn lit; its snapshot carries the asset counts
(`meshes`, `meshVertices`, `meshTriangles`) on every target.

### Why glTF 2.0 (and not OBJ)

- **The conventions are the engine's.** glTF fixes meters, right-handed +Y up, counter-clockwise front faces and
  *linear* vertex colors and material factors ([docs/conventions.md](conventions.md) uses the same). Positions,
  normals and colors are therefore copied bit for bit; there is no axis swap, mirror, winding flip or color decode to
  get wrong. OBJ defines none of these: handedness, up axis and units depend on the exporter's settings, vertex colors
  are an unofficial `v x y z r g b` extension with no stated color space, and `usemtl` points into a separate `.mtl`
  file whose `Kd` is commonly sRGB. An OBJ importer would have to guess, and a wrong guess imports mirrored or
  wrongly colored geometry without any error.
- **It is a precise, versioned format.** Every value has a declared type, count and byte range, so out-of-range or
  truncated data is detected rather than read as garbage. It is the default interchange format of Blender and most
  tools, and it is parsed here with `System.Text.Json` and `BinaryPrimitives`: no dependency.

### What is imported

1. **The default scene.** `scene` (or `scenes[0]`), every node depth first in file order, every primitive of every
   node with a mesh, merged into one `MeshData` per file. A file with no scene is `InvalidMesh`.
2. **Node transforms are baked.** Each node's `matrix` (column-major, i.e. the engine's row-vector `M11…M44` order) or
   `translation`/`rotation`/`scale` (`scale · rotation · translation`, like `EntityPose`) is composed child · parent in
   double precision and applied to positions; normals go through the inverse transpose and are renormalized. A node
   whose transform mirrors (negative determinant) gets its triangles' winding reversed, as glTF requires, so faces stay
   front-facing. A node with an identity transform keeps the stored floats exactly. A singular transform (zero scale)
   is `InvalidMesh`. The game then places the whole mesh with its own world matrix.
3. **Triangles.** Mode 4 (triangles) as stored; 5 (strip) and 6 (fan) unrolled with glTF's winding rules; with or
   without `indices` (unsigned byte, short or int). Points and lines are `UnsupportedFeature`.
4. **Normals** (`"normals"`): `required` (default) imports `NORMAL` and fails with `MissingNormals` for a primitive
   without it — lit materials need normals, so a lit-ready mesh never silently lacks them. `flat` ignores source
   normals and gives every triangle three own vertices with its face normal (faceted look; zero-area triangles are
   `InvalidMesh`). `none` drops normals: the mesh is G0 data for unlit materials. Nothing is smoothed or guessed.
5. **Colors.** Vertex color = `COLOR_0.rgb` (float, or normalized unsigned byte/short; white without it) ×
   `baseColorFactor.rgb` of the primitive's material (white without one), linear, as glTF defines albedo. Baking the
   factor into the vertices lets primitives with different materials share one mesh; draw it with
   `MaterialData(Vector4.One, UseVertexColors: true)` (lit or not), or tint it with the base color.
6. **Validation and limits.** Every accessor and buffer view is bounds-checked; indices must address existing
   vertices; positions, normals and colors must be finite and normals non-zero. A mesh over the protocol limits
   ([protocol.md](protocol.md): 2^20 vertices, 3·2^20 indices) is `MeshTooLarge` — split the model. The finished mesh
   also passes `RenderValidation.Validate`, the check every renderer runs on `CreateMesh`.

### What fails instead of being dropped

Anything the engine cannot draw the way the file describes it is `UnsupportedFeature`, never ignored: any entry in
`extensionsRequired` (Draco, meshopt, quantization, …); any `extensions` on a node, mesh, primitive, accessor or buffer
view (e.g. GPU instancing); skins, `JOINTS_n`/`WEIGHTS_n`, morph targets and node weights; textures
(`baseColorTexture`, `metallicRoughnessTexture`, `normalTexture`, `occlusionTexture`, `emissiveTexture`); a non-zero
`emissiveFactor`; `alphaMode` other than `OPAQUE`, base color alpha < 1 or `COLOR_0` alpha < 1 (engine meshes are
opaque); sparse accessors; glTF 1.x or a `minVersion` above 2.0; absolute or remote buffer URIs. `doubleSided`
materials are rejected too, because the engine culls back faces and the surface would vanish from behind: enable
backface culling in the exporter (Blender: material → Settings → Backface Culling), or set `"doubleSided": "cull"` to
import the faces once, knowingly.

Ignored because they do not change the geometry or the Lambert model the engine draws: `metallicFactor`,
`roughnessFactor`, material extensions, `TEXCOORD_n`, `TANGENT`, `COLOR_1+`, custom `_ATTRIBUTES`, cameras,
animations (the default pose is imported), `extras`, names other than the file name.

Blender: `File → Export → glTF 2.0`, Format `glTF Separate` or `glTF Binary`; under Data → Mesh: UVs off (unused),
Normals on, Vertex Color "Active"; Animation off. `samples/ImportedMesh/art/export_models.py` passes exactly these
options to `bpy.ops.export_scene.gltf` (Blender 5.1, exporter v5.1.18 for the committed files).

### Pinned version, hashes and `--check`

- `MeshImporter.Version` (now 1) is the pinned importer version. It is written into the generated header
  (`// ukiyo-import mesh v1 (glTF 2.0)`) and hashed into every content hash, and it is bumped whenever the same model
  would produce different code or data. `--check` compares the whole file, so files generated by another importer
  version, from other source bytes (the header lists the SHA-256 of the model and of every `.bin` it reads) or with
  other options (the header's `Options:` line) all fail with `[IMPORT:Stale]` until regenerated.
- Deterministic like the atlas importer: same spec and bytes → byte-identical `.g.cs` (`\n`, invariant formatting,
  meshes sorted by name, floats in shortest round-trip form so the compiled values are exactly the imported bits).
- `ContentHash` per mesh is the SHA-256 of version, name, counts, positions, colors, normals and indices; the class
  `ContentHash` covers every mesh. Re-exporting the same geometry in another layout (`.glb` instead of `.gltf`, other
  buffer order) keeps them. Hosts hash the generated file into their source identity (`[ukiyo] target=…
  SceneMeshes.g.cs=…`), which is how parity runs prove every target compiled the same meshes.

### Mapping to the UKIYO-29 task text

The task asked for "source under `Assets/` to `Assets/gen/*.g.cs` … linked via UkiyoSharedSource". As with textures,
the source lives in `<game>/art/` and the output in `<game>/Shared/gen/`: everything under `Shared/assets` is embedded
as a runtime resource, and on macOS's case-insensitive file system `Shared/Assets` *is* that folder, so `Assets/`
would ship the source models inside every build. `Shared/gen/` is compiled through the recursive
`UkiyoSharedSource Include="../Shared/**/*.cs"` of each host. The "manifest hash entries" are the generated header's
source SHA-256 list plus `ContentHash`, and the hosts' `ukiyo.source:*` identity. Generated code is large text
(~200 bytes per vertex); keep shipped meshes in the thousands of vertices, not at the protocol limit.

### Results · 2026-10-16 · Apple M4 Pro, macOS 27.0, .NET SDK 10.0.401, Bun 1.3.13, Blender 5.1.0 (glTF I/O v5.1.18)

| Check | Result |
|---|---|
| `dotnet build Ukiyo.slnx -c Release` | pass, 0 warnings, 0 errors (with the three ImportedMesh hosts) |
| `dotnet test tests/Ukiyo.Tests` | pass, 188/188 (171 before + 17 in `MeshImportTests`: round trip of the compiled sample against a fresh import and against the raw `.bin`, determinism and the checked-in file, transform/normal/winding/handedness conventions incl. a CPU render from front and back, strips/fans, colors, normals modes, limits, malformed and unsupported input, names and specs) |
| `ukiyo-import --check samples` | exit 0, both generated files up to date; a header from another importer version (`v0`) or one changed source byte in `suzanne.bin` → `[IMPORT:Stale]`, exit 1 |
| NativeAOT headless | snapshot and CPU captures (ticks 0, 600) byte-identical to the JIT build |
| `SAMPLE=ImportedMesh scripts/g1lit.sh` snapshots | pass: ticks 0/60/180/600 identical on CPU, Metal and WebGL2 (max Δ = 0), same source hashes including `SceneMeshes.g.cs`, asset counts `meshes=2 meshVertices=555 meshTriangles=996` on all three |
| `SAMPLE=ImportedMesh scripts/g1lit.sh` pixels | exit 1. CPU vs Metal: interior max Δ = 1 at every tick (pass). CPU vs WebGL2: interior max Δ 13–39, from 3–11 isolated pixels per frame (of 614 400) |

The WebGL2 pixels counted as "interior" are single pixels on Suzanne's own low-contrast silhouettes: the seam where an
ear meets the head, eye-socket rims and dark rims against the dark background, where the reference's neighbors differ
by less than the script's 24-level edge threshold. Each one is a coverage difference (one renderer shows the farther
surface or the background), the same rasterization property the unlit G0 cube shows at its edges; no region of the
surfaces differs by more than one 8-bit step. The gate stays as `scripts/g1lit.sh` defines it, so this run is reported
as failing the pixel gate rather than re-scored.
