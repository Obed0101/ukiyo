// Drives a running development build through the dev bridge (see ../bridge.ts for discovery and the wire format).
import { bridgeRequest, liveSessions, publicSessions } from "../bridge.ts";
import { failure, png, text, ToolInputError, type Tool, type ToolResult } from "../protocol.ts";
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

const live: Tool = {
  name: "ukiyo_live",
  title: "Drive a running game",
  description:
    "Talks to a desktop development build while it runs (dev bridge). Actions: list (running sessions), status, pause, resume, step (ticks), snapshot, capture (real GPU frame; with pass=K only camera K of a multi-camera game, alone at its rect size; a pass the frame does not have is refused with [RENDER:OutOfRange]), input (inject key/pointer events, then resume or step to apply them), record (on=true starts recording player input; on=false returns it as a replayable input script), patch (submit one scene patch: queued for its tick, or applied now when its tick is the current one; refusals return the typed [SCENE:Code] message — read the tick with status, or pause, before choosing patch.tick). Pass pid when several games run; default is the newest.",
  inputSchema: {
    type: "object",
    required: ["action"],
    additionalProperties: false,
    properties: {
      action: { type: "string", enum: ["list", "status", "pause", "resume", "step", "snapshot", "capture", "input", "record", "patch"] },
      pid: { type: "integer", minimum: 1 },
      ticks: { type: "integer", minimum: 0, maximum: 36_000, default: 1 },
      events: { type: "array", items: inputEventSchema, maxItems: 1000 },
      on: { type: "boolean" },
      pass: { type: "integer", minimum: 0, description: "capture action: one pass (camera) id of a multi-camera frame; omitted, the whole composed frame." },
      patch: { ...patchSchema, description: "patch action: one scene patch (docs/scenes-patches.md); tick must be the current tick or later." },
    },
  },
  async run(args) {
    const sessions = liveSessions();
    if (args.action === "list") {
      const list = publicSessions(sessions);
      return { content: [text(list.length ? JSON.stringify(list, null, 2) : "No running dev builds. Start one: dotnet run --project games/<Name>/Hosts/Desktop")], structuredContent: { sessions: list } };
    }

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
