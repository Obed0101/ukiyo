import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { dotnetCommand, findGame, hostProject, parseHeadless, runProcess, tail } from "../engine.ts";
import { png, text, type Content, type JsonSchema, type Tool, type ToolContext, type ToolResult } from "../protocol.ts";

export const inputEventSchema: JsonSchema = {
  type: "object",
  description: 'One input event: {"tick":10,"key":"Space","down":true} · {"tick":12,"pointer":[320,180]} · {"tick":13,"button":"Left","down":true,"pointer":[320,180]}. Keys: A-Z, D0-D9, Space, Enter, Escape, Tab, Backspace, Left, Right, Up, Down, LeftShift, LeftControl, F1-F12 (or browser codes like "ArrowLeft"). Pointer is in drawing-buffer pixels.',
  properties: {
    tick: { type: "integer", minimum: 0 },
    key: { type: "string" },
    button: { type: "string", enum: ["Left", "Right", "Middle"] },
    down: { type: "boolean" },
    pointer: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 2 },
  },
};

const schema: JsonSchema & { type: "object" } = {
  type: "object",
  required: ["game"],
  additionalProperties: false,
  properties: {
    game: { type: "string", description: "Game folder name under games/ or samples/, e.g. LanternRun." },
    checkpoints: { type: "array", items: { type: "integer", minimum: 0, maximum: 216_000 }, minItems: 1, maxItems: 32, default: [0, 60, 180, 600], description: "Ticks (60 per second) at which to print a snapshot." },
    input: { type: "array", items: { ...inputEventSchema, required: ["tick"] }, maxItems: 10_000, description: "Scripted input fed tick by tick. Deterministic: the same script always gives the same snapshots." },
    size: { type: "string", pattern: "^\\d{2,4}x\\d{2,4}$", default: "1280x720", description: "Drawing-buffer size, e.g. 640x360." },
    audio: { type: "boolean", default: false, description: "Mix every sound the run plays into a WAV and report duration, sound count, peak and RMS (silence is peak 0)." },
    timeoutSeconds: { type: "integer", minimum: 10, maximum: 900, default: 240 },
  },
};

/** Runs a game's headless host. With capture=true the CPU renderer draws every checkpoint to PNG. */
export async function runHeadless(args: Record<string, unknown>, context: ToolContext, capture: boolean): Promise<ToolResult> {
  const game = findGame(context.root, String(args.game));
  const project = hostProject(game, "Headless");
  const work = mkdtempSync(join(tmpdir(), "ukiyo-run-"));
  const checkpoints = (args.checkpoints as number[]).join(",");
  const cliArgs = ["run", "--project", project, "-c", "Debug", "--", "--checkpoints", checkpoints, "--size", String(args.size)];
  if (Array.isArray(args.input) && args.input.length > 0) {
    const scriptPath = join(work, "input.json");
    writeFileSync(scriptPath, JSON.stringify(args.input));
    cliArgs.push("--input", scriptPath);
  }
  if (capture) cliArgs.push("--capture", join(work, "frames"));
  if (args.audio === true) cliArgs.push("--audio", join(work, "run.wav"));

  const { command, env } = dotnetCommand();
  context.log("info", "headless.start", { game: game.name, checkpoints, capture });
  const result = await runProcess(command, cliArgs, { cwd: context.root, env, timeoutMs: Number(args.timeoutSeconds) * 1000, signal: context.signal });
  const output = parseHeadless(result.stdout);
  const summary = {
    game: game.name,
    exitCode: result.code,
    timedOut: result.timedOut,
    ms: result.ms,
    snapshots: output.snapshots,
    captures: output.captures,
    audio: output.audio,
    info: output.info,
  };

  if (result.code !== 0 || result.timedOut) {
    return {
      isError: true,
      content: [text(`headless run failed (exit ${result.code}${result.timedOut ? ", timed out" : ""}).\n--- stdout ---\n${tail(result.stdout)}\n--- stderr ---\n${tail(result.stderr)}`)],
      structuredContent: summary,
    };
  }

  const content: Content[] = [text(JSON.stringify({ ...summary, snapshots: output.snapshots }, null, 2))];
  for (const path of output.captures.slice(-6)) {
    content.push(text(`frame ${path} (CPU renderer: same geometry and colors as wgpu/three, no MSAA)`));
    content.push(png(readFileSync(path)));
  }
  return { content, structuredContent: summary };
}

const run: Tool = {
  name: "ukiyo_run",
  title: "Run headless",
  description:
    "Builds and runs a game with no window or GPU, optionally with scripted input, and returns a JSON snapshot (entity poses + named values) at each checkpoint tick. Use it to prove behavior: positions, scores, state machines.",
  inputSchema: schema,
  run: (args, context) => runHeadless(args, context, false),
};

export default run;
export { schema as runSchema };
