#!/usr/bin/env bun
// Golden transcript of the ukiyo CLI (UKIYO-31): help, status, scaffold a game, run it headless, capture a CPU frame,
// check the import specs, remove the game. Every step is `bun tools/mcp/src/cli.ts …` in a temporary copy of the
// repository (tracked and untracked-but-not-ignored files, without games/), so the checkout is never touched and the
// starting state is the same on every machine: no bin/obj, no native/, no node_modules. The normalized transcript
// must equal tools/mcp/test/golden/cli-flow.txt; any difference is printed as a unified diff and exits 1.
// Usage: bun scripts/cli-flow.ts [--update]   (--update rewrites the golden file instead of comparing)
import { createHash } from "node:crypto";
import { chmodSync, copyFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readlinkSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";

const REPO = realpathSync(join(import.meta.dir, ".."));
const GOLDEN = join(REPO, "tools/mcp/test/golden/cli-flow.txt");
const ACTUAL = join(REPO, "artifacts/cli-flow/actual.txt");
const GAME = "CiProbe";

const args = process.argv.slice(2);
if (args.some((arg) => arg !== "--update")) {
  console.error("[CLI-FLOW]: usage: bun scripts/cli-flow.ts [--update]");
  process.exit(2);
}
const update = args.includes("--update");

/** realpath: on macOS the default temp dir is a symlink, and building a repository reached through one fails. */
const flow = realpathSync(mkdtempSync(join(tmpdir(), "ukiyo-cli-flow-")));
const copy = join(flow, "repo");
const childTmp = join(flow, "tmp");
const out = join(flow, "frames");
const cleanup = () => rmSync(flow, { recursive: true, force: true });
process.on("SIGINT", () => { cleanup(); process.exit(130); });
process.on("SIGTERM", () => { cleanup(); process.exit(143); });

function git(...gitArgs: string[]): string {
  const result = Bun.spawnSync(["git", ...gitArgs], { cwd: REPO, stdout: "pipe", stderr: "pipe" });
  if (result.exitCode !== 0) throw new Error(`[CLI-FLOW]: git ${gitArgs.join(" ")} failed: ${result.stderr.toString().trim()}`);
  return result.stdout.toString();
}

const isLink = (path: string) => {
  try { return lstatSync(path).isSymbolicLink(); } catch { return false; }
};

/** The working tree as git sees it, without ignored files and without games/ (the flow creates games/ itself). */
function copyRepository() {
  const files = git("ls-files", "-z", "--cached", "--others", "--exclude-standard").split("\0").filter(Boolean);
  for (const file of files) {
    if (file === "games" || file.startsWith("games/")) continue;
    const from = join(REPO, file);
    if (!isLink(from) && !existsSync(from)) continue; // deleted in the working tree
    const to = join(copy, file);
    mkdirSync(dirname(to), { recursive: true });
    if (isLink(from)) {
      // Tracked relative links (.claude/skills/* → skills/*) stay links inside the copy; nothing may point outside it.
      const target = readlinkSync(from);
      if (!isAbsolute(target) && resolve(dirname(to), target).startsWith(`${copy}/`)) symlinkSync(target, to);
      continue;
    }
    if (!lstatSync(from).isFile()) continue;
    copyFileSync(from, to);
    chmodSync(to, lstatSync(from).mode & 0o777);
  }
}

/** Machine- and run-specific strings: absolute paths of this run, mkdtemp suffixes. Longest path first. */
function normalizeText(value: string): string {
  return value
    .replaceAll(copy, "<repo>")
    .replaceAll(out, "<out>")
    .replaceAll(childTmp, "<tmp>")
    .replaceAll(flow, "<flow>")
    .replace(/(<tmp>\/[A-Za-z][A-Za-z-]*-)[A-Za-z0-9]{6}\b/g, "$1<id>");
}

/** Keys whose values vary by machine or run; everything else (snapshots, hashes, exit codes, file lists) is kept. */
function normalizeJson(value: unknown, path: string[] = []): unknown {
  const key = path.at(-1) ?? "";
  if (typeof value === "number" && (key === "ms" || key.endsWith("Ms"))) return "<ms>";
  if (path.join(".") === "toolchain.dotnet" && typeof value === "string") return value.replace(/^(\d+\.\d+)\.\d+$/, "$1.<patch>");
  if (path.join(".") === "live" && Array.isArray(value)) return "<live sessions on this machine>";
  if (typeof value === "string") return normalizeText(value);
  if (Array.isArray(value)) return value.map((item, i) => normalizeJson(item, [...path, String(i)]));
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, normalizeJson(v, [...path, k])]));
  return value;
}

function normalizeStdout(stdout: string): string {
  const trimmed = stdout.trimEnd();
  try {
    return JSON.stringify(normalizeJson(JSON.parse(trimmed)), null, 2);
  } catch {
    return trimmed.split("\n").map(normalizeText).join("\n");
  }
}

const transcript: string[] = [
  "# ukiyo CLI golden transcript. Produced and compared by scripts/cli-flow.ts; rewrite with: bun scripts/cli-flow.ts --update",
  "# Normalized: <repo> = temporary repository copy, <out> = capture --out dir, <tmp> = TMPDIR of the CLI, <id> = mkdtemp suffix,",
  "# <ms> = durations, dotnet patch version, live sessions. JSON stdout is re-indented; stderr keeps only [UKIYO]: lines.",
];

type Step = { exitCode: number; json: Record<string, unknown> | null };

// Bun.spawn rather than spawnSync, so Ctrl-C reaches the signal handlers above and the flow dir is still removed.

/** One CLI command in the copy: records the command, its normalized stdout, its [UKIYO] errors and its exit code. */
async function cli(...cliArgs: string[]): Promise<Step> {
  console.error(`[cli-flow] ukiyo ${cliArgs.join(" ")}`);
  const child = Bun.spawn([process.execPath, join(copy, "tools/mcp/src/cli.ts"), ...cliArgs], {
    cwd: copy,
    stdin: "ignore",
    stdout: "pipe",
    stderr: "pipe",
    // TMPDIR keeps the tools' own temp dirs (ukiyo-run-*) inside the flow dir, so cleanup removes them too.
    env: { ...process.env, UKIYO_ROOT: copy, TMPDIR: childTmp, MSBUILDDISABLENODEREUSE: "1", DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" },
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
  const errors = stderr.split("\n").filter((line) => line.startsWith("[UKIYO]:"));
  // Context for a failing step (the golden decides whether the failure is expected); not part of the transcript.
  if (exitCode !== 0) console.error(`[cli-flow] exit ${exitCode}, last stderr lines:\n${stderr.trimEnd().split("\n").slice(-20).map((line) => `  ${line}`).join("\n")}`);
  transcript.push("", `$ ukiyo ${cliArgs.map(normalizeText).join(" ")}`);
  if (stdout.trim()) transcript.push(normalizeStdout(stdout));
  for (const line of errors) transcript.push(`stderr: ${normalizeText(line)}`);
  transcript.push(`exit ${exitCode}`);
  let json: Record<string, unknown> | null = null;
  try { json = JSON.parse(stdout); } catch { json = null; }
  return { exitCode, json };
}

/** A step the script does itself (not a CLI command), recorded with its observable result. */
function note(command: string, result: string) {
  transcript.push("", `# ${command}`, result);
}

function compare(actual: string): boolean {
  if (update) {
    mkdirSync(dirname(GOLDEN), { recursive: true });
    writeFileSync(GOLDEN, actual);
    console.error(`[cli-flow] updated ${relative(REPO, GOLDEN)}`);
    return true;
  }
  const expected = existsSync(GOLDEN) ? readFileSync(GOLDEN, "utf8") : "";
  if (expected === actual) {
    rmSync(dirname(ACTUAL), { recursive: true, force: true }); // a previous mismatch's output is no longer current
    console.error(`[cli-flow] transcript matches ${relative(REPO, GOLDEN)}`);
    return true;
  }
  mkdirSync(dirname(ACTUAL), { recursive: true });
  writeFileSync(ACTUAL, actual);
  const diff = Bun.spawnSync(["diff", "-u", "--label", "expected", "--label", "actual", GOLDEN, ACTUAL], { stdout: "pipe" });
  console.log(existsSync(GOLDEN) ? diff.stdout.toString() : `${relative(REPO, GOLDEN)} does not exist; run with --update`);
  console.error(`[CLI-FLOW]: transcript differs from ${relative(REPO, GOLDEN)} (actual: ${relative(REPO, ACTUAL)}); if the change is intended: bun scripts/cli-flow.ts --update`);
  return false;
}

/** git status before vs after, ignoring the golden file itself (which --update rewrites on purpose). */
function statusLines(): string[] {
  const golden = relative(REPO, GOLDEN);
  return git("status", "--short", "--untracked-files=all").split("\n").filter((line) => line && !line.endsWith(golden));
}

let statusBefore: string[] = [];
let matched = false;
try {
  statusBefore = statusLines(); // inside try: if git fails, the flow dir is still removed
  copyRepository();
  mkdirSync(childTmp);
  const solution = readFileSync(join(copy, "Ukiyo.slnx"), "utf8");

  await cli("--help");
  await cli("status");
  await cli("new-game", GAME, "--hosts", "Headless");
  await cli("run", GAME, "--checkpoints", "0,60");
  const capture = await cli("capture", GAME, "--checkpoints", "0", "--out", out);
  for (const file of (capture.json?.captures as string[] | undefined) ?? []) {
    const bytes = readFileSync(file);
    note(`frame ${normalizeText(file)}`, `bytes=${bytes.length} sha256=${createHash("sha256").update(bytes).digest("hex")}`);
  }
  await cli("import", "--check", "samples");

  // The copy has no games/ of its own (copyRepository skips it), so removing the game removes games/ entirely.
  rmSync(join(copy, "games"), { recursive: true, force: true });
  writeFileSync(join(copy, "Ukiyo.slnx"), solution);
  note(`rm -r games/ && restore Ukiyo.slnx (script step, then the CLI no longer finds ${GAME})`, `games/ ${existsSync(join(copy, "games")) ? "present" : "absent"}; Ukiyo.slnx ${readFileSync(join(copy, "Ukiyo.slnx"), "utf8") === solution ? "identical to before new-game" : "differs"}`);
  await cli("run", GAME, "--checkpoints", "0");

  matched = compare(transcript.join("\n") + "\n");
} finally {
  cleanup();
}

const statusAfter = statusLines();
const leftovers = existsSync(flow) ? [`${flow} was not removed`] : [];
if (statusAfter.join("\n") !== statusBefore.join("\n")) leftovers.push(`git status --short changed:\n  before: ${statusBefore.join("\n          ") || "(clean)"}\n  after:  ${statusAfter.join("\n          ") || "(clean)"}`);
for (const leftover of leftovers) console.error(`[CLI-FLOW]: ${leftover}`);
process.exit(matched && leftovers.length === 0 ? 0 : 1);
