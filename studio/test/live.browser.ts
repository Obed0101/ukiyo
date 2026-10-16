// Explicit, destructive-to-game-state acceptance run. Start a fresh LanternRun/Desktop and Studio first.
// UKIYO_STUDIO_URL=http://127.0.0.1:4100 UKIYO_GAME_PID=123 bun studio/test/live.browser.ts
import { chromium } from "playwright";
import { strict as assert } from "node:assert";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const url = process.env.UKIYO_STUDIO_URL;
const pid = Number(process.env.UKIYO_GAME_PID);
assert.ok(url && Number.isInteger(pid) && pid > 0, "explicit Studio URL and fresh game PID required");
const out = resolve("artifacts/g3-live");
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ headless: true, channel: "chrome" });
const composition = readFileSync("studio/studio.json", "utf8");
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const errors: string[] = [];
  page.on("pageerror", e => errors.push(e.message));
  await page.goto(url);
  await page.locator('#layouts button').filter({ hasText: /^complete$/i }).click();
  const session = page.locator(`[data-panel="sessions"] button[data-pid="${pid}"]`);
  await session.waitFor();
  if (await session.getAttribute("aria-pressed") !== "true") await session.click();
  await page.waitForFunction(pid => document.querySelector(`[data-panel="sessions"] button[data-pid="${pid}"]`)?.getAttribute("aria-pressed") === "true", pid);
  async function command(cmd: string, args: Record<string, unknown> = {}) {
    return page.evaluate(async ({ cmd, args, pid }) => {
      const token = document.querySelector('meta[name="ukiyo-studio-token"]')!.getAttribute("content")!;
      const response = await fetch("/api/bridge", { method: "POST", headers: { "content-type": "application/json", "x-ukiyo-token": token }, body: JSON.stringify({ cmd, pid, ...args }) });
      const value = await response.json();
      if (!response.ok || !value.ok) throw new Error(value.error || "bridge failed");
      return value.result;
    }, { cmd, args, pid });
  }
  await command("pause");
  await page.getByRole("button", { name: "Resume the game", exact: true }).waitFor();
  for (const ticks of [1, 10, 60]) {
    const before = (await command("status")).tick;
    const response = page.waitForResponse(r => r.url().endsWith("/api/bridge") && r.request().postDataJSON()?.cmd === "step");
    await page.getByRole("button", { name: `Step ${ticks} tick${ticks === 1 ? "" : "s"}`, exact: true }).click();
    await response;
    assert.equal((await command("status")).tick, before + ticks);
  }
  assert.equal((await command("snapshot")).values.state, 0, "fresh title required");
  assert.equal((await command("status")).backend, "Metal");
  await page.getByRole("button", { name: "Capture the current frame", exact: true }).click();
  const image = page.locator('[data-panel="viewport"] img');
  await image.waitFor();
  await page.getByRole("button", { name: "Record", exact: true }).click();
  await page.getByRole("button", { name: "Stop recording", exact: true }).waitFor();
  await page.getByRole("button", { name: "Play:", exact: false }).click();
  // Canvas is 320x180; START is centered at (160,105). Both image and game may letterbox.
  const position = await image.evaluate(img => {
    const box = img.getBoundingClientRect();
    const w = img.naturalWidth, h = img.naturalHeight;
    const scale = Math.min(box.width / w, box.height / h);
    const ui = Math.min(w / 320, h / 180);
    return { x: box.left + box.width / 2, y: box.top + (box.height - h * scale) / 2 + ((h - 180 * ui) / 2 + 105 * ui) * scale };
  });
  const inputResponse = () => page.waitForResponse(r => r.url().endsWith("/api/bridge") && r.request().postDataJSON()?.cmd === "input");
  await page.mouse.move(position.x, position.y);
  const down = inputResponse();
  await page.mouse.down();
  await down;
  await command("step", { ticks: 1 });
  const up = inputResponse();
  await page.mouse.up();
  await up;
  await command("step", { ticks: 1 });
  assert.equal((await command("snapshot")).values.state, 1, "Studio pointer starts the game");
  const keyDown = inputResponse();
  await page.keyboard.down("ArrowRight");
  await keyDown;
  await command("step", { ticks: 60 });
  const keyUp = inputResponse();
  await page.keyboard.up("ArrowRight");
  await keyUp;
  await command("step", { ticks: 1 });
  assert.ok((await command("snapshot")).entities.player.position[0] > 6, "keyboard input moves the player");
  await page.getByRole("button", { name: "Watch:", exact: false }).click();
  await page.getByRole("button", { name: "Resume the game", exact: true }).click();
  const initial = await image.getAttribute("alt");
  await page.waitForFunction(alt => document.querySelector('[data-panel="viewport"] img')?.getAttribute("alt") !== alt, initial);
  await page.getByRole("button", { name: "Pause the game", exact: true }).click();
  const status = await command("status");
  await page.waitForFunction(tick => document.querySelector('[data-panel="viewport"] img')?.getAttribute("alt")?.endsWith(`tick ${tick}`), status.tick);
  const pausedFrame = await image.getAttribute("src");
  let extraCaptures = 0;
  const countCapture = (request: import("playwright").Request) => {
    if (request.url().endsWith("/api/bridge") && request.postDataJSON()?.cmd === "capture") extraCaptures++;
  };
  page.on("request", countCapture);
  await page.waitForTimeout(800);
  page.off("request", countCapture);
  assert.equal((await command("status")).tick, status.tick);
  assert.equal(await image.getAttribute("src"), pausedFrame);
  assert.equal(extraCaptures, 0, "Watch must not request captures while paused at the captured tick");
  await page.getByRole("button", { name: "Stop recording", exact: true }).click();
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Save input.json", exact: true }).click();
  const input = resolve(out, "input.json");
  await (await download).saveAs(input);
  const snapshot = await command("snapshot");
  await Bun.write(resolve(out, "desktop.json"), JSON.stringify(snapshot, null, 2));
  const run = Bun.spawn([process.env.DOTNET || "dotnet", "run", "--project", "samples/LanternRun/Headless", "--", "--input", input, "--checkpoints", String(status.tick), "--size", `${status.width}x${status.height}`], { stdout: "pipe", stderr: "pipe" });
  const [stdout, stderr, code] = await Promise.all([new Response(run.stdout).text(), new Response(run.stderr).text(), run.exited]);
  assert.equal(code, 0, stderr);
  const replay = JSON.parse(stdout.split("\n").find(line => line.startsWith("snapshot "))!.slice(9));
  for (const key of ["tick", "source", "entities", "values"]) assert.deepEqual(replay[key], snapshot[key], `replay ${key}`);
  await Bun.write(resolve(out, "headless.json"), JSON.stringify(replay, null, 2));
  for (const colorScheme of ["dark", "light"] as const) {
    await page.emulateMedia({ colorScheme });
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      await page.screenshot({ path: resolve(out, `studio-${colorScheme}-${width}.png`), fullPage: true });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "no horizontal overflow");
    }
  }
  assert.deepEqual(errors, []);
  console.log(`PASS: Metal pointer START, keyboard movement, Watch run/pause, downloaded recording → headless exact state; four screenshots; tick ${status.tick}`);
} finally {
  await browser.close();
  writeFileSync("studio/studio.json", composition);
}
