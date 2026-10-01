import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { dotnetCommand, listGames, runProcess } from "../engine.ts";
import { text, type Tool } from "../protocol.ts";
import { liveSessions } from "../bridge.ts";

const status: Tool = {
  name: "ukiyo_status",
  title: "Engine status",
  description:
    "Orientation for an agent: engine version, games and their hosts (headless/desktop/browser), installed toolchain (dotnet, native libs, three), skills and live dev-bridge sessions. Call it first.",
  inputSchema: { type: "object", properties: {}, additionalProperties: false },
  async run(_args, { root, signal }) {
    const props = readFileSync(join(root, "Directory.Build.props"), "utf8");
    const version = props.match(/<Version>([^<]+)<\/Version>/)?.[1] ?? "unknown";
    const { command, env } = dotnetCommand();
    const dotnet = await runProcess(command, ["--version"], { cwd: root, env, timeoutMs: 15_000, signal }).catch(() => null);
    const games = listGames(root).map((g) => ({ name: g.name, dir: relative(root, g.dir), hosts: Object.keys(g.hosts) }));
    const skillsDir = join(root, "skills");
    const skills = existsSync(skillsDir) ? readdirSync(skillsDir).filter((d) => existsSync(join(skillsDir, d, "SKILL.md"))) : [];
    const result = {
      engine: { version, root },
      toolchain: {
        dotnet: dotnet && dotnet.code === 0 ? dotnet.stdout.trim() : "not found (install the .NET SDK pinned in global.json)",
        nativeLibraries: existsSync(join(root, "native/osx-arm64/lib/libwgpu_native.dylib")) ? "present" : "missing: run scripts/fetch-native.sh for desktop hosts",
        three: existsSync(join(root, "web/three-adapter/node_modules/three")) ? "present" : "missing: (cd web/three-adapter && bun install) for browser hosts",
      },
      games,
      skills,
      live: liveSessions().map(({ pid, game, target, port }) => ({ pid, game, target, port })),
    };
    return { content: [text(JSON.stringify(result, null, 2))], structuredContent: result };
  },
};

export default status;
