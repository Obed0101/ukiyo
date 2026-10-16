// Pixel detail for the G1Lit parity run: for every checkpoint, compares each target with the CPU reference of the same
// size and splits the differing pixels into silhouette edges (the reference changes sharply around the pixel, so a
// one-pixel rasterization difference shows up) and interiors (shading). Lighting math drift shows up in interiors.
// Usage: bun scripts/lit-pixel-report.ts <evidence-dir>   (after scripts/g1-parity.ts wrote web/*.png)
import { existsSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { decodePng, type Image } from "./png.ts";

const dir = process.argv[2] ?? "artifacts/evidence-g1lit";
// A pixel is an edge pixel when some 8-neighbor in the reference differs from it by more than this (0..255 per channel).
const EDGE_CONTRAST = 24;

const at = (img: Image, x: number, y: number, c: number) => img.pixels[(y * img.width + x) * img.channels + c]!;

function isEdge(img: Image, x: number, y: number): boolean {
  for (let dy = -1; dy <= 1; dy++) {
    for (let dx = -1; dx <= 1; dx++) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= img.width || ny >= img.height) continue;
      for (let c = 0; c < 3; c++) if (Math.abs(at(img, nx, ny, c) - at(img, x, y, c)) > EDGE_CONTRAST) return true;
    }
  }
  return false;
}

type Stats = { pixels: number; differing: number; maxDelta: number; edge: number; edgeMax: number; interior: number; interiorMax: number; histogram: number[] };

function compare(referencePath: string, targetPath: string): Stats | string {
  const ref = decodePng(referencePath), img = decodePng(targetPath);
  if (ref.width !== img.width || ref.height !== img.height) return `${ref.width}x${ref.height} vs ${img.width}x${img.height}`;
  const s: Stats = { pixels: ref.width * ref.height, differing: 0, maxDelta: 0, edge: 0, edgeMax: 0, interior: 0, interiorMax: 0, histogram: [0, 0, 0, 0] };
  for (let y = 0; y < ref.height; y++) {
    for (let x = 0; x < ref.width; x++) {
      let d = 0;
      for (let c = 0; c < 3; c++) d = Math.max(d, Math.abs(at(ref, x, y, c) - at(img, x, y, c)));
      s.histogram[Math.min(d, 3)]!++;
      if (d === 0) continue;
      s.differing++;
      s.maxDelta = Math.max(s.maxDelta, d);
      if (isEdge(ref, x, y)) { s.edge++; s.edgeMax = Math.max(s.edgeMax, d); } else { s.interior++; s.interiorMax = Math.max(s.interiorMax, d); }
    }
  }
  return s;
}

const rows = ["| Tick | Pair | Size | Max Δ | Pixels Δ>0 | Edge px (max Δ) | Interior px (max Δ) | Δ=0 / 1 / 2 / ≥3 |", "|---|---|---|---|---|---|---|---|"];
const ticks = readdirSync(join(dir, "headless")).filter((f) => f.endsWith(".png")).map((f) => Number(f.match(/(\d+)\.png$/)![1])).sort((a, b) => a - b);
const png = (folder: string, target: string, tick: number) => join(dir, folder, `${target}-tick-${String(tick).padStart(6, "0")}.png`);
for (const tick of ticks) {
  for (const [target, reference] of [["web", "headless"], ["desktop", "headless-desktop-size"]] as const) {
    const t = png(target, target, tick), r = png(reference, "headless", tick);
    if (!existsSync(t) || !existsSync(r)) { rows.push(`| ${tick} | CPU vs ${target} | – | missing | | | | |`); continue; }
    const s = compare(r, t);
    if (typeof s === "string") { rows.push(`| ${tick} | CPU vs ${target} | ${s} | size mismatch | | | | |`); continue; }
    const size = `${decodePng(r).width}x${decodePng(r).height}`;
    rows.push(`| ${tick} | CPU vs ${target} | ${size} | ${s.maxDelta} | ${s.differing} of ${s.pixels} | ${s.edge} (${s.edgeMax}) | ${s.interior} (${s.interiorMax}) | ${s.histogram.join(" / ")} |`);
  }
}

const report = rows.join("\n");
writeFileSync(join(dir, "lit-pixels.md"), `${report}\n`);
console.log(report);
