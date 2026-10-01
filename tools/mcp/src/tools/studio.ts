// The Studio is the person's window onto a running game. Its UI is files in studio/ (panels, layouts, themes,
// studio.json), so agents change it by editing code; this tool covers what files cannot: what the person is looking
// at right now, telling them something, and validated composition changes.
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { studioRequest, studioSessions } from "../../../../studio/src/discovery.ts";
import { checkStudio, readManifest, writeComposition, type CompositionPatch } from "../../../../studio/src/manifest.ts";
import { failure, text, ToolInputError, type Tool } from "../protocol.ts";

const START = "bun studio/src/server.ts";

function panelSummary(dir: string, id: string) {
  const source = readFileSync(join(dir, "panels", `${id}.js`), "utf8");
  const field = (name: string) => source.match(new RegExp(`^\\s*${name}:\\s*"([^"]*)"`, "m"))?.[1] ?? "";
  return { id, title: field("title"), description: field("description") };
}

const studio: Tool = {
  name: "ukiyo_studio",
  title: "Studio (the person's view)",
  description:
    "Works with the ukiyo Studio, the local web UI where a person watches and steers a running game. Actions: list (panels, layouts, themes and the current composition from studio/), check (validate every Studio file), compose (change layout, theme, shown/hidden panels or panel options in studio/studio.json; open Studios apply it live), view (what the person is looking at right now: layout, focused panel, selected entity, tick, paused, errors), notify (show the person a message in the Studio). To change how a panel looks or works, edit studio/panels/<id>.js, studio/web/studio.css or studio/themes/*.json directly: the Studio reloads them live. See the ukiyo-studio skill.",
  inputSchema: {
    type: "object",
    required: ["action"],
    additionalProperties: false,
    properties: {
      action: { type: "string", enum: ["list", "check", "compose", "view", "notify"] },
      layout: { type: "string", pattern: "^[a-z][a-z0-9-]{0,47}$" },
      theme: { type: "string", pattern: "^[a-z][a-z0-9-]{0,47}$", description: '"auto" follows the system light/dark setting.' },
      show: { type: "array", items: { type: "string", pattern: "^[a-z][a-z0-9-]{0,47}$" }, maxItems: 64, description: "Panels to show in addition to the layout (they go to the dock row). Replaces the current list." },
      hide: { type: "array", items: { type: "string", pattern: "^[a-z][a-z0-9-]{0,47}$" }, maxItems: 64, description: "Panels of the layout to hide. Replaces the current list." },
      panelOptions: { type: "object", description: 'Per-panel options, e.g. {"values":{"pin":["score","lives"]}}. null removes a panel\'s options.' },
      text: { type: "string", minLength: 1, maxLength: 500 },
      level: { type: "string", enum: ["info", "success", "warn", "error"], default: "info" },
    },
  },
  async run(args, { root, log }) {
    const dir = join(root, "studio");
    if (!existsSync(join(dir, "src", "server.ts"))) throw new ToolInputError(`no Studio in ${root} (expected studio/src/server.ts)`);

    switch (args.action) {
      case "list": {
        const manifest = readManifest(dir);
        const result = {
          panels: manifest.panels.map(({ id }) => panelSummary(dir, id)),
          layouts: Object.fromEntries(Object.entries(manifest.layouts).map(([name, l]) => [name, { description: l.description, panels: l.panels }])),
          themes: Object.fromEntries(Object.entries(manifest.themes).map(([name, t]) => [name, { description: t.description, colorScheme: t.colorScheme }])),
          composition: manifest.composition,
          running: studioSessions().map(({ pid, url, startedAt }) => ({ pid, url, startedAt })),
        };
        return { content: [text(JSON.stringify(result, null, 2))], structuredContent: result };
      }
      case "check": {
        const problems = checkStudio(dir);
        return problems.length
          ? { content: [text(`Studio files have problems:\n${problems.join("\n")}`)], structuredContent: { problems }, isError: true }
          : { content: [text("All Studio files are valid.")], structuredContent: { problems } };
      }
      case "compose": {
        const patch: CompositionPatch = {};
        for (const key of ["layout", "theme", "show", "hide"] as const) {
          if (args[key] !== undefined) (patch as Record<string, unknown>)[key] = args[key];
        }
        if (args.panelOptions !== undefined) patch.panels = args.panelOptions as CompositionPatch["panels"];
        if (Object.keys(patch).length === 0) throw new ToolInputError("compose needs layout, theme, show, hide or panelOptions");
        const composition = writeComposition(patch, dir);
        log("info", "studio.composed", { patch });
        const open = studioSessions().length;
        return {
          content: [text(`studio/studio.json updated${open ? `; ${open} open Studio${open > 1 ? "s" : ""} apply it live` : `; no Studio is open (start one: ${START})`}.\n${JSON.stringify(composition, null, 2)}`)],
          structuredContent: { composition, openStudios: open },
        };
      }
      case "view":
      case "notify": {
        const session = studioSessions()[0];
        if (!session) return failure(`No Studio is running. Start it with: ${START} (then open the URL it prints).`);
        if (args.action === "view") {
          const view = (await studioRequest(session, "/api/view")) as Record<string, unknown>;
          if (!view.updatedAt) return { content: [text(`The Studio at ${session.url} is running but no page has reported a view yet (is it open in a browser?).`)], structuredContent: { url: session.url, ...view } };
          return { content: [text(JSON.stringify(view, null, 2))], structuredContent: { url: session.url, ...view } };
        }
        if (typeof args.text !== "string") throw new ToolInputError("notify needs text");
        const result = (await studioRequest(session, "/api/notice", { text: args.text, level: args.level, from: "agent" })) as { delivered: number };
        return {
          content: [text(result.delivered ? `Shown in ${result.delivered} open Studio page(s).` : `No Studio page is open at ${session.url}; the notice was not seen.`)],
          structuredContent: result,
        };
      }
    }
    throw new ToolInputError(`unknown action ${String(args.action)}`);
  },
};

export default studio;
