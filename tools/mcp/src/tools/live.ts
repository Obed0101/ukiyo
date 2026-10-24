// Drives a running development build through the dev bridge (see ../bridge.ts for discovery and the wire format).
import { bridgeRequest, liveSessions, publicSessions } from "../bridge.ts";
import { failure, png, text, ToolInputError, type Tool, type ToolResult } from "../protocol.ts";
import { studioRequest, studioSessions, type StudioSession } from "../../../../studio/src/discovery.ts";
import { inputEventSchema, patchSchema } from "./run.ts";

/** The bridge request for an action (everything but list), from already schema-validated arguments. */
export function liveRequest(args: Record<string, unknown>): Record<string, unknown> {
  const request: Record<string, unknown> = { cmd: args.action };
  if (args.action === "step") request.ticks = args.ticks;
  if (args.action === "input") {
    if (!Array.isArray(args.events) || args.events.length === 0) throw new ToolInputError("input needs events");
    request.events = args.events;
  }
  if (args.action === "record") request.on = args.on === true;
  if (args.pass !== undefined) {
    if (args.action !== "capture") throw new ToolInputError(`pass is a capture option; ${String(args.action)} does not take it`);
    request.pass = args.pass;
  }
  if (args.action === "patch") {
    if (args.patch === undefined) throw new ToolInputError('patch needs patch: {"id":"p1","tick":<current tick + margin>,"ops":[…]}; read the tick with status first');
    request.patch = args.patch;
  }
  return request;
}

/** The bridge's capture answer: `pass`, `passes` and `primaryPass` only when one pass was asked for. */
type CaptureResponse = { tick: number; width: number; height: number; backend: string; pass?: number; passes?: number; primaryPass?: number; png: string };

/**
 * A command the bridge refused, as an isError result. The engine's typed code (`[RENDER:OutOfRange]`,
 * `[SCENE:PastTick]`, …) stays in the message and is also returned as `code` ("RENDER:OutOfRange"), null without one.
 */
export function bridgeFailure(where: string, error: string | undefined): ToolResult {
  const message = error ?? "bridge command failed";
  const code = /\[([A-Z]+:[A-Za-z]+)\]/.exec(message)?.[1] ?? null;
  return { ...failure(`${where}: ${message}`), structuredContent: { error: message, code } };
}

/** What the Studio's reload answers: the run after the relaunch attached (or failed), with the comparison report. */
type ReloadRun = { name: string; state: string; pid: number | null; detail: string; reload: { ok: boolean; error?: string; fromTick?: number; match?: boolean; sourceChanged?: boolean; differences?: string[]; wasPaused?: boolean } | null };

/**
 * Reload is the Studio's job (it rebuilds and relaunches the processes it started), so this needs a running Studio and a
 * game it launched. The call returns when the new process is attached at the same tick, or says why it did not.
 */
export async function reload(pid: number | undefined, sessions: StudioSession[] = studioSessions(), request = studioRequest): Promise<ToolResult> {
  const studio = sessions[0];
  if (!studio) throw new ToolInputError("reload needs the Studio: start it with `bun studio/src/server.ts`, run the game from its Games panel, then reload");
  let run: ReloadRun;
  try {
    ({ run } = (await request(studio, "/api/run/reload", { wait: true, ...(pid === undefined ? {} : { pid }) }, 600_000)) as { run: ReloadRun });
  } catch (error) {
    return failure(error instanceof Error ? error.message : String(error));
  }
  const report = run.reload;
  if (!report?.ok) return { ...failure(`reload: ${report?.error ?? run.detail}`), structuredContent: { run } };
  const verdict = report.match
    ? `identical to the old process${report.sourceChanged ? " (the code changed, and the state still matches)" : ""}`
    : `DIFFERENT: ${(report.differences ?? []).slice(0, 5).join("; ")}${report.sourceChanged ? " (the code changed)" : ""}`;
  return {
    content: [text(`${run.name} reloaded at tick ${report.fromTick} (pid ${run.pid}${report.wasPaused ? ", paused" : ", running"}); restored state ${verdict}`)],
    structuredContent: { run },
  };
}

const live: Tool = {
  name: "ukiyo_live",
  title: "Drive a running game",
  description:
    "Talks to a desktop development build while it runs (dev bridge). Actions: list (running sessions), status, pause, resume, step (ticks), snapshot, capture (real GPU frame; with pass=K (or pass=primary, the game's view pass) only that camera of a multi-camera game, alone at its rect size; a pass the frame does not have is refused with [RENDER:OutOfRange]), input (inject key/pointer events, then resume or step to apply them), record (on=true starts recording player input; on=false returns it as a replayable input script), patch (submit one scene patch: queued for its tick, or applied now when its tick is the current one; refusals return the typed [SCENE:Code] message — read the tick with status, or pause, before choosing patch.tick), reload (rebuild and relaunch a game the Studio started and restore the same tick by replaying its recorded input and patches; needs a running Studio; answers with whether the restored state matches the old process). Pass pid when several games run; default is the newest.",
  inputSchema: {
    type: "object",
    required: ["action"],
    additionalProperties: false,
    properties: {
      action: { type: "string", enum: ["list", "status", "pause", "resume", "step", "snapshot", "capture", "input", "record", "patch", "reload"] },
      pid: { type: "integer", minimum: 1 },
      ticks: { type: "integer", minimum: 0, maximum: 36_000, default: 1 },
      events: { type: "array", items: inputEventSchema, maxItems: 1000 },
      on: { type: "boolean" },
      pass: {
        anyOf: [{ type: "integer", minimum: 0 }, { type: "string", enum: ["primary"] }],
        description: 'capture action: one pass (camera) of a multi-camera frame, by id or "primary" (the pass the game marks as its view); omitted, the whole composed frame.',
      },
      patch: { ...patchSchema, description: "patch action: one scene patch (docs/scenes-patches.md); tick must be the current tick or later." },
    },
  },
  async run(args) {
    const sessions = liveSessions();
    if (args.action === "list") {
      const list = publicSessions(sessions);
      return { content: [text(list.length ? JSON.stringify(list, null, 2) : "No running dev builds. Start one: dotnet run --project games/<Name>/Hosts/Desktop")], structuredContent: { sessions: list } };
    }

    if (args.action === "reload") return reload(args.pid as number | undefined);

    const request = liveRequest(args);
    const session = args.pid === undefined ? sessions[0] : sessions.find((s) => s.pid === args.pid);
    if (!session) {
      throw new ToolInputError(args.pid === undefined ? "no running dev build found in ~/.ukiyo/live" : `no running dev build with pid ${args.pid}`);
    }

    const response = await bridgeRequest(session, request);
    if (!response.ok) return bridgeFailure(`${session.game} (pid ${session.pid})`, response.error);
    if (args.action === "capture") {
      const { png: data, ...metadata } = response.result as CaptureResponse;
      const which = metadata.pass === undefined ? "" : ` · pass ${metadata.pass} of ${metadata.passes}${metadata.pass === metadata.primaryPass ? " (primary)" : ""}`;
      return {
        content: [text(`${session.game} tick ${metadata.tick} · ${metadata.width}x${metadata.height}${which} · ${metadata.backend} (GPU readback)`), png(Buffer.from(data, "base64"))],
        structuredContent: metadata,
      };
    }

    return { content: [text(JSON.stringify(response.result, null, 2))], structuredContent: { result: response.result as Record<string, unknown> } };
  },
};

export default live;
