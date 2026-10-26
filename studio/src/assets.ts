// What the Assets panel may see of a game's folder. Two roots only (Shared/assets and art), every path is confined to
// them with realpath so ../, absolute paths and symlinks pointing out all end at the same refusal.
import { readdirSync, realpathSync, statSync } from "node:fs";
import { isAbsolute, join, relative, sep } from "node:path";

export const ASSET_ROOTS = ["Shared/assets", "art"] as const;
export type AssetRoot = (typeof ASSET_ROOTS)[number];

/** Largest file the preview route serves. */
export const PREVIEW_MAX_BYTES = 4 * 1024 * 1024;
/** Entries listed per folder; a folder with more is cut and says so. */
export const LIST_LIMIT = 500;

export class AssetError extends Error {
  constructor(message: string, readonly status: number) {
    super(`[ASSETS]: ${message}`);
  }
}

export type AssetKind = "folder" | "image" | "scene" | "import" | "data" | "audio" | "code" | "file";

export type AssetEntry = {
  name: string;
  /** Path under the asset root, forward slashes. */
  path: string;
  kind: AssetKind;
  size: number;
  /** Only for kind "import": the generated output is missing or out of date. Filled by the route from the importer check. */
  stale?: boolean;
};

export type AssetListing = { root: AssetRoot; path: string; entries: AssetEntry[]; truncated: boolean };

export function isAssetRoot(value: unknown): value is AssetRoot {
  return typeof value === "string" && (ASSET_ROOTS as readonly string[]).includes(value);
}

const IMAGE = new Set([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"]);
const AUDIO = new Set([".wav", ".ogg", ".mp3", ".flac"]);
const DATA = new Set([".json", ".txt", ".csv", ".md", ".toml", ".yaml", ".yml"]);
const CODE = new Set([".cs", ".wgsl", ".js", ".ts"]);

export function classify(name: string): Exclude<AssetKind, "folder"> {
  const lower = name.toLowerCase();
  if (lower.endsWith(".import.json")) return "import";
  const dot = lower.lastIndexOf(".");
  const ext = dot < 0 ? "" : lower.slice(dot);
  if (IMAGE.has(ext)) return "image";
  if (AUDIO.has(ext)) return "audio";
  if (ext === ".json" && /(^|\/)scenes\//.test(lower)) return "scene";
  if (DATA.has(ext)) return "data";
  if (CODE.has(ext)) return "code";
  return "file";
}

/**
 * The real absolute path of `rel` inside `<gameDir>/<root>`, or an AssetError. `rel` is a forward-slash path under the
 * root; "" means the root itself. A root that does not exist yet is a 404, not a way to escape.
 */
export function confine(gameDir: string, root: AssetRoot, rel: string): string {
  if (typeof rel !== "string" || rel.includes("\0")) throw new AssetError("bad path", 400);
  if (isAbsolute(rel) || /^[a-zA-Z]:/.test(rel) || rel.startsWith("\\")) throw new AssetError("path must be relative to the asset root", 400);
  if (rel.split(/[\\/]/).some((part) => part === "..")) throw new AssetError("path may not contain ..", 400);

  let base: string;
  try {
    base = realpathSync(join(gameDir, root));
  } catch {
    throw new AssetError(`${root} does not exist in this project`, 404);
  }
  let target: string;
  try {
    target = realpathSync(join(base, rel));
  } catch {
    throw new AssetError("no such file or folder", 404);
  }
  const inside = relative(base, target);
  if (inside === ".." || inside.startsWith(`..${sep}`) || isAbsolute(inside)) throw new AssetError("path leaves the asset root", 403);
  return target;
}

/** The roots a project has, so the panel can offer only those. */
export function existingRoots(gameDir: string): AssetRoot[] {
  return ASSET_ROOTS.filter((root) => {
    try {
      return statSync(join(gameDir, root)).isDirectory();
    } catch {
      return false;
    }
  });
}

export function listFolder(gameDir: string, root: AssetRoot, rel: string): AssetListing {
  const folder = confine(gameDir, root, rel);
  if (!statSync(folder).isDirectory()) throw new AssetError("not a folder", 400);
  const base = realpathSync(join(gameDir, root));
  const clean = rel.split(/[\\/]/).filter(Boolean).join("/");
  const entries: AssetEntry[] = [];
  for (const name of readdirSync(folder).sort((a, b) => a.localeCompare(b))) {
    if (name.startsWith(".")) continue;
    const full = join(folder, name);
    let real: string;
    try {
      real = realpathSync(full);
    } catch {
      continue; // a dangling link
    }
    const inside = relative(base, real);
    if (inside === ".." || inside.startsWith(`..${sep}`) || isAbsolute(inside)) continue; // a link out of the root is not listed
    const stats = statSync(real);
    const path = clean ? `${clean}/${name}` : name;
    entries.push(stats.isDirectory() ? { name, path, kind: "folder", size: 0 } : { name, path, kind: classify(path), size: stats.size });
  }
  entries.sort((a, b) => (a.kind === "folder" ? 0 : 1) - (b.kind === "folder" ? 0 : 1));
  const truncated = entries.length > LIST_LIMIT;
  return { root, path: clean, entries: truncated ? entries.slice(0, LIST_LIMIT) : entries, truncated };
}

/** Absolute real path of a previewable PNG (size-capped), or an AssetError. */
export function previewPath(gameDir: string, root: AssetRoot, rel: string): string {
  const file = confine(gameDir, root, rel);
  const stats = statSync(file);
  if (!stats.isFile()) throw new AssetError("not a file", 400);
  if (!file.toLowerCase().endsWith(".png")) throw new AssetError("only PNG files have a preview", 415);
  if (stats.size > PREVIEW_MAX_BYTES) throw new AssetError(`image is larger than ${PREVIEW_MAX_BYTES / 1024 / 1024} MB`, 413);
  return file;
}

/** Largest JSON file the text route serves (scene files; the Hierarchy and Inspector read prefab names from them). */
export const TEXT_MAX_BYTES = 256 * 1024;

/** Absolute real path of a small JSON file, or an AssetError. */
export function textPath(gameDir: string, root: AssetRoot, rel: string): string {
  const file = confine(gameDir, root, rel);
  const stats = statSync(file);
  if (!stats.isFile()) throw new AssetError("not a file", 400);
  if (!file.toLowerCase().endsWith(".json")) throw new AssetError("only JSON files are served as text", 415);
  if (stats.size > TEXT_MAX_BYTES) throw new AssetError("file is larger than 256 KB", 413);
  return file;
}
