// Pixel detail for the G1Lit parity run: for every checkpoint, compares each target with the CPU reference of the same
// size and splits the differing pixels into silhouette edges and interiors (shading). Edges come from geometry, not
// color: the CPU reference also wrote an edge mask (headless --edge-masks, SurfaceMap in Ukiyo.Render.Software) that
// marks every pixel whose 3x3 neighborhood spans more than one surface (background, another draw, a sprite, a pass, or
// triangles of one mesh that are not neighbors on one smooth sheet). There a GPU's rasterization rules may move the
// boundary by a pixel; everywhere else only lighting decides the color, so lighting math drift shows up in interiors.
// Usage: bun scripts/lit-pixel-report.ts <evidence-dir>   (after scripts/g1-parity.ts wrote web/*.png)
import { existsSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { decodePng, type Image } from "./png.ts";

const dir = process.argv[2] ?? "artifacts/evidence-g1lit";
// SurfaceMap.Edge: the mask is grayscale, 0 = background, 128 = interior of a surface, 255 = edge.
const EDGE = 255;
// Interior shading may differ by one 8-bit step (float rounding next to a quantization boundary), plus what a sample
// position off by up to one subpixel step moves along the surface's own gradient. 1/16 px is the GL ES 3.0 minimum of
// SUBPIXEL_BITS = 4, which WebGL2 inherits. The gradient G is the largest difference between the pixel and its 8
// neighbors in the reference; for an interior pixel they are all on the same smooth surface. So the allowance is
// Δ ≤ 1 + floor(G / 16): exactly 1 wherever the reference changes by less than 16 levels per pixel (flat faces, broad
// light falloff), more only on steep shading such as a curved surface turning away from the view.
const INTERIOR_TOLERANCE = 1;
const SUBPIXEL_STEPS = 16;

const at = (img: Image, x: number, y: number, c: number) => img.pixels[(y * img.width + x) * img.channels + c]!;

function gradient(ref: Image, x: number, y: number): number {
  let g = 0;
  for (let ny = Math.max(0, y - 1); ny <= Math.min(ref.height - 1, y + 1); ny++)
    for (let nx = Math.max(0, x - 1); nx <= Math.min(ref.width - 1, x + 1); nx++)
      for (let c = 0; c < 3; c++) g = Math.max(g, Math.abs(at(ref, nx, ny, c) - at(ref, x, y, c)));
  return g;
}

type Stats = { pixels: number; maskEdge: number; differing: number; maxDelta: number; edge: number; edgeMax: number; interior: number; interiorMax: number; steep: number; over: number; histogram: number[] };

function compare(ref: Image, img: Image, mask: Image): Stats | string {
  if (ref.width !== img.width || ref.height !== img.height) return `${ref.width}x${ref.height} vs ${img.width}x${img.height}`;
  if (mask.width !== ref.width || mask.height !== ref.height) return `mask ${mask.width}x${mask.height} vs ${ref.width}x${ref.height}`;
  const s: Stats = { pixels: ref.width * ref.height, maskEdge: 0, differing: 0, maxDelta: 0, edge: 0, edgeMax: 0, interior: 0, interiorMax: 0, steep: 0, over: 0, histogram: [0, 0, 0, 0] };
  for (let y = 0; y < ref.height; y++) {
    for (let x = 0; x < ref.width; x++) {
      const edge = at(mask, x, y, 0) === EDGE;
      if (edge) s.maskEdge++;
      let d = 0;
      for (let c = 0; c < 3; c++) d = Math.max(d, Math.abs(at(ref, x, y, c) - at(img, x, y, c)));
      s.histogram[Math.min(d, 3)]!++;
      if (d === 0) continue;
      s.differing++;
      s.maxDelta = Math.max(s.maxDelta, d);
      if (edge) { s.edge++; s.edgeMax = Math.max(s.edgeMax, d); continue; }
      s.interior++;
      s.interiorMax = Math.max(s.interiorMax, d);
      if (d <= INTERIOR_TOLERANCE) continue;
      if (d <= INTERIOR_TOLERANCE + Math.floor(gradient(ref, x, y) / SUBPIXEL_STEPS)) s.steep++; else s.over++;
    }
  }
  return s;
}

let failures = 0;
const rows = [
  "| Tick | Pair | Size | Max Δ | Pixels Δ>0 | Mask edge px | Edge px (max Δ) | Interior px (max Δ) | Interior Δ>1 on steep gradients | Interior over tolerance | Δ=0 / 1 / 2 / ≥3 |",
  "|---|---|---|---|---|---|---|---|---|---|---|",
];
// Every CPU capture is compared: whole frames (headless-tick-NNNNNN.png) and, for multi-camera frames, each pass alone
// (headless-tick-NNNNNN-pass-K.png), each with its edge mask (edges-tick-NNNNNN[-pass-K].png in <reference>-edges/).
// A target, reference or mask missing for any of them fails.
const captures = readdirSync(join(dir, "headless"))
  .map((f) => f.match(/^headless-tick-(\d+)(?:-pass-(\d+))?\.png$/))
  .filter((m): m is RegExpMatchArray => m !== null)
  .map((m) => ({ tick: Number(m[1]), pass: m[2] === undefined ? undefined : Number(m[2]) }))
  .sort((a, b) => a.tick - b.tick || (a.pass ?? -1) - (b.pass ?? -1));
const png = (folder: string, prefix: string, c: { tick: number; pass?: number }) =>
  join(dir, folder, `${prefix}-tick-${String(c.tick).padStart(6, "0")}${c.pass === undefined ? "" : `-pass-${c.pass}`}.png`);
// "web-gpu" is the optional real-GPU browser leg (WEB_GPU=1 in the gate scripts, captures written by g1-parity.ts):
// the same build and canvas size as "web", so it shares the CPU reference and its edge masks.
const webGpu = process.env.WEB_GPU === "1" || existsSync(join(dir, "web-gpu"));
const pairs: (readonly [string, string])[] = [["web", "headless"], ...(webGpu ? [["web-gpu", "headless"] as const] : []), ["desktop", "headless-desktop-size"]];
for (const capture of captures) {
  const tick = capture.pass === undefined ? `${capture.tick}` : `${capture.tick} · pass ${capture.pass}`;
  for (const [target, reference] of pairs) {
    const t = png(target, target, capture), r = png(reference, "headless", capture), m = png(`${reference}-edges`, "edges", capture);
    const missing = [t, r, m].filter((p) => !existsSync(p));
    if (missing.length) { failures++; rows.push(`| ${tick} | CPU vs ${target} | – | missing ${missing.join(", ")} | | | | | | | |`); continue; }
    const ref = decodePng(r);
    const s = compare(ref, decodePng(t), decodePng(m));
    if (typeof s === "string") { failures++; rows.push(`| ${tick} | CPU vs ${target} | ${s} | size mismatch | | | | | | | |`); continue; }
    if (s.over > 0) failures++;
    rows.push(`| ${tick} | CPU vs ${target} | ${ref.width}x${ref.height} | ${s.maxDelta} | ${s.differing} of ${s.pixels} | ${s.maskEdge} | ${s.edge} (${s.edgeMax}) | ${s.interior} (${s.interiorMax}) | ${s.steep} | ${s.over} | ${s.histogram.join(" / ")} |`);
  }
}

const report = rows.join("\n");
writeFileSync(join(dir, "lit-pixels.md"), `${report}\n`);
console.log(report);
console.log(failures
  ? `[lit-pixels] FAIL: ${failures} comparison(s) missing or with interior Δ > ${INTERIOR_TOLERANCE} + floor(gradient / ${SUBPIXEL_STEPS})`
  : `[lit-pixels] PASS: interior Δ ≤ ${INTERIOR_TOLERANCE} + floor(gradient / ${SUBPIXEL_STEPS}) everywhere`);
process.exit(failures ? 1 : 0);
