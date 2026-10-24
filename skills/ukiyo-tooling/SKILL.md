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
  line formats (`parseHeadless`: `snapshot`, `capture`, `capture-pass`, `audio`, `patches`), never by scraping prose.
- Shapes shared by several tools are exported once and reused: `inputEventSchema` and `patchSchema` (tools/run.ts)
  serve `ukiyo_run`, `ukiyo_capture` and `ukiyo_live`. The engine stays the judge: a schema only tells agents the
  shape; a scene patch's prefabs, ranges and ticks are refused by the engine with its typed `[SCENE:Code]` message,
  and the tool passes that message through instead of re-checking it.
- `tools/mcp/src/cli.ts` is the thin editor-neutral adapter over the same code. `live <action>` (bridge.ts) and
  `run <Game>` (`runHeadless`) have their own parsing and exit 1 on any error. Every other tool module is a command
  through one generic mapping (`src/cli-args.ts`, tools found by `src/tool-registry.ts` like the server):
  `ukiyo_new_game` → `new-game`, required string/number properties positional, the rest `--kebab-case`, lists
  comma-separated, objects and arrays of objects as JSON files, then `validate()` with the tool's own schema. A new
  tool module is a new command and gets its `--help` block from its schema; add optional `cli` hints
  (`CliHints`: `positional`, `switches`, `defaults`, `outputs`) only when the derived spelling reads badly, as
  `ukiyo_import` (`import [--check] [specs…]`) and `ukiyo_capture` (`--out DIR` copies every path under `captures` and `passes`) do. stdout is the
  `structuredContent` JSON, `[UKIYO]: …` errors on stderr; exit 2 for usage errors (including `ToolInputError`),
  exit 1 for tool failures (an `isError` result still prints its structured result). Tests: test/cli-tools.test.ts.
  The golden transcript `tools/mcp/test/golden/cli-flow.txt` records `--help` and a scaffold → run → capture →
  import check → removal flow; `bun scripts/cli-flow.ts` replays it in a temporary repository copy and fails with a
  diff on any change (CI runs it). When you change help text, a schema or a tool's output on purpose, run
  `bun scripts/cli-flow.ts --update` and commit the golden diff with the change. Output must stay the same on every
  OS: sort directory listings, report durations only under `ms` / `…Ms` keys and paths only inside the repository
  or `TMPDIR` (the script normalizes exactly those; the rules are in tools/mcp/README.md).
- Secrets only from environment variables (`OPENAI_API_KEY`, `GEMINI_API_KEY`); never log them or put them in results.
- Shared clients, reused by the Studio: `tools/mcp/src/bridge.ts` (dev bridge discovery and requests) and
  `studio/src/discovery.ts` + `studio/src/manifest.ts` (Studio discovery and files).
- Asset importer client: `tools/mcp/src/importer.ts` (spec discovery, repo-path checks, one importer build then one
  run per spec, `[import]` / `[IMPORT:Code]` parsing), used by `ukiyo_import` (run/check) and `ukiyo_status`. Layout conformance
  (`ukiyo_check`): `tools/mcp/src/conformance.ts`. Facts read
  from a game's source without building it (render profile, SceneRegistry): `tools/mcp/src/game-source.ts`. Tool list
  and output shapes: `tools/mcp/README.md`.

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
