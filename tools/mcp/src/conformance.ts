// Does a game follow the layout contract (docs/game-layout.md)? The same rules GameLayoutTests enforces in C#, read
// from the files without building anything, plus the two facts a game is only trusted with: its imported assets are
// not stale (the importer's own --check) and, for samples, its golden snapshot exists. Every finding has a typed code.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import type { GameInfo } from "./engine.ts";
import { checkImports, findSpecs, type ImportCheck } from "./importer.ts";

export type FindingLevel = "error" | "warn" | "info";
export type FindingCode =
  | "MissingEntry" | "HostLayout" | "HostProject" | "PlatformLeak" | "ProgramShape"
  | "StaleAssets" | "ImportError" | "AssetsUnchecked" | "MissingGolden" | "LegacyLayout" | "MissingHost";
export type Finding = { code: FindingCode; level: FindingLevel; path: string; message: string };
export type CheckName = "layout" | "hosts" | "shared" | "program" | "assets" | "golden";
export type CheckStatus = "pass" | "fail" | "skipped" | "unchecked";
export type GameConformance = {
  game: string;
  dir: string;
  layout: "template" | "legacy";
  ok: boolean;
  checks: Record<CheckName, CheckStatus>;
  findings: Finding[];
};

export const HOSTS = ["Headless", "Desktop", "Browser", "Export.Desktop"] as const;
/** Present in every game of the layout; Www/ is required only when a Browser host exists. */
export const REQUIRED_ENTRIES = [
  "Shared/Program.cs", "Shared/Game.cs", "Shared/Scenes", "Shared/Entities", "Shared/Systems", "Shared/Lib",
  "Shared/gen", "Shared/assets", "Platform/Desktop", "Platform/Browser",
] as const;
export const WWW_ENTRIES = ["Www/index.html", "Www/main.js"] as const;
const FORBIDDEN_IN_SHARED = ["JSImport", "JSExport", "LibraryImport", "DllImport", "OperatingSystem.Is"] as const;
const GAME_DIR_LINE = "<UkiyoGameDir>$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '..', '..'))</UkiyoGameDir>";
const SHARED_GLOB = '<UkiyoSharedSource Include="$(UkiyoGameDir)Shared/**/*.cs" />';
const SKIPPED = new Set(["bin", "obj"]);

const finding = (code: FindingCode, level: FindingLevel, path: string, message: string): Finding => ({ code, level, path, message });

function filesBelow(dir: string, keep: (relativePath: string) => boolean = () => true): string[] {
  const found: string[] = [];
  const walk = (current: string) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (SKIPPED.has(entry.name)) continue;
      const path = join(current, entry.name);
      if (entry.isDirectory()) walk(path);
      else if (keep(relative(dir, path))) found.push(path);
    }
  };
  if (existsSync(dir) && statSync(dir).isDirectory()) walk(dir);
  return found.sort();
}

/** Code lines of a C# file: whole-line // comments dropped, as GameLayoutTests does. */
const codeLines = (file: string) => readFileSync(file, "utf8").split("\n").map((text, i) => ({ line: i + 1, text })).filter((l) => !l.text.trimStart().startsWith("//"));

function checkEntries(root: string, game: GameInfo): Finding[] {
  const missing = (entry: string) => !existsSync(join(game.dir, entry));
  const entries = [...REQUIRED_ENTRIES, ...(existsSync(join(game.dir, "Hosts", "Browser")) ? WWW_ENTRIES : [])];
  return entries.filter(missing).map((entry) => finding("MissingEntry", "error", relative(root, join(game.dir, entry)), `${entry} is missing; the layout (docs/game-layout.md) requires it`));
}

function checkHosts(root: string, game: GameInfo): Finding[] {
  const out: Finding[] = [];
  const hostsDir = join(game.dir, "Hosts");
  const present = readdirSync(hostsDir, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name).sort();
  for (const host of HOSTS.filter((h) => !present.includes(h))) {
    out.push(finding("MissingHost", "info", relative(root, join(hostsDir, host)), `no ${host} host (scaffolded with a host subset, or not added yet)`));
  }
  for (const host of present) {
    const dir = join(hostsDir, host);
    const rel = relative(root, dir);
    const files = filesBelow(dir).map((f) => relative(dir, f));
    const expected = `${game.name}.${host}.csproj`;
    if (!(HOSTS as readonly string[]).includes(host)) out.push(finding("HostLayout", "warn", rel, `${host} is not one of ${HOSTS.join(", ")}`));
    if (files.length !== 1 || files[0] !== expected) {
      out.push(finding("HostLayout", "error", rel, `Hosts/${host} must hold only ${expected}, found: ${files.join(", ") || "nothing"}`));
      if (!files.includes(expected)) continue;
    }
    const project = readFileSync(join(dir, expected), "utf8");
    if (!project.includes(GAME_DIR_LINE)) out.push(finding("HostProject", "error", `${rel}/${expected}`, `does not set UkiyoGameDir to the game folder: ${GAME_DIR_LINE}`));
    if (!project.includes(SHARED_GLOB)) out.push(finding("HostProject", "error", `${rel}/${expected}`, `does not link the shared code: ${SHARED_GLOB}`));
    const platform = host === "Desktop" || host === "Export.Desktop" ? "Desktop" : host === "Browser" ? "Browser" : null;
    for (const folder of ["Desktop", "Browser"]) {
      const linked = project.includes(`$(UkiyoGameDir)Platform/${folder}/**/*.cs`);
      if (linked !== (folder === platform)) out.push(finding("HostProject", "error", `${rel}/${expected}`, `${linked ? "must not" : "must"} compile Platform/${folder}`));
    }
  }
  return out;
}

function checkShared(root: string, game: GameInfo): Finding[] {
  const out: Finding[] = [];
  const shared = join(game.dir, "Shared");
  for (const file of filesBelow(shared, (p) => p.endsWith(".cs"))) {
    const lines = codeLines(file);
    const where = (line: number) => `${relative(root, file)}:${line}`;
    for (const l of lines.filter((l) => l.text.trimStart().startsWith("#if"))) out.push(finding("PlatformLeak", "error", where(l.line), "#if in Shared/; host-specific code goes in Platform/<Host>/"));
    for (const forbidden of FORBIDDEN_IN_SHARED) {
      for (const l of lines.filter((l) => l.text.includes(forbidden))) out.push(finding("PlatformLeak", "error", where(l.line), `${forbidden} in Shared/; host-specific code goes in Platform/<Host>/`));
    }
  }
  return out;
}

function checkProgram(root: string, game: GameInfo): Finding[] {
  const file = join(game.dir, "Shared", "Program.cs");
  if (!existsSync(file)) return []; // reported as MissingEntry
  const code = codeLines(file).map((l) => l.text).join("\n");
  const out: Finding[] = [];
  const rel = relative(root, file);
  if (!/GameApplication\.RunAsync\(\s*new\s+\w+\(\)\s*,\s*PlatformBootstrap\.Create\(args\)\s*\)/.test(code)) {
    out.push(finding("ProgramShape", "error", rel, "Program.cs must be the single entry point: return await GameApplication.RunAsync(new <Game>(), PlatformBootstrap.Create(args));"));
  }
  if (/^\s*#/m.test(code)) out.push(finding("ProgramShape", "error", rel, "Program.cs has a preprocessor directive; all hosts compile this exact file, so it cannot branch by platform"));
  return out;
}

async function checkAssets(root: string, game: GameInfo, options: { build: (() => Promise<{ ok: boolean; message?: string }>) | null; signal: AbortSignal }): Promise<{ status: CheckStatus; findings: Finding[] }> {
  const specs = findSpecs(game.dir);
  if (specs.length === 0) return { status: "skipped", findings: [] };
  if (options.build) {
    const built = await options.build();
    if (!built.ok) return { status: "unchecked", findings: [finding("AssetsUnchecked", "warn", relative(root, game.dir), `importer did not build: ${built.message}`)] };
  }
  const check: ImportCheck | undefined = (await checkImports(root, new Map([[game.name, specs]]), options.signal)).get(game.name);
  if (!check || !check.ran) return { status: "unchecked", findings: [finding("AssetsUnchecked", "warn", relative(root, game.dir), check && !check.ran ? check.reason : "importer check did not run")] };
  const findings: Finding[] = [];
  for (const r of check.results) {
    if (r.status === "stale") findings.push(finding("StaleAssets", "error", r.spec, `${r.message ?? "generated files are out of date"}; regenerate: ukiyo import --game ${game.name}`));
    else if (r.status === "error") findings.push(finding("ImportError", "error", r.spec, `${r.code ?? "error"}: ${r.message ?? "importer failed"}`));
  }
  return { status: findings.length ? "fail" : "pass", findings };
}

function checkGolden(root: string, game: GameInfo): { status: CheckStatus; findings: Finding[] } {
  // The engine's goldens (SnapshotGoldenTests) cover samples/; a game under games/ is the person's and has none required.
  if (relative(root, game.dir).split("/")[0] !== "samples") return { status: "skipped", findings: [] };
  const golden = join(root, "tests", "Ukiyo.Tests", "golden", "snapshots", `${game.name}.txt`);
  if (existsSync(golden)) return { status: "pass", findings: [] };
  return { status: "fail", findings: [finding("MissingGolden", "error", relative(root, golden), `samples/${game.name} has no golden snapshot; add a SnapshotGoldenTests case and generate it (UKIYO_UPDATE_GOLDEN, see skills/ukiyo-testing)`)] };
}

const statusOf = (findings: Finding[]): CheckStatus => (findings.some((f) => f.level === "error") ? "fail" : "pass");

export type ConformanceOptions = { signal: AbortSignal; build?: (() => Promise<{ ok: boolean; message?: string }>) | null };

export async function checkGame(root: string, game: GameInfo, { signal, build = null }: ConformanceOptions): Promise<GameConformance> {
  const base = { game: game.name, dir: relative(root, game.dir) };
  if (!existsSync(join(game.dir, "Hosts"))) {
    const checks: GameConformance["checks"] = { layout: "skipped", hosts: "skipped", shared: "skipped", program: "skipped", assets: "skipped", golden: "skipped" };
    return { ...base, layout: "legacy", ok: true, checks, findings: [finding("LegacyLayout", "info", base.dir, "older <game>/<Host>/ layout: not checked against docs/game-layout.md (it predates the layout; new games use templates/game)")] };
  }
  const layout = checkEntries(root, game);
  const hosts = checkHosts(root, game);
  const shared = checkShared(root, game);
  const program = checkProgram(root, game);
  const assets = await checkAssets(root, game, { build, signal });
  const golden = checkGolden(root, game);
  const findings = [...layout, ...hosts, ...shared, ...program, ...assets.findings, ...golden.findings];
  return {
    ...base,
    layout: "template",
    ok: !findings.some((f) => f.level === "error"),
    checks: { layout: statusOf(layout), hosts: statusOf(hosts), shared: statusOf(shared), program: statusOf(program), assets: assets.status, golden: golden.status },
    findings,
  };
}
