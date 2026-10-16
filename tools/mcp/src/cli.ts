#!/usr/bin/env bun
// Editor-neutral adapter: JSON stdout, diagnostics stderr, no MCP client required. `run` and `live` have their own
// parsing below; every other tool module in src/tools is a command through the generic mapping in cli-args.ts.
import { constants, copyFileSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { BRIDGE_COMMANDS, bridgeRequest, liveSessions, publicSessions, type LiveSession } from "./bridge.ts";
import { CliUsageError, commandName, parseToolArgs, toolHelp } from "./cli-args.ts";
import { findRepoRoot } from "./engine.ts";
import { ToolInputError, validate, type Tool, type ToolContext, type ToolResult } from "./protocol.ts";
import { loadTools } from "./tool-registry.ts";
import { runHeadless, runSchema } from "./tools/run.ts";

/** Commands parsed by hand in this file; their errors always exit 1, as before the generic commands existed. */
const OWN_COMMANDS = new Set(["run", "live"]);

const HEADER = `ukiyo CLI: the MCP tools without an MCP client.   bun tools/mcp/src/cli.ts <command> … (--help after a command: just that command)
stdout: the tool's JSON result. stderr: logs and "[UKIYO]: …" errors. Exit 2: usage error (unknown command or option,
missing argument, input the tool's schema refuses); exit 1: the tool failed (its JSON result, when it has one, is still
on stdout). run and live exit 1 on every error.
UKIYO_ROOT=<repo> selects the repository (default: the one this file is in), resolved like the MCP server. Give a real
path (realpath): on macOS /tmp and the default temp dir are symlinks, a known build problem.`;

const FOOTER = `Generic commands: required arguments are positional; lists are comma-separated (A,B); FILE options read a JSON file and
pass its content inline; path values are passed as the tool takes them (repository-relative or absolute inside it).
  bun tools/mcp/src/cli.ts new-game SkyLantern --template 2d --hosts Headless,Desktop
  bun tools/mcp/src/cli.ts capture SkyLantern --checkpoints 0,60 --out frames
  bun tools/mcp/src/cli.ts import --check samples/TexturedQuad`;

export const HELP = `ukiyo live <list|status|pause|resume|step|snapshot|capture|input|record|patch> [options]
  --pid N           Required when more than one game is running
  --ticks N         step: 0..36000 (default 1)
  --events FILE     input: JSON array of key/pointer events
  --on true|false   record: explicit start/stop
  --patch FILE      patch: one scene patch object (docs/scenes-patches.md), tick >= the game's current tick
  --out FILE        capture: PNG; record: input script; others: result JSON
Output files are created exclusively: existing files are never overwritten.
ukiyo run <Game> [options]   headless run (same as the ukiyo_run MCP tool)
  --checkpoints A,B,…   snapshot ticks (default 0,60,180,600)
  --patches FILE        patch log: JSON array of patches, submitted before tick 0
  --input FILE          input script: JSON array of events with ticks
  --size WxH            drawing-buffer size (default 1280x720)
Examples:
  bun tools/mcp/src/cli.ts live list
  bun tools/mcp/src/cli.ts live step --pid 123 --ticks 60
  bun tools/mcp/src/cli.ts live capture --pid 123 --out frame.png
  bun tools/mcp/src/cli.ts live patch --pid 123 --patch patch.json
  bun tools/mcp/src/cli.ts run SceneHop --checkpoints 0,60,120 --patches patches.json`;

function integer(value: string, min: number, max: number) {
  const n = Number(value);
  if (!/^\d+$/.test(value) || !Number.isSafeInteger(n) || n < min || n > max) throw new Error(`expected integer within ${min}..${max}`);
  return n;
}

function options(args: string[], allowed: Set<string>) {
  const options: Record<string, string> = {};
  for (let i = 0; i < args.length; i += 2) {
    const key = args[i]!;
    const value = args[i + 1];
    if (!allowed.has(key) || options[key] !== undefined || !value || value.startsWith("--")) throw new Error(`invalid or duplicate option ${key}`);
    options[key] = value;
  }
  return options;
}

export function parseArgs(args: string[]) {
  if (args[0] !== "live") throw new Error("expected 'live' or 'run'; use --help");
  const action = args[1];
  if (action !== "list" && !(BRIDGE_COMMANDS as readonly string[]).includes(action ?? "")) throw new Error("unknown live action");
  const perAction: Record<string, string[]> = { step: ["--ticks"], input: ["--events"], record: ["--on"], patch: ["--patch"] };
  const given = options(args.slice(2), new Set(["--pid", "--out", ...(perAction[action!] ?? [])]));
  const pid = given["--pid"] === undefined ? undefined : integer(given["--pid"], 1, 2147483647);
  const request: Record<string, unknown> = { cmd: action };
  if (action === "step") request.ticks = integer(given["--ticks"] ?? "1", 0, 36000);
  if (action === "record") {
    if (!["true", "false"].includes(given["--on"] ?? "")) throw new Error("record needs --on true|false");
    request.on = given["--on"] === "true";
  }
  if (action === "input" && !given["--events"]) throw new Error("input needs --events FILE");
  if (action === "patch" && !given["--patch"]) throw new Error("patch needs --patch FILE (one patch object)");
  if (action === "capture" && !given["--out"]) throw new Error("capture needs --out FILE");
  return { action: action!, pid, request, output: given["--out"], events: given["--events"], patch: given["--patch"] };
}

/** `run <Game> …` → ukiyo_run arguments. Files are read here and passed inline, so any local path works. */
export function parseRunArgs(args: string[]) {
  const game = args[1];
  if (args[0] !== "run" || !game || game.startsWith("--")) throw new Error("run needs a game: run <Game> [--checkpoints A,B] [--patches FILE] [--input FILE] [--size WxH]");
  const given = options(args.slice(2), new Set(["--checkpoints", "--patches", "--input", "--size"]));
  const input: Record<string, unknown> = { game };
  if (given["--checkpoints"]) input.checkpoints = given["--checkpoints"].split(",").map((tick) => integer(tick, 0, 216_000));
  if (given["--size"]) input.size = given["--size"];
  return { input, patches: given["--patches"], events: given["--input"] };
}

function readJsonArray(path: string, what: string): unknown[] {
  const value: unknown = JSON.parse(readFileSync(path, "utf8"));
  if (!Array.isArray(value) || value.length === 0) throw new Error(`${what} ${path} must be a non-empty JSON array`);
  return value;
}

export function selectSession(sessions: LiveSession[], pid?: number) {
  if (pid === undefined && sessions.length > 1) throw new Error("multiple games are running; select one with --pid");
  const session = pid === undefined ? sessions[0] : sessions.find(s => s.pid === pid);
  if (!session) throw new Error("no matching development build; start a Desktop project first");
  return session;
}

/** The tool context of a CLI process: UKIYO_ROOT resolved as server.ts does, logs on stderr, Ctrl-C aborts. */
function cliContext(): ToolContext {
  const controller = new AbortController();
  process.once("SIGINT", () => controller.abort());
  return {
    root: findRepoRoot(process.env.UKIYO_ROOT ?? import.meta.dir),
    log: (level, event, data) => console.error(JSON.stringify({ level, event, ...data })),
    signal: controller.signal,
  };
}

const resultText = (result: ToolResult) => result.content.map((part) => (part.type === "text" ? part.text : "")).join("\n");

async function runGame(args: string[]) {
  const parsed = parseRunArgs(args);
  if (parsed.patches) parsed.input.patches = readJsonArray(parsed.patches, "patch log");
  if (parsed.events) parsed.input.input = readJsonArray(parsed.events, "input script");
  const context = cliContext();
  const result = await runHeadless(validate(runSchema, parsed.input) as Record<string, unknown>, context, false);
  if (result.isError) throw new Error(resultText(result));
  return result.structuredContent;
}

/** A result the tool marked isError. Its structured result, when there is one, still goes to stdout. Exit 1. */
export class ToolFailure extends Error {
  constructor(message: string, readonly result?: Record<string, unknown>) {
    super(message);
  }
}

let discovered: Map<string, Tool> | undefined;

/** Command name → tool, for every tool module except the ones with their own parsing (run, live). */
export async function toolCommands(): Promise<Map<string, Tool>> {
  discovered ??= new Map([...(await loadTools()).values()].filter((tool) => !OWN_COMMANDS.has(commandName(tool))).map((tool) => [commandName(tool), tool]));
  return discovered;
}

/** Copies the files a result lists under `keys` into `dir` and points the result at the copies. Never overwrites. */
export function copyOutputs(result: Record<string, unknown>, keys: string[], dir: string): Record<string, unknown> {
  const out = resolve(dir);
  const copies = keys.flatMap((key) => (Array.isArray(result[key]) ? (result[key] as string[]) : []).map((from) => ({ key, from, to: join(out, basename(from)) })));
  const taken = copies.find(({ to }) => existsSync(to));
  // The tool already succeeded: its result (with the temp paths) still goes to stdout, the exit code says the copy failed.
  if (taken) throw new ToolFailure(`${taken.to} already exists; choose a new --out directory (the files are still at ${taken.from})`, result);
  mkdirSync(out, { recursive: true });
  for (const { from, to } of copies) copyFileSync(from, to, constants.COPYFILE_EXCL);
  const moved = Object.fromEntries(keys.map((key) => [key, copies.filter((c) => c.key === key).map((c) => c.to)]));
  return { ...result, ...moved, out };
}

/** One generic command: argv → the tool's own schema validation → tool.run → its structured result. */
export async function runTool(tool: Tool, args: string[], context: () => ToolContext = cliContext) {
  const { input, out } = parseToolArgs(tool, args);
  if (out !== undefined && existsSync(out) && !statSync(out).isDirectory()) throw new CliUsageError(`--out ${out} exists and is not a directory`);
  const validated = validate(tool.inputSchema, input) as Record<string, unknown>;
  const result = await tool.run(validated, context());
  if (result.isError) throw new ToolFailure(resultText(result), result.structuredContent);
  const structured = result.structuredContent ?? { text: resultText(result) };
  return out === undefined ? structured : copyOutputs(structured, tool.cli?.outputs ?? [], out);
}

/** Full help (hand-written run/live plus one generated block per tool), or one command's block. */
export async function help(command?: string): Promise<string> {
  const tools = await toolCommands();
  const tool = command === undefined ? undefined : tools.get(command);
  if (tool) return toolHelp(tool);
  return [HEADER, HELP, ...[...tools.values()].map(toolHelp), FOOTER].join("\n");
}

/** Exit code of a failed command: 2 for usage errors of the generic commands, 1 otherwise (and always for run/live). */
export function exitCode(args: string[], error: unknown): number {
  if (OWN_COMMANDS.has(args[0] ?? "")) return 1;
  return error instanceof CliUsageError || error instanceof ToolInputError ? 2 : 1;
}

export async function run(args: string[]) {
  if (args[0] === "run") return runGame(args);
  if (args[0] === "live") return runLive(args);
  const tool = (await toolCommands()).get(args[0] ?? "");
  if (!tool) throw new CliUsageError(`${args[0] === undefined ? "no command" : `unknown command ${args[0]}`}; commands: ${[...OWN_COMMANDS, ...(await toolCommands()).keys()].join(", ")}; use --help`);
  return runTool(tool, args.slice(1));
}

async function runLive(args: string[]) {
  const parsed = parseArgs(args);
  // Reject a known collision before issuing a state-changing command; wx still protects against races.
  if (parsed.output && existsSync(parsed.output)) throw new Error("output already exists; choose a new file");
  const sessions = liveSessions();
  if (parsed.events) {
    const events = JSON.parse(readFileSync(parsed.events, "utf8"));
    if (!Array.isArray(events) || events.length === 0 || events.length > 1000) throw new Error("events must be a JSON array with 1..1000 entries");
    parsed.request.events = events;
  }
  if (parsed.patch) {
    const patch: unknown = JSON.parse(readFileSync(parsed.patch, "utf8"));
    if (typeof patch !== "object" || patch === null || Array.isArray(patch)) throw new Error("--patch must hold one patch object, not a log; submit log entries one by one");
    parsed.request.patch = patch;
  }
  let result: any;
  if (parsed.action === "list") result = { sessions: publicSessions(sessions) };
  else {
    const response = await bridgeRequest(selectSession(sessions, parsed.pid), parsed.request);
    if (!response.ok) throw new Error(response.error ?? "bridge command failed");
    result = response.result;
  }
  if (parsed.output) {
    const data = parsed.action === "capture" ? Buffer.from(result.png, "base64")
      : JSON.stringify(parsed.action === "record" ? result.script : result, null, 2) + "\n";
    writeFileSync(parsed.output, data, { flag: "wx", mode: 0o600 });
    if (parsed.action === "capture") {
      const { png: _png, ...metadata } = result;
      result = metadata;
    }
    result = { ...result, output: parsed.output };
  }
  return result;
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  try {
    if (args.some(arg => arg === "--help" || arg === "-h")) console.log(await help(OWN_COMMANDS.has(args[0] ?? "") ? undefined : args[0]));
    else console.log(JSON.stringify(await run(args)));
  } catch (error) {
    if (error instanceof ToolFailure && error.result) console.log(JSON.stringify(error.result));
    console.error(`[UKIYO]: ${error instanceof Error ? error.message : String(error)}`);
    process.exitCode = exitCode(args, error);
  }
}
