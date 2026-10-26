// Offline provider: a mirrored, seeded sprite generator (the classic "symmetric invader" technique), so the whole
// sprite pipeline works with no network and no key. Results are labeled procedural; they are not model art.
import { encodePng, createImage } from "../png.ts";
import type { ImageProvider } from "./types.ts";

const CELL = 24; // each logical pixel is drawn as a 24×24 block, like a model's soft "pixel art" output
const GRID = 12;

function mulberry32(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function hashText(text: string): number {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
  return h >>> 0;
}

const procedural: ImageProvider = {
  name: "procedural",
  available: () => true,
  async generate({ prompt, seed }) {
    const random = mulberry32(seed ^ hashText(prompt));
    const frames = Number(prompt.match(/exactly (\d+) animation frames/)?.[1] ?? 1);
    const hue = random() * 360;
    const body = hsl(hue, 0.55, 0.55);
    const shade = hsl(hue, 0.5, 0.35);
    const accent = hsl((hue + 150) % 360, 0.7, 0.6);
    const half = GRID / 2;
    const mask: number[][] = [];
    for (let y = 1; y < GRID - 1; y++) {
      mask[y] = [];
      for (let x = 1; x < half; x++) {
        const centerBias = 0.3 + (x / half) * 0.35;
        mask[y]![x] = random() < centerBias ? (random() < 0.15 ? 2 : random() < 0.3 ? 1 : 0) : -1;
      }
    }

    const image = createImage(GRID * CELL * frames, GRID * CELL);
    image.data.fill(255);
    for (let i = 0; i < image.data.length; i += 4) { image.data[i + 1] = 0; } // flat magenta background
    for (let f = 0; f < frames; f++) {
      const bob = f % 2 === 1 ? 1 : 0;
      for (let y = 1; y < GRID - 1; y++) {
        for (let x = 1; x < half; x++) {
          const v = mask[y]![x]!;
          if (v < 0) continue;
          const color = v === 2 ? accent : v === 1 ? shade : body;
          for (const gx of [x, GRID - 1 - x]) paint(image, f * GRID + gx, y + bob, color);
        }
      }
    }
    return { png: encodePng(image), provider: "procedural", model: "mirror-v1" };
  },
};

function paint(image: ReturnType<typeof createImage>, gx: number, gy: number, color: [number, number, number]) {
  if (gy >= GRID) return;
  for (let y = gy * CELL; y < (gy + 1) * CELL; y++) {
    for (let x = gx * CELL; x < (gx + 1) * CELL; x++) {
      const i = (y * image.width + x) * 4;
      image.data[i] = color[0]; image.data[i + 1] = color[1]; image.data[i + 2] = color[2]; image.data[i + 3] = 255;
    }
  }
}

function hsl(h: number, s: number, l: number): [number, number, number] {
  const k = (n: number) => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [Math.round(f(0) * 255), Math.round(f(8) * 255), Math.round(f(4) * 255)];
}

export default procedural;
