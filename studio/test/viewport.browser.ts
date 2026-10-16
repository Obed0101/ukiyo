// Run explicitly with `bun studio/test/viewport.browser.ts` when Playwright is installed.
// Real DOM and pointer capture; the bridge is a controlled test double, not live-game evidence.
import { chromium } from "playwright";
import { strict as assert } from "node:assert";
import { resolve } from "node:path";

const root = resolve(import.meta.dir, "..");
const server = Bun.serve({
  hostname: "127.0.0.1", port: 0,
  fetch(request) {
    const path = new URL(request.url).pathname;
    if (path === "/") return new Response('<div id="actions"></div><main></main>', { headers: { "content-type": "text/html" } });
    if (["/panels/viewport.js", "/web/dom.js"].includes(path)) return new Response(Bun.file(root + path));
    return new Response("not found", { status: 404 });
  },
});
const browser = await chromium.launch({ headless: true, channel: process.env.UKIYO_BROWSER_CHANNEL || "chrome" });
try {
  const page = await browser.newPage();
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.port}`);
  await page.evaluate(async () => {
    // @ts-ignore browser imports are served by the fixture server
    const { default: panel } = await import("/panels/viewport.js");
    // @ts-ignore browser imports are served by the fixture server
    const { h } = await import("/web/dom.js");
    const events: unknown[] = [];
    const handlers = new Map();
    const frame = { tick: 10, width: 640, height: 360, backend: "fixture", url: "data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' width='640' height='360'/>", pid: 1 };
    const studio = {
      h, icon: () => h("span"), options: { watchFps: 15 }, actions: document.querySelector("#actions"),
      session: { pid: 1, game: "fixture" }, status: { tick: 10, capture: true }, frame,
      calls: 0, fail: true, events,
      async capture() { this.calls++; if (this.fail) { this.fail = false; throw new Error("transient"); } return frame; },
      async call(_cmd: string, args: { events: unknown[] }) { events.push(...args.events); },
      view() {}, log() {}, on(name: string, handler: unknown) { handlers.set(name, handler); },
    };
    const cleanup = panel.mount(document.querySelector("main"), studio);
    Object.assign(window, { fixture: studio, cleanup });
  });
  await page.getByRole("button", { name: "Watch:", exact: false }).click();
  await page.waitForFunction(() => (window as any).fixture.calls >= 2, undefined, { timeout: 2000 });
  const calls = await page.evaluate(() => (window as any).fixture.calls);
  await page.waitForTimeout(200);
  assert.equal(await page.evaluate(() => (window as any).fixture.calls), calls, "paused tick must not recapture after success");
  await page.getByRole("button", { name: "Play:", exact: false }).click();
  const box = await page.locator("img").boundingBox();
  assert.ok(box);
  await page.mouse.move(box.x + 100, box.y + 100);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width + 30, box.y + 100);
  await page.mouse.up();
  const events = await page.evaluate(() => (window as any).fixture.events);
  assert.ok(events.some((e: any) => e.button === "Left" && e.down === true));
  assert.ok(events.some((e: any) => e.button === "Left" && e.down === false), "drag outside must release the button");
  assert.ok(events.find((e: any) => e.button === "Left" && e.down === false).pointer[0] > 640, "outside release must not click the last inside position");
  await page.mouse.move(box.x + 100, box.y + 100);
  await page.mouse.down();
  await page.locator(".screen").dispatchEvent("pointercancel", { pointerId: 1 });
  await page.mouse.up();
  await page.keyboard.down("ArrowRight");
  await page.keyboard.press("Tab");
  await page.keyboard.up("ArrowRight");
  const released = await page.evaluate(() => (window as any).fixture.events);
  assert.equal(released.filter((e: any) => e.button === "Left" && e.down === false).length, 2, "cancel releases exactly once");
  assert.deepEqual(released.filter((e: any) => e.button === "Left" && e.down === false)[1].pointer, [-1, -1], "cancellation must not activate a game UI button");
  assert.ok(released.some((e: any) => e.key === "ArrowRight" && e.down === false), "Tab releases held keys");
  const watch = page.getByRole("button", { name: "Watch:", exact: false });
  await watch.click();
  await page.evaluate(() => {
    const f = (window as any).fixture;
    f.status.tick = 11;
    f.frame.tick = 11;
    f.pending = [];
    f.capture = function () {
      this.calls++;
      return new Promise(resolve => this.pending.push(() => resolve({ ...this.frame })));
    };
  });
  await watch.click();
  await page.waitForFunction(() => (window as any).fixture.pending.length === 1);
  await watch.click();
  await watch.click();
  await page.waitForFunction(() => (window as any).fixture.pending.length === 2);
  await page.evaluate(() => {
    const f = (window as any).fixture;
    f.pending.splice(0).forEach((resolve: () => void) => resolve());
  });
  await page.waitForTimeout(150);
  await page.evaluate(() => {
    const f = (window as any).fixture;
    f.status.tick = 12;
    f.frame.tick = 12;
  });
  await page.waitForFunction(() => (window as any).fixture.pending.length >= 1);
  await page.waitForTimeout(150);
  assert.equal(await page.evaluate(() => (window as any).fixture.pending.length), 1, "rapid Watch toggles must not leave duplicate loops");
  await page.evaluate(() => (window as any).cleanup());
  const atCleanup = await page.evaluate(() => {
    const f = (window as any).fixture;
    f.pending.splice(0).forEach((resolve: () => void) => resolve());
    f.status.tick = 13;
    return f.calls;
  });
  await page.waitForTimeout(200);
  assert.equal(await page.evaluate(() => (window as any).fixture.calls), atCleanup, "in-flight capture must not restart Watch after unmount");
  assert.deepEqual(errors, []);
  console.log("PASS: Watch retry/deduplication/toggle races/unmount, outside pointer release, cancellation, Tab release; no page errors");
} finally {
  await browser.close();
  server.stop(true);
}
