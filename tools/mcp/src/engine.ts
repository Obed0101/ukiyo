// Everything the tools need to drive the engine: locating the repo, resolving games, running dotnet/bun with a
// timeout, and parsing the line protocol the headless host prints ("snapshot {json}", "capture <path>").
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

/** Games are folders under games/ or samples/ with a Shared/ directory and one folder per host. */
export function listGames(root: string): GameInfo[] {
  const games: GameInfo[] = [];
  for (const base of GAME_ROOTS) {
    const baseDir = join(root, base);
    if (!existsSync(baseDir)) continue;
    for (const name of readdirSync(baseDir).sort()) {
      const dir = join(baseDir, name);
      if (!statSync(dir).isDirectory() || !existsSync(join(dir, "Shared"))) continue;
      const hosts: Record<string, string> = {};
      for (const host of readdirSync(dir)) {
        const hostDir = join(dir, host);
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

export type HeadlessOutput = { snapshots: Record<string, unknown>[]; captures: string[]; audio: AudioSummary | null; info: string[] };

export function parseHeadless(stdout: string): HeadlessOutput {
  const output: HeadlessOutput = { snapshots: [], captures: [], audio: null, info: [] };
  for (const line of stdout.split("\n")) {
    if (line.startsWith("snapshot ")) output.snapshots.push(JSON.parse(line.slice("snapshot ".length)));
    else if (line.startsWith("capture ")) output.captures.push(line.slice("capture ".length).trim());
    else if (line.startsWith("audio ")) {
      // audio <path> seconds=… sounds=… peak=… rms=…
      const [, path = "", ...pairs] = line.trim().split(" ");
      const fields = Object.fromEntries(pairs.map((pair) => pair.split("=") as [string, string]));
      output.audio = { path, seconds: Number(fields.seconds), sounds: Number(fields.sounds), peak: Number(fields.peak), rms: Number(fields.rms) };
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
