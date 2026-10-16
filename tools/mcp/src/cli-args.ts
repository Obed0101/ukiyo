// One mapping from a command line to a tool's input, derived from the tool's input schema (plus its optional `cli`
// hints), and the help text derived the same way. Nothing here validates meaning: the result goes through validate()
// and the tool, exactly as an MCP call would.
import { readFileSync } from "node:fs";
import type { JsonSchema, JsonValue, Tool } from "./protocol.ts";

/** A command line that cannot become tool input: unknown option, missing argument, unreadable file. Exit 2. */
export class CliUsageError extends Error {}

export type ToolCommand = { input: Record<string, unknown>; out?: string };

/** ukiyo_new_game → new-game. */
export const commandName = (tool: Tool) => tool.name.replace(/^ukiyo_/, "").replaceAll("_", "-");

/** timeoutSeconds → --timeout-seconds. */
export const optionName = (property: string) => `--${property.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)}`;

const SCALAR = new Set(["string", "integer", "number"]);

/** Properties given as JSON files: objects and arrays whose items are not scalars. */
const takesFile = (schema: JsonSchema) =>
  schema.type === "object" || (schema.type === "array" && !(schema.items && SCALAR.has(schema.items.type)));

function positionalProperties(tool: Tool): string[] {
  const properties = tool.inputSchema.properties ?? {};
  return tool.cli?.positional ?? (tool.inputSchema.required ?? []).filter((key) => SCALAR.has(properties[key]?.type ?? ""));
}

/** --flag → property, for every property not already covered by a positional argument or a switch. */
function optionProperties(tool: Tool): Map<string, string> {
  const covered = new Set([...positionalProperties(tool), ...Object.values(tool.cli?.switches ?? {}).flatMap((s) => Object.keys(s.set))]);
  return new Map(Object.keys(tool.inputSchema.properties ?? {}).filter((key) => !covered.has(key)).map((key) => [optionName(key), key]));
}

function scalar(schema: JsonSchema, value: string, where: string): string | number {
  if (schema.type !== "integer" && schema.type !== "number") return value;
  if (!/^-?\d+(\.\d+)?$/.test(value)) throw new CliUsageError(`${where} expects a number, got ${JSON.stringify(value)}`);
  return Number(value);
}

function readJson(path: string, where: string): unknown {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    throw new CliUsageError(`${where} FILE must be a readable JSON file: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function convert(schema: JsonSchema, value: string, where: string): unknown {
  if (takesFile(schema)) return readJson(value, where);
  if (schema.type === "array") return value.split(",").map((item) => scalar(schema.items!, item, where));
  return scalar(schema, value, where);
}

/** argv after the command name → tool input (not yet validated) and the CLI-only --out directory. */
export function parseToolArgs(tool: Tool, args: string[]): ToolCommand {
  const command = commandName(tool);
  const properties = tool.inputSchema.properties ?? {};
  const positional = positionalProperties(tool);
  const options = optionProperties(tool);
  const switches = tool.cli?.switches ?? {};
  const acceptsOut = (tool.cli?.outputs ?? []).length > 0;
  const given: Record<string, unknown> = {};
  const seen = new Set<string>();
  let out: string | undefined;
  let next = 0;

  for (let i = 0; i < args.length; i++) {
    const arg = args[i]!;
    if (!arg.startsWith("--")) {
      const key = positional[next];
      if (key === undefined) throw new CliUsageError(`${command}: unexpected argument ${JSON.stringify(arg)}; usage: ${usageLine(tool)}`);
      const schema = properties[key]!;
      if (schema.type === "array") ((given[key] ??= []) as unknown[]).push(scalar(schema.items!, arg, `<${key}>`));
      else {
        given[key] = convert(schema, arg, `<${key}>`);
        next++;
      }
      continue;
    }
    if (seen.has(arg)) throw new CliUsageError(`${command}: duplicate option ${arg}`);
    seen.add(arg);
    const switchSpec = switches[arg.slice(2)];
    if (switchSpec) {
      Object.assign(given, switchSpec.set);
      continue;
    }
    const key = options.get(arg);
    const isOut = acceptsOut && arg === "--out";
    if (!key && !isOut) throw new CliUsageError(`${command}: unknown option ${arg}; options: ${[...Object.keys(switches).map((s) => `--${s}`), ...options.keys(), ...(acceptsOut ? ["--out"] : [])].join(" ") || "none"}`);
    if (key && properties[key]!.type === "boolean") {
      const value = args[i + 1];
      given[key] = value === "false" ? false : true;
      if (value === "true" || value === "false") i++;
      continue;
    }
    const value = args[++i];
    if (value === undefined || value.startsWith("--")) throw new CliUsageError(`${command}: ${arg} needs a value`);
    if (isOut) out = value;
    else given[key!] = convert(properties[key!]!, value, arg);
  }

  const missing = positional.filter((key) => (tool.inputSchema.required ?? []).includes(key) && given[key] === undefined);
  if (missing.length > 0) throw new CliUsageError(`${command} needs ${missing.map((key) => `<${key}>`).join(" ")}; usage: ${usageLine(tool)}`);
  return { input: { ...structuredClone(tool.cli?.defaults ?? {}), ...given }, ...(out === undefined ? {} : { out }) };
}

function usageLine(tool: Tool): string {
  const properties = tool.inputSchema.properties ?? {};
  const required = tool.inputSchema.required ?? [];
  const positional = positionalProperties(tool).map((key) => {
    const many = properties[key]!.type === "array" ? "…" : "";
    return required.includes(key) ? `<${key}${many}>` : `[${key}${many}]`;
  });
  return ["ukiyo", commandName(tool), ...positional, "[options]"].join(" ");
}

function valueHint(schema: JsonSchema): string {
  if (schema.type === "boolean") return "[true|false]";
  if (takesFile(schema)) return "FILE";
  if (schema.type === "array") return "A,B,…";
  if (schema.enum) return schema.enum.join("|");
  return schema.type === "string" ? "TEXT" : "N";
}

/** What the schema says about one property, in one line: description, accepted values, range, default. */
function detail(schema: JsonSchema, withEnum: boolean): string {
  const parts: string[] = [];
  if (schema.description) parts.push(schema.description.trim().replace(/\.$/, ""));
  if (takesFile(schema)) parts.push(schema.type === "object" ? "JSON object, passed inline" : "JSON array, passed inline");
  const values = schema.type === "array" ? schema.items?.enum : withEnum ? schema.enum : undefined;
  if (values) parts.push(`one of ${values.join("|")}`);
  if (schema.minimum !== undefined || schema.maximum !== undefined) parts.push(`${schema.minimum ?? ""}..${schema.maximum ?? ""}`);
  if (schema.default !== undefined) parts.push(`default ${Array.isArray(schema.default) ? schema.default.join(",") : String(schema.default as JsonValue)}`);
  return parts.join("; ");
}

/** The help block of one command, generated from the tool definition. */
export function toolHelp(tool: Tool): string {
  const properties = tool.inputSchema.properties ?? {};
  const lines = [`${usageLine(tool)}   ${tool.title} (${tool.name})`];
  const row = (flag: string, text: string) => lines.push(`  ${flag.length < 30 ? flag.padEnd(30) : `${flag}  `}${text}`.trimEnd());
  for (const key of positionalProperties(tool)) row(`<${key}>`, detail(properties[key]!, true));
  for (const [flag, spec] of Object.entries(tool.cli?.switches ?? {})) row(`--${flag}`, spec.description);
  for (const [flag, key] of optionProperties(tool)) row(`${flag} ${valueHint(properties[key]!)}`, detail(properties[key]!, false));
  if ((tool.cli?.outputs ?? []).length > 0) row("--out DIR", `copy ${tool.cli!.outputs!.join(", ")} into DIR (created; existing files are never overwritten)`);
  return lines.join("\n");
}
