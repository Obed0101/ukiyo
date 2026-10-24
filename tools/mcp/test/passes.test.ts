// Multi-camera passes in the tools (docs/rendering-passes.md): the headless `capture-pass` lines, `passes` in
// ukiyo_capture and its --out copies, and ukiyo_live capture with `pass`. The stdout fixtures are real headless output
// (samples/Minimap: two cameras; samples/LitCube: one) with the frame directory replaced by <frames>.
import { afterAll, describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { copyOutputs, help } from "../src/cli.ts";
import { parseArgs } from "../src/cli.ts";
import { parseHeadless } from "../src/engine.ts";
import { validate } from "../src/protocol.ts";
import capture from "../src/tools/capture.ts";
import live, { bridgeFailure, liveRequest, reload } from "../src/tools/live.ts";
import { headlessSummary } from "../src/tools/run.ts";

const scratch = realpathSync(mkdtempSync(join(tmpdir(), "ukiyo-passes-")));
afterAll(() => rmSync(scratch, { recursive: true, force: true }));

const fixture = (name: string, frames: string) => readFileSync(join(import.meta.dir, "fixtures", name), "utf8").replaceAll("<frames>", frames);
const minimap = (frames = "/frames") => parseHeadless(fixture("minimap-capture-stdout.txt", frames));
const litCube = (frames = "/frames") => parseHeadless(fixture("litcube-capture-stdout.txt", frames));
const process0 = { code: 0, timedOut: false, ms: 1 };

describe("capture-pass lines from the headless host", () => {
  test("Minimap (two cameras): each checkpoint lists its composed frame and both passes, pass 0 primary", () => {
    const output = minimap();
    expect(output.captures).toEqual(["/frames/headless-tick-000000.png", "/frames/headless-tick-000060.png"]);
    expect(output.passes).toEqual([0, 60].map((tick) => {
      const stem = `/frames/headless-tick-${String(tick).padStart(6, "0")}`;
      return {
        tick,
        frame: `${stem}.png`,
        passes: [
          { pass: 0, primary: true, path: `${stem}-pass-0.png` },
          { pass: 1, primary: false, path: `${stem}-pass-1.png` },
        ],
      };
    }));
    expect(output.snapshots.map((s) => s.tick)).toEqual([0, 60]);
    // The pass lines are protocol, not engine info.
    expect(output.info.every((line) => line.startsWith("[ukiyo]"))).toBe(true);
  });

  test("LitCube (one camera): no capture-pass lines, so no passes", () => {
    const output = litCube();
    expect(output.captures).toHaveLength(2);
    expect(output.passes).toEqual([]);
  });

  test("paths with spaces; repeated checkpoints stay separate; a pass line before any capture is ignored", () => {
    const output = parseHeadless([
      "capture-pass 0 primary=true /early/x.png",
      'snapshot {"tick":5}',
      "capture /my frames/headless-tick-000005.png",
      "capture-pass 0 primary=false /my frames/headless-tick-000005-pass-0.png",
      "capture-pass 1 primary=true /my frames/headless-tick-000005-pass-1.png",
      'snapshot {"tick":5}',
      "capture /my frames/headless-tick-000005.png",
      "capture-pass 0 primary=false /my frames/headless-tick-000005-pass-0.png",
    ].join("\n"));
    expect(output.passes).toEqual([
      { tick: 5, frame: "/my frames/headless-tick-000005.png", passes: [
        { pass: 0, primary: false, path: "/my frames/headless-tick-000005-pass-0.png" },
        { pass: 1, primary: true, path: "/my frames/headless-tick-000005-pass-1.png" },
      ] },
      { tick: 5, frame: "/my frames/headless-tick-000005.png", passes: [{ pass: 0, primary: false, path: "/my frames/headless-tick-000005-pass-0.png" }] },
    ]);
  });
});

describe("ukiyo_capture result", () => {
  test("multi-camera: passes sits next to captures", () => {
    const summary = headlessSummary("Minimap", process0, minimap());
    expect(Object.keys(summary)).toEqual(["game", "exitCode", "timedOut", "ms", "snapshots", "captures", "passes", "audio", "info"]);
    expect(summary.passes).toEqual(minimap().passes);
  });

  test("single-camera: exactly the keys it always had, no passes key", () => {
    const summary = headlessSummary("LitCube", process0, litCube());
    expect(Object.keys(summary)).toEqual(["game", "exitCode", "timedOut", "ms", "snapshots", "captures", "audio", "info"]);
    expect("passes" in summary).toBe(false);
  });

  test("--out copies the pass PNGs with the frames and points every path at the copies", () => {
    const frames = join(scratch, "frames");
    mkdirSync(frames);
    const output = minimap(frames);
    for (const path of [...output.captures, ...output.passes.flatMap((c) => c.passes.map((p) => p.path))]) writeFileSync(path, path);
    const out = join(scratch, "out");
    const copied = copyOutputs(headlessSummary("Minimap", process0, output), capture.cli!.outputs!, out);

    expect(readdirSync(out).sort()).toEqual([
      "headless-tick-000000-pass-0.png", "headless-tick-000000-pass-1.png", "headless-tick-000000.png",
      "headless-tick-000060-pass-0.png", "headless-tick-000060-pass-1.png", "headless-tick-000060.png",
    ]);
    expect(copied.captures).toEqual([join(out, "headless-tick-000000.png"), join(out, "headless-tick-000060.png")]);
    expect(copied.passes).toEqual(minimap(out).passes);
    expect(readFileSync(join(out, "headless-tick-000060-pass-1.png"), "utf8")).toBe(join(frames, "headless-tick-000060-pass-1.png"));
    expect(copied.out).toBe(out);
  });

  test("--out on a single-camera result adds no passes key", () => {
    const frames = join(scratch, "single");
    mkdirSync(frames);
    const output = litCube(frames);
    for (const path of output.captures) writeFileSync(path, "png");
    const summary = headlessSummary("LitCube", process0, output);
    const copied = copyOutputs(summary, capture.cli!.outputs!, join(scratch, "single-out"));
    expect(Object.keys(copied)).toEqual([...Object.keys(summary), "out"]);
    expect(readdirSync(join(scratch, "single-out"))).toHaveLength(2);
  });

  test("--help names both outputs", async () => {
    expect(await help("capture")).toContain("--out DIR                     copy captures, passes into DIR");
  });
});

describe("ukiyo_live capture with pass", () => {
  test("pass is forwarded to the bridge's capture; omitted, the request is unchanged", () => {
    expect(liveRequest(validate(live.inputSchema, { action: "capture", pass: 1 }) as Record<string, unknown>)).toEqual({ cmd: "capture", pass: 1 });
    expect(liveRequest(validate(live.inputSchema, { action: "capture" }) as Record<string, unknown>)).toEqual({ cmd: "capture" });
    expect(liveRequest(validate(live.inputSchema, { action: "rewind", tick: 120 }) as Record<string, unknown>)).toEqual({ cmd: "rewind", tick: 120 });
    expect(() => liveRequest(validate(live.inputSchema, { action: "rewind" }) as Record<string, unknown>)).toThrow("rewind needs tick");
    expect(liveRequest(validate(live.inputSchema, { action: "capture", pass: 0 }) as Record<string, unknown>)).toEqual({ cmd: "capture", pass: 0 });
  });

  test('pass "primary" is forwarded as the bridge understands it', () => {
    expect(liveRequest(validate(live.inputSchema, { action: "capture", pass: "primary" }) as Record<string, unknown>)).toEqual({ cmd: "capture", pass: "primary" });
    expect(parseArgs(["live", "capture", "--pass", "primary", "--out", "m.png"]).request).toEqual({ cmd: "capture", pass: "primary" });
  });

  test.each([[-1, "none of its allowed forms"], [1.5, "must be an integer"], ["second", "must be one of primary"], [true, "none of its allowed forms"]])("the schema refuses pass %j", (pass, message) => {
    expect(() => validate(live.inputSchema, { action: "capture", pass })).toThrow(message);
  });

  test("pass on another action is an input error", () => {
    expect(() => liveRequest({ action: "snapshot", pass: 1 })).toThrow("pass is a capture option; snapshot does not take it");
  });

  test("the CLI takes --pass only on capture", () => {
    expect(parseArgs(["live", "capture", "--pid", "12", "--pass", "1", "--out", "m.png"]).request).toEqual({ cmd: "capture", pass: 1 });
    expect(parseArgs(["live", "capture", "--out", "m.png"]).request).toEqual({ cmd: "capture" });
    expect(() => parseArgs(["live", "capture", "--pass", "-1", "--out", "m.png"])).toThrow("expected integer");
    expect(() => parseArgs(["live", "snapshot", "--pass", "1"])).toThrow("invalid or duplicate option --pass");
  });

  test("a bridge refusal keeps the engine's typed code", () => {
    const refused = bridgeFailure("Minimap (pid 7)", "RenderException: [RENDER:OutOfRange]: capture of pass 2, but the last frame has 2 pass(es)");
    expect(refused.isError).toBe(true);
    expect(refused.content).toEqual([{ type: "text", text: "Minimap (pid 7): RenderException: [RENDER:OutOfRange]: capture of pass 2, but the last frame has 2 pass(es)" }]);
    expect(refused.structuredContent).toEqual({ error: "RenderException: [RENDER:OutOfRange]: capture of pass 2, but the last frame has 2 pass(es)", code: "RENDER:OutOfRange" });
    expect(bridgeFailure("X (pid 1)", "unknown cmd").structuredContent).toEqual({ error: "unknown cmd", code: null });
  });
});

describe("through a dev bridge on a socket (a fake game answering like DevBridgeServer)", () => {
  const PNG_1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAwS2OUAAAAABJRU5ErkJggg==";
  const OUT_OF_RANGE = "RenderException: [RENDER:OutOfRange]: capture of pass 2, but the last frame has 2 pass(es)";
  const requests: Record<string, unknown>[] = [];
  let server: Server;
  const home = join(scratch, "home");

  async function startGame() {
    server = createServer((socket) => socket.once("data", (chunk) => {
      const request = JSON.parse(String(chunk)) as Record<string, unknown>;
      requests.push(request);
      const pass = request.pass === "primary" ? 0 : (request.pass as number | undefined);
      const answer = pass === undefined ? { ok: true, result: { tick: 60, width: 960, height: 640, backend: "Metal", png: PNG_1x1 } }
        : pass >= 2 ? { ok: false, error: OUT_OF_RANGE }
          : { ok: true, result: { tick: 60, width: 217, height: 217, backend: "Metal", pass, passes: 2, primaryPass: 0, png: PNG_1x1 } };
      socket.end(`${JSON.stringify({ id: request.id, ...answer })}\n`);
    }));
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    mkdirSync(join(home, ".ukiyo", "live"), { recursive: true });
    const session = { pid: process.pid, port: (server.address() as { port: number }).port, token: "test-only", game: "Minimap", target: "desktop", startedAt: "2026-10-16T00:00:00Z" };
    writeFileSync(join(home, ".ukiyo", "live", `${process.pid}.json`), JSON.stringify(session));
  }

  /** The tool and the CLI read ~/.ukiyo/live, so both run in a child process whose HOME is the scratch home. */
  async function child(...args: string[]) {
    const proc = Bun.spawn([process.execPath, ...args], { stdout: "pipe", stderr: "pipe", env: { ...process.env, HOME: home } });
    const [stdout, stderr, code] = await Promise.all([new Response(proc.stdout).text(), new Response(proc.stderr).text(), proc.exited]);
    return { stdout, stderr, code };
  }

  const toolCall = (args: Record<string, unknown>) =>
    child("-e", `import live from ${JSON.stringify(join(import.meta.dir, "../src/tools/live.ts"))};
      const result = await live.run(${JSON.stringify(args)}, {});
      console.log(JSON.stringify({ isError: result.isError ?? false, text: result.content.find((c) => c.type === "text")?.text, images: result.content.filter((c) => c.type === "image").length, structured: result.structuredContent }));`);

  test("ukiyo_live capture: pass 1 → that pass's metadata and image; pass 2 of 2 → isError with RENDER:OutOfRange", async () => {
    await startGame();
    try {
      const minimapPass = await toolCall({ action: "capture", pass: 1 });
      expect(minimapPass.code).toBe(0);
      expect(JSON.parse(minimapPass.stdout)).toEqual({
        isError: false,
        text: "Minimap tick 60 · 217x217 · pass 1 of 2 · Metal (GPU readback)",
        images: 1,
        structured: { tick: 60, width: 217, height: 217, backend: "Metal", pass: 1, passes: 2, primaryPass: 0 },
      });
      expect(requests.at(-1)).toMatchObject({ cmd: "capture", pass: 1 });

      const primary = JSON.parse((await toolCall({ action: "capture", pass: "primary" })).stdout);
      expect(requests.at(-1)).toMatchObject({ cmd: "capture", pass: "primary" });
      expect(primary).toMatchObject({ isError: false, structured: { pass: 0, passes: 2, primaryPass: 0 } });
      expect(primary.text).toContain("pass 0 of 2 (primary)");

      const whole = JSON.parse((await toolCall({ action: "capture" })).stdout);
      expect(whole.structured).toEqual({ tick: 60, width: 960, height: 640, backend: "Metal" });
      expect("pass" in requests.at(-1)!).toBe(false);

      const missing = JSON.parse((await toolCall({ action: "capture", pass: 2 })).stdout);
      expect(missing).toEqual({ isError: true, text: `Minimap (pid ${process.pid}): ${OUT_OF_RANGE}`, images: 0, structured: { error: OUT_OF_RANGE, code: "RENDER:OutOfRange" } });

      const cli = join(import.meta.dir, "../src/cli.ts");
      const png = join(scratch, "minimap.png");
      const written = await child(cli, "live", "capture", "--pid", String(process.pid), "--pass", "1", "--out", png);
      expect(written.code).toBe(0);
      expect(JSON.parse(written.stdout)).toEqual({ tick: 60, width: 217, height: 217, backend: "Metal", pass: 1, passes: 2, primaryPass: 0, output: png });
      expect(readFileSync(png)).toEqual(Buffer.from(PNG_1x1, "base64"));

      const refused = await child(cli, "live", "capture", "--pid", String(process.pid), "--pass", "2", "--out", join(scratch, "none.png"));
      expect(refused).toMatchObject({ code: 1, stdout: "" });
      expect(refused.stderr).toContain(`[UKIYO]: ${OUT_OF_RANGE}`);
      expect(existsSync(join(scratch, "none.png"))).toBe(false);
    } finally {
      server.close();
    }
  }, 20_000);
});

describe("ukiyo_live reload", () => {
  const session = { pid: 1, port: 2, token: "t", url: "u", startedAt: "", file: "f" };
  const run = (reload: unknown) => ({ run: { name: "LanternRun", state: "running", pid: 77, detail: "", reload } });

  test("is in the schema and asks the Studio to reload and wait", async () => {
    expect(() => validate(live.inputSchema, { action: "reload" })).not.toThrow();
    const calls: unknown[][] = [];
    const result = await reload(5, [session], async (...args) => (calls.push(args), run({ ok: true, fromTick: 300, match: true, sourceChanged: false, wasPaused: false })));
    expect(calls[0]!.slice(1, 3)).toEqual(["/api/run/reload", { wait: true, pid: 5 }]);
    expect(result.isError).toBeUndefined();
    expect((result.content[0] as { text: string }).text).toContain("reloaded at tick 300 (pid 77, running); restored state identical");
  });

  test("a state that differs is reported, a failed reload is an error, no Studio is a clear input error", async () => {
    const differs = await reload(undefined, [session], async () => run({ ok: true, fromTick: 9, match: false, differences: ["values.score: 1 → 2"] }));
    expect((differs.content[0] as { text: string }).text).toContain("DIFFERENT: values.score: 1 → 2");
    const failed = await reload(undefined, [session], async () => run({ ok: false, error: "build failed (exit 1); the running game was not touched" }));
    expect(failed.isError).toBe(true);
    const refused = await reload(undefined, [session], async () => { throw new Error("[STUDIO]: /api/run/reload → 404 no running game"); });
    expect(refused.isError).toBe(true);
    await expect(reload(undefined, [])).rejects.toThrow("needs the Studio");
  });
});
