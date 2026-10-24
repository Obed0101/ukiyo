// Projects: the games and samples the Studio can open, with the little it can honestly say about each, when the person
// last opened it, and how a new one is made. Discovery is the launcher's (a project is a game with a Desktop host);
// metadata is read from the game's Shared/ source the way ukiyo_status does; creation is the ukiyo_new_game tool itself.
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { validate } from "../../tools/mcp/src/protocol.ts";
import { declaredProfile, sharedSourceLines } from "../../tools/mcp/src/game-source.ts";
import newGame from "../../tools/mcp/src/tools/new-game.ts";
import { discoverGames, type GameEntry } from "./launcher.ts";

export type ProjectTemplate = "2d" | "3d" | "lit";

export type Project = {
  /** Launcher id ("game/Foo", "sample/Bar"): what POST /api/run takes. */
  id: string;
  name: string;
  kind: GameEntry["kind"];
  /** What the source declares: a 2D camera, the lit 3D profile, or plain 3D. */
  template: ProjectTemplate;
  /** Repository-relative folder, e.g. games/Foo. */
  path: string;
  /** Absolute folder, for reveal and copy. */
  dir: string;
  /** False when the desktop host does not link the dev bridge, so the Studio cannot attach to it. */
  bridge: boolean;
  /** ISO time of the last time the person opened it in the Studio, or null. */
  lastOpened: string | null;
};

export class ProjectError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

// ── recents ───────────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Last-opened times live outside the repository, in the person's home (~/.ukiyo/studio-recent.json, next to the
 * ~/.ukiyo/studio and ~/.ukiyo/live announcements). They are state, not configuration: the user config folder is for
 * dotfiles people edit and commit, and a timestamp that changes on every click does not belong in them.
 */
export function recentsFile(env: Record<string, string | undefined> = process.env): string {
  return env.UKIYO_STUDIO_RECENT ?? join(homedir(), ".ukiyo", "studio-recent.json");
}

const RECENT_LIMIT = 200;

export function readRecents(file = recentsFile()): Record<string, string> {
  try {
    const value = JSON.parse(readFileSync(file, "utf8"));
    if (typeof value !== "object" || value === null || Array.isArray(value)) return {};
    return Object.fromEntries(Object.entries(value).filter(([, at]) => typeof at === "string" && !Number.isNaN(Date.parse(at as string)))) as Record<string, string>;
  } catch {
    return {};
  }
}

export function recordOpened(id: string, { file = recentsFile(), now = new Date() }: { file?: string; now?: Date } = {}): void {
  const recents = { ...readRecents(file), [id]: now.toISOString() };
  const newest = Object.entries(recents).sort((a, b) => b[1].localeCompare(a[1])).slice(0, RECENT_LIMIT);
  mkdirSync(dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(Object.fromEntries(newest), null, 2)}\n`);
  renameSync(temporary, file);
}

// ── discovery ─────────────────────────────────────────────────────────────────────────────────────────────────────

function templateOf(repoRoot: string, game: GameEntry): ProjectTemplate {
  try {
    const dir = join(repoRoot, game.kind === "sample" ? "samples" : "games", game.name);
    const lines = sharedSourceLines(repoRoot, dir);
    if (declaredProfile(lines).profile === "G1Lit") return "lit";
    return lines.some((line) => /\bCamera2D\b/.test(line.text)) ? "2d" : "3d";
  } catch {
    return "3d";
  }
}

/** Every project, most recently opened first, then by name. */
export function listProjects(repoRoot: string, recents = readRecents()): Project[] {
  const projects = discoverGames(repoRoot).map((game): Project => {
    const folder = game.kind === "sample" ? "samples" : "games";
    return {
      id: game.id, name: game.name, kind: game.kind, template: templateOf(repoRoot, game),
      path: `${folder}/${game.name}`, dir: join(repoRoot, folder, game.name), bridge: game.bridge, lastOpened: recents[game.id] ?? null,
    };
  });
  return projects.sort((a, b) => (b.lastOpened ?? "").localeCompare(a.lastOpened ?? "") || a.name.localeCompare(b.name));
}

/** The project a request names. It names an id from the listing, never a path. */
export function findProject(repoRoot: string, id: unknown): Project {
  const project = typeof id === "string" ? listProjects(repoRoot).find((p) => p.id === id) : undefined;
  if (!project) throw new ProjectError(`[PROJECT]: ${JSON.stringify(id)} is not a project in this repository`, 404);
  return project;
}

// ── creation ──────────────────────────────────────────────────────────────────────────────────────────────────────

const HOSTS = ["Headless", "Desktop", "Browser", "Export.Desktop"];

/** Scaffolds games/<name> with the ukiyo_new_game tool (one implementation of the layout) and returns the new project. */
export async function createProject({ repoRoot, name, template, log }: {
  repoRoot: string;
  name: unknown;
  template: unknown;
  log: (level: "debug" | "info" | "warn" | "error", event: string, data?: Record<string, unknown>) => void;
}): Promise<Project> {
  try {
    const args = validate(newGame.inputSchema, { name, template: template ?? "2d", hosts: HOSTS }) as Record<string, unknown>;
    await newGame.run(args, { root: repoRoot, log, signal: new AbortController().signal });
  } catch (error) {
    // The tool's own wording, without its protocol tag.
    const message = (error instanceof Error ? error.message : String(error)).replace(/^\[MCP:InvalidParams\]: /, "");
    throw new ProjectError(message, 422);
  }
  const project = listProjects(repoRoot).find((p) => p.name === name && p.kind === "game");
  if (!project) throw new ProjectError(`[PROJECT]: games/${String(name)} was created but has no desktop host the Studio can run`, 500);
  return project;
}

// ── reveal ────────────────────────────────────────────────────────────────────────────────────────────────────────

/** Opening a folder in Finder is the one thing the server can do for the person on macOS only. */
export const canReveal = (platform: string = process.platform) => platform === "darwin";

export function reveal(path: string, { spawn = (command: string[]) => void Bun.spawn(command, { stdin: "ignore", stdout: "ignore", stderr: "ignore" }) }: { spawn?: (command: string[]) => void } = {}): void {
  if (!canReveal()) throw new ProjectError("[PROJECT]: revealing a folder is only available on macOS", 501);
  if (!existsSync(path)) throw new ProjectError("[PROJECT]: that folder does not exist yet", 404);
  spawn(["open", "-R", path]);
}
