// The Studio is files: panels/*.js, layouts/*.json, themes/*.json and studio.json (the current composition). This
// module reads and validates them. The server serves the result, the MCP tool and the tests check it, and agents change
// the Studio by editing these files — there is no other place where Studio state lives.
import { existsSync, readFileSync, readdirSync, renameSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

export const STUDIO_DIR = resolve(import.meta.dir, "..");

/** Every theme defines all of these as CSS custom properties (--bg, --panel, …). */
export const THEME_TOKENS = [
  "bg",
  "panel",
  "panel-raised",
  "line",
  "line-strong",
  "fg",
  "muted",
  "subtle",
  "accent",
  "accent-fg",
  "ok",
  "warn",
  "danger",
  "screen",
  "screen-fg",
  "screen-muted",
  "screen-line",
  "selection",
  "selection-fg",
  "radius-control",
  "radius-panel",
  "font-sans",
  "font-mono",
  "font-pixel",
] as const;

export const ID_PATTERN = /^[a-z][a-z0-9-]{0,47}$/;

export type Layout = { description: string; columns: string; rows: string; areas: string[] };
export type Theme = { description: string; colorScheme: "dark" | "light"; tokens: Record<string, string> };
export type Composition = {
  layout: string;
  theme: string;
  auto: { dark: string; light: string };
  hide: string[];
  show: string[];
  panels: Record<string, Record<string, unknown>>;
};
export type PanelEntry = { id: string; file: string; version: number };
export type Manifest = {
  panels: PanelEntry[];
  layouts: Record<string, Layout & { panels: string[] }>;
  themes: Record<string, Theme>;
  composition: Composition;
};

export class StudioFileError extends Error {
  constructor(file: string, message: string) {
    super(`[STUDIO]: ${file}: ${message}`);
  }
}

const DEFAULT_COMPOSITION: Composition = { layout: "balanced", theme: "auto", auto: { dark: "ink", light: "paper" }, hide: [], show: [], panels: {} };

function readJson(path: string): unknown {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    throw new StudioFileError(path, error instanceof Error ? error.message : String(error));
  }
}

function jsonFiles(directory: string): string[] {
  return existsSync(directory) ? readdirSync(directory).filter((f) => f.endsWith(".json")).sort() : [];
}

/** Panel ids named by a layout, in reading order (left to right, top to bottom). "." is an empty cell. */
export function layoutPanels(layout: Layout): string[] {
  const seen: string[] = [];
  for (const row of layout.areas) {
    for (const cell of row.trim().split(/\s+/)) {
      if (cell !== "." && !seen.includes(cell)) seen.push(cell);
    }
  }
  return seen;
}

const CSS_VALUE = /^[^;{}<>\\]{1,200}$/;

/** Checks the grid strings a layout feeds to CSS: equal row widths, known cell names and rectangular areas. */
export function checkLayout(name: string, value: unknown): Layout {
  const file = `layouts/${name}.json`;
  if (typeof value !== "object" || value === null) throw new StudioFileError(file, "must be an object");
  const layout = value as Partial<Layout>;
  if (typeof layout.description !== "string") throw new StudioFileError(file, "needs a description");
  for (const key of ["columns", "rows"] as const) {
    if (typeof layout[key] !== "string" || !CSS_VALUE.test(layout[key] as string)) throw new StudioFileError(file, `${key} must be a CSS grid track list`);
  }
  if (!Array.isArray(layout.areas) || layout.areas.length === 0 || layout.areas.some((row) => typeof row !== "string")) {
    throw new StudioFileError(file, "areas must be a non-empty array of strings");
  }

  const grid = layout.areas.map((row) => row.trim().split(/\s+/));
  const width = grid[0]!.length;
  if (grid.some((row) => row.length !== width)) throw new StudioFileError(file, "every areas row needs the same number of cells");
  if (layout.columns!.trim().split(/\s+(?![^(]*\))/).length !== width) throw new StudioFileError(file, `columns must list ${width} tracks`);
  if (layout.rows!.trim().split(/\s+(?![^(]*\))/).length !== grid.length) throw new StudioFileError(file, `rows must list ${grid.length} tracks`);

  for (const id of layoutPanels(layout as Layout)) {
    if (!ID_PATTERN.test(id)) throw new StudioFileError(file, `"${id}" is not a panel id`);
    let top = Infinity, left = Infinity, bottom = -1, right = -1, cells = 0;
    grid.forEach((row, y) => row.forEach((cell, x) => {
      if (cell !== id) return;
      cells++;
      top = Math.min(top, y); bottom = Math.max(bottom, y);
      left = Math.min(left, x); right = Math.max(right, x);
    }));
    if (cells !== (bottom - top + 1) * (right - left + 1)) throw new StudioFileError(file, `area "${id}" is not a rectangle`);
  }

  return { description: layout.description, columns: layout.columns!, rows: layout.rows!, areas: layout.areas };
}

export function checkTheme(name: string, value: unknown): Theme {
  const file = `themes/${name}.json`;
  if (typeof value !== "object" || value === null) throw new StudioFileError(file, "must be an object");
  const theme = value as Partial<Theme>;
  if (typeof theme.description !== "string") throw new StudioFileError(file, "needs a description");
  if (theme.colorScheme !== "dark" && theme.colorScheme !== "light") throw new StudioFileError(file, 'colorScheme must be "dark" or "light"');
  if (typeof theme.tokens !== "object" || theme.tokens === null) throw new StudioFileError(file, "needs tokens");
  const tokens = theme.tokens as Record<string, unknown>;
  const missing = THEME_TOKENS.filter((t) => typeof tokens[t] !== "string");
  if (missing.length) throw new StudioFileError(file, `missing tokens: ${missing.join(", ")}`);
  const unknown = Object.keys(tokens).filter((t) => !(THEME_TOKENS as readonly string[]).includes(t));
  if (unknown.length) throw new StudioFileError(file, `unknown tokens: ${unknown.join(", ")} (add them to THEME_TOKENS and every theme together)`);
  for (const [token, css] of Object.entries(tokens)) {
    if (!CSS_VALUE.test(css as string)) throw new StudioFileError(file, `token ${token} has an unsafe or empty value`);
  }
  return { description: theme.description, colorScheme: theme.colorScheme, tokens: tokens as Record<string, string> };
}

type Known = { panels: string[]; layouts: string[]; themes: string[] };

export function checkComposition(value: unknown, known: Known): Composition {
  const file = "studio.json";
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new StudioFileError(file, "must be an object");
  const input = value as Record<string, unknown>;
  const allowed = new Set(["$comment", "layout", "theme", "auto", "hide", "show", "panels"]);
  const extra = Object.keys(input).filter((k) => !allowed.has(k));
  if (extra.length) throw new StudioFileError(file, `unknown fields: ${extra.join(", ")}`);

  const composition: Composition = structuredClone(DEFAULT_COMPOSITION);
  if (input.layout !== undefined) {
    if (typeof input.layout !== "string" || !known.layouts.includes(input.layout)) throw new StudioFileError(file, `layout must be one of ${known.layouts.join(", ")}`);
    composition.layout = input.layout;
  }
  if (input.theme !== undefined) {
    if (typeof input.theme !== "string" || (input.theme !== "auto" && !known.themes.includes(input.theme))) throw new StudioFileError(file, `theme must be "auto" or one of ${known.themes.join(", ")}`);
    composition.theme = input.theme;
  }
  if (input.auto !== undefined) {
    const auto = input.auto as Record<string, unknown>;
    if (typeof auto !== "object" || auto === null || !known.themes.includes(auto.dark as string) || !known.themes.includes(auto.light as string)) {
      throw new StudioFileError(file, "auto must be {\"dark\": <theme>, \"light\": <theme>}");
    }
    composition.auto = { dark: auto.dark as string, light: auto.light as string };
  }
  for (const key of ["hide", "show"] as const) {
    if (input[key] === undefined) continue;
    const list = input[key];
    if (!Array.isArray(list) || list.some((id) => typeof id !== "string" || !known.panels.includes(id))) {
      throw new StudioFileError(file, `${key} must list panel ids from panels/: ${known.panels.join(", ")}`);
    }
    composition[key] = [...new Set(list as string[])];
  }
  if (input.panels !== undefined) {
    const panels = input.panels as Record<string, unknown>;
    if (typeof panels !== "object" || panels === null || Array.isArray(panels)) throw new StudioFileError(file, "panels must map panel ids to option objects");
    for (const [id, options] of Object.entries(panels)) {
      if (!known.panels.includes(id)) throw new StudioFileError(file, `panels.${id}: no such panel`);
      if (typeof options !== "object" || options === null || Array.isArray(options)) throw new StudioFileError(file, `panels.${id} must be an object`);
    }
    if (JSON.stringify(panels).length > 64_000) throw new StudioFileError(file, "panel options are larger than 64 KB");
    composition.panels = panels as Record<string, Record<string, unknown>>;
  }
  return composition;
}

function knownIds(dir: string): Known {
  const panels = existsSync(join(dir, "panels"))
    ? readdirSync(join(dir, "panels")).filter((f) => f.endsWith(".js")).map((f) => f.slice(0, -3)).filter((id) => ID_PATTERN.test(id)).sort()
    : [];
  const names = (sub: string) => jsonFiles(join(dir, sub)).map((f) => f.slice(0, -5)).filter((id) => ID_PATTERN.test(id));
  return { panels, layouts: names("layouts"), themes: names("themes") };
}

/** Reads every Studio file. Throws StudioFileError naming the file and the problem. */
export function readManifest(dir = STUDIO_DIR): Manifest {
  const known = knownIds(dir);
  const panels = known.panels.map((id) => {
    const file = join(dir, "panels", `${id}.js`);
    return { id, file: `panels/${id}.js`, version: Math.round(statSync(file).mtimeMs) };
  });

  const layouts: Manifest["layouts"] = {};
  for (const name of known.layouts) {
    const layout = checkLayout(name, readJson(join(dir, "layouts", `${name}.json`)));
    const ids = layoutPanels(layout);
    const missing = ids.filter((id) => !known.panels.includes(id));
    if (missing.length) throw new StudioFileError(`layouts/${name}.json`, `no panel file for ${missing.join(", ")} (expected panels/<id>.js)`);
    layouts[name] = { ...layout, panels: ids };
  }

  const themes: Manifest["themes"] = {};
  for (const name of known.themes) themes[name] = checkTheme(name, readJson(join(dir, "themes", `${name}.json`)));

  const compositionFile = join(dir, "studio.json");
  const composition = existsSync(compositionFile) ? checkComposition(readJson(compositionFile), known) : structuredClone(DEFAULT_COMPOSITION);
  if (!known.layouts.includes(composition.layout)) throw new StudioFileError("studio.json", `layout ${composition.layout} does not exist`);
  return { panels, layouts, themes, composition };
}

/** Every problem in the Studio files, or an empty list. Used by tests and by ukiyo_studio {"action":"check"}. */
export function checkStudio(dir = STUDIO_DIR): string[] {
  try {
    readManifest(dir);
    return [];
  } catch (error) {
    return [error instanceof Error ? error.message : String(error)];
  }
}

export type CompositionPatch = Partial<Pick<Composition, "layout" | "theme" | "auto" | "hide" | "show">> & {
  panels?: Record<string, Record<string, unknown> | null>;
};

/**
 * Applies a patch to studio.json and writes it atomically. `panels.<id>: null` removes that panel's options. Humans
 * (the layout controls in the Studio) and agents (ukiyo_studio compose, or a plain file edit) change the same file.
 */
export function writeComposition(patch: CompositionPatch, dir = STUDIO_DIR): Composition {
  const known = knownIds(dir);
  const file = join(dir, "studio.json");
  const current = existsSync(file) ? (readJson(file) as Record<string, unknown>) : {};
  const panels = { ...((current.panels as Record<string, Record<string, unknown>> | undefined) ?? {}) };
  for (const [id, options] of Object.entries(patch.panels ?? {})) {
    if (options === null) delete panels[id];
    else panels[id] = options;
  }
  const next: Record<string, unknown> = { ...current, ...patch, panels };
  const composition = checkComposition(next, known);
  const temporary = `${file}.${process.pid}.tmp`;
  writeFileSync(temporary, `${JSON.stringify({ $comment: current.$comment ?? COMPOSITION_COMMENT, ...composition }, null, 2)}\n`);
  renameSync(temporary, file);
  return composition;
}

const COMPOSITION_COMMENT = "What the Studio shows. The layout menu in the Studio, ukiyo_studio compose and plain edits all change this file; the Studio applies it live.";
