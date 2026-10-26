---
name: ukiyo-sprites
description: Use for 2D games and any 2D drawing — sprites, sprite sheets and animations, textures from PNG or code (PixelArt), the 2D camera, layers, tint, flipping and screen-space sprites.
---

# ukiyo sprites (2D layer)

Sprites are drawn after 3D meshes, in two spaces: **World** (units, +Y up, follows `frame.Camera2D`) then **Screen**
(drawing-buffer pixels, +Y down, for HUDs). Inside a space: by `layer`, then by submission order. Alpha blending is
straight alpha; textures are sRGB, tint and blending are linear.

## Textures from PNG: import them (no decoding at runtime)

Ship art as imported C#, not as PNGs decoded at startup. Source PNGs (and the atlas JSON the sprite tools write) live in
`games/<Game>/art/`; an import spec packs them into one atlas and generates `Shared/gen/<Class>.g.cs` with the pixels
compiled in. Full rules: docs/assets.md. Working example: `samples/TexturedQuad`.

```jsonc
// games/MyGame/art/hero.import.json (paths relative to this file)
{ "class": "HeroArt", "namespace": "Ukiyo.Games.MyGame", "output": "../Shared/gen/HeroArt.g.cs",
  "inputs": ["hero_run.png", "hero_idle.png", "stone.png"] }
```

```sh
dotnet run --project tools/Ukiyo.Import -- games/MyGame/art            # regenerate every spec under art/
dotnet run --project tools/Ukiyo.Import -- --check games/MyGame/art    # exit 1 if a .g.cs is stale
```

```csharp
var sheet = HeroArt.Load(context);              // one CreateTexture; frames/animations keep their source names
var run = sheet.Animation("hero_run");          // from hero_run.json; a PNG without JSON is one frame named after the file
var tex = context.CreateTexture(HeroArt.Texture(TextureFilter.Linear));   // just the texture
```

- Atlas: frames packed tallest first, then widest, then by name; each frame gets a 1 px extruded edge (`"extrude"`),
  so neighbors never bleed at fractional scales or with Linear. Duplicate names fail; nothing is renamed.
- Pixels stay what the protocol takes: sRGB RGBA8, straight alpha, identical to a runtime decode of the PNG.
- Errors are `ImportException` `[IMPORT:<Code>]` (InvalidPng, FrameOutOfBounds, DuplicateName, UnknownFrame, …).
- Regenerate after every art change and commit the `.g.cs` with the art; the header lists source SHA-256s and
  `ContentHash` changes only when the game's pixels or frames do.

## Other textures

```csharp
var tex = context.LoadTexture(context.Assets.Read("sprites/hero.png"));        // prototypes: decodes a PNG from Shared/assets at startup
var tex2 = context.CreateTexture(new TextureData(w, h, rgbaBytes, TextureFilter.Linear));
var art = PixelArt.FromRows(["..kk..", ".kwwk.", "kwwwwk"], new Dictionary<char, uint> { ['k'] = PixelArt.Rgb(0x111214), ['w'] = PixelArt.Rgb(0xF2F0EA) });
var strip = PixelArt.Strip([frame0Rows, frame1Rows], palette);                 // frames side by side
```

`.` is transparent; every other character must be in the palette and every row must have the same width (otherwise
`ArgumentException`). Max texture size 4096.

## Frames, sheets, animations

```csharp
var hero = SpriteFrame.Whole(tex, 16, 16);
var cell = SpriteFrame.FromPixels(tex, texW, texH, x, y, w, h);
var sheet = HeroArt.Load(context);                         // imported atlas (above): frames + animations, no parsing
var proto = SpriteSheet.Load(context, "sprites/hero");     // prototypes: decodes sprites/hero.png + parses .json at startup
var grid  = SpriteSheet.FromGrid(tex, texW, texH, 16, 16);   // frames "frame_0", "frame_1"… row by row
var run = sheet.Animation("hero_run");                        // SpriteAnimation(Name, Frames, FramesPerSecond, Loop)
SpriteFrame now = run.At(tick.Tick - _runStartedAt);          // ticks since the animation started
bool done = run.IsFinished(ticksSince);                       // non-looping animations
```

Atlas JSON (what `ukiyo_sprite_generate`/`_process`/`_sheet` write):
`{"image":"hero.png","frames":{"hero_run_0":{"x":0,"y":0,"w":16,"h":16}},"animations":{"hero_run":{"frames":["hero_run_0"],"fps":8,"loop":true}}}`.
Extra keys such as `meta` are ignored by the engine.

## Drawing

```csharp
frame.Camera2D = new Camera2D(new Vector2(px, 3f), 10f);                        // center, world units visible vertically
frame.DrawSprite(now, position, new Vector2(1, 1), rotation: 0f, layer: 0, tint: null, flipX: _facingLeft);
frame.DrawScreenSprite(icon, new Vector2(8, 8), new Vector2(32, 32), layer: 10); // top-left, size in pixels
// Full control: texture, space, position, size, pivot (0..1 in image space, y down: (0.5, 1) = feet), rotation,
// uv (u0, v0, u1, v1), linear color, layer
frame.DrawSprite(new SpriteInstance(tex, SpriteSpace.World, pos, size, new Vector2(0.5f, 1f), rotation, SpriteInstance.FullUv, Vector4.One, 5));
```

`DrawSprite(SpriteFrame…)` centers the sprite on `position` (pivot 0.5, 0.5); `DrawScreenSprite` anchors at the top-left.
Rotation is counter-clockwise on screen in both spaces. Convert a click to world: `SpriteGeometry.ScreenToWorld(pixel, camera, viewport)`.

## Pixel-art checklist

- Nearest filter (default), integer sprite sizes in world units per pixel (e.g. 16 px = 1 unit), camera centers that
  do not shimmer (round to 1/16 if needed).
- Keep one atlas per set of things drawn together: consecutive sprites with the same texture batch into one draw call.
- Prove it with `ukiyo_capture` and look at the frame.

New art: see ukiyo-sprite-generation.
