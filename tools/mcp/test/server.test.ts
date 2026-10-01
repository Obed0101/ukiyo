import { describe, expect, test } from "bun:test";
import { validate, ToolInputError } from "../src/protocol.ts";

describe("input validation", () => {
  const schema = {
    type: "object" as const,
    required: ["name"],
    additionalProperties: false,
    properties: {
      name: { type: "string" as const, pattern: "^[a-z]+$" },
      size: { type: "integer" as const, minimum: 4, maximum: 64, default: 16 },
      tags: { type: "array" as const, items: { type: "string" as const }, maxItems: 2 },
    },
  };

  test("fills defaults and keeps valid values", () => {
    expect(validate(schema, { name: "hero" })).toEqual({ name: "hero", size: 16 });
  });

  test.each([
    [{}, "arguments.name is required"],
    [{ name: "Hero" }, "arguments.name must match"],
    [{ name: "hero", size: 4.5 }, "must be an integer"],
    [{ name: "hero", size: 100 }, "must be <= 64"],
    [{ name: "hero", tags: ["a", "b", "c"] }, "at most 2 items"],
    [{ name: "hero", extra: 1 }, "not a known property"],
  ])("rejects %j", (input, message) => {
    expect(() => validate(schema, input)).toThrow(ToolInputError);
    expect(() => validate(schema, input)).toThrow(message);
  });
});

describe("stdio server", () => {
  test("initializes and lists every tool with a schema", async () => {
    const server = Bun.spawn(["bun", new URL("../src/server.ts", import.meta.url).pathname], { stdin: "pipe", stdout: "pipe", stderr: "pipe" });
    const requests = [
      { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "test", version: "0" } } },
      { jsonrpc: "2.0", method: "notifications/initialized" },
      { jsonrpc: "2.0", id: 2, method: "tools/list" },
      { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "ukiyo_sprite_generate", arguments: { name: "Bad Name", subject: "x" } } },
      { jsonrpc: "2.0", id: 4, method: "nope" },
    ];
    server.stdin.write(requests.map((r) => JSON.stringify(r)).join("\n") + "\n");
    server.stdin.flush();

    const responses = new Map<number, { result?: Record<string, unknown>; error?: { code: number; message: string } }>();
    const reader = server.stdout.getReader();
    let buffer = "";
    while (responses.size < 4) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += new TextDecoder().decode(value);
      let newline: number;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const message = JSON.parse(buffer.slice(0, newline));
        buffer = buffer.slice(newline + 1);
        responses.set(message.id, message);
      }
    }
    server.stdin.end();
    server.kill();

    expect(responses.get(1)?.result?.protocolVersion).toBe("2025-06-18");
    const tools = (responses.get(2)?.result?.tools ?? []) as { name: string; inputSchema: { type: string } }[];
    expect(tools.map((t) => t.name).sort()).toEqual([
      "ukiyo_capture",
      "ukiyo_live",
      "ukiyo_new_game",
      "ukiyo_run",
      "ukiyo_skills",
      "ukiyo_sound_generate",
      "ukiyo_sprite_generate",
      "ukiyo_sprite_process",
      "ukiyo_sprite_sheet",
      "ukiyo_status",
      "ukiyo_studio",
      "ukiyo_test",
    ]);
    expect(tools.every((t) => t.inputSchema.type === "object")).toBe(true);
    expect(responses.get(3)?.error?.code).toBe(-32602);
    expect(responses.get(4)?.error?.code).toBe(-32601);
  }, 20_000);
});
