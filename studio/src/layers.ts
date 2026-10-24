// The person's own Studio files on top of the repo's: ~/.config/ukiyo/studio/ (or $XDG_CONFIG_HOME/ukiyo/studio, or
// $UKIYO_STUDIO_CONFIG) has the same shape as studio/ — studio.json, themes/, layouts/, panels/ — plus an optional
// user.css that loads last. A user file replaces the repo file with the same name, user studio.json fields override the
// repo's, and a broken user file is reported (never fatal): the repo file of that name, or nothing, stays in effect.
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, watch, writeFileSync, type FSWatcher } from "node:fs";
import { homedir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import {
  ID_PATTERN,
  STUDIO_DIR,
  StudioFileError,
  checkComposition,
  checkLayout,
  checkTheme,
  layoutPanels,
  readManifest,
  writeComposition,
  type Composition,
  type CompositionPatch,
  type Manifest,
} from "./manifest.ts";

export type Layer = "repo" | "user";
export type Problem = { layer: Layer; message: string };
export type Sources = {
  panels: Record<string, Layer>;
  layouts: Record<string, Layer>;
  themes: Record<string, Layer>;
  /** Layers that contribute to the composition, lowest first. */
  composition: Layer[];
};
export type LayeredManifest = Manifest & {
  sources: Sources;
  /** Problems in user files. The files are skipped; everything else keeps working. */
  problems: Problem[];
  /** The user config directory in effect, or null when there is none on disk. */
  userDir: string | null;
  /** Version of user.css (its mtime) or null when there is no such file. */
  userCss: number | null;
};

const SUBDIRS = ["panels", "layouts", "themes"] as const;

export function userConfigDir(env: Record<string, string | undefined> = process.env): string {
  if (env.UKIYO_STUDIO_CONFIG) return resolve(env.UKIYO_STUDIO_CONFIG);
  const base = env.XDG_CONFIG_HOME && isAbsolute(env.XDG_CONFIG_HOME) ? env.XDG_CONFIG_HOME : join(homedir(), ".config");
  return join(base, "ukiyo", "studio");
}

export function problemText({ layer, message }: Problem): string {
  return `[${layer}] ${message}`;
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function readJson(path: string): unknown {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    throw new StudioFileError(path, describe(error));
  }
}

function idsIn(directory: string, extension: string): string[] {
  if (!existsSync(directory)) return [];
  return readdirSync(directory)
    .filter((f) => f.endsWith(extension))
    .map((f) => f.slice(0, -extension.length))
    .filter((id) => ID_PATTERN.test(id))
    .sort();
}

const transpiler = new Bun.Transpiler({ loader: "js" });

/** A user panel is a module the page imports: a syntax error must be caught here, not as a blank panel. */
function panelProblem(path: string): string | null {
  try {
    transpiler.transformSync(readFileSync(path, "utf8"));
    return null;
  } catch (error) {
    return `${path}: ${describe(error)}`;
  }
}

/** The file that serves panel `id`: the user's when it exists and parses, else the repo's. */
export function resolvePanel(id: string, dir = STUDIO_DIR, userDir: string | null = userConfigDir()): { path: string; layer: Layer } | null {
  if (!ID_PATTERN.test(id)) return null;
  const user = userDir ? join(userDir, "panels", `${id}.js`) : null;
  if (user && existsSync(user) && panelProblem(user) === null) return { path: user, layer: "user" };
  const repo = join(dir, "panels", `${id}.js`);
  return existsSync(repo) ? { path: repo, layer: "repo" } : null;
}

/** The raw studio.json fields of both layers, user over repo. Lists replace, `auto` and each panel's options merge. */
function mergeStudio(repo: Record<string, unknown>, user: Record<string, unknown>): Record<string, unknown> {
  const merged: Record<string, unknown> = { ...repo, ...user };
  if (repo.auto && user.auto) merged.auto = { ...(repo.auto as object), ...(user.auto as object) };
  if (repo.panels && user.panels) {
    const panels: Record<string, unknown> = { ...(repo.panels as object) };
    for (const [id, options] of Object.entries(user.panels as Record<string, unknown>)) {
      const base = panels[id];
      panels[id] = base && options && typeof base === "object" && typeof options === "object" ? { ...base, ...options } : options;
    }
    merged.panels = panels;
  }
  return merged;
}

function readRaw(path: string): Record<string, unknown> {
  if (!existsSync(path)) return {};
  const value = readJson(path);
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new StudioFileError(path, "must be an object");
  return value as Record<string, unknown>;
}

/**
 * Repo manifest plus the user's layer. A repo file that is broken throws, as before; a user file that is broken becomes
 * a Problem and is skipped. With no user directory this equals readManifest().
 */
export function readLayeredManifest(dir = STUDIO_DIR, userDir: string | null = userConfigDir()): LayeredManifest {
  const base = readManifest(dir);
  const sources: Sources = {
    panels: Object.fromEntries(base.panels.map((p) => [p.id, "repo" as Layer])),
    layouts: Object.fromEntries(Object.keys(base.layouts).map((n) => [n, "repo" as Layer])),
    themes: Object.fromEntries(Object.keys(base.themes).map((n) => [n, "repo" as Layer])),
    composition: ["repo"],
  };
  const result: LayeredManifest = { ...base, sources, problems: [], userDir: null, userCss: null };
  if (!userDir || !existsSync(userDir)) return result;
  result.userDir = userDir;
  const problem = (message: string) => result.problems.push({ layer: "user", message });

  const cssFile = join(userDir, "user.css");
  if (existsSync(cssFile)) result.userCss = Math.round(statSync(cssFile).mtimeMs);

  for (const id of idsIn(join(userDir, "panels"), ".js")) {
    const path = join(userDir, "panels", `${id}.js`);
    const broken = panelProblem(path);
    if (broken) {
      problem(broken);
      continue;
    }
    const entry = { id, file: `panels/${id}.js`, version: Math.round(statSync(path).mtimeMs) };
    const at = result.panels.findIndex((p) => p.id === id);
    if (at >= 0) result.panels[at] = entry;
    else result.panels.push(entry);
    sources.panels[id] = "user";
  }
  result.panels.sort((a, b) => a.id.localeCompare(b.id));
  const panelIds = result.panels.map((p) => p.id);

  // A theme may say "extends": "<theme>" and list only the tokens it changes.
  for (const name of idsIn(join(userDir, "themes"), ".json")) {
    const path = join(userDir, "themes", `${name}.json`);
    try {
      const raw = readJson(path) as Record<string, unknown>;
      const parent = typeof raw?.extends === "string" ? result.themes[raw.extends] : undefined;
      if (raw?.extends !== undefined && !parent) throw new StudioFileError(path, `extends "${String(raw.extends)}": no such theme`);
      const { extends: _extends, ...own } = raw;
      const tokens = { ...parent?.tokens, ...((own.tokens as object | undefined) ?? {}) };
      result.themes[name] = checkTheme(name, { colorScheme: parent?.colorScheme, ...own, tokens });
      sources.themes[name] = "user";
    } catch (error) {
      problem(describe(error));
    }
  }

  for (const name of idsIn(join(userDir, "layouts"), ".json")) {
    const path = join(userDir, "layouts", `${name}.json`);
    try {
      const layout = checkLayout(name, readJson(path));
      const ids = layoutPanels(layout);
      const missing = ids.filter((id) => !panelIds.includes(id));
      if (missing.length) throw new StudioFileError(`layouts/${name}.json`, `no panel file for ${missing.join(", ")} (expected panels/<id>.js in either layer)`);
      result.layouts[name] = { ...layout, panels: ids };
      sources.layouts[name] = "user";
    } catch (error) {
      problem(describe(error));
    }
  }

  const known = { panels: panelIds, layouts: Object.keys(result.layouts), themes: Object.keys(result.themes) };
  // Layouts and themes of the repo may have been replaced; the repo composition must still hold against the merge.
  const userStudio = join(userDir, "studio.json");
  try {
    if (existsSync(userStudio)) {
      const merged = mergeStudio(readRaw(join(dir, "studio.json")), readRaw(userStudio));
      result.composition = checkComposition(merged, known);
      sources.composition = ["repo", "user"];
    }
  } catch (error) {
    problem(describe(error));
  }
  return result;
}

/** Every problem in the repo files and the user's, each tagged with its layer. Empty when everything is valid. */
export function checkLayered(dir = STUDIO_DIR, userDir: string | null = userConfigDir()): string[] {
  try {
    return readLayeredManifest(dir, userDir).problems.map(problemText);
  } catch (error) {
    return [problemText({ layer: "repo", message: describe(error) })];
  }
}

export type CompositionTarget = "user" | "repo";
export type ComposeOptions = {
  dir?: string;
  userDir?: string | null;
  /** The person's UI writes to their dotfiles; tooling writes where the file already lives. */
  from: "ui" | "agent";
  /** Forces a layer. "user" creates the user studio.json. */
  target?: CompositionTarget;
};

/**
 * Where a composition change goes. An existing user studio.json always wins (a repo write would be shadowed by it).
 * Without one, the person's UI creates it (the repo stays clean) and agents write the repo's studio.json, as before.
 */
export function compositionTarget({ dir = STUDIO_DIR, userDir = userConfigDir(), from, target }: ComposeOptions): CompositionTarget {
  if (target === "user" && !userDir) throw new RangeError("[STUDIO]: there is no user config directory");
  if (target) return target;
  if (userDir && existsSync(join(userDir, "studio.json"))) return "user";
  return from === "ui" && userDir ? "user" : "repo";
}

/** Applies a patch to the chosen studio.json (atomically) and returns the effective composition and where it went. */
export function writeLayeredComposition(patch: CompositionPatch, options: ComposeOptions): { composition: Composition; target: CompositionTarget; file: string } {
  const dir = options.dir ?? STUDIO_DIR;
  const userDir = options.userDir === undefined ? userConfigDir() : options.userDir;
  const target = compositionTarget({ ...options, dir, userDir });
  if (target === "repo") {
    writeComposition(patch, dir);
    return { composition: readLayeredManifest(dir, userDir).composition, target, file: join(dir, "studio.json") };
  }

  const file = join(userDir!, "studio.json");
  const current = readRaw(file);
  const next: Record<string, unknown> = { ...current };
  for (const [key, value] of Object.entries(patch)) if (key !== "panels") next[key] = value;
  if (patch.panels) {
    const panels = { ...((current.panels as Record<string, unknown> | undefined) ?? {}) };
    for (const [id, options] of Object.entries(patch.panels)) {
      if (options === null) delete panels[id];
      else panels[id] = options;
    }
    next.panels = panels;
  }

  // Validate against both layers' names before touching the file.
  const staged = readLayeredManifest(dir, userDir);
  const known = { panels: staged.panels.map((p) => p.id), layouts: Object.keys(staged.layouts), themes: Object.keys(staged.themes) };
  checkComposition(mergeStudio(readRaw(join(dir, "studio.json")), next), known);

  mkdirSync(userDir!, { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(next, null, 2)}\n`);
  renameSync(temporary, file);
  return { composition: readLayeredManifest(dir, userDir).composition, target, file };
}

export type FileChange = (label: string) => void;

/**
 * Watches both layers. `onChange` gets a label per changed file: "panels/x.js" for the repo, "user:panels/x.js" for the
 * user's. A user directory (or one of its subfolders) that appears later is picked up within two seconds.
 */
export function watchLayers({ dir = STUDIO_DIR, userDir = userConfigDir() }: { dir?: string; userDir?: string | null }, onChange: FileChange): () => void {
  const roots: [Layer, string][] = [["repo", dir]];
  if (userDir) roots.push(["user", userDir]);
  const open = new Map<string, FSWatcher>();

  const wanted = (layer: Layer, sub: string, name: string | null): name is string => {
    if (!name || name.endsWith(".tmp") || name.startsWith(".")) return false;
    return sub !== "." || name === "studio.json" || (layer === "user" && name === "user.css");
  };

  const attach = (announce: boolean) => {
    for (const [layer, root] of roots) {
      for (const sub of [".", ...SUBDIRS]) {
        const path = join(root, sub);
        if (open.has(path) || !existsSync(path)) continue;
        try {
          const watcher = watch(path, (_event, name) => {
            if (wanted(layer, sub, name)) onChange(`${layer === "user" ? "user:" : ""}${sub === "." ? "" : `${sub}/`}${name}`);
          });
          watcher.on("error", () => {
            watcher.close();
            open.delete(path);
          });
          open.set(path, watcher);
          if (announce) onChange(`${layer === "user" ? "user:" : ""}${sub}`);
        } catch {
          // Unwatchable for now; the next scan tries again.
        }
      }
    }
  };

  attach(false);
  const timer = setInterval(() => attach(true), 2000);
  return () => {
    clearInterval(timer);
    for (const watcher of open.values()) watcher.close();
    open.clear();
  };
}
