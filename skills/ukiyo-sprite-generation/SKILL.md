---
name: ukiyo-sprite-generation
description: Use when a game needs new 2D art — characters, animation strips, tiles, items — generated with an image model (OpenAI gpt-image, Gemini image) or procedurally, then cleaned into real pixel art and wired into the game.
---

# Generating sprites

The MCP tools turn image-model output into engine-ready pixel art. Models draw soft, oversized "pixel-art looking"
images; the pipeline makes them real: flat background removed by flood fill from the border, one crop box for all
frames, one palette, majority-vote downscale (every output pixel is one clean color), optional 1-px outline.

## Tools

| Tool | Use |
|---|---|
| `ukiyo_sprite_generate` | prompt → model → pixel art. `kind`: `single`, `strip` (N animation frames in one image, so the character stays consistent), `tile` (seamless square, no background removal). |
| `ukiyo_sprite_process` | the same pipeline on a PNG already in the repo (concept art, a CC0 download, a raw generation from `.ukiyo/sprites`). |
| `ukiyo_sprite_sheet` | pack several sprites into one atlas (one texture = one draw batch). |

Every tool writes `games/<Game>/Shared/assets/sprites/<name>.png` + `<name>.json` (pass `game`, or `outDir`) and
returns an enlarged preview. Look at the preview before using the sprite.

## Providers

- `openai` — `OPENAI_API_KEY`, model `gpt-image-2` by default (`UKIYO_OPENAI_IMAGE_MODEL` overrides).
- `gemini` — `GEMINI_API_KEY`, model `gemini-2.5-flash-image` by default (`UKIYO_GEMINI_IMAGE_MODEL` overrides).
- `procedural` — offline, seeded, mirrored creature generator. No model, no key. Good for prototypes and tests.
- `auto` (default) — openai, then gemini, then procedural.

Keys come from the environment only (load them from your secret manager); they are never written or logged.
Prompts, styles (`ukiyo`, `pixel`, `gameboy`, `nes`) and palettes (`ukiyo`, `pico8`, `gameboy`) live in
`tools/mcp/config/sprite-prompts.json`; edit that file to change house style.

## Recipe

```jsonc
// 1. A 4-frame run cycle, 16 px, in the game's palette
ukiyo_sprite_generate {"game":"LanternRun","name":"fox_run","subject":"a small fox ronin with a straw hat","kind":"strip","frames":4,"action":"running","palette":"ukiyo","size":16}
// 2. Idle and a tile
ukiyo_sprite_generate {"game":"LanternRun","name":"fox_idle","subject":"…same fox…","kind":"strip","frames":2,"action":"idle breathing","palette":"ukiyo"}
ukiyo_sprite_generate {"game":"LanternRun","name":"stone","subject":"mossy temple stone bricks","kind":"tile","palette":"ukiyo","size":16}
// 3. One atlas for the character
ukiyo_sprite_sheet {"game":"LanternRun","name":"fox","inputs":["games/LanternRun/Shared/assets/sprites/fox_run.png","games/LanternRun/Shared/assets/sprites/fox_idle.png"]}
```

```csharp
// Initialize
_fox = SpriteSheet.Load(context, "sprites/fox");
_run = _fox.Animation("fox_run");
// Extract (_tick saved in Update)
frame.DrawSprite(_run.At(_tick - _runStart), _player.Position, new Vector2(1, 1), flipX: _facingLeft);
```

Then `ukiyo_capture` to see it in the game.

## Getting good results

- Describe one subject, side view, and keep the description identical between strips of the same character.
- Strips: 2–6 frames work best. If frames come out uneven, regenerate with another `seed`/wording, or fix a frame by hand
  and run `ukiyo_sprite_process` on the edited raw image (`frames` = count).
- If background removal eats the subject, the subject touches the border or matches the background color: ask for
  more margin, or process a cleaned image.
- Use a fixed palette across a game; `auto` palettes drift between assets.

## Honesty

The atlas `meta` records provider, model, prompt and the raw image path in `.ukiyo/sprites/`. Say which art is
generated. Do not present generated or procedural art as hand-drawn, and check license terms of the provider you use.
