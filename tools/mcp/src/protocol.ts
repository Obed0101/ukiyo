// MCP wire types and the input validation every tool goes through. Tools declare a JSON Schema subset once; the same
// object is advertised in tools/list and enforced here, so what an agent is told and what is checked never drift.

export const SUPPORTED_PROTOCOL_VERSIONS = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"] as const;

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type JsonSchema = {
  type: "object" | "string" | "number" | "integer" | "boolean" | "array";
  description?: string;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  items?: JsonSchema;
  enum?: readonly (string | number)[];
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  minItems?: number;
  maxItems?: number;
  pattern?: string;
  default?: JsonValue;
  additionalProperties?: boolean;
};

export type TextContent = { type: "text"; text: string };
export type ImageContent = { type: "image"; data: string; mimeType: string };
export type Content = TextContent | ImageContent;

export type ToolResult = {
  content: Content[];
  structuredContent?: Record<string, unknown>;
  isError?: boolean;
};

export type ToolContext = {
  /** Absolute path of the ukiyo repository the server works on. */
  root: string;
  log: (level: "debug" | "info" | "warn" | "error", event: string, data?: Record<string, unknown>) => void;
  signal: AbortSignal;
};

/** Shape every module in src/tools exports (default export: one tool or an array of tools). */
export type Tool = {
  name: string;
  title: string;
  description: string;
  inputSchema: JsonSchema & { type: "object" };
  run: (args: Record<string, unknown>, context: ToolContext) => Promise<ToolResult>;
};

export type JsonRpcRequest = { jsonrpc: "2.0"; id?: string | number; method: string; params?: Record<string, unknown> };

export const ErrorCode = Object.freeze({
  ParseError: -32700,
  InvalidRequest: -32600,
  MethodNotFound: -32601,
  InvalidParams: -32602,
  InternalError: -32603,
});

export class ToolInputError extends Error {
  constructor(message: string) {
    super(`[MCP:InvalidParams]: ${message}`);
  }
}

/** Validates and fills defaults. Throws ToolInputError naming the exact path that failed. */
export function validate(schema: JsonSchema, value: unknown, path = "arguments"): unknown {
  if (value === undefined && schema.default !== undefined) return structuredClone(schema.default);
  switch (schema.type) {
    case "object": {
      if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ToolInputError(`${path} must be an object`);
      const input = value as Record<string, unknown>;
      const output: Record<string, unknown> = {};
      for (const key of schema.required ?? []) {
        if (input[key] === undefined) throw new ToolInputError(`${path}.${key} is required`);
      }
      for (const [key, child] of Object.entries(input)) {
        const childSchema = schema.properties?.[key];
        if (!childSchema) {
          if (schema.additionalProperties === false) throw new ToolInputError(`${path}.${key} is not a known property`);
          output[key] = child;
          continue;
        }
        output[key] = validate(childSchema, child, `${path}.${key}`);
      }
      for (const [key, childSchema] of Object.entries(schema.properties ?? {})) {
        if (output[key] === undefined && childSchema.default !== undefined) output[key] = structuredClone(childSchema.default);
      }
      return output;
    }
    case "string": {
      if (typeof value !== "string") throw new ToolInputError(`${path} must be a string`);
      if (schema.minLength !== undefined && value.length < schema.minLength) throw new ToolInputError(`${path} is shorter than ${schema.minLength}`);
      if (schema.maxLength !== undefined && value.length > schema.maxLength) throw new ToolInputError(`${path} is longer than ${schema.maxLength}`);
      if (schema.pattern !== undefined && !new RegExp(schema.pattern).test(value)) throw new ToolInputError(`${path} must match ${schema.pattern}`);
      if (schema.enum && !schema.enum.includes(value)) throw new ToolInputError(`${path} must be one of ${schema.enum.join(", ")}`);
      return value;
    }
    case "number":
    case "integer": {
      if (typeof value !== "number" || !Number.isFinite(value)) throw new ToolInputError(`${path} must be a finite number`);
      if (schema.type === "integer" && !Number.isInteger(value)) throw new ToolInputError(`${path} must be an integer`);
      if (schema.minimum !== undefined && value < schema.minimum) throw new ToolInputError(`${path} must be >= ${schema.minimum}`);
      if (schema.maximum !== undefined && value > schema.maximum) throw new ToolInputError(`${path} must be <= ${schema.maximum}`);
      if (schema.enum && !schema.enum.includes(value)) throw new ToolInputError(`${path} must be one of ${schema.enum.join(", ")}`);
      return value;
    }
    case "boolean": {
      if (typeof value !== "boolean") throw new ToolInputError(`${path} must be a boolean`);
      return value;
    }
    case "array": {
      if (!Array.isArray(value)) throw new ToolInputError(`${path} must be an array`);
      if (schema.minItems !== undefined && value.length < schema.minItems) throw new ToolInputError(`${path} needs at least ${schema.minItems} items`);
      if (schema.maxItems !== undefined && value.length > schema.maxItems) throw new ToolInputError(`${path} allows at most ${schema.maxItems} items`);
      return schema.items ? value.map((item, i) => validate(schema.items as JsonSchema, item, `${path}[${i}]`)) : value;
    }
  }
}

export const text = (value: string): TextContent => ({ type: "text", text: value });

export const png = (bytes: Uint8Array): ImageContent => ({ type: "image", data: Buffer.from(bytes).toString("base64"), mimeType: "image/png" });

export const failure = (message: string): ToolResult => ({ content: [text(message)], isError: true });
