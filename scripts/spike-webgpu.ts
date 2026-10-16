// SPIKE (UKIYO-15): WebGPU browser-path measurements. Not part of g0.sh/g1.sh and not needed by any shipped page.
// Decision record: docs/decisions/webgpu-browser.md.
//
//   bun scripts/spike-webgpu.ts probe   --name <id> [--headed] [--flags "<chrome flags>"]
//   bun scripts/spike-webgpu.ts run     --name <id> --root <published wwwroot> --renderer webgl|webgpu [--headed] [--flags "..."] [--query "..."]
//   bun scripts/spike-webgpu.ts compare <dirA> <dirB> [--out <diff dir>]
//
// probe/run serve files with a local Bun server (no Ukiyo daemon), start a fresh Chrome profile with the given flags,
// wait for the page to POST its report and stop Chrome. `run` derives its page from the sample's own index.html; with
// --renderer webgl|webgpu the import map points the shared host's "./ukiyo-three.js" at spike/timed-adapter.js, which
// loads the shipped adapter or spike/ukiyo-three-webgpu.js behind the same timers, so no shipped file changes. Output: artifacts/spike-webgpu/<id>/ (report.json, chrome.log, web-tick-NNNNNN.png/json).
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { deflateSync, inflateSync } from "node:zlib";

const ROOT = resolve(import.meta.dir, "..");
const ADAPTER = join(ROOT, "web/three-adapter");
const CHROME = process.env.UKIYO_CHROME ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const OUT = join(ROOT, "artifacts/spike-webgpu");
const TIMEOUT_MS = 180_000;

const [command, ...rest] = process.argv.slice(2);
const flag = (name: string) => { const i = rest.indexOf(`--${name}`); return i >= 0 ? rest[i + 1] : undefined; };
const has = (name: string) => rest.includes(`--${name}`);

function spikePage(root: string, renderer: string): string {
  const index = readFileSync(join(root, "index.html"), "utf8");
  const imports: Record<string, string> = { three: "./lib/three/three.module.js" };
  if (renderer === "webgpu") imports["three/webgpu"] = "./lib/three/three.webgpu.js";
  // Both paths go through the same timing wrapper, which loads the shipped adapter or the spike one.
  imports["/lib/ukiyo-three.js"] = `/spike/timed-adapter.js?impl=${renderer}`;
  // Classic script so it runs before any module: collects console warnings/errors and posts the evidence once the
  // shared host publishes it (or the status line reports an error).
  const reporter = `<script>
(() => {
  const logs = [];
  for (const level of ["warn", "error"]) { const original = console[level]; console[level] = (...a) => { logs.push(level + ": " + a.map(String).join(" ").slice(0, 400)); original.apply(console, a); }; }
  const send = (body) => fetch("/report", { method: "POST", body: JSON.stringify({ ...body, logs, userAgent: navigator.userAgent, spike: globalThis.__ukiyoSpike ?? null, timings: globalThis.__ukiyoSpikeTimings ?? null }) });
  addEventListener("error", (e) => send({ error: String(e.message) }));
  addEventListener("unhandledrejection", (e) => send({ error: String(e.reason && e.reason.stack || e.reason) }));
  const timer = setInterval(() => {
    const evidence = document.getElementById("ukiyo-evidence");
    const status = (document.getElementById("status") || {}).textContent || "";
    if (evidence) {
      clearInterval(timer);
      const resources = performance.getEntriesByType("resource").filter((r) => /three|ukiyo|dotnet/.test(r.name)).map((r) => ({ name: r.name.replace(location.origin, ""), decoded: r.decodedBodySize, ms: Math.round(r.duration) }));
      send({ evidence: evidence.textContent, readyMs: Math.round(performance.now()), resources });
    } else if (status.startsWith("error")) { clearInterval(timer); send({ error: status }); }
  }, 20);
})();
</script>`;
  return index
    .replace(/<script type="importmap">[\s\S]*?<\/script>/, `${reporter}\n  <script type="importmap">${JSON.stringify({ imports })}</script>`);
}

async function launch(name: string, page: (path: string) => Response | null, url: string): Promise<{ report: any; dir: string }> {
  const dir = join(OUT, name);
  mkdirSync(dir, { recursive: true });
  let deliver!: (body: string) => void;
  const reported = new Promise<string>((r) => { deliver = r; });
  const server = Bun.serve({
    hostname: "127.0.0.1", port: 0,
    async fetch(request) {
      const path = decodeURIComponent(new URL(request.url).pathname);
      if (request.method === "POST" && path === "/report") { deliver(await request.text()); return new Response("ok"); }
      return page(path) ?? new Response("not found", { status: 404 });
    },
  });
  const headed = has("headed");
  const extra = (flag("flags") ?? "").split(" ").filter(Boolean);
  const args = [
    `--user-data-dir=${join(dir, "profile")}`, "--no-first-run", "--no-default-browser-check", "--disable-extensions",
    "--window-size=1200,800", "--force-device-scale-factor=1", ...(headed ? [] : ["--headless=new"]), ...extra,
    `http://127.0.0.1:${server.port}${url}`,
  ];
  const started = performance.now();
  const chrome = Bun.spawn([CHROME, ...args], { stdout: "ignore", stderr: Bun.file(join(dir, "chrome.log")) });
  let timer: Timer | undefined;
  const timeout = new Promise<string>((r) => { timer = setTimeout(() => r(JSON.stringify({ error: `timeout after ${TIMEOUT_MS} ms` })), TIMEOUT_MS); });
  const body = await Promise.race([reported, timeout]);
  clearTimeout(timer);
  const wallMs = Math.round(performance.now() - started);
  chrome.kill();
  await chrome.exited;
  server.stop(true);
  const report = { name, chrome: CHROME, version: (await new Response(Bun.spawn([CHROME, "--version"]).stdout).text()).trim(), headed, flags: extra, wallMs, ...JSON.parse(body) };
  return { report, dir };
}

const file = (path: string) => (existsSync(path) ? new Response(Bun.file(path)) : null);

if (command === "probe") {
  const name = flag("name") ?? "probe";
  const { report, dir } = await launch(name, (path) => (path.startsWith("/spike/") ? file(join(ADAPTER, path)) : null), "/spike/probe.html");
  writeFileSync(join(dir, "report.json"), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
} else if (command === "run") {
  const name = flag("name") ?? "run";
  const root = resolve(flag("root") ?? "");
  const renderer = flag("renderer") ?? "webgl";
  const query = flag("query") ?? "checkpoints=0,60,180,600&fixed=1";
  const html = spikePage(root, renderer);
  const { report, dir } = await launch(name, (path) => {
    if (path === "/__spike.html") return new Response(html, { headers: { "content-type": "text/html" } });
    if (path === "/lib/three/three.webgpu.js") return file(join(ADAPTER, "node_modules/three/build/three.webgpu.js"));
    if (path.startsWith("/spike/")) return file(join(ADAPTER, path));
    return file(join(root, path));
  }, `/__spike.html?${query}`);
  if (report.evidence) {
    const evidence = JSON.parse(report.evidence);
    for (const c of evidence.captures) {
      const base = `web-tick-${String(c.tick).padStart(6, "0")}`;
      writeFileSync(join(dir, `${base}.png`), Buffer.from(c.png, "base64"));
      writeFileSync(join(dir, `${base}.json`), JSON.stringify(c.snapshot));
    }
    const frames = evidence.pauseCheck.frames;
    report.summary = {
      backend: evidence.captures[0].snapshot.backend,
      renderer: evidence.captures[0].snapshot.renderer,
      checkpoints: evidence.captures.map((c: any) => `${c.tick}:${c.width}x${c.height}`),
      pauseCheck: { tickBefore: evidence.pauseCheck.tickBefore, tickAfter: evidence.pauseCheck.tickAfter, framesIdentical: frames.length === 2 && frames[0].png === frames[1].png },
      bytesSent: evidence.bytesSent,
    };
    delete report.evidence;
  }
  writeFileSync(join(dir, "report.json"), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ...report, resources: undefined }, null, 2));
} else if (command === "compare") {
  compare(rest[0]!, rest[1]!, flag("out"));
} else {
  console.error("usage: bun scripts/spike-webgpu.ts probe|run|compare … (see header)");
  process.exit(2);
}

// Same decoder as scripts/g1-parity.ts (8-bit RGB/RGBA, non-interlaced), duplicated here so the spike stays separate.
function decodePng(path: string) {
  const data = readFileSync(path);
  const width = data.readUInt32BE(16), height = data.readUInt32BE(20);
  const [depth, colorType, , , interlace] = data.subarray(24, 29);
  if (depth !== 8 || (colorType !== 2 && colorType !== 6) || interlace !== 0) throw new Error(`[SPIKE]: unsupported PNG layout in ${path}`);
  const idat: Buffer[] = [];
  for (let at = 8; at < data.length;) {
    const length = data.readUInt32BE(at);
    if (data.toString("latin1", at + 4, at + 8) === "IDAT") idat.push(data.subarray(at + 8, at + 8 + length));
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
      if (predicted === undefined) throw new Error(`[SPIKE]: bad PNG filter ${filter} in ${path}`);
      pixels[row + i] = (raw[source + i]! + predicted) & 255;
    }
  }
  return { width, height, channels, pixels };
}

function encodePng(width: number, height: number, rgb: Uint8Array): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf: Buffer) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255]! ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type: string, body: Buffer) => { const head = Buffer.alloc(8); head.writeUInt32BE(body.length); head.write(type, 4, "latin1"); const tail = Buffer.alloc(4); tail.writeUInt32BE(crc(Buffer.concat([head.subarray(4), body]))); return Buffer.concat([head, body, tail]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(width); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 2;
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) raw.set(rgb.subarray(y * width * 3, (y + 1) * width * 3), y * (width * 3 + 1) + 1);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/** Per tick: max RGB channel delta, pixels over 0 and over 1, bounding box of differences; optional amplified diff PNG. */
function compare(a: string, b: string, out?: string) {
  const ticks = (dir: string) => new Map(readdirSync(dir).filter((f) => /-tick-\d+\.png$/.test(f)).map((f) => [f.match(/tick-(\d+)/)![1]!, join(dir, f)]));
  const ta = ticks(a), tb = ticks(b);
  if (out) mkdirSync(out, { recursive: true });
  console.log("| tick | size | max Δ | px Δ>0 | px Δ>1 | diff bbox |\n|---|---|---|---|---|---|");
  for (const [tick, pathA] of ta) {
    const pathB = tb.get(tick);
    if (!pathB) { console.log(`| ${+tick} | missing in B | | | | |`); continue; }
    const pa = decodePng(pathA), pb = decodePng(pathB);
    if (pa.width !== pb.width || pa.height !== pb.height) { console.log(`| ${+tick} | ${pa.width}x${pa.height} vs ${pb.width}x${pb.height} | | | | |`); continue; }
    let max = 0, over0 = 0, over1 = 0, x0 = Infinity, y0 = Infinity, x1 = -1, y1 = -1;
    const diff = new Uint8Array(pa.width * pa.height * 3);
    for (let i = 0; i < pa.width * pa.height; i++) {
      let d = 0;
      for (let c = 0; c < 3; c++) d = Math.max(d, Math.abs(pa.pixels[i * pa.channels + c]! - pb.pixels[i * pb.channels + c]!));
      if (d > 0) { over0++; const x = i % pa.width, y = Math.floor(i / pa.width); x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
      if (d > 1) over1++;
      max = Math.max(max, d);
      diff.fill(Math.min(255, d * 32), i * 3, i * 3 + 3);
    }
    if (out && over0) writeFileSync(join(out, `diff-tick-${tick}.png`), encodePng(pa.width, pa.height, diff));
    console.log(`| ${+tick} | ${pa.width}x${pa.height} | ${max}/255 | ${over0} | ${over1} | ${over0 ? `${x0},${y0}–${x1},${y1}` : "-"} |`);
  }
}
