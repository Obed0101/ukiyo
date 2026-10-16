# Asset import: textures and sprite atlases

Games ship without image decoders or file access. Source art (PNG, plus the atlas JSON the MCP sprite tools write)
is imported on a development machine into C# that the game compiles: pixels, frame rectangles, animations and a
content hash. At runtime the game uploads those bytes with the existing `CreateTexture` command; nothing is parsed,
downloaded or read from disk.

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
`samples/TexturedQuad`, do; LanternRun, SceneHop and LitCube glob `Shared/*.cs`). In the template layout
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
`UnknownFrame`, `Stale`. The CLI prints the message and exits 1 (2 for usage errors).

## Runtime decoding still exists, for prototypes

`context.LoadTexture(Assets.Read("x.png"))` and `SpriteSheet.Load(context, "sprites/x")` decode embedded PNGs at
startup. They are fine while iterating on art straight out of `ukiyo_sprite_generate`; import before shipping.
