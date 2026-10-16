// G0.4 parity check. Compares the same checkpoints across headless (NullRenderer), desktop (WgpuRenderer/Metal)
// and web (ThreeJsRenderer/WebGL2): identical shared-source hashes, rotations within tolerance, pause stability.
// Usage: bun scripts/g0-parity.ts <evidence-dir>   (expects headless.log, desktop/*.json, web-dom.html)
// A web-gpu-dom.html (the real-GPU browser leg, WEB_GPU=1) adds the target "web-gpu"; with WEB_GPU=1 it is required.
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const TOLERANCE = 1e-5; // quaternion component tolerance; q and -q describe the same orientation
const dir = process.argv[2] ?? "artifacts/evidence";

type Snapshot = { tick: number; renderer: string; backend: string; source: Record<string, string>; entities: Record<string, { rotation: number[] }> };

const headless: Snapshot[] = readFileSync(join(dir, "headless.log"), "utf8")
  .split("\n").filter((line) => line.startsWith("snapshot ")).map((line) => JSON.parse(line.slice(9)));

const desktop: Snapshot[] = readdirSync(join(dir, "desktop")).filter((f) => f.endsWith(".json")).sort()
  .map((f) => JSON.parse(readFileSync(join(dir, "desktop", f), "utf8")));

type PauseCheck = { tickBefore: number; tickAfter: number; frames: { tick: number; png: string }[] };
type WebEvidence = { device?: string; captures: { tick: number; png: string; snapshot: Snapshot }[]; pauseCheck: PauseCheck };
type WebLeg = { device: string; snapshots: Snapshot[]; pause: PauseCheck };
/** Reads a browser leg's DOM dump and writes its captures to <target>/; undefined when missing or without evidence. */
function readWeb(file: string, target: string): WebLeg | undefined {
  if (!existsSync(join(dir, file))) return undefined;
  const match = readFileSync(join(dir, file), "utf8").match(/<script type="application\/json" id="ukiyo-evidence">([\s\S]*?)<\/script>/);
  if (!match) return undefined;
  const evidence: WebEvidence = JSON.parse(match[1]!);
  mkdirSync(join(dir, target), { recursive: true });
  const snapshots = evidence.captures.map((c) => {
    const name = `${target}-tick-${String(c.tick).padStart(6, "0")}`;
    writeFileSync(join(dir, target, `${name}.png`), Buffer.from(c.png, "base64"));
    writeFileSync(join(dir, target, `${name}.json`), JSON.stringify(c.snapshot));
    return c.snapshot;
  });
  return { device: evidence.device ?? "not reported", snapshots, pause: evidence.pauseCheck };
}
const webLeg = readWeb("web-dom.html", "web");
if (!webLeg) throw new Error("[PARITY]: web evidence not found in DOM dump");
const web = webLeg.snapshots;
// Optional real-GPU browser leg (WEB_GPU=1 in g0.sh): the same build in Chrome on ANGLE Metal, checked like "web".
const webGpuRequired = process.env.WEB_GPU === "1";
const webGpuLeg = readWeb("web-gpu-dom.html", "web-gpu");
/** ANGLE's Metal backend on an Apple GPU; SwiftShader (the CPU implementation) never counts as a real GPU. */
const isAppleGpu = (device: string) => device.includes("Metal Renderer: Apple") && !/swiftshader/i.test(device);

const sameOrientation = (a: number[], b: number[]) => {
  const direct = Math.max(...a.map((v, i) => Math.abs(v - b[i])));
  const flipped = Math.max(...a.map((v, i) => Math.abs(v + b[i])));
  return Math.min(direct, flipped);
};

const rows: string[] = [];
let failures = 0;
const check = (name: string, ok: boolean, detail: string) => {
  rows.push(`| ${name} | ${ok ? "PASS" : "FAIL"} | ${detail} |`);
  if (!ok) failures++;
};

const hash = (s: Snapshot) => JSON.stringify(s.source);
check("shared source hashes identical", hash(headless[0]) === hash(desktop[0]) && hash(desktop[0]) === hash(web[0]),
  Object.entries(web[0].source).map(([f, h]) => `${f}=${h.slice(0, 12)}`).join(" "));

const ticks = (process.env.PARITY_TICKS ?? "0,60,180,600").split(",").map(Number);
for (const tick of ticks) {
  const h = headless.find((s) => s.tick === tick), d = desktop.find((s) => s.tick === tick), w = web.find((s) => s.tick === tick);
  if (!h || !d || !w) { check(`tick ${tick} present in all targets`, false, `headless=${!!h} desktop=${!!d} web=${!!w}`); continue; }
  const dd = sameOrientation(h.entities.cube.rotation, d.entities.cube.rotation);
  const dw = sameOrientation(h.entities.cube.rotation, w.entities.cube.rotation);
  check(`tick ${tick} rotation headless≈desktop≈web`, dd <= TOLERANCE && dw <= TOLERANCE, `max|Δ| desktop=${dd.toExponential(2)} web=${dw.toExponential(2)} (tol ${TOLERANCE})`);
}

// Change propagation: compared with a baseline run, the edited C# must change every target the same way.
const baselineDir = process.env.BASELINE_DIR;
if (baselineDir) {
  const baseline: Snapshot[] = readFileSync(join(baselineDir, "headless.log"), "utf8")
    .split("\n").filter((line) => line.startsWith("snapshot ")).map((line) => JSON.parse(line.slice(9)));
  for (const tick of ticks.filter((t) => t > 0)) {
    const before = baseline.find((s) => s.tick === tick)!, after = web.find((s) => s.tick === tick)!;
    const delta = sameOrientation(before.entities.cube.rotation, after.entities.cube.rotation);
    check(`tick ${tick} differs from baseline after the C# edit`, delta > 1e-3, `max|Δ| vs baseline=${delta.toExponential(2)}`);
  }
  check("shared source hash changed with the edit", hash(baseline[0]) !== hash(web[0]), `Game.cs ${baseline[0].source["Game.cs"].slice(0, 12)} → ${web[0].source["Game.cs"].slice(0, 12)}`);
}

const pauseChecks = (target: string, pause: PauseCheck) => {
  const [first, second] = pause.frames;
  check(`${target} paused: 1.5 s of real time advances no ticks`, pause.tickBefore === pause.tickAfter && first.tick === second.tick,
    `tick ${pause.tickBefore} → ${pause.tickAfter}`);
  check(`${target} paused: two frames 500 ms apart are pixel-identical`, first.png === second.png, `png bytes ${Buffer.from(first.png, "base64").length}`);
};
pauseChecks("web", webLeg.pause);
check("backends reported", desktop[0].backend === "Metal" && web[0].backend.startsWith("WebGL") && headless[0].backend === "None",
  `desktop=${desktop[0].backend} web=${web[0].backend} ("${webLeg.device}") headless=${headless[0].backend}`);

// The real-GPU leg: present, on the Apple GPU, same source, same rotations, paused frames identical.
if (webGpuRequired || webGpuLeg) {
  const gpu = webGpuLeg?.snapshots ?? [];
  check("web-gpu evidence present", gpu.length > 0, webGpuLeg ? `${gpu.length} checkpoints` : "no web-gpu-dom.html (real-GPU leg failed?)");
  check("web-gpu ran on the Apple GPU", isAppleGpu(webGpuLeg?.device ?? "") && (gpu[0]?.backend ?? "").startsWith("WebGL"),
    `backend=${gpu[0]?.backend ?? "none"} device="${webGpuLeg?.device ?? "not run"}"`);
  if (webGpuLeg && gpu.length) {
    check("shared source hashes identical on web-gpu", hash(gpu[0]) === hash(headless[0]), Object.entries(gpu[0].source).map(([f, h]) => `${f}=${h.slice(0, 12)}`).join(" "));
    for (const tick of ticks) {
      const h = headless.find((s) => s.tick === tick), g = gpu.find((s) => s.tick === tick);
      if (!h || !g) { check(`tick ${tick} present on web-gpu`, false, `headless=${!!h} web-gpu=${!!g}`); continue; }
      const dg = sameOrientation(h.entities.cube.rotation, g.entities.cube.rotation);
      check(`tick ${tick} rotation headless≈web-gpu`, dg <= TOLERANCE, `max|Δ|=${dg.toExponential(2)} (tol ${TOLERANCE})`);
    }
    pauseChecks("web-gpu", webGpuLeg.pause);
  }
}

const report = ["| Check | Result | Detail |", "|---|---|---|", ...rows].join("\n");
writeFileSync(join(dir, "parity.md"), report + "\n");
console.log(report);
process.exit(failures ? 1 : 0);
