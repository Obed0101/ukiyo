// Explicit acceptance run: runs LanternRun from the Studio, plays it, reloads it and checks the tick came back.
// Start a Studio on a free port first (it must not be the owner's):
//   UKIYO_STUDIO_PORT=0 bun studio/src/server.ts      (prints the URL)
//   UKIYO_STUDIO_URL=http://127.0.0.1:PORT bun studio/test/run.browser.ts
// It starts and stops its own game; it touches no other session.
import { chromium } from "playwright";
import { strict as assert } from "node:assert";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const url = process.env.UKIYO_STUDIO_URL;
assert.ok(url, "explicit Studio URL required");
const out = resolve("artifacts/g3-run");
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ headless: true, channel: "chrome" });
const composition = readFileSync("studio/studio.json", "utf8");
let stopGame: (() => Promise<void>) | null = null;
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto(`${url}#/p/LanternRun`);
  const panel = page.locator("#topbar");

  async function api(path: string, body?: unknown) {
    return page.evaluate(async ({ path, body }) => {
      const token = document.querySelector('meta[name="ukiyo-studio-token"]')!.getAttribute("content")!;
      const response = await fetch(path, {
        method: body === undefined ? "GET" : "POST",
        headers: { "x-ukiyo-token": token, ...(body === undefined ? {} : { "content-type": "application/json" }) },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      return response.json();
    }, { path, body });
  }
  const command = async (cmd: string, args: Record<string, unknown> = {}) => {
    const value = await api("/api/bridge", { cmd, ...args });
    if (!value.ok) throw new Error(value.error || "bridge failed");
    return value.result;
  };

  // 1. Run it from the Studio and wait for it to attach (the first build can take a while).
  await panel.getByRole("button", { name: "Run the game" }).click();
  stopGame = async () => { await api("/api/run/stop", { game: "sample/LanternRun" }); };
  await page.waitForSelector('#topbar .state[data-kind="attached"]', { timeout: 240_000 });
  const first = (await api("/api/games")).runs.at(-1);
  assert.equal(first.state, "running");
  await panel.screenshot({ path: `${out}/running.png` });

  // 2. Play: pause, press START on the title with the keyboard, move right, so there is real state and input to replay.
  await command("pause");
  await command("step", { ticks: 30 });
  await command("input", { events: [{ pointer: [160, 105] }] });
  await command("step", { ticks: 2 });
  await command("input", { events: [{ key: "Enter", down: true }] });
  await command("step", { ticks: 1 });
  await command("input", { events: [{ key: "Enter", down: false }] });
  await command("step", { ticks: 2 });
  await command("input", { events: [{ key: "ArrowRight", down: true }] });
  await command("step", { ticks: 90 });
  await command("input", { events: [{ key: "ArrowLeft", down: true }, { key: "ArrowRight", down: false }] });
  await command("step", { ticks: 37 });
  const before = { status: await command("status"), snapshot: await command("snapshot") };
  assert.ok(before.snapshot.values.state === 1, `the game was started (state ${before.snapshot.values.state})`);
  assert.ok(before.status.tick > 150);
  writeFileSync(`${out}/before.json`, JSON.stringify(before.snapshot, null, 2));

  // 3. Reload from the panel.
  await panel.getByRole("button", { name: "Reload at the same tick" }).click();
  await page.waitForFunction(async () => {
    const token = document.querySelector('meta[name="ukiyo-studio-token"]')!.getAttribute("content")!;
    const listing = await (await fetch("/api/games", { headers: { "x-ukiyo-token": token } })).json();
    return listing.runs.at(-1)?.reloads > 0 && listing.runs.at(-1)?.state === "running";
  }, null, { timeout: 240_000, polling: 1000 });
  const run = (await api("/api/games")).runs.at(-1);
  const after = { status: await command("status"), snapshot: await command("snapshot") };
  writeFileSync(`${out}/after.json`, JSON.stringify(after.snapshot, null, 2));
  await panel.screenshot({ path: `${out}/reloaded-panel.png` });
  await page.screenshot({ path: `${out}/reloaded.png` });

  assert.notEqual(run.pid, first.pid, "a new process");
  assert.equal(after.status.tick, before.status.tick, "the same tick");
  assert.equal(after.status.paused, true, "paused, as it was");
  const { source: _a, ...b } = before.snapshot;
  const { source: _b, ...a } = after.snapshot;
  assert.deepEqual(a, b, "the restored state is the old state");
  assert.ok(run.reload.ok && run.reload.match, `reload report: ${JSON.stringify(run.reload)}`);
  console.log(`reload ok: tick ${before.status.tick}, pid ${first.pid} → ${run.pid}, ${run.reload.ms} ms, match=${run.reload.match}`);
  assert.deepEqual(errors, []);
} finally {
  await stopGame?.().catch(() => undefined);
  writeFileSync("studio/studio.json", composition);
  await browser.close();
}
