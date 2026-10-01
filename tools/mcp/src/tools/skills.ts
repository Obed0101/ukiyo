import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, realpathSync } from "node:fs";
import { join, relative } from "node:path";
import { insideRoot } from "../engine.ts";
import { text, ToolInputError, type Tool } from "../protocol.ts";

type Presets = { presets: Record<string, { description: string; skills: string[] }> };

function readPresets(root: string): Presets {
  return JSON.parse(readFileSync(join(root, "skills", "presets.json"), "utf8")) as Presets;
}

function skillSummary(root: string, name: string) {
  const body = readFileSync(join(root, "skills", name, "SKILL.md"), "utf8");
  const description = body.match(/^description:\s*(.+)$/m)?.[1]?.replace(/^["']|["']$/g, "") ?? "";
  return { name, description };
}

const skills: Tool = {
  name: "ukiyo_skills",
  title: "Engine skills",
  description:
    "Lists the ukiyo skills (per-area guides an agent loads: core loop, rendering, sprites, sprite generation, physics, input, UI, audio, testing, live bridge, new game, Studio; and for working on the engine itself: engine architecture, renderer development, hosts and dev bridge, tooling) and the simple/balanced/complete presets for games plus the engine preset, or installs a preset or chosen skills into a project for Claude Code (.claude/skills) and/or Codex-style agents (.agents/skills).",
  inputSchema: {
    type: "object",
    required: ["action"],
    additionalProperties: false,
    properties: {
      action: { type: "string", enum: ["list", "install"] },
      preset: { type: "string", enum: ["simple", "balanced", "complete", "engine"] },
      skills: { type: "array", items: { type: "string", pattern: "^[a-z0-9-]+$" }, maxItems: 64 },
      target: { type: "string", enum: ["claude", "agents", "both"], default: "both" },
      projectDir: { type: "string", description: "Where to install, inside the repository. Default: the repository root." },
    },
  },
  async run(args, { root, log }) {
    const available = readdirSync(join(root, "skills")).filter((d) => existsSync(join(root, "skills", d, "SKILL.md"))).sort();
    const { presets } = readPresets(root);
    if (args.action === "list") {
      const result = { skills: available.map((name) => skillSummary(root, name)), presets };
      return { content: [text(JSON.stringify(result, null, 2))], structuredContent: result };
    }

    const chosen = new Set<string>(args.skills as string[] | undefined);
    if (typeof args.preset === "string") {
      for (const name of presets[args.preset]?.skills ?? []) chosen.add(name);
    }
    if (chosen.size === 0) throw new ToolInputError("install needs a preset or a list of skills");
    const unknown = [...chosen].filter((s) => !available.includes(s));
    if (unknown.length) throw new ToolInputError(`unknown skills: ${unknown.join(", ")}. Available: ${available.join(", ")}`);

    const projectDir = typeof args.projectDir === "string" ? insideRoot(root, args.projectDir) : root;
    const targets = args.target === "both" ? [".claude/skills", ".agents/skills"] : [args.target === "claude" ? ".claude/skills" : ".agents/skills"];
    const installed: string[] = [];
    for (const target of targets) {
      for (const name of chosen) {
        const destination = join(projectDir, target, name);
        // The engine repository links its own skills (.claude/skills/<name> → skills/<name>); copying would overwrite the source.
        if (existsSync(destination) && realpathSync(destination) === realpathSync(join(root, "skills", name))) {
          installed.push(`${relative(root, destination)} (already linked)`);
          continue;
        }
        mkdirSync(destination, { recursive: true });
        cpSync(join(root, "skills", name), destination, { recursive: true });
        installed.push(relative(root, destination));
      }
    }
    log("info", "skills.installed", { count: installed.length, preset: args.preset });
    return { content: [text(`Installed ${chosen.size} skills:\n${installed.join("\n")}`)], structuredContent: { installed } };
  },
};

export default skills;
