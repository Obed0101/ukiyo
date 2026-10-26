// Everything the tools need to drive the engine: locating the repo, resolving games, running dotnet/bun with a
// timeout, and parsing the line protocol the headless host prints ("snapshot {json}", "capture <path>",
// "capture-pass K primary=true|false <path>").
import { existsSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { ToolInputError } from "./protocol.ts";

export const GAME_ROOTS = ["games", "samples"] as const;

/** Walks up from `start` to the directory holding global.json and Ukiyo.slnx. */
export function findRepoRoot(start: string): string {
  let dir = resolve(start);
  while (true) {
    if (existsSync(join(dir, "global.json")) && existsSync(join(dir, "Ukiyo.slnx"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) throw new Error(`[ENGINE]: no ukiyo repository (global.json + Ukiyo.slnx) above ${start}; set UKIYO_ROOT`);
    dir = parent;
  }
}

/** Resolves a path the agent passed and refuses anything outside the repository. */
export function insideRoot(root: string, path: string): string {
  const absolute = isAbsolute(path) ? resolve(path) : resolve(root, path);
  const rel = relative(root, absolute);
  if (rel.startsWith("..") || isAbsolute(rel)) throw new ToolInputError(`${path} is outside the ukiyo repository ${root}`);
  return absolute;
}

export type GameInfo = { name: string; dir: string; hosts: Record<string, string> };

/**
 * Games are folders under games/ or samples/ with a Shared/ directory and one folder per host: <game>/Hosts/<Host>
 * in the template layout (templates/game, docs/game-layout.md), <game>/<Host> in the older one.
 */
export function listGames(root: string): GameInfo[] {
  const games: GameInfo[] = [];
  for (const base of GAME_ROOTS) {
    const baseDir = join(root, base);
    if (!existsSync(baseDir)) continue;
    for (const name of readdirSync(baseDir).sort()) {
      const dir = join(baseDir, name);
      if (!statSync(dir).isDirectory() || !existsSync(join(dir, "Shared"))) continue;
      const hostsDir = existsSync(join(dir, "Hosts")) ? join(dir, "Hosts") : dir;
      const hosts: Record<string, string> = {};
      for (const host of readdirSync(hostsDir).sort()) {
        const hostDir = join(hostsDir, host);
        if (!statSync(hostDir).isDirectory()) continue;
        const project = readdirSync(hostDir).find((file) => file.endsWith(".csproj"));
        if (project) hosts[host] = join(hostDir, project);
      }
      games.push({ name, dir, hosts });
    }
  }
  return games;
}

export function findGame(root: string, name: string): GameInfo {
  const games = listGames(root);
  const game = games.find((g) => g.name.toLowerCase() === name.toLowerCase());
  if (!game) throw new ToolInputError(`no game "${name}". Known: ${games.map((g) => g.name).join(", ") || "none"}`);
  return game;
}

export function hostProject(game: GameInfo, host: string): string {
  const project = game.hosts[host];
  if (!project) throw new ToolInputError(`${game.name} has no ${host} host. It has: ${Object.keys(game.hosts).join(", ")}`);
  return project;
}

/** dotnet from PATH, DOTNET_ROOT or the per-user install (~/.dotnet), in that order. */
export function dotnetCommand(): { command: string; env: Record<string, string> } {
  const env: Record<string, string> = { DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" };
  const candidates = [process.env.DOTNET_ROOT, join(homedir(), ".dotnet")].filter((d): d is string => Boolean(d));
  for (const dir of candidates) {
    const exe = join(dir, "dotnet");
    if (existsSync(exe)) return { command: exe, env: { ...env, DOTNET_ROOT: dir } };
  }
  return { command: "dotnet", env };
}

export type ProcessResult = { code: number; stdout: string; stderr: string; timedOut: boolean; ms: number };

export async function runProcess(command: string, args: string[], options: { cwd: string; env?: Record<string, string>; timeoutMs: number; signal?: AbortSignal }): Promise<ProcessResult> {
  const started = performance.now();
  const child = Bun.spawn([command, ...args], {
    cwd: options.cwd,
    env: { ...process.env, ...options.env },
    stdout: "pipe",
    stderr: "pipe",
    stdin: "ignore",
  });
  let timedOut = false;
  const kill = () => child.kill();
  const timer = setTimeout(() => { timedOut = true; kill(); }, options.timeoutMs);
  options.signal?.addEventListener("abort", kill, { once: true });
  const [stdout, stderr, code] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
  clearTimeout(timer);
  options.signal?.removeEventListener("abort", kill);
  return { code, stdout, stderr, timedOut, ms: Math.round(performance.now() - started) };
}

export type AudioSummary = { path: string; seconds: number; sounds: number; peak: number; rms: number };

/** `skipped` appears only when resuming from a save (--load): patches the save already held. */
export type PatchLogSummary = { path: string; submitted: number; skipped?: number };

/** A `load`/`save` line of the headless host (docs/save-replay.md). */
export type SaveLineSummary = { path: string; tick: number; bytes?: number };

/** One camera of a multi-camera frame, captured alone at its rect size (docs/rendering-passes.md). */
export type PassCapture = { pass: number; primary: boolean; path: string };

/** The per-pass captures of one checkpoint: `frame` is the composed capture they belong to (the `capture` line before them). */
export type CheckpointPasses = { tick: number | null; frame: string; passes: PassCapture[] };

export type HeadlessOutput = {
  snapshots: Record<string, unknown>[];
  captures: string[];
  /** Only multi-camera frames print `capture-pass` lines; empty for single-camera games. */
  passes: CheckpointPasses[];
  audio: AudioSummary | null;
  patches: PatchLogSummary | null;
  /** Present only when the run used --load / --save. */
  load?: SaveLineSummary;
  save?: SaveLineSummary;
  info: string[];
};

export function parseHeadless(stdout: string): HeadlessOutput {
  const output: HeadlessOutput = { snapshots: [], captures: [], passes: [], audio: null, patches: null, info: [] };
  // The passes of the checkpoint whose capture line came last; a new capture line starts the next checkpoint.
  let checkpoint: CheckpointPasses | undefined;
  for (const line of stdout.split("\n")) {
    if (line.startsWith("snapshot ")) output.snapshots.push(JSON.parse(line.slice("snapshot ".length)));
    else if (line.startsWith("capture ")) {
      output.captures.push(line.slice("capture ".length).trim());
      checkpoint = undefined;
    }
    else if (line.startsWith("capture-pass ")) {
      // capture-pass K primary=true|false <path> (the path may contain spaces), after the checkpoint's capture line
      const match = /^capture-pass (\d+) primary=(true|false) (.+)$/.exec(line.trim());
      const frame = output.captures.at(-1);
      if (!match || frame === undefined) continue;
      if (!checkpoint) {
        const tick = output.snapshots.at(-1)?.tick;
        checkpoint = { tick: typeof tick === "number" ? tick : null, frame, passes: [] };
        output.passes.push(checkpoint);
      }
      checkpoint.passes.push({ pass: Number(match[1]), primary: match[2] === "true", path: match[3]! });
    }
    else if (line.startsWith("audio ")) {
      // audio <path> seconds=… sounds=… peak=… rms=…
      const [, path = "", ...pairs] = line.trim().split(" ");
      const fields = Object.fromEntries(pairs.map((pair) => pair.split("=") as [string, string]));
      output.audio = { path, seconds: Number(fields.seconds), sounds: Number(fields.sounds), peak: Number(fields.peak), rms: Number(fields.rms) };
    }
    else if (line.startsWith("patches ")) {
      // patches <path> submitted=N (the path may contain spaces; the count is always last)
      const match = /^patches (.+) submitted=(\d+)(?: skipped=(\d+))?$/.exec(line.trim());
      if (match) output.patches = { path: match[1]!, submitted: Number(match[2]), ...(match[3] === undefined ? {} : { skipped: Number(match[3]) }) };
    }
    else if (line.startsWith("load ") || line.startsWith("save ")) {
      // load <path> tick=N · save <path> tick=N bytes=B (the path may contain spaces; the fields are last)
      const match = /^(load|save) (.+) tick=(\d+)(?: bytes=(\d+))?$/.exec(line.trim());
      if (match) output[match[1] as "load" | "save"] = { path: match[2]!, tick: Number(match[3]), ...(match[4] === undefined ? {} : { bytes: Number(match[4]) }) };
    }
    else if (line.startsWith("[ukiyo]")) output.info.push(line);
  }
  return output;
}

/** Last lines of a log, so a failure is reported with its cause and without flooding the agent's context. */
export function tail(text: string, lines = 40): string {
  const all = text.trimEnd().split("\n");
  return all.slice(-lines).join("\n");
}
