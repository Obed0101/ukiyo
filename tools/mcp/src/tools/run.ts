import { existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { dotnetCommand, findGame, hostProject, insideRoot, parseHeadless, runProcess, tail, type HeadlessOutput } from "../engine.ts";
import { png, text, ToolInputError, type Content, type JsonSchema, type Tool, type ToolContext, type ToolResult } from "../protocol.ts";

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

const SCENE_ID = "^[A-Za-z0-9_.-]{1,64}$";

const patchOpSchema: JsonSchema = {
  type: "object",
  required: ["op", "id"],
  additionalProperties: false,
  description: 'spawn {"op":"spawn","id","prefab","position":[x,y(,z)],"values"?} · dispose {"op":"dispose","id"} · retarget {"op":"retarget","id","position"?,"values"?} (at least one of position/values).',
  properties: {
    op: { type: "string", enum: ["spawn", "dispose", "retarget"] },
    id: { type: "string", pattern: SCENE_ID, description: "Entity id." },
    prefab: { type: "string", description: "spawn only: a prefab the game registered." },
    position: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 3 },
    values: { type: "object", description: "Prefab parameters by name, each a number within the prefab's declared range." },
  },
};

/** One scene patch (docs/scenes-patches.md). The engine re-checks everything; this schema is what agents are told. */
export const patchSchema: JsonSchema = {
  type: "object",
  required: ["id", "tick", "ops"],
  additionalProperties: false,
  description: "A scene patch: a typed diff applied whole or not at all at the boundary before `tick`.",
  properties: {
    id: { type: "string", pattern: SCENE_ID, description: "Unique for the session; recorded in snapshots." },
    tick: { type: "integer", minimum: 0, description: "Boundary it applies at: after `tick` updates, before Update(tick)." },
    scene: { type: "string", description: "Optional guard: refused at its tick unless this scene is active." },
    ops: { type: "array", items: patchOpSchema, minItems: 1, maxItems: 1000 },
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
    patches: { type: "array", items: patchSchema, minItems: 1, maxItems: 1000, description: "Scene patch log, submitted in order before tick 0 (games with a SceneRegistry). Same log ⇒ same snapshots." },
    patchLog: { type: "string", description: "Instead of `patches`: path (inside the repository) of a patch log file, a JSON array of patches." },
    size: { type: "string", pattern: "^\\d{2,4}x\\d{2,4}$", default: "1280x720", description: "Drawing-buffer size, e.g. 640x360." },
    audio: { type: "boolean", default: false, description: "Mix every sound the run plays into a WAV and report duration, sound count, peak and RMS (silence is peak 0)." },
    timeoutSeconds: { type: "integer", minimum: 10, maximum: 900, default: 240 },
  },
};

type PatchRef = { id: string; tick: number };
type PatchRejection = PatchRef & { code: string; message: string };

/** The snapshot's bounded patch history (docs/scenes-patches.md): windows of the last 16, counts and hashes of all. */
type SceneHistory = {
  appliedCount: number;
  appliedHash: string;
  patches: PatchRef[];
  rejectedCount: number;
  rejectedHash: string;
  rejected: PatchRejection[];
};

export type PatchReport = {
  log: string;
  submitted: number;
  /** Patches applied by the last checkpoint, all of them. */
  appliedCount: number;
  /** FNV-1a 64 chain over every applied patch's id and tick: equal hashes ⇒ the same patches applied in the same order. */
  appliedHash: string | null;
  /** The last 16 applied patches, in application order (the snapshot's window; earlier ones are only counted). */
  applied: PatchRef[];
  /** Patches the world refused at their tick, all of them. */
  rejectedCount: number;
  rejectedHash: string | null;
  /** The last 16 refusals, with the typed [SCENE:Code] message. */
  rejected: PatchRejection[];
  /** Submitted patches whose tick is after the last checkpoint: neither applied nor refused yet. */
  notReached: PatchRef[];
};

/** Where a run's patch log comes from: inline patches are written to a temp file; a path is used as is. */
export function patchLogFile(args: Record<string, unknown>, root: string, work: string): { path: string; patches: PatchRef[] | null } | null {
  const inline = Array.isArray(args.patches) ? (args.patches as PatchRef[]) : null;
  if (inline && args.patchLog !== undefined) throw new ToolInputError("pass either patches (inline) or patchLog (a file), not both");
  if (inline) {
    const path = join(work, "patches.json");
    writeFileSync(path, JSON.stringify(inline));
    return { path, patches: inline.map(({ id, tick }) => ({ id, tick })) };
  }
  if (typeof args.patchLog !== "string") return null;
  const path = insideRoot(root, args.patchLog);
  if (!existsSync(path)) throw new ToolInputError(`patchLog ${args.patchLog} does not exist`);
  // The host is the judge of the log; this read only names the patches for notReached. A malformed log is refused there.
  let patches: PatchRef[] | null = null;
  try {
    const parsed: unknown = JSON.parse(readFileSync(path, "utf8"));
    if (Array.isArray(parsed)) patches = parsed.map((patch) => ({ id: String(patch?.id), tick: Number(patch?.tick) }));
  } catch {
    patches = null;
  }
  return { path, patches };
}

/** Applied / rejected / not reached, read from the last snapshot's `scene` section and the host's `patches` line. */
export function patchReport(output: HeadlessOutput, patches: PatchRef[] | null): PatchReport | null {
  if (!output.patches) return null;
  const last = output.snapshots.at(-1);
  const scene = (last?.scene ?? {}) as Partial<SceneHistory>;
  // Every patch whose tick the run reached was applied or refused at that boundary, before the snapshot was taken, so
  // "not reached" follows from ticks alone; the windows are too short to look ids up in.
  const lastTick = typeof last?.tick === "number" ? last.tick : -1;
  return {
    log: output.patches.path,
    submitted: output.patches.submitted,
    appliedCount: scene.appliedCount ?? 0,
    appliedHash: scene.appliedHash ?? null,
    applied: scene.patches ?? [],
    rejectedCount: scene.rejectedCount ?? 0,
    rejectedHash: scene.rejectedHash ?? null,
    rejected: scene.rejected ?? [],
    notReached: (patches ?? []).filter((patch) => patch.tick > lastTick),
  };
}

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
  const patchLog = patchLogFile(args, context.root, work);
  if (patchLog) cliArgs.push("--patches", patchLog.path);
  if (capture) cliArgs.push("--capture", join(work, "frames"));
  if (args.audio === true) cliArgs.push("--audio", join(work, "run.wav"));

  const { command, env } = dotnetCommand();
  context.log("info", "headless.start", { game: game.name, checkpoints, capture, patches: patchLog?.patches?.length });
  const result = await runProcess(command, cliArgs, { cwd: context.root, env, timeoutMs: Number(args.timeoutSeconds) * 1000, signal: context.signal });
  const output = parseHeadless(result.stdout);
  const summary: Record<string, unknown> = {
    game: game.name,
    exitCode: result.code,
    timedOut: result.timedOut,
    ms: result.ms,
    snapshots: output.snapshots,
    captures: output.captures,
    audio: output.audio,
    info: output.info,
  };
  if (patchLog) {
    // A refused log stops the host before tick 0 with "[HEADLESS]: --patches <path>: [SCENE:Code]: …" on stderr.
    const refusal = result.stderr.split("\n").find((line) => line.startsWith("[HEADLESS]: --patches "));
    summary.patches = patchReport(output, patchLog.patches) ?? { log: patchLog.path, refused: refusal ?? null };
  }

  if (result.code !== 0 || result.timedOut) {
    return {
      isError: true,
      content: [text(`headless run failed (exit ${result.code}${result.timedOut ? ", timed out" : ""}).\n--- stdout ---\n${tail(result.stdout)}\n--- stderr ---\n${tail(result.stderr)}`)],
      structuredContent: summary,
    };
  }

  const content: Content[] = [text(JSON.stringify(summary, null, 2))];
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
    "Builds and runs a game with no window or GPU, optionally with scripted input and a scene patch log (patches inline or patchLog file), and returns a JSON snapshot (entity poses + named values) at each checkpoint tick. With patches it also reports how many were applied and how many the world rejected at their tick (the last 16 of each, rejections with the typed [SCENE:Code] message, plus a hash chain over all of them that two runs can compare), and which were not reached by the last checkpoint; an invalid log fails the run before tick 0. Use it to prove behavior: positions, scores, state machines.",
  inputSchema: schema,
  run: (args, context) => runHeadless(args, context, false),
};

export default run;
export { schema as runSchema };
