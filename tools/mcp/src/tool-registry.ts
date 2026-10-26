// Tool discovery shared by the MCP server and the CLI: every module in src/tools is a tool (or an array of tools), so
// adding a file adds an MCP tool and a CLI command.
import { readdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import type { Tool } from "./protocol.ts";

export const TOOLS_DIR = join(import.meta.dir, "tools");

export async function loadTools(dir = TOOLS_DIR): Promise<Map<string, Tool>> {
  const tools = new Map<string, Tool>();
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
