#!/usr/bin/env bun
// Editor-neutral adapter: JSON stdout, diagnostics stderr, no MCP client required.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { BRIDGE_COMMANDS, bridgeRequest, liveSessions, publicSessions, type LiveSession } from "./bridge.ts";
import { findRepoRoot } from "./engine.ts";
import { validate, type ToolContext } from "./protocol.ts";
import { runHeadless, runSchema } from "./tools/run.ts";

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

async function runGame(args: string[]) {
  const parsed = parseRunArgs(args);
  if (parsed.patches) parsed.input.patches = readJsonArray(parsed.patches, "patch log");
  if (parsed.events) parsed.input.input = readJsonArray(parsed.events, "input script");
  const controller = new AbortController();
  process.once("SIGINT", () => controller.abort());
  const context: ToolContext = {
    root: findRepoRoot(process.env.UKIYO_ROOT ?? import.meta.dir),
    log: (level, event, data) => console.error(JSON.stringify({ level, event, ...data })),
    signal: controller.signal,
  };
  const result = await runHeadless(validate(runSchema, parsed.input) as Record<string, unknown>, context, false);
  if (result.isError) throw new Error(result.content.map((part) => (part.type === "text" ? part.text : "")).join("\n"));
  return result.structuredContent;
}

export async function run(args: string[]) {
  if (args[0] === "run") return runGame(args);
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
  if (process.argv.slice(2).some(arg => arg === "--help" || arg === "-h")) console.log(HELP);
  else {
    try { console.log(JSON.stringify(await run(process.argv.slice(2)))); }
    catch (error) {
      console.error(`[UKIYO]: ${error instanceof Error ? error.message : String(error)}`);
      process.exitCode = 1;
    }
  }
}
