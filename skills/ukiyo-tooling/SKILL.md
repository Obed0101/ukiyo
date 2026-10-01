---
name: ukiyo-tooling
description: Use when extending ukiyo's agent tooling — adding or changing an MCP tool in tools/mcp, writing or updating a skill in skills/ and its presets, or changing what the headless host prints for tools. Covers the tool module contract, input schemas, errors vs results, logging, tests and the truth rules tools must follow.
---

# Agent tooling: MCP tools and skills

Agents work on ukiyo through two layers. **Skills** (skills/*/SKILL.md) teach the APIs and rules; most work is code
edits guided by them. **MCP tools** (tools/mcp) do what code edits cannot: run headless builds, capture frames, drive a
live game, generate assets, read what the person sees in the Studio. A capability that is just "edit a file" belongs
in a skill, not a tool.

## MCP server shape

`tools/mcp/src/server.ts` is a dependency-free JSON-RPC 2.0 server on stdio (Bun). stdout is the protocol only; logs
are JSON lines on stderr through `context.log(level, event, data)`. Tools are discovered from `src/tools/*.ts`:
adding a tool is adding a file.

```ts
import { text, failure, ToolInputError, type Tool } from "../protocol.ts";

const tool: Tool = {
  name: "ukiyo_thing",                          // ^[a-z][a-z0-9_]{2,63}$, unique
  title: "Short human title",
  description: "What it does, every action, defaults, and what its output proves (and does not).",
  inputSchema: {                                // JSON Schema subset, enforced by validate() before run
    type: "object", required: ["game"], additionalProperties: false,
    properties: { game: { type: "string" }, ticks: { type: "integer", minimum: 0, maximum: 36000, default: 60 } },
  },
  async run(args, { root, log, signal }) {
    if (bad) throw new ToolInputError("why, and what to pass instead");   // → isError result naming the input
    const outcome = await work(signal);                                     // honor cancellation
    if (!outcome.ok) return failure(`what failed: ${outcome.message}`);   // a result the agent can act on
    return { content: [text(summary)], structuredContent: outcome };       // text for people, structure for agents
  },
};
export default tool;                                                       // or an array of tools
```

- The schema is advertised in `tools/list` and enforced by `validate()` (protocol.ts): types, enums, ranges,
  patterns, defaults, `additionalProperties: false`. Do not re-validate the same things by hand; validate meaning.
- Thrown errors become `isError` results, not protocol errors. Messages say what to do next.
- Paths from arguments go through `insideRoot(root, path)`; never read or write outside the repository except the
  documented state dirs (`~/.ukiyo/live`, `~/.ukiyo/studio`).
- Long work uses `runProcess` from engine.ts with a timeout and the abort signal. Parse host output by its documented
  line formats (`parseHeadless`), never by scraping prose.
- Secrets only from environment variables (`OPENAI_API_KEY`, `GEMINI_API_KEY`); never log them or put them in results.
- Shared clients, reused by the Studio: `tools/mcp/src/bridge.ts` (dev bridge discovery and requests) and
  `studio/src/discovery.ts` + `studio/src/manifest.ts` (Studio discovery and files).

After adding a tool: add its name to the expected list in `tools/mcp/test/server.test.ts`, add focused tests for its
pure parts, mention it in the server `INSTRUCTIONS` if agents need to know when to reach for it, and teach it in the
skill of its area.

## Truth rules every tool keeps

- Say which renderer produced a frame (CPU capture vs GPU readback) and never present one as the other.
- A build or test run reports what ran, what failed and what was skipped. No "should work".
- Generated art and sounds are labeled generated; procedural fallbacks are labeled procedural.

## Skills

Each skill is `skills/<name>/SKILL.md` with frontmatter `name` (= folder) and a `description` that starts with "Use
when…" and names the triggers. Body: the exact API as it exists (copy signatures from the code), the rules that keep
games deterministic, and how to prove the change. Short, concrete, no aspirational APIs.

- Add the skill to `skills/README.md` and to the presets in `skills/presets.json` (`simple`, `balanced`, `complete`
  for game makers; `engine` for people and agents working on ukiyo itself).
- `ukiyo_skills {"action":"install","preset":…}` copies them into `.claude/skills` and/or `.agents/skills`.
- When an API changes, the skill that teaches it changes in the same commit. A skill that lies is worse than none.

Related: ukiyo-engine, ukiyo-studio, ukiyo-live.
