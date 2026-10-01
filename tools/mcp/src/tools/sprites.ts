import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, join, relative } from "node:path";
import { findGame, insideRoot } from "../engine.ts";
import { fillTemplate, spriteConfig } from "../image/config.ts";
import { packAtlas, pixelize, stripAtlas, writeSpriteFiles, type Atlas, type PackInput } from "../image/pipeline.ts";
import { upscale } from "../image/pixel.ts";
import { decodePng, encodePng, isPng, type Image } from "../image/png.ts";
import { pickProvider } from "../image/providers/index.ts";
import { png, text, ToolInputError, type JsonSchema, type Tool, type ToolContext, type ToolResult } from "../protocol.ts";

const NAME_PATTERN = "^[a-z][a-z0-9_]{0,47}$";

const pixelProperties: Record<string, JsonSchema> = {
  size: { type: "integer", minimum: 4, maximum: 256, default: 16, description: "Frame size in pixels (square). 16 or 32 for characters." },
  palette: { type: "string", default: "auto", description: 'A palette name from config/sprite-prompts.json (ukiyo, pico8, gameboy), comma-separated hex colors ("#111214,#F2F0EA,…"), or "auto" to extract colors from the image.' },
  colors: { type: "integer", minimum: 2, maximum: 64, default: 12, description: "Colors to extract when palette is auto." },
  outline: { type: "boolean", default: true, description: "Add a 1-pixel outline in the darkest palette color (frame grows by 2)." },
  fps: { type: "number", minimum: 0.5, maximum: 60, default: 8 },
  loop: { type: "boolean", default: true },
};

/** Where sprites go: games/<Game>/Shared/assets/sprites, which the build embeds as assets/sprites/<name>.png. */
function spriteDir(root: string, args: Record<string, unknown>): string {
  if (typeof args.game === "string") return join(findGame(root, args.game).dir, "Shared", "assets", "sprites");
  if (typeof args.outDir === "string") return insideRoot(root, args.outDir);
  throw new ToolInputError("pass game (a game name) or outDir (a folder inside the repository)");
}

function resolvePalette(args: Record<string, unknown>): string[] | number {
  const value = String(args.palette);
  if (value === "auto") return Number(args.colors);
  const named = spriteConfig().palettes[value];
  if (named) return named;
  const colors = value.split(",").map((c) => c.trim()).filter(Boolean);
  if (colors.length < 2 || !colors.every((c) => /^#?[0-9a-fA-F]{6}$/.test(c))) throw new ToolInputError(`palette ${value} is not a known name or a list of hex colors`);
  return colors.map((c) => (c.startsWith("#") ? c : `#${c}`));
}

function preview(image: Image, frameHeight: number): ReturnType<typeof png> {
  return png(encodePng(upscale(image, Math.max(1, Math.min(16, Math.floor(256 / frameHeight))))));
}

function usage(name: string, assetPath: string, frames: number): string {
  const sheet = assetPath.replace(/\.png$/, ""); // GameAssets paths are relative to Shared/assets
  return frames > 1
    ? `In C#: var sheet = SpriteSheet.Load(context, "${sheet}"); var anim = sheet.Animation("${name}"); … frame.DrawSprite(anim.At(tick.Tick), position, size);`
    : `In C#: var sheet = SpriteSheet.Load(context, "${sheet}"); frame.DrawSprite(sheet.Frame("${name}_0"), position, size);`;
}

function finish(context: ToolContext, options: { name: string; dir: string; source: Image; tile: boolean; frames: number; args: Record<string, unknown>; meta: Record<string, unknown> }): ToolResult {
  const { name, dir, args } = options;
  const result = pixelize(options.source, {
    frames: options.tile ? 1 : options.frames,
    size: Number(args.size),
    palette: resolvePalette(args),
    outline: !options.tile && args.outline === true,
    removeBackground: !options.tile,
    tile: options.tile,
  });
  const pngPath = join(dir, `${name}.png`);
  const atlas = stripAtlas({
    name,
    image: `${name}.png`,
    frames: options.tile ? 1 : options.frames,
    frameWidth: result.frameWidth,
    frameHeight: result.frameHeight,
    fps: Number(args.fps),
    loop: args.loop === true,
    meta: { ...options.meta, palette: result.palette.map((c) => `#${c.map((v) => v.toString(16).padStart(2, "0")).join("")}`) },
  });
  writeSpriteFiles(pngPath, result.strip, atlas);
  const rel = relative(context.root, pngPath);
  const assetIndex = rel.indexOf("/assets/");
  const assetPath = assetIndex >= 0 ? rel.slice(assetIndex + "/assets/".length) : basename(pngPath);
  context.log("info", "sprite.written", { name, path: rel, frames: options.frames, size: result.frameWidth });
  return {
    content: [
      text(`${rel} (${result.strip.width}x${result.strip.height}, ${options.tile ? 1 : options.frames} frame(s) of ${result.frameWidth}x${result.frameHeight}, ${result.palette.length} colors) + ${rel.replace(/\.png$/, ".json")}\n${usage(name, assetPath, options.frames)}\nPreview below is enlarged with nearest-neighbor; check it before using the sprite in a game.`),
      preview(result.strip, result.frameHeight),
    ],
    structuredContent: { png: rel, atlas: rel.replace(/\.png$/, ".json"), frameWidth: result.frameWidth, frameHeight: result.frameHeight, frames: options.frames, palette: atlas.meta?.palette, ...options.meta },
  };
}

const generate: Tool = {
  name: "ukiyo_sprite_generate",
  title: "Generate a sprite",
  description:
    "Generates game art with an image model and turns it into real pixel art the engine loads: flat-background prompt → background removal → crop → palette → majority downscale → outline → PNG strip + atlas JSON in the game's Shared/assets/sprites. kind=single (one sprite), strip (an animation of N frames from one image, so frames stay consistent) or tile (a tileable square). Providers: openai (OPENAI_API_KEY), gemini (GEMINI_API_KEY), procedural (offline, seeded, no model). The raw model image is kept in .ukiyo/sprites for provenance; the atlas records provider, model and prompt.",
  inputSchema: {
    type: "object",
    required: ["name", "subject"],
    additionalProperties: false,
    properties: {
      name: { type: "string", pattern: NAME_PATTERN, description: "Asset and animation name, e.g. hero_run." },
      subject: { type: "string", minLength: 3, maxLength: 600, description: "What to draw, e.g. 'a small fox ronin with a straw hat'." },
      kind: { type: "string", enum: ["single", "strip", "tile"], default: "single" },
      frames: { type: "integer", minimum: 1, maximum: 12, default: 4, description: "Frames for kind=strip." },
      action: { type: "string", maxLength: 200, default: "walking", description: "What the strip animates, e.g. 'running', 'idle breathing'." },
      style: { type: "string", default: "ukiyo", description: "A style name from config/sprite-prompts.json or free text." },
      provider: { type: "string", enum: ["auto", "openai", "gemini", "procedural"], default: "auto" },
      seed: { type: "integer", minimum: 0, maximum: 2_147_483_647, default: 1 },
      game: { type: "string" },
      outDir: { type: "string" },
      ...pixelProperties,
    },
  },
  async run(args, context) {
    const name = String(args.name);
    const dir = spriteDir(context.root, args);
    const config = spriteConfig();
    const kind = args.kind as "single" | "strip" | "tile";
    const frames = kind === "strip" ? Number(args.frames) : 1;
    const style = config.styles[String(args.style)] ?? String(args.style);
    const prompt = fillTemplate(config.templates[kind], { subject: String(args.subject), frames: String(frames), action: String(args.action), style });
    const provider = await pickProvider(String(args.provider));
    context.log("info", "sprite.generate", { name, provider: provider.name, kind, frames });

    const generated = await provider.generate({ prompt, seed: Number(args.seed), signal: context.signal });
    if (!isPng(generated.png)) throw new Error(`[SPRITE]: ${generated.provider} returned an image that is not PNG`);
    const rawDir = join(context.root, ".ukiyo", "sprites");
    mkdirSync(rawDir, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    const rawPath = join(rawDir, `${name}-${stamp}.png`);
    writeFileSync(rawPath, generated.png);

    return finish(context, {
      name,
      dir,
      source: decodePng(generated.png),
      tile: kind === "tile",
      frames,
      args,
      meta: {
        generatedBy: generated.provider,
        model: generated.model,
        prompt,
        revisedPrompt: generated.revisedPrompt,
        seed: generated.provider === "procedural" ? args.seed : undefined,
        raw: relative(context.root, rawPath),
        createdAt: new Date().toISOString(),
      },
    });
  },
};

const processSprite: Tool = {
  name: "ukiyo_sprite_process",
  title: "Pixelize an image",
  description:
    "Runs the same pixel-art pipeline on an existing PNG inside the repository (concept art, a downloaded CC0 asset, an earlier raw generation in .ukiyo/sprites): background removal, palette, downscale, outline, optional split of a horizontal strip into frames. Writes PNG + atlas JSON.",
  inputSchema: {
    type: "object",
    required: ["input", "name"],
    additionalProperties: false,
    properties: {
      input: { type: "string", description: "PNG path inside the repository." },
      name: { type: "string", pattern: NAME_PATTERN },
      frames: { type: "integer", minimum: 1, maximum: 64, default: 1, description: "Equal-width frames laid out left to right in the input." },
      tile: { type: "boolean", default: false, description: "Treat the input as a full-bleed tile (no background removal or crop)." },
      game: { type: "string" },
      outDir: { type: "string" },
      ...pixelProperties,
    },
  },
  async run(args, context) {
    const input = insideRoot(context.root, String(args.input));
    if (!existsSync(input)) throw new ToolInputError(`${args.input} does not exist`);
    const bytes = new Uint8Array(readFileSync(input));
    if (!isPng(bytes)) throw new ToolInputError(`${args.input} is not a PNG`);
    return finish(context, {
      name: String(args.name),
      dir: spriteDir(context.root, args),
      source: decodePng(bytes),
      tile: args.tile === true,
      frames: Number(args.frames),
      args,
      meta: { processedFrom: relative(context.root, input), createdAt: new Date().toISOString() },
    });
  },
};

const sheet: Tool = {
  name: "ukiyo_sprite_sheet",
  title: "Pack a sprite atlas",
  description:
    "Packs several sprites (PNG + atlas JSON pairs, or plain PNGs treated as one frame) into a single texture and atlas JSON, keeping frame and animation names. One texture means one draw batch per atlas in every renderer.",
  inputSchema: {
    type: "object",
    required: ["inputs", "name"],
    additionalProperties: false,
    properties: {
      inputs: { type: "array", items: { type: "string" }, minItems: 1, maxItems: 256, description: "PNG paths inside the repository." },
      name: { type: "string", pattern: NAME_PATTERN },
      maxWidth: { type: "integer", minimum: 16, maximum: 4096, default: 512 },
      padding: { type: "integer", minimum: 0, maximum: 8, default: 1 },
      game: { type: "string" },
      outDir: { type: "string" },
    },
  },
  async run(args, context) {
    const inputs: PackInput[] = (args.inputs as string[]).map((path) => {
      const absolute = insideRoot(context.root, path);
      const image = decodePng(new Uint8Array(readFileSync(absolute)));
      const jsonPath = absolute.replace(/\.png$/, ".json");
      const name = basename(absolute, ".png");
      const atlas: Atlas = existsSync(jsonPath)
        ? (JSON.parse(readFileSync(jsonPath, "utf8")) as Atlas)
        : { image: basename(absolute), frames: { [name]: { x: 0, y: 0, w: image.width, h: image.height } }, animations: {} };
      return { name, image, atlas };
    });
    const name = String(args.name);
    const packed = packAtlas(inputs, { maxWidth: Number(args.maxWidth), padding: Number(args.padding), image: `${name}.png` });
    packed.atlas.meta = { packedFrom: (args.inputs as string[]).map((p) => relative(context.root, insideRoot(context.root, p))), createdAt: new Date().toISOString() };
    const pngPath = join(spriteDir(context.root, args), `${name}.png`);
    writeSpriteFiles(pngPath, packed.image, packed.atlas);
    const rel = relative(context.root, pngPath);
    return {
      content: [
        text(`${rel} (${packed.image.width}x${packed.image.height}, ${Object.keys(packed.atlas.frames).length} frames, ${Object.keys(packed.atlas.animations).length} animations)`),
        preview(packed.image, Math.max(...Object.values(packed.atlas.frames).map((f) => f.h))),
      ],
      structuredContent: { png: rel, atlas: rel.replace(/\.png$/, ".json"), frames: Object.keys(packed.atlas.frames), animations: Object.keys(packed.atlas.animations) },
    };
  },
};

export default [generate, processSprite, sheet];
