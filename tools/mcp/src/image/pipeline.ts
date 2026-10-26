// Source image → engine-ready sprite strip + atlas JSON in the exact format SpriteSheet.Parse reads
// (src/Ukiyo.Core/Sprites.cs): {"image","frames":{name:{x,y,w,h}},"animations":{name:{frames,fps,loop}}}.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import {
  contentBounds,
  crop,
  darkest,
  downscaleMajority,
  hexToRgb,
  joinColumns,
  medianCut,
  outline,
  padToSquare,
  quantize,
  removeBackground,
  splitColumns,
  type Box,
  type Rgb,
} from "./pixel.ts";
import { createImage, encodePng, type Image } from "./png.ts";

export type PixelizeOptions = {
  frames: number;
  /** Output frame size in pixels (square), before the optional outline adds 2. */
  size: number;
  /** Palette colors as hex, or a number of colors to extract from the image. */
  palette: string[] | number;
  outline: boolean;
  removeBackground: boolean;
  tile: boolean;
};

export type PixelizeResult = { strip: Image; frameWidth: number; frameHeight: number; palette: Rgb[] };

export function pixelize(source: Image, options: PixelizeOptions): PixelizeResult {
  if (options.tile) {
    const palette = resolvePalette(source, options.palette);
    const tile = downscaleMajority(quantize(source, palette), options.size, options.size);
    return { strip: tile, frameWidth: options.size, frameHeight: options.size, palette };
  }

  const clean = options.removeBackground ? removeBackground(source) : source;
  const columns = splitColumns(clean, options.frames);
  const union = unionBounds(columns);
  if (!union) throw new RangeError("[SPRITE]: nothing left after removing the background — is the subject the same color as the border?");

  // One crop box and one palette for every frame, so the animation does not jitter or shift colors.
  const framed = columns.map((column) => padToSquare(crop(column, union)));
  const palette = resolvePalette(joinColumns(framed), options.palette);
  const ink = darkest(palette);
  const frames = framed.map((frame) => {
    const small = downscaleMajority(quantize(frame, palette), options.size, options.size);
    return options.outline ? outline(small, ink) : small;
  });
  const strip = joinColumns(frames);
  return { strip, frameWidth: frames[0]!.width, frameHeight: frames[0]!.height, palette };
}

function unionBounds(columns: Image[]): Box | null {
  let union: Box | null = null;
  for (const column of columns) {
    const box = contentBounds(column);
    if (!box) continue;
    if (!union) { union = box; continue; }
    const x0 = Math.min(union.x, box.x);
    const y0 = Math.min(union.y, box.y);
    const x1 = Math.max(union.x + union.width, box.x + box.width);
    const y1 = Math.max(union.y + union.height, box.y + box.height);
    union = { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
  }
  return union;
}

function resolvePalette(image: Image, palette: string[] | number): Rgb[] {
  if (Array.isArray(palette)) return palette.map(hexToRgb);
  const extracted = medianCut(image, palette);
  if (extracted.length === 0) throw new RangeError("[SPRITE]: the image has no opaque pixels to take colors from");
  return extracted;
}

export type Atlas = {
  image: string;
  frames: Record<string, { x: number; y: number; w: number; h: number }>;
  animations: Record<string, { frames: string[]; fps: number; loop: boolean }>;
  meta?: Record<string, unknown>;
};

export function stripAtlas(options: { name: string; image: string; frames: number; frameWidth: number; frameHeight: number; fps: number; loop: boolean; meta?: Record<string, unknown> }): Atlas {
  const atlas: Atlas = { image: options.image, frames: {}, animations: {} };
  const names: string[] = [];
  for (let i = 0; i < options.frames; i++) {
    const frameName = `${options.name}_${i}`;
    atlas.frames[frameName] = { x: i * options.frameWidth, y: 0, w: options.frameWidth, h: options.frameHeight };
    names.push(frameName);
  }
  atlas.animations[options.name] = { frames: names, fps: options.fps, loop: options.loop };
  if (options.meta) atlas.meta = options.meta;
  return atlas;
}

export type PackInput = { name: string; image: Image; atlas: Atlas };

/**
 * Shelf-packs several sprites (each with its own atlas) into one texture. Frame and animation names are kept; a name
 * used by two inputs is prefixed with its sprite name so nothing is silently overwritten.
 */
export function packAtlas(inputs: PackInput[], options: { maxWidth: number; padding: number; image: string }): { image: Image; atlas: Atlas } {
  type Rect = { input: PackInput; frame: string; x: number; y: number; w: number; h: number; dx: number; dy: number };
  const rects: Rect[] = inputs.flatMap((input) => Object.entries(input.atlas.frames).map(([frame, r]) => ({ input, frame, x: r.x, y: r.y, w: r.w, h: r.h, dx: 0, dy: 0 })));
  const order = [...rects].sort((a, b) => b.h - a.h || b.w - a.w || a.frame.localeCompare(b.frame));
  let x = options.padding;
  let y = options.padding;
  let shelf = 0;
  let width = 0;
  for (const rect of order) {
    if (rect.w + 2 * options.padding > options.maxWidth) throw new RangeError(`[SPRITE]: frame ${rect.frame} is wider than maxWidth ${options.maxWidth}`);
    if (x + rect.w + options.padding > options.maxWidth) { x = options.padding; y += shelf + options.padding; shelf = 0; }
    rect.dx = x;
    rect.dy = y;
    x += rect.w + options.padding;
    shelf = Math.max(shelf, rect.h);
    width = Math.max(width, x);
  }
  const height = y + shelf + options.padding;
  const image = createImage(Math.max(1, width), Math.max(1, height));
  const seen = new Map<string, number>();
  for (const rect of rects) seen.set(rect.frame, (seen.get(rect.frame) ?? 0) + 1);
  const rename = (input: PackInput, frame: string) => ((seen.get(frame) ?? 0) > 1 ? `${input.name}.${frame}` : frame);

  const atlas: Atlas = { image: options.image, frames: {}, animations: {} };
  for (const rect of rects) {
    for (let row = 0; row < rect.h; row++) {
      const from = ((rect.y + row) * rect.input.image.width + rect.x) * 4;
      image.data.set(rect.input.image.data.subarray(from, from + rect.w * 4), ((rect.dy + row) * image.width + rect.dx) * 4);
    }
    atlas.frames[rename(rect.input, rect.frame)] = { x: rect.dx, y: rect.dy, w: rect.w, h: rect.h };
  }
  for (const input of inputs) {
    for (const [name, animation] of Object.entries(input.atlas.animations)) {
      const key = atlas.animations[name] ? `${input.name}.${name}` : name;
      atlas.animations[key] = { ...animation, frames: animation.frames.map((f) => rename(input, f)) };
    }
  }
  return { image, atlas };
}

export function writeSpriteFiles(pngPath: string, image: Image, atlas: Atlas | null): void {
  mkdirSync(dirname(pngPath), { recursive: true });
  writeFileSync(pngPath, encodePng(image));
  if (atlas) writeFileSync(pngPath.replace(/\.png$/, ".json"), `${JSON.stringify(atlas, null, 2)}\n`);
}
