// G1 parity check for any game: compares whole snapshots (every entity's position, rotation and scale, and every
// named value) at the same checkpoints across headless (CPU capture), desktop (Metal) and web (WebGL2), all driven by
// the same input script, the shared-source identity, and the captured pixels wherever two hosts drew the same size.
// Usage: bun scripts/g1-parity.ts <evidence-dir>   (expects headless.log, desktop/*.json, web-dom.html)
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { inflateSync } from "node:zlib";

const TOLERANCE = 1e-5;
// Renderers quantize the same linear math to 8 bits; one step of rounding is not a visible difference.
const PIXEL_TOLERANCE = 1;
const dir = process.argv[2] ?? "artifacts/evidence-g1";

type Pose = { position: number[]; rotation: number[]; scale: number[] };
type Snapshot = { tick: number; backend: string; source: Record<string, string>; entities: Record<string, Pose>; values?: Record<string, number> };

const headless: Snapshot[] = readFileSync(join(dir, "headless.log"), "utf8")
  .split("\n").filter((line) => line.startsWith("snapshot ")).map((line) => JSON.parse(line.slice(9)));
// A missing desktop run (no visible window) is reported as a failure, but headless and web are still compared.
const desktop: Snapshot[] = existsSync(join(dir, "desktop"))
  ? readdirSync(join(dir, "desktop")).filter((f) => f.endsWith(".json")).sort().map((f) => JSON.parse(readFileSync(join(dir, "desktop", f), "utf8")))
  : [];
const dom = readFileSync(join(dir, "web-dom.html"), "utf8");
const match = dom.match(/<script type="application\/json" id="ukiyo-evidence">([\s\S]*?)<\/script>/);
if (!match) throw new Error("[PARITY]: web evidence not found in DOM dump");
mkdirSync(join(dir, "web"), { recursive: true });
const web: Snapshot[] = JSON.parse(match[1]!).captures.map((c: { tick: number; png: string; snapshot: Snapshot }) => {
  const name = `web-tick-${String(c.tick).padStart(6, "0")}`;
  writeFileSync(join(dir, "web", `${name}.png`), Buffer.from(c.png, "base64"));
  writeFileSync(join(dir, "web", `${name}.json`), JSON.stringify(c.snapshot));
  return c.snapshot;
});

/** Largest absolute difference over every number two snapshots share, plus anything only one of them has. */
function compare(a: Snapshot, b: Snapshot): { delta: number; missing: string[] } {
  let delta = 0;
  const missing: string[] = [];
  const names = new Set([...Object.keys(a.entities), ...Object.keys(b.entities)]);
  for (const name of names) {
    const pa = a.entities[name], pb = b.entities[name];
    if (!pa || !pb) { missing.push(`entity ${name}`); continue; }
    for (const key of ["position", "rotation", "scale"] as const) {
      pa[key].forEach((v, i) => { delta = Math.max(delta, Math.abs(v - pb[key][i]!)); });
    }
  }
  const values = new Set([...Object.keys(a.values ?? {}), ...Object.keys(b.values ?? {})]);
  for (const name of values) {
    const va = a.values?.[name], vb = b.values?.[name];
    if (va === undefined || vb === undefined) { missing.push(`value ${name}`); continue; }
    delta = Math.max(delta, Math.abs(va - vb));
  }
  return { delta, missing };
}

const rows: string[] = [];
let failures = 0;
const check = (name: string, ok: boolean, detail: string) => {
  rows.push(`| ${name} | ${ok ? "PASS" : "FAIL"} | ${detail} |`);
  if (!ok) failures++;
};

const hash = (s: Snapshot) => JSON.stringify(s.source);
const others = { desktop, web };
check("desktop evidence present", desktop.length > 0, desktop.length ? `${desktop.length} checkpoints` : "no desktop/*.json (window not visible?)");
check("shared source hashes identical", [desktop[0], web[0]].every((s) => !s || hash(s) === hash(headless[0]!)),
  Object.entries(headless[0]!.source).map(([f, h]) => `${f}=${h.slice(0, 12)}`).join(" "));

for (const tick of headless.map((s) => s.tick)) {
  const h = headless.find((s) => s.tick === tick)!;
  const values = Object.entries(h.values ?? {}).map(([k, v]) => `${k}=${v}`).join(" ");
  for (const [target, snapshots] of Object.entries(others)) {
    if (snapshots.length === 0) continue;
    const s = snapshots.find((x) => x.tick === tick);
    if (!s) { check(`tick ${tick} present on ${target}`, false, "missing"); continue; }
    const c = compare(h, s);
    check(`tick ${tick} snapshot headless≈${target}`, c.delta <= TOLERANCE && !c.missing.length,
      `max|Δ|=${c.delta.toExponential(2)}${c.missing.length ? ` missing: ${c.missing.join(", ")}` : ""} · ${values}`);
  }
}

/** Decodes an 8-bit RGB or RGBA, non-interlaced PNG (what every host writes) to raw rows without the filter bytes. */
function decodePng(path: string): { width: number; height: number; channels: number; pixels: Uint8Array } {
  const file = readFileSync(path);
  const width = file.readUInt32BE(16), height = file.readUInt32BE(20);
  const [depth, colorType, , , interlace] = file.subarray(24, 29);
  if (depth !== 8 || (colorType !== 2 && colorType !== 6) || interlace !== 0) throw new Error(`[PARITY]: unsupported PNG layout in ${path}`);
  const idat: Buffer[] = [];
  for (let at = 8; at < file.length;) {
    const length = file.readUInt32BE(at);
    if (file.toString("latin1", at + 4, at + 8) === "IDAT") idat.push(file.subarray(at + 8, at + 8 + length));
    at += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const channels = colorType === 6 ? 4 : 3, stride = width * channels;
  const pixels = new Uint8Array(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)]!, row = y * stride, source = y * (stride + 1) + 1;
    for (let i = 0; i < stride; i++) {
      const left = i >= channels ? pixels[row + i - channels]! : 0;
      const up = y > 0 ? pixels[row - stride + i]! : 0;
      const corner = y > 0 && i >= channels ? pixels[row - stride + i - channels]! : 0;
      const p = left + up - corner, pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - corner);
      const predicted = [0, left, up, (left + up) >> 1, pa <= pb && pa <= pc ? left : pb <= pc ? up : corner][filter];
      if (predicted === undefined) throw new Error(`[PARITY]: bad PNG filter ${filter} in ${path}`);
      pixels[row + i] = (raw[source + i]! + predicted) & 255;
    }
  }
  return { width, height, channels, pixels };
}

/** Largest per-channel RGB difference between two captures and how many pixels exceed the tolerance. */
function comparePixels(a: string, b: string): { same: boolean; detail: string } {
  const pa = decodePng(a), pb = decodePng(b);
  if (pa.width !== pb.width || pa.height !== pb.height) return { same: false, detail: `${pa.width}x${pa.height} vs ${pb.width}x${pb.height}` };
  let max = 0, over = 0;
  for (let i = 0; i < pa.width * pa.height; i++) {
    let d = 0;
    for (let c = 0; c < 3; c++) d = Math.max(d, Math.abs(pa.pixels[i * pa.channels + c]! - pb.pixels[i * pb.channels + c]!));
    max = Math.max(max, d);
    if (d > PIXEL_TOLERANCE) over++;
  }
  return { same: over === 0, detail: `${pa.width}x${pa.height} max channel Δ=${max}/255, ${over} px over ${PIXEL_TOLERANCE}` };
}

// The CPU renderer and three.js draw the same size (--size and ?fixed=1), so their captures are compared exactly.
// The desktop window is sized by the display's scale factor, so g1.sh renders a second CPU reference at that size.
const png = (folder: string, target: string, tick: number) => join(dir, folder, `${target}-tick-${String(tick).padStart(6, "0")}.png`);
for (const tick of headless.map((s) => s.tick)) {
  for (const [target, reference] of [["web", "headless"], ["desktop", "headless-desktop-size"]] as const) {
    if (!existsSync(png(target, target, tick))) continue;
    if (!existsSync(png(reference, "headless", tick))) { check(`tick ${tick} pixels headless≈${target}`, false, `no CPU reference in ${reference}/`); continue; }
    const result = comparePixels(png(reference, "headless", tick), png(target, target, tick));
    check(`tick ${tick} pixels headless≈${target}`, result.same, result.detail);
  }
}

const moved = new Set(headless.map((s) => JSON.stringify(s.entities))).size > 1;
check("the input script changed the world", moved, `${new Set(headless.map((s) => JSON.stringify(s.entities))).size} distinct entity states over ${headless.length} checkpoints`);
check("backends reported", (desktop[0]?.backend ?? "Metal") === "Metal" && web[0]!.backend.startsWith("WebGL") && headless[0]!.backend === "CPU",
  `headless=${headless[0]!.backend} desktop=${desktop[0]?.backend ?? "not run"} web=${web[0]!.backend}`);

const report = ["| Check | Result | Detail |", "|---|---|---|", ...rows].join("\n");
writeFileSync(join(dir, "parity.md"), `${report}\n`);
console.log(report);
process.exit(failures ? 1 : 0);
