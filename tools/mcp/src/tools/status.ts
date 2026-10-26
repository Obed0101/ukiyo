import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { dotnetCommand, listGames, runProcess, type GameInfo } from "../engine.ts";
import { declaredProfile, sceneRegistry, sharedSourceLines } from "../game-source.ts";
import { checkImports, findSpecs } from "../importer.ts";
import { text, type Tool } from "../protocol.ts";
import { liveSessions } from "../bridge.ts";

function sourceFacts(root: string, game: GameInfo) {
  const lines = sharedSourceLines(root, game.dir);
  return { profile: declaredProfile(lines), scenes: sceneRegistry(lines) };
}

const status: Tool = {
  name: "ukiyo_status",
  title: "Engine status",
  description:
    "Orientation for an agent: engine version, games and their hosts (headless/desktop/browser), installed toolchain (dotnet, native libs, three), skills and live dev-bridge sessions. Call it first. " +
    "Per game, read from its Shared/ source without building: the render profile it declares (IGame.Profile; G0Unlit when not overridden, G1Lit for lit 3D), whether it uses a SceneRegistry (snapshots then carry \"scene\"), " +
    "and its import specs (*.import.json) with an importer --check of each when the importer build is already current; otherwise it says the check needs a build (ukiyo_import check) instead of guessing.",
  inputSchema: { type: "object", properties: {}, additionalProperties: false },
  async run(_args, { root, signal }) {
    const props = readFileSync(join(root, "Directory.Build.props"), "utf8");
    const version = props.match(/<Version>([^<]+)<\/Version>/)?.[1] ?? "unknown";
    const { command, env } = dotnetCommand();
    const dotnet = await runProcess(command, ["--version"], { cwd: root, env, timeoutMs: 15_000, signal }).catch(() => null);
    const found = listGames(root);
    const specsByGame = new Map(found.map((g) => [g.name, findSpecs(g.dir)] as const).filter(([, specs]) => specs.length > 0));
    const importChecks = await checkImports(root, specsByGame, signal);
    const games = found.map((g) => ({
      name: g.name,
      dir: relative(root, g.dir),
      hosts: Object.keys(g.hosts),
      ...sourceFacts(root, g),
      imports: { specs: (specsByGame.get(g.name) ?? []).map((s) => relative(root, s)), ...(importChecks.has(g.name) ? { check: importChecks.get(g.name) } : {}) },
    }));
    const skillsDir = join(root, "skills");
    // Sorted: readdir order is the file system's (alphabetical on APFS, hash order on ext4), and status must not vary by OS.
    const skills = existsSync(skillsDir) ? readdirSync(skillsDir).filter((d) => existsSync(join(skillsDir, d, "SKILL.md"))).sort() : [];
    const result = {
      engine: { version, root },
      toolchain: {
        dotnet: dotnet && dotnet.code === 0 ? dotnet.stdout.trim() : "not found (install the .NET SDK pinned in global.json)",
        nativeLibraries: existsSync(join(root, "native/osx-arm64/lib/libwgpu_native.dylib")) ? "present" : "missing: run scripts/fetch-native.sh for desktop hosts",
        three: existsSync(join(root, "web/three-adapter/node_modules/three")) ? "present" : "missing: (cd web/three-adapter && bun install) for browser hosts",
      },
      games,
      notes: [
        "profile and scenes come from each game's source, not from a run: the headless [ukiyo] renderer line shows the renderer's profile, not the game's.",
      ],
      skills,
      live: liveSessions().map(({ pid, game, target, port }) => ({ pid, game, target, port })),
    };
    return { content: [text(JSON.stringify(result, null, 2))], structuredContent: result };
  },
};

export default status;
