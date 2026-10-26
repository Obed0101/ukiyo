// Turns image-model output (large, soft, "pixel-art looking") into real pixel art: remove the flat background, crop to
// the subject, reduce to a palette, downscale by majority vote so every output pixel is one clean color, and add an
// optional outline. Every step is deterministic, so the same input image always gives the same sprite.
import { createImage, type Image } from "./png.ts";

export type Rgb = [number, number, number];

export function hexToRgb(hex: string): Rgb {
  const value = Number.parseInt(hex.replace(/^#/, ""), 16);
  if (!/^#?[0-9a-fA-F]{6}$/.test(hex)) throw new RangeError(`[PIXEL]: bad color ${hex}`);
  return [(value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff];
}

const distance = (a: Rgb, r: number, g: number, b: number) => {
  // Weighted RGB distance ("redmean"): cheap and closer to perception than plain Euclidean.
  const rm = (a[0] + r) / 2;
  const dr = a[0] - r;
  const dg = a[1] - g;
  const db = a[2] - b;
  return (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
};

/** Most common opaque color along the border: the background a model painted (it is asked for flat magenta). */
export function borderColor(image: Image): Rgb {
  const counts = new Map<number, number>();
  const { width, height, data } = image;
  const add = (x: number, y: number) => {
    const i = (y * width + x) * 4;
    if (data[i + 3]! < 128) return;
    const key = ((data[i]! >> 3) << 10) | ((data[i + 1]! >> 3) << 5) | (data[i + 2]! >> 3);
    counts.set(key, (counts.get(key) ?? 0) + 1);
  };
  for (let x = 0; x < width; x++) { add(x, 0); add(x, height - 1); }
  for (let y = 0; y < height; y++) { add(0, y); add(width - 1, y); }
  let best = 0;
  let bestCount = -1;
  for (const [key, count] of counts) if (count > bestCount) { best = key; bestCount = count; }
  return [((best >> 10) & 31) * 8 + 4, ((best >> 5) & 31) * 8 + 4, (best & 31) * 8 + 4];
}

/**
 * Clears the background by flood fill from the border with a color tolerance, so interior pixels that happen to match
 * the background stay. Images that already carry transparency are left as they are.
 */
export function removeBackground(image: Image, tolerance = 48): Image {
  const { width, height, data } = image;
  let transparent = 0;
  for (let i = 3; i < data.length; i += 4) if (data[i]! < 16) transparent++;
  if (transparent > width * height * 0.05) return image;

  const background = borderColor(image);
  const limit = tolerance * tolerance * 9;
  const out = { width, height, data: Uint8Array.from(data) };
  const visited = new Uint8Array(width * height);
  const stack: number[] = [];
  const push = (x: number, y: number) => {
    if (x < 0 || y < 0 || x >= width || y >= height) return;
    const p = y * width + x;
    if (visited[p]) return;
    visited[p] = 1;
    const i = p * 4;
    if (distance(background, data[i]!, data[i + 1]!, data[i + 2]!) <= limit) stack.push(p);
  };
  for (let x = 0; x < width; x++) { push(x, 0); push(x, height - 1); }
  for (let y = 0; y < height; y++) { push(0, y); push(width - 1, y); }
  while (stack.length) {
    const p = stack.pop()!;
    out.data[p * 4 + 3] = 0;
    const x = p % width;
    const y = (p - x) / width;
    push(x + 1, y); push(x - 1, y); push(x, y + 1); push(x, y - 1);
  }
  return out;
}

export type Box = { x: number; y: number; width: number; height: number };

export function contentBounds(image: Image, alphaThreshold = 128): Box | null {
  const { width, height, data } = image;
  let minX = width, minY = height, maxX = -1, maxY = -1;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (data[(y * width + x) * 4 + 3]! >= alphaThreshold) {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
  }
  return maxX < 0 ? null : { x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1 };
}

export function crop(image: Image, box: Box): Image {
  const out = createImage(box.width, box.height);
  for (let y = 0; y < box.height; y++) {
    const from = ((box.y + y) * image.width + box.x) * 4;
    out.data.set(image.data.subarray(from, from + box.width * 4), y * box.width * 4);
  }
  return out;
}

/** Pads to a square (subject centered horizontally, standing on the bottom edge), so frames share a baseline. */
export function padToSquare(image: Image): Image {
  const size = Math.max(image.width, image.height);
  const out = createImage(size, size);
  const ox = Math.floor((size - image.width) / 2);
  const oy = size - image.height;
  for (let y = 0; y < image.height; y++) {
    out.data.set(image.data.subarray(y * image.width * 4, (y + 1) * image.width * 4), ((oy + y) * size + ox) * 4);
  }
  return out;
}

/** Median cut over opaque pixels. Returns up to `count` colors, most populated boxes first. */
export function medianCut(image: Image, count: number): Rgb[] {
  const pixels: Rgb[] = [];
  for (let i = 0; i < image.data.length; i += 4) {
    if (image.data[i + 3]! >= 128) pixels.push([image.data[i]!, image.data[i + 1]!, image.data[i + 2]!]);
  }
  if (pixels.length === 0) return [];
  let boxes: Rgb[][] = [pixels];
  while (boxes.length < count) {
    boxes.sort((a, b) => b.length - a.length);
    const index = boxes.findIndex((box) => box.length > 1 && spread(box).range > 0);
    if (index < 0) break;
    const box = boxes[index]!;
    const { channel } = spread(box);
    box.sort((a, b) => a[channel] - b[channel]);
    const middle = box.length >> 1;
    boxes = [...boxes.slice(0, index), box.slice(0, middle), box.slice(middle), ...boxes.slice(index + 1)];
  }
  return boxes.map((box) => {
    const sum = box.reduce((acc, p) => [acc[0] + p[0], acc[1] + p[1], acc[2] + p[2]] as Rgb, [0, 0, 0] as Rgb);
    return [Math.round(sum[0] / box.length), Math.round(sum[1] / box.length), Math.round(sum[2] / box.length)] as Rgb;
  });
}

function spread(box: Rgb[]): { channel: 0 | 1 | 2; range: number } {
  let best: 0 | 1 | 2 = 0;
  let bestRange = -1;
  for (const channel of [0, 1, 2] as const) {
    let min = 255, max = 0;
    for (const p of box) { min = Math.min(min, p[channel]); max = Math.max(max, p[channel]); }
    if (max - min > bestRange) { bestRange = max - min; best = channel; }
  }
  return { channel: best, range: bestRange };
}

export function nearest(palette: Rgb[], r: number, g: number, b: number): number {
  let best = 0;
  let bestDistance = Number.POSITIVE_INFINITY;
  for (let i = 0; i < palette.length; i++) {
    const d = distance(palette[i]!, r, g, b);
    if (d < bestDistance) { bestDistance = d; best = i; }
  }
  return best;
}

/** Snaps every opaque pixel to the palette; alpha becomes 0 or 255. */
export function quantize(image: Image, palette: Rgb[], alphaThreshold = 128): Image {
  const out = createImage(image.width, image.height);
  for (let i = 0; i < image.data.length; i += 4) {
    if (image.data[i + 3]! < alphaThreshold) continue;
    const c = palette[nearest(palette, image.data[i]!, image.data[i + 1]!, image.data[i + 2]!)]!;
    out.data[i] = c[0]; out.data[i + 1] = c[1]; out.data[i + 2] = c[2]; out.data[i + 3] = 255;
  }
  return out;
}

/**
 * Downscales a quantized image to width×height by majority vote per block: each output pixel takes the most frequent
 * color of its source block (transparent if most of the block is transparent). Keeps edges hard, unlike averaging.
 */
export function downscaleMajority(image: Image, width: number, height: number): Image {
  const out = createImage(width, height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const x0 = Math.floor((x * image.width) / width);
      const x1 = Math.max(x0 + 1, Math.floor(((x + 1) * image.width) / width));
      const y0 = Math.floor((y * image.height) / height);
      const y1 = Math.max(y0 + 1, Math.floor(((y + 1) * image.height) / height));
      const votes = new Map<number, number>();
      let transparent = 0;
      for (let sy = y0; sy < y1; sy++) {
        for (let sx = x0; sx < x1; sx++) {
          const i = (sy * image.width + sx) * 4;
          if (image.data[i + 3]! < 128) { transparent++; continue; }
          const key = (image.data[i]! << 16) | (image.data[i + 1]! << 8) | image.data[i + 2]!;
          votes.set(key, (votes.get(key) ?? 0) + 1);
        }
      }
      let best = -1;
      let bestCount = 0;
      for (const [key, count] of votes) if (count > bestCount || (count === bestCount && key < best)) { best = key; bestCount = count; }
      if (best < 0 || transparent > bestCount * 1.5) continue;
      const o = (y * width + x) * 4;
      out.data[o] = (best >> 16) & 0xff; out.data[o + 1] = (best >> 8) & 0xff; out.data[o + 2] = best & 0xff; out.data[o + 3] = 255;
    }
  }
  return out;
}

/** Adds a 1-pixel outline (4-neighborhood) around opaque pixels, growing the canvas by one pixel on each side. */
export function outline(image: Image, color: Rgb): Image {
  const out = createImage(image.width + 2, image.height + 2);
  const opaque = (x: number, y: number) => x >= 0 && y >= 0 && x < image.width && y < image.height && image.data[(y * image.width + x) * 4 + 3]! >= 128;
  for (let y = 0; y < out.height; y++) {
    for (let x = 0; x < out.width; x++) {
      const sx = x - 1;
      const sy = y - 1;
      const o = (y * out.width + x) * 4;
      if (opaque(sx, sy)) {
        out.data.set(image.data.subarray((sy * image.width + sx) * 4, (sy * image.width + sx) * 4 + 4), o);
      } else if (opaque(sx + 1, sy) || opaque(sx - 1, sy) || opaque(sx, sy + 1) || opaque(sx, sy - 1)) {
        out.data[o] = color[0]; out.data[o + 1] = color[1]; out.data[o + 2] = color[2]; out.data[o + 3] = 255;
      }
    }
  }
  return out;
}

/** Nearest-neighbor enlargement, for previews an agent or a person can actually see. */
export function upscale(image: Image, factor: number): Image {
  const out = createImage(image.width * factor, image.height * factor);
  for (let y = 0; y < out.height; y++) {
    for (let x = 0; x < out.width; x++) {
      const i = (Math.floor(y / factor) * image.width + Math.floor(x / factor)) * 4;
      out.data.set(image.data.subarray(i, i + 4), (y * out.width + x) * 4);
    }
  }
  return out;
}

/** Splits a horizontal strip into `count` equal columns. */
export function splitColumns(image: Image, count: number): Image[] {
  const width = Math.floor(image.width / count);
  return Array.from({ length: count }, (_, i) => crop(image, { x: i * width, y: 0, width, height: image.height }));
}

/** Places equal-size frames left to right in one strip. */
export function joinColumns(frames: Image[]): Image {
  const first = frames[0];
  if (!first) throw new RangeError("[PIXEL]: no frames");
  const out = createImage(first.width * frames.length, first.height);
  frames.forEach((frame, i) => {
    if (frame.width !== first.width || frame.height !== first.height) throw new RangeError("[PIXEL]: frames differ in size");
    for (let y = 0; y < frame.height; y++) {
      out.data.set(frame.data.subarray(y * frame.width * 4, (y + 1) * frame.width * 4), (y * out.width + i * frame.width) * 4);
    }
  });
  return out;
}

export function darkest(palette: Rgb[]): Rgb {
  return palette.reduce((a, b) => (a[0] * 0.299 + a[1] * 0.587 + a[2] * 0.114 <= b[0] * 0.299 + b[1] * 0.587 + b[2] * 0.114 ? a : b));
}
