#!/usr/bin/env bun
// ukiyo MCP server (stdio). Newline-delimited JSON-RPC 2.0 on stdin/stdout; logs are structured JSON on stderr only,
// because stdout belongs to the protocol. Tools are discovered from src/tools: adding a tool means adding a file.
//
//   claude mcp add ukiyo -- bun /path/to/ukiyo/tools/mcp/src/server.ts
//   UKIYO_ROOT=/path/to/ukiyo   (optional; defaults to the repository this file lives in)
import { readdirSync } from "node:fs";
import { createInterface } from "node:readline";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { findRepoRoot } from "./engine.ts";
import {
  ErrorCode,
  SUPPORTED_PROTOCOL_VERSIONS,
  ToolInputError,
  failure,
  validate,
  type JsonRpcRequest,
  type Tool,
  type ToolContext,
} from "./protocol.ts";

const SERVER_INFO = { name: "ukiyo", title: "ukiyo engine", version: "0.1.0" };
const here = dirname(fileURLToPath(import.meta.url));
const root = findRepoRoot(process.env.UKIYO_ROOT ?? here);

const INSTRUCTIONS = `ukiyo is an agent-native C# game engine. The game is code: edit files under games/<Name>/Shared (or samples/), then prove the change.
Loop: ukiyo_status → edit code → ukiyo_run (snapshots at ticks, scripted input) → ukiyo_capture (real frames from the CPU renderer) → ukiyo_test.
A running desktop build exposes a dev bridge: ukiyo_live pauses, steps, captures and injects input into it.
Sprites: ukiyo_sprite_generate (image model → pixel-art PNG + atlas in Shared/assets), ukiyo_sprite_process, ukiyo_sprite_sheet.
Sound: ukiyo_sound_generate (synth preset + seed, no audio files); ukiyo_run {"audio":true} reports what the run played.
Studio (bun studio/src/server.ts) is the person's window: ukiyo_studio view says what they are looking at (focused panel, selected entity, tick); notify tells them something; compose changes layout/theme/panels. Its UI is files in studio/ (panels/*.js, themes/*.json, layouts/*.json): edit them to change it, open pages reload live.
Skills with the APIs per area live in skills/; ukiyo_skills lists or installs them. Never present generated art as hand-made, and never call a build a visual check.`;

function log(level: "debug" | "info" | "warn" | "error", event: string, data: Record<string, unknown> = {}) {
  process.stderr.write(`${JSON.stringify({ time: new Date().toISOString(), level, source: "ukiyo-mcp", event, ...data })}\n`);
}

async function loadTools(): Promise<Map<string, Tool>> {
  const tools = new Map<string, Tool>();
  const dir = join(here, "tools");
  for (const file of readdirSync(dir).filter((f) => f.endsWith(".ts") && !f.endsWith(".test.ts")).sort()) {
    const module = (await import(pathToFileURL(join(dir, file)).href)) as { default?: Tool | Tool[] };
    const exported = module.default;
    if (!exported) throw new Error(`[MCP]: ${file} has no default export`);
    for (const tool of Array.isArray(exported) ? exported : [exported]) {
      if (!/^[a-z][a-z0-9_]{2,63}$/.test(tool.name) || typeof tool.run !== "function" || tool.inputSchema?.type !== "object") {
        throw new TypeError(`[MCP]: ${file} exports an invalid tool ${JSON.stringify(tool.name)}`);
      }
      if (tools.has(tool.name)) throw new Error(`[MCP]: duplicate tool ${tool.name} in ${file}`);
      tools.set(tool.name, tool);
    }
  }
  return tools;
}

const tools = await loadTools();
const inFlight = new Map<string | number, AbortController>();

function send(message: Record<string, unknown>) {
  process.stdout.write(`${JSON.stringify({ jsonrpc: "2.0", ...message })}\n`);
}

function reply(id: string | number, result: unknown) {
  send({ id, result });
}

function replyError(id: string | number | null, code: number, message: string) {
  send({ id, error: { code, message } });
}

async function callTool(id: string | number, params: Record<string, unknown>) {
  const name = String(params.name ?? "");
  const tool = tools.get(name);
  if (!tool) return replyError(id, ErrorCode.InvalidParams, `unknown tool ${name}`);
  let args: Record<string, unknown>;
  try {
    args = validate(tool.inputSchema, params.arguments ?? {}) as Record<string, unknown>;
  } catch (error) {
    if (error instanceof ToolInputError) return replyError(id, ErrorCode.InvalidParams, error.message);
    throw error;
  }

  const controller = new AbortController();
  inFlight.set(id, controller);
  const context: ToolContext = { root, log, signal: controller.signal };
  const started = performance.now();
  try {
    const result = await tool.run(args, context);
    log("info", "tool.completed", { tool: name, ms: Math.round(performance.now() - started), isError: Boolean(result.isError) });
    reply(id, result);
  } catch (error) {
    // Tool failures are results the agent can read and act on, not protocol errors.
    const message = error instanceof Error ? error.message : String(error);
    log("error", "tool.failed", { tool: name, message });
    reply(id, failure(message));
  } finally {
    inFlight.delete(id);
  }
}

async function handle(request: JsonRpcRequest) {
  const { id, method, params = {} } = request;
  if (id === undefined) {
    if (method === "notifications/cancelled") inFlight.get(params.requestId as string | number)?.abort();
    return; // other notifications (initialized, progress) need no answer
  }

  switch (method) {
    case "initialize": {
      const requested = String(params.protocolVersion ?? "");
      const protocolVersion = (SUPPORTED_PROTOCOL_VERSIONS as readonly string[]).includes(requested) ? requested : SUPPORTED_PROTOCOL_VERSIONS[0];
      log("info", "session.initialize", { client: params.clientInfo, protocolVersion, root, tools: [...tools.keys()] });
      return reply(id, { protocolVersion, capabilities: { tools: { listChanged: false } }, serverInfo: SERVER_INFO, instructions: INSTRUCTIONS });
    }
    case "ping":
      return reply(id, {});
    case "tools/list":
      return reply(id, {
        tools: [...tools.values()].map(({ name, title, description, inputSchema }) => ({ name, title, description, inputSchema })),
      });
    case "tools/call":
      return callTool(id, params);
    default:
      return replyError(id, ErrorCode.MethodNotFound, `method ${method} is not supported`);
  }
}

const lines = createInterface({ input: process.stdin, crlfDelay: Number.POSITIVE_INFINITY });
lines.on("line", (line) => {
  if (!line.trim()) return;
  let request: JsonRpcRequest;
  try {
    request = JSON.parse(line) as JsonRpcRequest;
  } catch {
    return replyError(null, ErrorCode.ParseError, "invalid JSON");
  }
  if (request.jsonrpc !== "2.0" || typeof request.method !== "string") {
    return replyError(request.id ?? null, ErrorCode.InvalidRequest, "not a JSON-RPC 2.0 request");
  }
  handle(request).catch((error: unknown) => {
    log("error", "request.failed", { method: request.method, message: error instanceof Error ? error.message : String(error) });
    if (request.id !== undefined) replyError(request.id, ErrorCode.InternalError, error instanceof Error ? error.message : String(error));
  });
});
lines.on("close", () => process.exit(0));
log("info", "server.ready", { root, tools: tools.size });
