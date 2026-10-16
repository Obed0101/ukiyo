#!/usr/bin/env bun
// Editor-neutral adapter: JSON stdout, diagnostics stderr, no MCP client required.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { BRIDGE_COMMANDS, bridgeRequest, liveSessions, publicSessions, type LiveSession } from "./bridge.ts";

export const HELP = `ukiyo live <list|status|pause|resume|step|snapshot|capture|input|record> [options]
  --pid N           Required when more than one game is running
  --ticks N         step: 0..36000 (default 1)
  --events FILE     input: JSON array of key/pointer events
  --on true|false   record: explicit start/stop
  --out FILE        capture: PNG; record: input script; others: result JSON
Output files are created exclusively: existing files are never overwritten.
Examples:
  bun tools/mcp/src/cli.ts live list
  bun tools/mcp/src/cli.ts live step --pid 123 --ticks 60
  bun tools/mcp/src/cli.ts live capture --pid 123 --out frame.png`;

export function parseArgs(args: string[]) {
  if (args[0] !== "live") throw new Error("expected 'live'; use --help");
  const action = args[1];
  if (action !== "list" && !(BRIDGE_COMMANDS as readonly string[]).includes(action ?? "")) throw new Error("unknown live action");
  const options: Record<string, string> = {};
  const allowed = new Set(["--pid", "--out", ...(action === "step" ? ["--ticks"] : []), ...(action === "input" ? ["--events"] : []), ...(action === "record" ? ["--on"] : [])]);
  for (let i = 2; i < args.length; i += 2) {
    const key = args[i]!;
    const value = args[i + 1];
    if (!allowed.has(key) || options[key] !== undefined || !value || value.startsWith("--")) throw new Error(`invalid or duplicate option ${key}`);
    options[key] = value;
  }
  function integer(value: string, min: number, max: number) {
    const n = Number(value);
    if (!/^\d+$/.test(value) || !Number.isSafeInteger(n) || n < min || n > max) throw new Error(`expected integer within ${min}..${max}`);
    return n;
  }
  const pid = options["--pid"] === undefined ? undefined : integer(options["--pid"], 1, 2147483647);
  const request: Record<string, unknown> = { cmd: action };
  if (action === "step") request.ticks = integer(options["--ticks"] ?? "1", 0, 36000);
  if (action === "record") {
    if (!["true", "false"].includes(options["--on"] ?? "")) throw new Error("record needs --on true|false");
    request.on = options["--on"] === "true";
  }
  if (action === "input" && !options["--events"]) throw new Error("input needs --events FILE");
  if (action === "capture" && !options["--out"]) throw new Error("capture needs --out FILE");
  return { action: action!, pid, request, output: options["--out"], events: options["--events"] };
}

export function selectSession(sessions: LiveSession[], pid?: number) {
  if (pid === undefined && sessions.length > 1) throw new Error("multiple games are running; select one with --pid");
  const session = pid === undefined ? sessions[0] : sessions.find(s => s.pid === pid);
  if (!session) throw new Error("no matching development build; start a Desktop project first");
  return session;
}

export async function run(args: string[]) {
  const parsed = parseArgs(args);
  // Reject a known collision before issuing a state-changing command; wx still protects against races.
  if (parsed.output && existsSync(parsed.output)) throw new Error("output already exists; choose a new file");
  const sessions = liveSessions();
  if (parsed.events) {
    const events = JSON.parse(readFileSync(parsed.events, "utf8"));
    if (!Array.isArray(events) || events.length === 0 || events.length > 1000) throw new Error("events must be a JSON array with 1..1000 entries");
    parsed.request.events = events;
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
