// The dev-time asset importer (tools/Ukiyo.Import, docs/assets.md) as the MCP tools see it: finding import specs,
// keeping their paths inside the repository, building the importer once and running it one spec at a time, and
// reading its documented output ("[import] up to date|wrote <output> (<summary>)" on stdout, "[IMPORT:<Code>]: …" on
// stderr, exit 0 ok · 1 import error or stale · 2 usage). Nothing here knows what kind of asset a spec describes.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { dotnetCommand, findGame, insideRoot, listGames, runProcess, tail } from "./engine.ts";
import { ToolInputError } from "./protocol.ts";

export const SPEC_SUFFIX = ".import.json";
export const IMPORTER_PROJECT = "tools/Ukiyo.Import/Ukiyo.Import.csproj";
const SKIPPED_DIRS = new Set(["bin", "obj", "node_modules"]);
/** Spec fields that name files. Specs are passed through as they are; only these are checked to stay in the repo. */
const PATH_FIELDS = ["output", "input", "inputs"] as const;

/** Every *.import.json under a folder, skipping bin/obj/node_modules, in ordinal path order (as ImportSpec.Find). */
export function findSpecs(dir: string): string[] {
  const found: string[] = [];
  const walk = (current: string) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = join(current, entry.name);
      if (entry.isDirectory()) {
        if (!SKIPPED_DIRS.has(entry.name)) walk(path);
      } else if (entry.name.endsWith(SPEC_SUFFIX)) {
        found.push(path);
      }
    }
  };
  if (existsSync(dir) && statSync(dir).isDirectory()) walk(dir);
  return found.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** Absolute spec paths for a game and/or explicit spec files and folders; refuses anything outside the repository. */
export function resolveSpecTargets(root: string, { game, specs = [], whenEmpty }: { game?: string; specs?: string[]; whenEmpty?: "all" }): string[] {
  if (game === undefined && specs.length === 0) {
    if (whenEmpty !== "all") throw new ToolInputError("pass a game (every *.import.json under it) or specs (spec files or folders)");
    const everything = listGames(root).flatMap((info) => findSpecs(info.dir));
    if (everything.length === 0) throw new ToolInputError("no *" + SPEC_SUFFIX + " in any game under games/ or samples/ (see docs/assets.md)");
    return everything;
  }
  const targets: string[] = [];
  if (game !== undefined) {
    const info = findGame(root, game);
    const found = findSpecs(info.dir);
    if (found.length === 0) throw new ToolInputError(`${info.name} has no *${SPEC_SUFFIX} (put one next to the source art, e.g. ${relative(root, join(info.dir, "art", "sprites" + SPEC_SUFFIX))}; see docs/assets.md)`);
    targets.push(...found);
  }
  for (const spec of specs) {
    const absolute = insideRoot(root, spec);
    if (existsSync(absolute) && statSync(absolute).isDirectory()) {
      const found = findSpecs(absolute);
      if (found.length === 0) throw new ToolInputError(`no *${SPEC_SUFFIX} under ${spec}`);
      targets.push(...found);
    } else {
      targets.push(absolute); // a missing file is reported by the importer as [IMPORT:MissingFile]
    }
  }
  return [...new Set(targets)];
}

/**
 * Refuses a spec whose output or inputs resolve outside the repository (the importer itself does not check this).
 * Unreadable or invalid JSON is left to the importer, which reports it as a typed error.
 */
export function assertSpecInsideRoot(root: string, specPath: string): void {
  let spec: unknown;
  try {
    spec = JSON.parse(readFileSync(specPath, "utf8"));
  } catch {
    return;
  }
  if (typeof spec !== "object" || spec === null || Array.isArray(spec)) return;
  const record = spec as Record<string, unknown>;
  for (const field of PATH_FIELDS) {
    const value = record[field];
    const paths = typeof value === "string" ? [value] : Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
    for (const path of paths) {
      const absolute = isAbsolute(path) ? resolve(path) : resolve(dirname(specPath), path);
      const rel = relative(root, absolute);
      if (rel.startsWith("..") || isAbsolute(rel)) {
        throw new ToolInputError(`${relative(root, specPath)}: "${field}" ${path} resolves outside the ukiyo repository; keep source art and generated files inside it`);
      }
    }
  }
}

export type ImportedOutput = { state: "up-to-date" | "wrote"; output: string; summary: string; contentHash: string | null };

/** Reads the importer's stdout lines; the content hash is the short one it prints ("content <hex>"). */
export function parseImportStdout(stdout: string): { outputs: ImportedOutput[]; info: string[] } {
  const outputs: ImportedOutput[] = [];
  const info: string[] = [];
  for (const line of stdout.split("\n").map((l) => l.trimEnd())) {
    const match = line.match(/^\[import\] (up to date|wrote) (.+?) \((.*)\)$/);
    if (match) {
      const [, state, output = "", summary = ""] = match;
      outputs.push({ state: state === "wrote" ? "wrote" : "up-to-date", output, summary, contentHash: summary.match(/\bcontent ([0-9a-f]{6,64})\b/)?.[1] ?? null });
    } else if (line.startsWith("[import]")) {
      info.push(line);
    }
  }
  return { outputs, info };
}

export type ImportFailure = { code: string; message: string };

/** The typed error the importer printed on stderr ("[IMPORT:<Code>]: message"), or null if there is none. */
export function parseImportError(stderr: string): ImportFailure | null {
  const match = stderr.match(/^\[IMPORT:([A-Za-z]+)\]: (.*)$/m);
  return match ? { code: match[1] ?? "", message: match[0].trim() } : null;
}

/** `[IMPORT:Stale]: <output> is missing|out of date; run: <command>` → its parts. */
export function parseStale(message: string): { output: string; reason: "missing" | "out of date"; regenerate: string } | null {
  const match = message.match(/^\[IMPORT:Stale\]: (.+?) is (missing|out of date); run: (.+)$/);
  if (!match) return null;
  return { output: match[1] ?? "", reason: match[2] === "missing" ? "missing" : "out of date", regenerate: match[3] ?? "" };
}

/** The full 64-hex ContentHash constant from a generated file, when it has one that extends the printed prefix. */
export function readContentHash(root: string, output: string, prefix: string | null): string | null {
  const path = resolve(root, output);
  if (!existsSync(path)) return null;
  const full = readFileSync(path, "utf8").match(/\bContentHash\s*=\s*"([0-9a-f]{64})"/)?.[1] ?? null;
  return full && (prefix === null || full.startsWith(prefix)) ? full : null;
}

export type SpecStatus = "up-to-date" | "wrote" | "ok" | "stale" | "error";

export type SpecResult = {
  spec: string;
  status: SpecStatus;
  exitCode: number;
  outputs: ImportedOutput[];
  info: string[];
  error?: ImportFailure;
  stale?: { output: string; reason: "missing" | "out of date"; regenerate: string };
};

/** Turns one importer process result into a spec result. Pure, so the mapping is testable without dotnet. */
export function specResult(spec: string, run: { code: number; stdout: string; stderr: string; timedOut: boolean }): SpecResult {
  const { outputs, info } = parseImportStdout(run.stdout);
  const base = { spec, exitCode: run.code, outputs, info };
  if (run.timedOut) return { ...base, status: "error", error: { code: "Timeout", message: `importer timed out on ${spec}` } };
  if (run.code === 0) {
    const status: SpecStatus = outputs.some((o) => o.state === "wrote") ? "wrote" : outputs.length > 0 ? "up-to-date" : "ok";
    return { ...base, status };
  }
  const error = parseImportError(run.stderr);
  if (run.code === 1 && error) {
    const stale = error.code === "Stale" ? parseStale(error.message) : null;
    return stale ? { ...base, status: "stale", error, stale } : { ...base, status: error.code === "Stale" ? "stale" : "error", error };
  }
  const code = run.code === 2 ? "Usage" : "Crash";
  return { ...base, status: "error", error: { code, message: `importer exited ${run.code} on ${spec}: ${tail(`${run.stderr}\n${run.stdout}`, 12).trim()}` } };
}

type RunOptions = { signal?: AbortSignal; timeoutMs: number };

/** Builds the importer (incremental) and returns the path of its assembly, or the tail of the failed build. */
export async function buildImporter(root: string, { signal, timeoutMs }: RunOptions): Promise<{ ok: true; dll: string; ms: number } | { ok: false; message: string }> {
  const { command, env } = dotnetCommand();
  const project = join(root, IMPORTER_PROJECT);
  const build = await runProcess(command, ["build", project, "-c", "Debug", "--nologo", "-t:Build", "-getProperty:TargetPath"], { cwd: root, env, timeoutMs, signal });
  const dll = build.stdout.trim().split("\n").at(-1)?.trim() ?? "";
  if (build.code !== 0 || build.timedOut || !dll.endsWith(".dll") || !existsSync(dll)) {
    return { ok: false, message: `importer build failed (exit ${build.code}${build.timedOut ? ", timed out" : ""}):\n${tail(`${build.stdout}\n${build.stderr}`, 30)}` };
  }
  return { ok: true, dll, ms: build.ms };
}

/** Where the importer assembly is, without building (MSBuild evaluation only). */
export async function importerAssemblyPath(root: string, { signal, timeoutMs }: RunOptions): Promise<string | null> {
  const { command, env } = dotnetCommand();
  const evaluation = await runProcess(command, ["msbuild", join(root, IMPORTER_PROJECT), "-p:Configuration=Debug", "-nologo", "-getProperty:TargetPath"], { cwd: root, env, timeoutMs, signal });
  const dll = evaluation.stdout.trim().split("\n").at(-1)?.trim() ?? "";
  return evaluation.code === 0 && dll.endsWith(".dll") ? dll : null;
}

/** A project file and every project it references, transitively. */
export function projectClosure(projectFile: string): string[] {
  const seen = new Set<string>();
  const visit = (file: string) => {
    const absolute = resolve(file);
    if (seen.has(absolute) || !existsSync(absolute)) return;
    seen.add(absolute);
    for (const match of readFileSync(absolute, "utf8").matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
      visit(resolve(dirname(absolute), (match[1] ?? "").replaceAll("\\", "/")));
    }
  };
  visit(projectFile);
  return [...seen];
}

function newestFile(dir: string): { path: string; mtimeMs: number } | null {
  let newest: { path: string; mtimeMs: number } | null = null;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory() && SKIPPED_DIRS.has(entry.name)) continue;
    const path = join(dir, entry.name);
    const candidate = entry.isDirectory() ? newestFile(path) : { path, mtimeMs: statSync(path).mtimeMs };
    if (candidate && (!newest || candidate.mtimeMs > newest.mtimeMs)) newest = candidate;
  }
  return newest;
}

export type ImporterBuildState = { built: true; dll: string } | { built: false; reason: string };

/**
 * Whether an existing importer build can be trusted without rebuilding: the assembly exists and is newer than every
 * file of the importer, the projects it references and the repository build files. Conservative: any newer file
 * (even an unchanged one that was touched) means "needs a build".
 */
export function importerBuildState(root: string, dll: string | null): ImporterBuildState {
  if (!dll || !existsSync(dll)) return { built: false, reason: "the importer is not built" };
  const built = statSync(dll).mtimeMs;
  const inputs = projectClosure(join(root, IMPORTER_PROJECT)).map((project) => newestFile(dirname(project)));
  for (const file of ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"]) {
    const path = join(root, file);
    if (existsSync(path)) inputs.push({ path, mtimeMs: statSync(path).mtimeMs });
  }
  const newer = inputs.find((input) => input && input.mtimeMs > built);
  return newer ? { built: false, reason: `${relative(root, newer.path)} is newer than the importer build` } : { built: true, dll };
}

/** Runs the built importer on one spec (cwd = repository root, so printed outputs are repository-relative). */
export async function runImporter(root: string, dll: string, specPath: string, check: boolean, { signal, timeoutMs }: RunOptions): Promise<SpecResult> {
  const { command, env } = dotnetCommand();
  const spec = relative(root, specPath);
  const run = await runProcess(command, [dll, ...(check ? ["--check"] : []), spec], { cwd: root, env, timeoutMs, signal });
  const result = specResult(spec, run);
  for (const output of result.outputs) {
    output.contentHash = readContentHash(root, output.output, output.contentHash) ?? output.contentHash;
  }
  return result;
}

export type SpecCheck = { spec: string; status: string; outputs: { output: string; contentHash: string | null }[]; code?: string; message?: string };
export type ImportCheck = { ran: true; basis: string; results: SpecCheck[] } | { ran: false; reason: string };

/** `--check` on every spec of every game, with an importer build that is already current; never builds. */
export async function checkImports(root: string, specsByGame: Map<string, string[]>, signal: AbortSignal): Promise<Map<string, ImportCheck>> {
  const checks = new Map<string, ImportCheck>();
  if (specsByGame.size === 0) return checks;
  const state = importerBuildState(root, await importerAssemblyPath(root, { signal, timeoutMs: 30_000 }).catch(() => null));
  for (const [game, specs] of specsByGame) {
    if (!state.built) {
      checks.set(game, { ran: false, reason: `not checked: ${state.reason}; ukiyo_import {"action":"check","game":"${game}"} builds it and checks` });
      continue;
    }
    const results: SpecCheck[] = [];
    for (const spec of specs) {
      try {
        assertSpecInsideRoot(root, spec);
      } catch (error) {
        results.push({ spec: relative(root, spec), status: "error", outputs: [], code: "OutsideRepository", message: error instanceof Error ? error.message : String(error) });
        continue;
      }
      const r = await runImporter(root, state.dll, spec, true, { signal, timeoutMs: 30_000 });
      results.push({ spec: r.spec, status: r.status, outputs: r.outputs.map(({ output, contentHash }) => ({ output, contentHash })), ...(r.error ? { code: r.error.code, message: r.error.message } : {}) });
    }
    checks.set(game, { ran: true, basis: `importer --check with the existing build ${relative(root, state.dll)} (newer than its sources)`, results });
  }
  return checks;
}
