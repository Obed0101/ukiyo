// Drives a running development build through the dev bridge (see ../bridge.ts for discovery and the wire format).
import { bridgeRequest, liveSessions, publicSessions } from "../bridge.ts";
import { failure, png, text, ToolInputError, type Tool } from "../protocol.ts";
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
  if (args.action === "patch") {
    if (args.patch === undefined) throw new ToolInputError('patch needs patch: {"id":"p1","tick":<current tick + margin>,"ops":[…]}; read the tick with status first');
    request.patch = args.patch;
  }
  return request;
}

const live: Tool = {
  name: "ukiyo_live",
  title: "Drive a running game",
  description:
    "Talks to a desktop development build while it runs (dev bridge). Actions: list (running sessions), status, pause, resume, step (ticks), snapshot, capture (real GPU frame), input (inject key/pointer events, then resume or step to apply them), record (on=true starts recording player input; on=false returns it as a replayable input script), patch (submit one scene patch: queued for its tick, or applied now when its tick is the current one; refusals return the typed [SCENE:Code] message — read the tick with status, or pause, before choosing patch.tick). Pass pid when several games run; default is the newest.",
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
    if (!response.ok) return failure(`${session.game} (pid ${session.pid}): ${response.error}`);
    if (args.action === "capture") {
      const capture = response.result as { tick: number; width: number; height: number; backend: string; png: string };
      return {
        content: [text(`${session.game} tick ${capture.tick} · ${capture.width}x${capture.height} · ${capture.backend} (GPU readback)`), png(Buffer.from(capture.png, "base64"))],
        structuredContent: { tick: capture.tick, width: capture.width, height: capture.height, backend: capture.backend },
      };
    }

    return { content: [text(JSON.stringify(response.result, null, 2))], structuredContent: { result: response.result as Record<string, unknown> } };
  },
};

export default live;
