// Run and reload games from the Studio. The Studio lists the desktop hosts it finds in the repository, starts one with
// `dotnet run --project <csproj>`, attaches when the game announces itself in ~/.ukiyo/live, stops what it started, and
// reloads it: rebuild, then relaunch restoring the same tick by replaying the old process's input/patch log from tick 0.
//
// Safety: only csproj files discovered under samples/*/Desktop, games/*/Desktop and games/*/Hosts/Desktop are ever
// run, chosen by an id the server derives itself; a request never supplies a path or a command line.
import { chmodSync, existsSync, lstatSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { homedir, tmpdir } from "node:os";
import { join, relative } from "node:path";
import type { BridgeResponse, LiveSession } from "../../tools/mcp/src/bridge.ts";

// ── discovery ─────────────────────────────────────────────────────────────────────────────────────────────────────

export type GameEntry = { id: string; name: string; kind: "sample" | "game"; project: string; bridge: boolean };

const NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/** Directory layouts a runnable host can have, relative to the repository: [folder, kind, path below the game folder]. */
const LAYOUTS: [string, GameEntry["kind"], string[]][] = [
  ["samples", "sample", ["Desktop"]],
  ["games", "game", ["Desktop"]],
  ["games", "game", ["Hosts", "Desktop"]],
];

function regularFile(path: string): boolean {
  try {
    return lstatSync(path).isFile();
  } catch {
    return false;
  }
}

function subdirectories(path: string): string[] {
  try {
    return readdirSync(path, { withFileTypes: true }).filter((e) => e.isDirectory() && NAME.test(e.name)).map((e) => e.name);
  } catch {
    return [];
  }
}

/** The games the Studio can run: one entry per Desktop host project that exists, sorted by id. */
export function discoverGames(repoRoot: string): GameEntry[] {
  const found = new Map<string, GameEntry>();
  for (const [folder, kind, below] of LAYOUTS) {
    for (const name of subdirectories(join(repoRoot, folder))) {
      const directory = join(repoRoot, folder, name, ...below);
      const projects = existsSync(directory) ? readdirSync(directory).filter((f) => f.endsWith(".csproj") && NAME.test(f)) : [];
      if (projects.length !== 1) continue; // none, or ambiguous: nothing safe to pick
      const path = join(directory, projects[0]!);
      if (!regularFile(path)) continue;
      const id = `${kind}/${name}`;
      if (found.has(id)) continue;
      found.set(id, { id, name, kind, project: relative(repoRoot, path), bridge: /<UkiyoDevBridge>\s*true\s*<\/UkiyoDevBridge>/.test(readFileSync(path, "utf8")) });
    }
  }
  return [...found.values()].sort((a, b) => a.id.localeCompare(b.id));
}

/** dotnet from UKIYO_DOTNET, then ~/.dotnet/dotnet, then PATH. */
export function findDotnet(env: Record<string, string | undefined> = process.env): string {
  if (env.UKIYO_DOTNET) return env.UKIYO_DOTNET;
  const local = join(homedir(), ".dotnet", "dotnet");
  return existsSync(local) ? local : "dotnet";
}

// ── snapshots ─────────────────────────────────────────────────────────────────────────────────────────────────────

/** Where two snapshots differ, as `path: before → after`, at most `limit` entries. `source` (file hashes) is excluded. */
export function snapshotDifferences(before: unknown, after: unknown, limit = 8): string[] {
  const strip = (value: unknown) => {
    if (typeof value !== "object" || value === null) return value;
    const { source: _source, ...rest } = value as Record<string, unknown>;
    return rest;
  };
  const differences: string[] = [];
  const walk = (a: unknown, b: unknown, path: string) => {
    if (differences.length >= limit) return;
    if (typeof a === "object" && a !== null && typeof b === "object" && b !== null) {
      const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
      for (const key of [...keys].sort()) {
        walk((a as Record<string, unknown>)[key], (b as Record<string, unknown>)[key], Array.isArray(a) ? `${path}[${key}]` : path ? `${path}.${key}` : key);
      }
    } else if (!Object.is(a, b)) {
      differences.push(`${path}: ${JSON.stringify(a)} → ${JSON.stringify(b)}`);
    }
  };
  walk(strip(before), strip(after), "");
  return differences;
}

export function sourceChanged(before: unknown, after: unknown): boolean {
  const source = (value: unknown) => JSON.stringify((value as { source?: unknown } | null)?.source ?? null);
  return source(before) !== source(after);
}

// ── runs ──────────────────────────────────────────────────────────────────────────────────────────────────────────

export type RunState = "building" | "starting" | "running" | "reloading" | "stopping" | "stopped" | "failed";

export type ReloadReport = {
  ok: boolean;
  at: string;
  /** Why nothing changed (build failed) or why the relaunch failed. */
  error?: string;
  errors?: string[];
  fromTick?: number;
  tick?: number;
  /** The restored process's snapshot equals the old one's (file hashes excluded), at the same tick. */
  match?: boolean;
  /** The build's source hashes differ from the old process's: differences are then expected. */
  sourceChanged?: boolean;
  differences?: string[];
  wasPaused?: boolean;
  ms?: number;
};

export type Run = {
  id: string;
  game: string;
  name: string;
  project: string;
  state: RunState;
  /** One plain line for the person: what is happening now. */
  detail: string;
  startedAt: string;
  /** The game's own pid once it has announced itself. */
  pid: number | null;
  exitCode: number | null;
  error: string | null;
  /** Compiler/host error lines seen so far (bounded). */
  errors: string[];
  /** Last output lines of dotnet and the game (bounded). */
  output: string[];
  reload: ReloadReport | null;
  reloads: number;
};

export type Child = {
  pid: number;
  exited: Promise<number>;
  stdout: ReadableStream<Uint8Array> | null;
  stderr: ReadableStream<Uint8Array> | null;
  kill(signal?: NodeJS.Signals): void;
};

export type Spawn = (command: string[], options: { cwd: string; env: Record<string, string | undefined> }) => Child;

export type LauncherOptions = {
  repoRoot: string;
  dotnet?: string;
  spawn?: Spawn;
  sessions: () => LiveSession[];
  bridge: (session: LiveSession, request: Record<string, unknown>, timeoutMs?: number) => Promise<BridgeResponse>;
  /** Parent pid of a process (to tell which announced game belongs to which `dotnet run`). */
  parentOf?: (pid: number) => number | null;
  /** How long a launch may take before it has announced itself (build included). */
  attachTimeoutMs?: number;
  attachPollMs?: number;
  killGraceMs?: number;
  /** Every non-empty output line of dotnet and the game (ANSI stripped), for the Console panel. */
  onOutput?: (line: { game: string; name: string; stream: "stdout" | "stderr"; text: string }) => void;
};

export class LauncherError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

type Record_ = Run & { proc: Child | null; sinceMs: number; timer: ReturnType<typeof setInterval> | null; busy: boolean; restoreDir: string | null };

const OUTPUT_LINES = 200;
const ERROR_LINES = 12;
const ERROR_LINE = /\berror\b\s*[A-Za-z]*\d*\s*:|Unhandled exception|\[ukiyo\].*(?:failed|disabled)|\[REPLAY\]|Exception:/i;

function bunSpawn(command: string[], { cwd, env }: { cwd: string; env: Record<string, string | undefined> }): Child {
  return Bun.spawn(command, { cwd, env, stdin: "ignore", stdout: "pipe", stderr: "pipe" }) as unknown as Child;
}

function psParent(pid: number): number | null {
  try {
    const result = Bun.spawnSync(["ps", "-o", "ppid=", "-p", String(pid)]);
    const value = Number(result.stdout.toString().trim());
    return Number.isInteger(value) && value > 0 ? value : null;
  } catch {
    return null;
  }
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

export class Launcher {
  readonly dotnet: string;
  private readonly records = new Map<string, Record_>();
  private readonly spawn: Spawn;
  private readonly parentOf: (pid: number) => number | null;
  private sequence = 0;

  constructor(private readonly options: LauncherOptions) {
    this.dotnet = options.dotnet ?? findDotnet();
    this.spawn = options.spawn ?? bunSpawn;
    this.parentOf = options.parentOf ?? psParent;
  }

  games(): GameEntry[] {
    return discoverGames(this.options.repoRoot);
  }

  runs(): Run[] {
    return [...this.records.values()].map((r) => this.view(r));
  }

  run(id: string): Run | null {
    const record = this.records.get(id);
    return record ? this.view(record) : null;
  }

  /** Starts `dotnet run` for a discovered game. Throws LauncherError for an unknown game or one that is already running. */
  start(gameId: string): Run {
    const game = this.games().find((g) => g.id === gameId);
    if (!game) throw new LauncherError(`[LAUNCH]: ${JSON.stringify(gameId)} is not a game the Studio can run (it runs ${this.games().map((g) => g.id).join(", ") || "nothing here"})`, 404);
    if (!game.bridge) throw new LauncherError(`[LAUNCH]: ${game.project} does not link the dev bridge (UkiyoDevBridge), so the Studio could not attach to it`, 422);
    const active = [...this.records.values()].find((r) => r.game === game.id && !this.finished(r));
    if (active) throw new LauncherError(`[LAUNCH]: ${game.name} is already ${active.state} (run ${active.id}); stop it first`, 409);

    const record: Record_ = {
      id: `r${++this.sequence}`, game: game.id, name: game.name, project: game.project, state: "building", detail: "Building…",
      startedAt: new Date().toISOString(), pid: null, exitCode: null, error: null, errors: [], output: [], reload: null, reloads: 0,
      proc: null, sinceMs: Date.now(), timer: null, busy: false, restoreDir: null,
    };
    this.records.set(record.id, record);
    this.prune();
    this.launch(record, { noBuild: false });
    return this.view(record);
  }

  /** Stops the game this Studio started (SIGTERM, then SIGKILL after a grace period). */
  async stop(id: string): Promise<Run> {
    const record = this.require(id);
    if (this.finished(record)) return this.view(record);
    if (record.busy) throw new LauncherError(`[LAUNCH]: ${record.name} is busy (${record.state}); try again when it settles`, 409);
    record.state = "stopping";
    record.detail = "Stopping…";
    await this.terminate(record);
    record.state = "stopped";
    record.detail = "Stopped";
    record.pid = null;
    return this.view(record);
  }

  /** Stops everything this Studio started (Studio shutdown). */
  async stopAll(): Promise<void> {
    await Promise.all([...this.records.values()].filter((r) => !this.finished(r)).map((r) => this.terminate(r).then(() => { r.state = "stopped"; })));
  }

  /**
   * Controlled reload. Validation errors throw now; the work continues in `done`, which never rejects: the outcome is
   * in the run (`state`, `reload`, `error`).
   */
  beginReload(id: string): { run: Run; done: Promise<Run> } {
    const record = this.require(id);
    if (record.state !== "running" || record.pid === null) throw new LauncherError(`[RELOAD]: ${record.name} is ${record.state}; only a running game can be reloaded`, 409);
    if (record.busy) throw new LauncherError(`[RELOAD]: ${record.name} is already reloading`, 409);
    record.busy = true;
    record.state = "reloading";
    record.detail = "Rebuilding…";
    const done = this.reload(record).catch((error) => {
      record.state = "failed";
      record.error = error instanceof Error ? error.message : String(error);
      record.detail = record.error;
      record.reload = { ok: false, at: new Date().toISOString(), error: record.error };
    }).finally(() => {
      record.busy = false;
    }).then(() => this.view(record));
    return { run: this.view(record), done };
  }

  // ── internals ───────────────────────────────────────────────────────────────────────────────────────────────────

  private finished(record: Record_): boolean {
    return record.state === "stopped" || record.state === "failed";
  }

  private require(id: string): Record_ {
    const record = this.records.get(id);
    if (!record) throw new LauncherError(`[LAUNCH]: no run ${JSON.stringify(id)}`, 404);
    return record;
  }

  private view(record: Record_): Run {
    const { proc: _p, sinceMs: _s, timer: _t, busy: _b, restoreDir: _r, ...run } = record;
    return { ...run, errors: [...run.errors], output: [...run.output], reload: run.reload ? { ...run.reload } : null };
  }

  private prune() {
    const finished = [...this.records.values()].filter((r) => this.finished(r));
    for (const old of finished.slice(0, Math.max(0, finished.length - 8))) this.records.delete(old.id);
  }

  private note(record: Record_, line: string, stream: "stdout" | "stderr" = "stdout") {
    const text = line.replace(/\u001b\[[0-9;]*m/g, "").trimEnd();
    if (!text.trim()) return;
    this.options.onOutput?.({ game: record.game, name: record.name, stream, text });
    record.output.push(text);
    if (record.output.length > OUTPUT_LINES) record.output.shift();
    if (ERROR_LINE.test(text) && record.errors.length < ERROR_LINES && !record.errors.includes(text)) record.errors.push(text);
    if (record.state === "building" || record.state === "reloading" || record.state === "starting") record.detail = text.trim().slice(0, 200);
    if (/^\[ukiyo\] target=/.test(text) && (record.state === "building" || record.state === "reloading")) {
      if (record.state === "building") record.state = "starting";
      record.detail = "Game started, waiting for it to announce itself…";
    }
  }

  private pump(record: Record_, stream: ReadableStream<Uint8Array> | null, name: "stdout" | "stderr"): Promise<void> {
    if (!stream) return Promise.resolve();
    const decoder = new TextDecoder();
    let pending = "";
    return (async () => {
      try {
        for await (const chunk of stream) {
          pending += decoder.decode(chunk, { stream: true });
          const lines = pending.split(/\r?\n/);
          pending = lines.pop() ?? "";
          for (const line of lines) this.note(record, line, name);
        }
      } catch {
        // The stream closes when the process dies.
      }
      if (pending) this.note(record, pending, name);
    })();
  }

  private launch(record: Record_, { noBuild, restore }: { noBuild: boolean; restore?: string }) {
    const project = join(this.options.repoRoot, record.project);
    const command = [this.dotnet, "run", ...(noBuild ? ["--no-build"] : []), "--project", project];
    const env: Record<string, string | undefined> = { ...process.env, UKIYO_RESTORE: restore };
    if (!restore) delete env.UKIYO_RESTORE;
    const proc = this.spawn(command, { cwd: this.options.repoRoot, env });
    record.proc = proc;
    record.pid = null;
    record.exitCode = null;
    record.error = null;
    record.sinceMs = Date.now();
    const drained = Promise.all([this.pump(record, proc.stdout, "stdout"), this.pump(record, proc.stderr, "stderr")]);

    const timeout = this.options.attachTimeoutMs ?? 180_000;
    record.timer = setInterval(() => {
      if (record.proc !== proc) return this.clearTimer(record);
      const session = this.options.sessions().find((s) => this.descendsFrom(s.pid, proc.pid) && Date.parse(s.startedAt) >= record.sinceMs - 2_000);
      if (session) {
        this.clearTimer(record);
        record.pid = session.pid;
        if (record.state !== "reloading") record.state = "running";
        record.detail = `Attached to ${session.game} (pid ${session.pid})`;
      } else if (Date.now() - record.sinceMs > timeout) {
        this.clearTimer(record);
        record.error = `[LAUNCH]: ${record.name} did not announce itself within ${Math.round(timeout / 1000)} s`;
        record.state = "failed";
        record.detail = record.error;
        proc.kill("SIGKILL");
      }
    }, this.options.attachPollMs ?? 250);

    void proc.exited.then(async (code) => {
      await drained; // the last lines (compiler errors) can arrive after the exit is reported
      if (record.proc !== proc) return; // replaced by a reload
      this.clearTimer(record);
      record.exitCode = code;
      if (record.state === "stopping" || record.state === "stopped") return;
      if (record.state === "failed") return;
      if (record.pid !== null && record.state === "running") {
        record.state = "stopped";
        record.detail = `${record.name} closed`;
        record.pid = null;
        return;
      }
      record.state = "failed";
      record.pid = null;
      const why = record.errors.length > 0 ? record.errors.join("\n") : record.output.slice(-6).join("\n");
      record.error = `${record.name} stopped before it was running (exit ${code})${why ? `:\n${why}` : ""}`;
      record.detail = record.errors[0] ?? `Exited with code ${code} before it announced itself`;
    });
  }

  /** Whether `pid` is `ancestor` or one of its descendants (`dotnet run` may start the game through a host process). */
  private descendsFrom(pid: number, ancestor: number): boolean {
    let current: number | null = pid;
    for (let depth = 0; depth < 5 && current !== null && current > 1; depth++) {
      if (current === ancestor) return true;
      current = this.parentOf(current);
    }
    return false;
  }

  private clearTimer(record: Record_) {
    if (record.timer) clearInterval(record.timer);
    record.timer = null;
  }

  /** SIGTERM the game and its `dotnet run`, SIGKILL what is left after the grace period. */
  private async terminate(record: Record_): Promise<void> {
    this.clearTimer(record);
    const proc = record.proc;
    record.proc = null; // its exit is expected: the exit handler ignores a process that is no longer the record's
    const gamePid = record.pid;
    const grace = this.options.killGraceMs ?? 4_000;
    if (gamePid !== null && alive(gamePid)) process.kill(gamePid, "SIGTERM");
    proc?.kill("SIGTERM");
    if (proc) await Promise.race([proc.exited, sleep(grace)]);
    if (gamePid !== null && alive(gamePid)) process.kill(gamePid, "SIGKILL");
    if (proc) {
      proc.kill("SIGKILL");
      await Promise.race([proc.exited, sleep(2_000)]);
    }
    if (gamePid !== null) {
      for (const session of this.options.sessions()) if (session.pid === gamePid) rmSync(session.file, { force: true });
    }
  }

  private async runToEnd(record: Record_, command: string[]): Promise<number> {
    const proc = this.spawn(command, { cwd: this.options.repoRoot, env: { ...process.env } });
    const drained = Promise.all([this.pump(record, proc.stdout, "stdout"), this.pump(record, proc.stderr, "stderr")]);
    const code = await proc.exited;
    await drained;
    return code;
  }

  private async reload(record: Record_): Promise<void> {
    const started = Date.now();
    const old = this.options.sessions().find((s) => s.pid === record.pid);
    if (!old) throw new Error(`[RELOAD]: ${record.name} (pid ${record.pid}) is no longer announced in ~/.ukiyo/live`);
    const fail = (error: string, extra: Partial<ReloadReport> = {}) => {
      record.reload = { ok: false, at: new Date().toISOString(), error, errors: [...record.errors], ...extra };
    };

    // 1. Build while the old game keeps running: a broken build changes nothing.
    record.errors = [];
    const code = await this.runToEnd(record, [this.dotnet, "build", join(this.options.repoRoot, record.project), "--nologo", "-v", "minimal"]);
    if (code !== 0) {
      record.state = "running";
      record.detail = `Reload not done: the build failed (exit ${code}); ${record.name} keeps running unchanged`;
      fail(`build failed (exit ${code}); the running game was not touched`);
      return;
    }

    // 2. Pause at a tick boundary and take what a fresh process needs. Everything here is one consistent tick.
    record.detail = "Pausing at a tick boundary…";
    const status = await this.options.bridge(old, { cmd: "status" }, 10_000);
    const wasPaused = (status.result as { paused?: boolean } | undefined)?.paused === true;
    const resumeIfNeeded = async () => {
      if (!wasPaused) await this.options.bridge(old, { cmd: "resume" }, 10_000).catch(() => undefined);
    };
    const paused = await this.options.bridge(old, { cmd: "pause" }, 10_000);
    if (!paused.ok) throw new Error(`[RELOAD]: could not pause ${record.name}: ${paused.error}`);
    const snapshot = await this.options.bridge(old, { cmd: "snapshot" }, 10_000);
    const history = await this.options.bridge(old, { cmd: "history" }, 60_000);
    if (!snapshot.ok || !history.ok) {
      await resumeIfNeeded();
      record.state = "running";
      record.detail = `Reload not done: ${snapshot.error ?? history.error}`;
      fail(`${snapshot.error ?? history.error}`);
      return;
    }
    const past = history.result as { tick: number; complete: boolean; log: unknown };
    if (!past.complete) {
      await resumeIfNeeded();
      record.state = "running";
      record.detail = "Reload not done: this session's history is too long to replay";
      fail("the replay log outgrew its limit, so tick-exact restore is not possible; restart the game instead");
      return;
    }

    // 3. Stop the old process and start the rebuilt one with the log. It replays to the tick before announcing itself.
    record.restoreDir = mkdtempSync(join(tmpdir(), "ukiyo-restore-"));
    chmodSync(record.restoreDir, 0o700);
    const restoreFile = join(record.restoreDir, "restore.json");
    writeFileSync(restoreFile, JSON.stringify(past.log), { mode: 0o600 });
    try {
      record.detail = "Stopping the old process…";
      await this.terminate(record);
      record.errors = [];
      record.detail = "Restarting and replaying to tick " + past.tick + "…";
      this.launch(record, { noBuild: true, restore: restoreFile });
      await this.attached(record);
    } finally {
      rmSync(record.restoreDir, { recursive: true, force: true });
      record.restoreDir = null;
    }
    if (record.state === "failed") {
      fail(`${record.error ?? "the relaunch failed"}; the old process had already been stopped`);
      return;
    }

    // 4. Compare the restored game with what the old one had at the same boundary.
    const fresh = this.options.sessions().find((s) => s.pid === record.pid);
    if (!fresh) throw new Error(`[RELOAD]: the relaunched ${record.name} is not announced`);
    const after = await this.options.bridge(fresh, { cmd: "snapshot" }, 10_000);
    if (!after.ok) throw new Error(`[RELOAD]: could not read the restored snapshot: ${after.error}`);
    const differences = snapshotDifferences(snapshot.result, after.result);
    const match = differences.length === 0;
    if (!wasPaused) await this.options.bridge(fresh, { cmd: "resume" }, 10_000);
    record.state = "running";
    record.reloads++;
    record.reload = {
      ok: true, at: new Date().toISOString(), fromTick: past.tick, tick: (after.result as { tick?: number }).tick ?? past.tick,
      match, sourceChanged: sourceChanged(snapshot.result, after.result), differences, wasPaused, ms: Date.now() - started,
    };
    record.detail = match
      ? `Reloaded at tick ${past.tick}: state identical`
      : `Reloaded at tick ${past.tick}: state differs (${differences.length}${differences.length >= 8 ? "+" : ""} fields)`;
  }

  /** Resolves when the launch announced itself (state running/reloading with a pid) or failed. */
  private async attached(record: Record_): Promise<void> {
    while (record.pid === null && record.state !== "failed" && record.state !== "stopped") await sleep(50);
  }
}
