import { findGame, listGames } from "../engine.ts";
import { checkGame, type GameConformance } from "../conformance.ts";
import { buildImporter } from "../importer.ts";
import { text, type Tool } from "../protocol.ts";

const describe = (g: GameConformance) =>
  [`${g.ok ? "ok" : "FAIL"} ${g.game} (${g.layout} layout) ${Object.entries(g.checks).map(([k, v]) => `${k}=${v}`).join(" ")}`, ...g.findings.map((f) => `  ${f.level} [CHECK:${f.code}] ${f.path}: ${f.message}`)].join("\n");

const check: Tool = {
  name: "ukiyo_check",
  title: "Check game conformance",
  description:
    "Verifies that a game follows the layout contract (docs/game-layout.md, the rules GameLayoutTests enforces) without building or running it: required Shared/Platform/Hosts/Www entries, one correctly wired csproj per host, no #if or host interop in Shared/, Program.cs as the single GameApplication.RunAsync entry point, " +
    "imported assets not stale (the importer's own --check; builds the importer unless build=false), and for samples/ a golden snapshot. Without game, checks every game; games in the older <game>/<Host>/ layout are reported as legacy, not failed. " +
    "Findings carry a typed code ([CHECK:MissingEntry], HostLayout, HostProject, PlatformLeak, ProgramShape, StaleAssets, ImportError, AssetsUnchecked, MissingGolden; info: LegacyLayout, MissingHost) and a level; any error finding makes the result an error. " +
    "It proves the layout and generated code, not behavior: use ukiyo_run / ukiyo_test for that.",
  inputSchema: {
    type: "object",
    additionalProperties: false,
    properties: {
      game: { type: "string", description: "Game folder name under games/ or samples/; omitted, every game." },
      build: { type: "boolean", default: true, description: "Build the importer to check assets. false: never build; assets are checked only when a current importer build exists, otherwise reported unchecked." },
    },
  },
  cli: { positional: ["game"] },
  async run(args, { root, log, signal }) {
    const games = args.game === undefined ? listGames(root) : [findGame(root, args.game as string)];
    let built: Promise<{ ok: boolean; message?: string }> | null = null;
    const build = args.build === false ? null : () => (built ??= buildImporter(root, { signal, timeoutMs: 300_000 }).then((r) => (r.ok ? { ok: true } : { ok: false, message: r.message })));
    log("info", "check.start", { games: games.length });
    const results: GameConformance[] = [];
    for (const game of games) results.push(await checkGame(root, game, { signal, build }));
    const failed = results.filter((g) => !g.ok);
    const outcome = {
      ok: failed.length === 0,
      checked: results.filter((g) => g.layout === "template").length,
      legacy: results.filter((g) => g.layout === "legacy").map((g) => g.game),
      failed: failed.map((g) => g.game),
      games: results,
    };
    log("info", "check.done", { checked: outcome.checked, failed: failed.length });
    const headline = outcome.ok
      ? `check: ${outcome.checked} game(s) conform${outcome.legacy.length ? `, ${outcome.legacy.length} in the older layout not checked` : ""}`
      : `check: ${failed.length} of ${results.length} game(s) do not conform: ${outcome.failed.join(", ")}`;
    return { isError: !outcome.ok, content: [text(`${headline}\n${results.map(describe).join("\n")}`)], structuredContent: outcome };
  },
};

export default check;
