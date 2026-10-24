import { afterEach, describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import type { BridgeResponse, LiveSession } from "../../tools/mcp/src/bridge.ts";
import { Launcher, LauncherError, discoverGames, snapshotDifferences, sourceChanged, type Child, type Spawn } from "../src/launcher.ts";
import { runRoutes } from "../src/run-routes.ts";
import { STUDIO_DIR } from "../src/manifest.ts";
import { refuse } from "../src/guard.ts";

const BRIDGE_CSPROJ = "<Project><PropertyGroup><UkiyoDevBridge>true</UkiyoDevBridge></PropertyGroup></Project>";
const scratch: string[] = [];

function repo(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), "ukiyo-launcher-"));
  scratch.push(root);
  for (const [path, content] of Object.entries(files)) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), content);
  }
  return root;
}

afterEach(() => {
  for (const dir of scratch.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe("game discovery", () => {
  test("lists the desktop hosts of samples/ and games/ (both host layouts), only from the repository", () => {
    const root = repo({
      "samples/Alpha/Desktop/Alpha.Desktop.csproj": BRIDGE_CSPROJ,
      "games/Beta/Hosts/Desktop/Beta.Desktop.csproj": BRIDGE_CSPROJ,
      "games/Gamma/Desktop/Gamma.Desktop.csproj": "<Project />",
      "samples/NoDesktop/Headless/NoDesktop.Headless.csproj": BRIDGE_CSPROJ,
      "samples/Ambiguous/Desktop/One.csproj": BRIDGE_CSPROJ,
      "samples/Ambiguous/Desktop/Two.csproj": BRIDGE_CSPROJ,
      "samples/.hidden/Desktop/Hidden.csproj": BRIDGE_CSPROJ,
    });
    symlinkSync("/etc/hosts", join(root, "samples/Alpha/Desktop/Alias.csproj")); // a second, non-file project: ignored as ambiguous with Alpha
    expect(discoverGames(root).map((g) => [g.id, g.project, g.bridge])).toEqual([
      ["game/Beta", "games/Beta/Hosts/Desktop/Beta.Desktop.csproj", true],
      ["game/Gamma", "games/Gamma/Desktop/Gamma.Desktop.csproj", false],
    ]);
  });

  test("a symlinked project file is never offered", () => {
    const root = repo({ "elsewhere/Evil.csproj": BRIDGE_CSPROJ });
    mkdirSync(join(root, "samples/Evil/Desktop"), { recursive: true });
    symlinkSync(join(root, "elsewhere/Evil.csproj"), join(root, "samples/Evil/Desktop/Evil.csproj"));
    expect(discoverGames(root)).toEqual([]);
  });

  test("the real repository offers LanternRun", () => {
    const games = discoverGames(join(STUDIO_DIR, ".."));
    expect(games.find((g) => g.id === "sample/LanternRun")).toMatchObject({ name: "LanternRun", project: "samples/LanternRun/Desktop/LanternRun.Desktop.csproj", bridge: true });
  });
});

// ── a controllable process and bridge ────────────────────────────────────────────────────────────────────────────

const FAKE_PID_BASE = 2_100_000_000; // above any real pid: kill(pid, 0) says it is gone

type FakeChild = Child & { say(line: string): void; finish(code: number): void; signals: string[]; command: string[]; env: Record<string, string | undefined> };

function harness(files: Record<string, string> = { "samples/Lantern/Desktop/Lantern.Desktop.csproj": BRIDGE_CSPROJ }) {
  const root = repo(files);
  const children: FakeChild[] = [];
  const sessions: LiveSession[] = [];
  const parents = new Map<number, number>();
  const bridgeCalls: { pid: number; cmd: string }[] = [];
  let nextPid = FAKE_PID_BASE;
  const responses: Record<string, (session: LiveSession) => BridgeResponse> = {};

  const spawn: Spawn = (command, { env }) => {
    let out!: ReadableStreamDefaultController<Uint8Array>;
    let exit!: (code: number) => void;
    let closed = false;
    /** The process ends: its output stream ends with it (once). */
    const resolve = (code: number) => {
      exit(code);
      if (!closed) {
        closed = true;
        out.close();
      }
    };
    const encoder = new TextEncoder();
    const child: FakeChild = {
      pid: nextPid++,
      exited: new Promise<number>((r) => (exit = r)),
      stdout: new ReadableStream<Uint8Array>({ start: (c) => (out = c) }),
      stderr: null,
      kill(signal = "SIGTERM") {
        child.signals.push(signal);
        resolve(signal === "SIGKILL" ? 137 : 143);
      },
      say: (line) => out.enqueue(encoder.encode(`${line}\n`)),
      finish: (code) => resolve(code),
      signals: [],
      command,
      env,
    };
    children.push(child);
    return child;
  };

  /** The game process `dotnet run` (child) started: it announces itself in the live directory. */
  function announce(child: FakeChild, game = "Lantern"): LiveSession {
    const session: LiveSession = { pid: nextPid++, port: 1, token: "t", game, target: "desktop", startedAt: new Date().toISOString(), file: join(root, `${nextPid}.json`) };
    parents.set(session.pid, child.pid);
    sessions.push(session);
    return session;
  }

  const launcher = new Launcher({
    repoRoot: root,
    dotnet: "dotnet-under-test",
    spawn,
    sessions: () => sessions,
    bridge: async (session, request) => {
      const cmd = String(request.cmd);
      bridgeCalls.push({ pid: session.pid, cmd });
      return responses[cmd]?.(session) ?? { ok: true, result: {} };
    },
    parentOf: (pid) => parents.get(pid) ?? null,
    attachPollMs: 5,
    killGraceMs: 20,
    attachTimeoutMs: 400,
  });
  return { root, launcher, children, sessions, announce, bridgeCalls, responses };
}

const until = async (condition: () => boolean, what: string) => {
  for (let i = 0; i < 400 && !condition(); i++) await Bun.sleep(5);
  if (!condition()) throw new Error(`timed out waiting for ${what}`);
};

describe("starting games", () => {
  test("only a discovered game id starts, and nothing else is ever spawned", () => {
    const { launcher, children } = harness();
    for (const bad of ["../../etc/passwd", "sample/../../x", "/bin/sh", "sample/Lantern/../Lantern", "Lantern", "dotnet run --project x", ""]) {
      expect(() => launcher.start(bad)).toThrow(LauncherError);
    }
    expect(children).toHaveLength(0);
  });

  test("a host without the dev bridge is refused (the Studio could never attach)", () => {
    const { launcher, children } = harness({ "games/Mute/Desktop/Mute.Desktop.csproj": "<Project />" });
    expect(() => launcher.start("game/Mute")).toThrow(/dev bridge/);
    expect(children).toHaveLength(0);
  });

  test("runs `dotnet run --project <csproj>` from the repository, then follows build, start and attach", async () => {
    const { launcher, children, announce, root } = harness();
    const run = launcher.start("sample/Lantern");
    expect(run.state).toBe("building");
    expect(children[0]!.command).toEqual(["dotnet-under-test", "run", "--project", join(root, "samples/Lantern/Desktop/Lantern.Desktop.csproj")]);
    expect(() => launcher.start("sample/Lantern")).toThrow(/already building/);

    children[0]!.say("  Determining projects to restore...");
    await until(() => launcher.run(run.id)!.detail.includes("Determining"), "build output");
    children[0]!.say("[ukiyo] target=desktop game=Lantern");
    await until(() => launcher.run(run.id)!.state === "starting", "starting");

    const session = announce(children[0]!);
    await until(() => launcher.run(run.id)!.state === "running", "attach");
    expect(launcher.run(run.id)!.pid).toBe(session.pid);

    children[0]!.finish(0); // the person closes the window
    await until(() => launcher.run(run.id)!.state === "stopped", "stopped after window close");
    expect(() => launcher.start("sample/Lantern")).not.toThrow();
  });

  test("a build that fails is shown plainly, with the compiler errors", async () => {
    const { launcher, children } = harness();
    const run = launcher.start("sample/Lantern");
    children[0]!.say("Game.cs(12,5): error CS1002: ; expected [Lantern.Desktop.csproj]");
    children[0]!.say("Build FAILED.");
    children[0]!.finish(1);
    await until(() => launcher.run(run.id)!.state === "failed", "failed");
    const failed = launcher.run(run.id)!;
    expect(failed.errors).toEqual(["Game.cs(12,5): error CS1002: ; expected [Lantern.Desktop.csproj]"]);
    expect(failed.detail).toContain("CS1002");
    expect(failed.error).toContain("exit 1");
  });

  test("a game that never announces itself is stopped and reported", async () => {
    const { launcher, children } = harness();
    const run = launcher.start("sample/Lantern");
    await until(() => launcher.run(run.id)!.state === "failed", "attach timeout");
    expect(launcher.run(run.id)!.error).toContain("did not announce itself");
    expect(children[0]!.signals).toContain("SIGKILL");
  });

  test("a game announced by someone else's process is not mistaken for ours", async () => {
    const { launcher, children, sessions } = harness();
    const run = launcher.start("sample/Lantern");
    sessions.push({ pid: 123456789, port: 1, token: "t", game: "Lantern", target: "desktop", startedAt: new Date().toISOString(), file: "x" });
    await Bun.sleep(40);
    expect(launcher.run(run.id)!.state).toBe("building");
    children[0]!.finish(0);
  });

  test("stop terminates the process the Studio started", async () => {
    const { launcher, children, announce } = harness();
    const run = launcher.start("sample/Lantern");
    announce(children[0]!);
    await until(() => launcher.run(run.id)!.state === "running", "attach");
    const stopped = await launcher.stop(run.id);
    expect(stopped.state).toBe("stopped");
    expect(children[0]!.signals[0]).toBe("SIGTERM");
  });
});

describe("controlled reload", () => {
  async function running(h = harness()) {
    const run = h.launcher.start("sample/Lantern");
    const session = h.announce(h.children[0]!);
    await until(() => h.launcher.run(run.id)!.state === "running", "attach");
    h.responses.status = () => ({ ok: true, result: { paused: false, tick: 300 } });
    h.responses.snapshot = () => ({ ok: true, result: { tick: 300, source: { "Game.cs": "aaa" }, entities: { player: { position: [1, 2, 3] } } } });
    h.responses.history = () => ({ ok: true, result: { tick: 300, complete: true, log: { version: 1, tick: 300, input: [] } } });
    return { ...h, run, session };
  }

  /** Builds succeed; the relaunch announces itself and replies with `after` as its snapshot. */
  function autopilot(h: Awaited<ReturnType<typeof running>>, after: unknown) {
    const original = h.children.length;
    const timer = setInterval(() => {
      const spawned = h.children.slice(original);
      for (const child of spawned) {
        if (child.command[1] === "build" && !child.signals.includes("done")) {
          child.signals.push("done");
          child.finish(0);
        }
        if (child.command[1] === "run" && !child.signals.includes("done")) {
          child.signals.push("done");
          h.responses.snapshot = (s) => ({ ok: true, result: s.pid === h.session.pid ? { tick: 300 } : after });
          h.announce(child);
        }
      }
    }, 5);
    return () => clearInterval(timer);
  }

  test("rebuilds, pauses at the boundary, relaunches with the replay log, compares, resumes", async () => {
    const h = await running();
    let restoreSeen: unknown = null;
    const stop = autopilot(h, { tick: 300, source: { "Game.cs": "aaa" }, entities: { player: { position: [1, 2, 3] } } });
    const original = h.children.length;
    const watcher = setInterval(() => {
      const next = h.children.slice(original).find((c) => c.command[1] === "run");
      if (next?.env.UKIYO_RESTORE && restoreSeen === null) restoreSeen = JSON.parse(readFileSync(next.env.UKIYO_RESTORE, "utf8"));
    }, 2);
    const { run: started, done } = h.launcher.beginReload(h.run.id);
    expect(started.state).toBe("reloading");
    const finished = await done;
    clearInterval(watcher);
    stop();

    expect(finished.state).toBe("running");
    expect(finished.reload).toMatchObject({ ok: true, fromTick: 300, match: true, sourceChanged: false, wasPaused: false, differences: [] });
    expect(restoreSeen).toEqual({ version: 1, tick: 300, input: [] });
    const relaunch = h.children.slice(original).find((c) => c.command[1] === "run")!;
    expect(relaunch.command).toContain("--no-build");
    expect(existsSync(relaunch.env.UKIYO_RESTORE!)).toBe(false); // the log file does not outlive the reload
    expect(h.children[0]!.signals[0]).toBe("SIGTERM");
    expect(h.bridgeCalls.map((c) => c.cmd)).toEqual(["status", "pause", "snapshot", "history", "snapshot", "resume"]);
    expect(finished.pid).not.toBe(h.session.pid);
  });

  test("a restored state that differs is reported with where, and a changed source is flagged", async () => {
    const h = await running();
    const stop = autopilot(h, { tick: 300, source: { "Game.cs": "bbb" }, entities: { player: { position: [1, 2.5, 3] } } });
    const finished = await h.launcher.beginReload(h.run.id).done;
    stop();
    expect(finished.reload).toMatchObject({ ok: true, match: false, sourceChanged: true });
    expect(finished.reload!.differences).toEqual(["entities.player.position[1]: 2 → 2.5"]);
  });

  test("a paused game stays paused after the reload", async () => {
    const h = await running();
    h.responses.status = () => ({ ok: true, result: { paused: true, tick: 300 } });
    const stop = autopilot(h, { tick: 300, source: { "Game.cs": "aaa" }, entities: { player: { position: [1, 2, 3] } } });
    const finished = await h.launcher.beginReload(h.run.id).done;
    stop();
    expect(finished.reload).toMatchObject({ ok: true, wasPaused: true });
    expect(h.bridgeCalls.map((c) => c.cmd)).not.toContain("resume");
  });

  test("a build that fails leaves the running game untouched", async () => {
    const h = await running();
    const original = h.children.length;
    const timer = setInterval(() => {
      const build = h.children.slice(original).find((c) => c.command[1] === "build");
      if (build && !build.signals.length) {
        build.signals.push("done");
        build.say("Game.cs(3,1): error CS0103: The name 'x' does not exist [Lantern.Desktop.csproj]");
        setTimeout(() => build.finish(1), 10);
      }
    }, 5);
    const finished = await h.launcher.beginReload(h.run.id).done;
    clearInterval(timer);
    expect(finished.state).toBe("running");
    expect(finished.pid).toBe(h.session.pid);
    expect(finished.reload).toMatchObject({ ok: false });
    expect(finished.reload!.errors?.[0]).toContain("CS0103");
    expect(h.bridgeCalls).toEqual([]);
    expect(h.children[0]!.signals).toEqual([]);
  });

  test("a relaunch that crashes while restoring is a failure that says the old process is gone", async () => {
    const h = await running();
    const original = h.children.length;
    const timer = setInterval(() => {
      for (const child of h.children.slice(original)) {
        if (child.signals.includes("done")) continue;
        child.signals.push("done");
        if (child.command[1] === "build") child.finish(0);
        else {
          child.say("Unhandled exception. System.InvalidOperationException: [REPLAY]: the history of this session outgrew 2000000 entries");
          setTimeout(() => child.finish(134), 10);
        }
      }
    }, 5);
    const finished = await h.launcher.beginReload(h.run.id).done;
    clearInterval(timer);
    expect(finished.state).toBe("failed");
    expect(finished.reload).toMatchObject({ ok: false });
    expect(finished.reload!.error).toContain("[REPLAY]");
    expect(finished.reload!.error).toContain("already been stopped");
  });

  test("only a running game reloads, and only once at a time", async () => {
    const h = await running();
    const stop = autopilot(h, { tick: 300 });
    const { done } = h.launcher.beginReload(h.run.id);
    expect(() => h.launcher.beginReload(h.run.id)).toThrow(/reloading/);
    await done;
    stop();
    expect(() => h.launcher.beginReload("r999")).toThrow(/no run/);
  });
});

describe("snapshot comparison", () => {
  test("ignores source hashes and names the first differing fields", () => {
    const a = { tick: 5, source: { f: "1" }, entities: { p: { position: [1, 2] } }, values: { score: 10 } };
    expect(snapshotDifferences(a, { ...a, source: { f: "2" } })).toEqual([]);
    expect(sourceChanged(a, { ...a, source: { f: "2" } })).toBe(true);
    expect(snapshotDifferences(a, { ...a, values: { score: 11 }, entities: { p: { position: [1, 3] } } })).toEqual(["entities.p.position[1]: 2 → 3", "values.score: 10 → 11"]);
  });
});

describe("HTTP routes", () => {
  const log = () => {};
  const post = (path: string, body: unknown) => new Request(`http://127.0.0.1:1${path}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });

  test("the run endpoints need the Studio token, a local Host and a same-origin JSON body (server guard)", () => {
    const options = { port: 4100, token: "secret" };
    const url = (path: string) => new URL(`http://127.0.0.1:4100${path}`);
    const headers = { host: "127.0.0.1:4100", "content-type": "application/json" };
    for (const path of ["/api/games", "/api/run", "/api/run/stop", "/api/run/reload"]) {
      expect(refuse(new Request(url(path), { method: path === "/api/games" ? "GET" : "POST", headers }), url(path), options)?.status).toBe(401);
      expect(refuse(new Request(url(path), { method: "POST", headers: { ...headers, "x-ukiyo-token": "secret", origin: "http://evil.example" } }), url(path), options)?.status).toBe(403);
      expect(refuse(new Request(url(path), { method: "POST", headers: { ...headers, host: "evil.example", "x-ukiyo-token": "secret" } }), url(path), options)?.status).toBe(421);
    }
    expect(refuse(new Request(url("/api/run"), { method: "POST", headers: { ...headers, "x-ukiyo-token": "secret", "content-type": "text/plain" } }), url("/api/run"), options)?.status).toBe(415);
    expect(refuse(new Request(url("/api/run"), { method: "POST", headers: { ...headers, "x-ukiyo-token": "secret" } }), url("/api/run"), options)).toBeNull();
  });

  test("a request names a game by id; a path or command is a 404 and spawns nothing", async () => {
    const { launcher, children } = harness();
    const bad = await runRoutes(launcher, post("/api/run", { game: "samples/Lantern/Desktop/Lantern.Desktop.csproj" }), "/api/run", log);
    expect(bad!.status).toBe(404);
    expect((await runRoutes(launcher, post("/api/run", { cmd: ["sh", "-c", "id"] }), "/api/run", log))!.status).toBe(400);
    expect((await runRoutes(launcher, post("/api/run", ["x"]), "/api/run", log))!.status).toBe(400);
    expect(children).toHaveLength(0);
  });

  test("lists games and runs, starts one, and ignores paths it does not own", async () => {
    const { launcher, children } = harness();
    const listing = await (await runRoutes(launcher, new Request("http://127.0.0.1:1/api/games"), "/api/games", log))!.json() as { games: { id: string }[]; runs: unknown[] };
    expect(listing.games.map((g) => g.id)).toEqual(["sample/Lantern"]);
    const started = await runRoutes(launcher, post("/api/run", { game: "sample/Lantern" }), "/api/run", log);
    expect(started!.status).toBe(202);
    expect(children).toHaveLength(1);
    expect(await runRoutes(launcher, new Request("http://127.0.0.1:1/api/health"), "/api/health", log)).toBeNull();
    children[0]!.finish(0);
  });

  test("stop and reload without a matching running game say so", async () => {
    const { launcher } = harness();
    expect((await runRoutes(launcher, post("/api/run/reload", {}), "/api/run/reload", log))!.status).toBe(404);
    expect((await runRoutes(launcher, post("/api/run/stop", { run: "r7" }), "/api/run/stop", log))!.status).toBe(404);
  });
});
