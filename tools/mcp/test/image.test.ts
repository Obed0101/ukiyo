import { describe, expect, test } from "bun:test";
import { packAtlas, pixelize, stripAtlas } from "../src/image/pipeline.ts";
import { downscaleMajority, medianCut, outline, removeBackground } from "../src/image/pixel.ts";
import { createImage, decodePng, encodePng, type Image } from "../src/image/png.ts";
import procedural from "../src/image/providers/procedural.ts";

function fill(image: Image, x0: number, y0: number, w: number, h: number, rgba: [number, number, number, number]) {
  for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) image.data.set(rgba, (y * image.width + x) * 4);
}

const pixel = (image: Image, x: number, y: number) => Array.from(image.data.subarray((y * image.width + x) * 4, (y * image.width + x) * 4 + 4));

describe("png", () => {
  test("encode then decode returns the same RGBA", () => {
    const image = createImage(5, 3);
    for (let i = 0; i < image.data.length; i++) image.data[i] = (i * 37) & 0xff;
    const decoded = decodePng(encodePng(image));
    expect([decoded.width, decoded.height]).toEqual([5, 3]);
    expect(Array.from(decoded.data)).toEqual(Array.from(image.data));
  });

  test("rejects bytes that are not a PNG", () => {
    expect(() => decodePng(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9]))).toThrow("[PNG]");
  });
});

describe("pixel pipeline", () => {
  test("background flood fill keeps interior pixels of the background color", () => {
    const image = createImage(20, 20);
    fill(image, 0, 0, 20, 20, [255, 0, 255, 255]);
    fill(image, 5, 5, 10, 10, [20, 20, 20, 255]);
    fill(image, 9, 9, 2, 2, [255, 0, 255, 255]); // enclosed: must stay
    const clean = removeBackground(image);
    expect(pixel(clean, 0, 0)[3]).toBe(0);
    expect(pixel(clean, 7, 7)[3]).toBe(255);
    expect(pixel(clean, 9, 9)[3]).toBe(255);
  });

  test("majority downscale keeps hard edges", () => {
    const image = createImage(8, 8);
    fill(image, 0, 0, 4, 8, [255, 0, 0, 255]);
    fill(image, 4, 0, 4, 8, [0, 0, 255, 255]);
    const small = downscaleMajority(image, 2, 2);
    expect(pixel(small, 0, 0)).toEqual([255, 0, 0, 255]);
    expect(pixel(small, 1, 1)).toEqual([0, 0, 255, 255]);
  });

  test("median cut finds the distinct colors of a flat image", () => {
    const image = createImage(4, 1);
    fill(image, 0, 0, 2, 1, [10, 200, 10, 255]);
    fill(image, 2, 0, 2, 1, [200, 10, 10, 255]);
    const palette = medianCut(image, 4).map((c) => c.join(","));
    expect(palette.sort()).toEqual(["10,200,10", "200,10,10"]);
  });

  test("outline surrounds opaque pixels and grows the frame by one on each side", () => {
    const image = createImage(1, 1);
    image.data.set([255, 255, 255, 255]);
    const out = outline(image, [0, 0, 0]);
    expect([out.width, out.height]).toEqual([3, 3]);
    expect(pixel(out, 1, 0)).toEqual([0, 0, 0, 255]);
    expect(pixel(out, 0, 0)[3]).toBe(0);
  });

  test("a 3-frame strip becomes three equal frames with one palette and an atlas", () => {
    const strip = createImage(300, 100);
    fill(strip, 0, 0, 300, 100, [255, 0, 255, 255]);
    for (let f = 0; f < 3; f++) fill(strip, f * 100 + 30, 20 + f * 5, 40, 60, [40, 120, 200, 255]);
    const result = pixelize(strip, { frames: 3, size: 16, palette: 4, outline: true, removeBackground: true, tile: false });
    expect([result.frameWidth, result.frameHeight]).toEqual([18, 18]);
    expect(result.strip.width).toBe(54);
    const atlas = stripAtlas({ name: "blob", image: "blob.png", frames: 3, frameWidth: 18, frameHeight: 18, fps: 8, loop: true });
    expect(atlas.frames.blob_2).toEqual({ x: 36, y: 0, w: 18, h: 18 });
    expect(atlas.animations.blob?.frames).toEqual(["blob_0", "blob_1", "blob_2"]);
  });

  test("an image that is all background fails with a clear error", () => {
    const image = createImage(10, 10);
    fill(image, 0, 0, 10, 10, [255, 0, 255, 255]);
    expect(() => pixelize(image, { frames: 1, size: 8, palette: 4, outline: false, removeBackground: true, tile: false })).toThrow("[SPRITE]");
  });
});

describe("atlas packing", () => {
  test("keeps names, avoids overlaps and prefixes clashing frame names", () => {
    const a = createImage(16, 8);
    const b = createImage(8, 8);
    const packed = packAtlas(
      [
        { name: "a", image: a, atlas: { image: "a.png", frames: { f_0: { x: 0, y: 0, w: 8, h: 8 }, f_1: { x: 8, y: 0, w: 8, h: 8 } }, animations: { walk: { frames: ["f_0", "f_1"], fps: 8, loop: true } } } },
        { name: "b", image: b, atlas: { image: "b.png", frames: { f_0: { x: 0, y: 0, w: 8, h: 8 } }, animations: {} } },
      ],
      { maxWidth: 64, padding: 1, image: "all.png" },
    );
    expect(Object.keys(packed.atlas.frames).sort()).toEqual(["a.f_0", "b.f_0", "f_1"]);
    expect(packed.atlas.animations.walk?.frames).toEqual(["a.f_0", "f_1"]);
    const rects = Object.values(packed.atlas.frames);
    for (const r of rects) for (const s of rects) if (r !== s) expect(r.x + r.w <= s.x || s.x + s.w <= r.x || r.y + r.h <= s.y || s.y + s.h <= r.y).toBe(true);
  });
});

describe("procedural provider", () => {
  test("same seed and prompt give the same image; frames come from the prompt", async () => {
    const signal = new AbortController().signal;
    const prompt = "A horizontal sprite strip of exactly 3 animation frames of a beetle";
    const a = await procedural.generate({ prompt, seed: 7, signal });
    const b = await procedural.generate({ prompt, seed: 7, signal });
    expect(Buffer.from(a.png).equals(Buffer.from(b.png))).toBe(true);
    expect(decodePng(a.png).width).toBe(3 * decodePng(a.png).height);
  });
});
