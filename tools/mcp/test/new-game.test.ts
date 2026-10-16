import { afterEach, describe, expect, test } from "bun:test";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { listGames } from "../src/engine.ts";
import { ToolInputError } from "../src/protocol.ts";
import newGame, { TEMPLATE_DIR, templateFiles } from "../src/tools/new-game.ts";

const repoTemplate = new URL(`../../../${TEMPLATE_DIR}`, import.meta.url).pathname;
const roots: string[] = [];

/** A throwaway repository: the real templates/game (without build output) and an empty solution. */
function fakeRepo(): string {
  const root = mkdtempSync(join(tmpdir(), "ukiyo-new-game-"));
  roots.push(root);
  for (const file of templateFiles(repoTemplate)) {
    mkdirSync(dirname(join(root, TEMPLATE_DIR, file)), { recursive: true });
    cpSync(join(repoTemplate, file), join(root, TEMPLATE_DIR, file));
  }
  writeFileSync(join(root, "Ukiyo.slnx"), "<Solution>\n</Solution>\n");
  writeFileSync(join(root, "global.json"), "{}\n");
  return root;
}

const context = (root: string) => ({ root, log: () => {}, signal: new AbortController().signal });
const run = (root: string, args: Record<string, unknown>) =>
  newGame.run({ template: "2d", hosts: ["Headless", "Desktop", "Browser", "Export.Desktop"], ...args }, context(root));
const read = (root: string, path: string) => readFileSync(join(root, path), "utf8");

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

describe("ukiyo_new_game", () => {
  test("copies the template layout with the game's names and lists every host under Hosts/", async () => {
    const root = fakeRepo();
    const result = await run(root, { name: "SkyLantern" });
    expect(result.isError).toBeUndefined();

    for (const path of ["Shared/Scenes/.gitkeep", "Shared/Entities/.gitkeep", "Shared/Systems/.gitkeep", "Shared/Lib/.gitkeep", "Shared/gen/.gitkeep",
      "Shared/assets/.gitkeep", "Platform/Desktop/.gitkeep", "Platform/Browser/.gitkeep", "Www/index.html", "Www/main.js"]) {
      expect(existsSync(join(root, "games/SkyLantern", path))).toBe(true);
    }
    const game = "games/SkyLantern";
    expect(read(root, `${game}/Shared/Game.cs`)).toContain("public sealed class SkyLanternGame : IGame");
    expect(read(root, `${game}/Shared/Program.cs`)).toContain("new SkyLanternGame()");
    const headless = read(root, `${game}/Hosts/Headless/SkyLantern.Headless.csproj`);
    expect(headless).toContain("<AssemblyName>sky-lantern-headless</AssemblyName>");
    expect(headless).toContain('<UkiyoSharedSource Include="$(UkiyoGameDir)Shared/**/*.cs" />');
    expect(headless).toContain('<ProjectReference Include="$(UkiyoRoot)src/Ukiyo.Physics/Ukiyo.Physics.csproj" />');
    expect(read(root, `${game}/Www/index.html`)).toContain("ukiyo · SkyLantern · web");
    for (const file of Object.values(result.structuredContent!.files as string[])) {
      expect(read(root, file)).not.toMatch(/MyGame|my-game/);
    }

    const solution = read(root, "Ukiyo.slnx");
    for (const host of ["Headless", "Desktop", "Browser", "Export.Desktop"]) {
      expect(solution).toContain(`<Project Path="${game}/Hosts/${host}/SkyLantern.${host}.csproj" />`);
    }
    const listed = listGames(root).find((g) => g.name === "SkyLantern")!;
    expect(Object.keys(listed.hosts).sort()).toEqual(["Browser", "Desktop", "Export.Desktop", "Headless"]);
    expect(listed.hosts.Headless).toBe(join(root, game, "Hosts/Headless/SkyLantern.Headless.csproj"));
  });

  test("3d adds no optional systems and only the requested hosts; Www/ comes only with Browser", async () => {
    const root = fakeRepo();
    await run(root, { name: "Orbit", template: "3d", hosts: ["Headless"] });
    const headless = read(root, "games/Orbit/Hosts/Headless/Orbit.Headless.csproj");
    expect(headless).not.toContain("Ukiyo.Physics");
    expect(existsSync(join(root, "games/Orbit/Hosts/Desktop"))).toBe(false);
    expect(existsSync(join(root, "games/Orbit/Www"))).toBe(false);
    expect(read(root, "games/Orbit/Shared/Game.cs")).toContain("public sealed class OrbitGame : IGame");
  });

  test("never overwrites an existing game and fails loudly without the template", async () => {
    const root = fakeRepo();
    await run(root, { name: "Twice" });
    await expect(run(root, { name: "Twice" })).rejects.toThrow(ToolInputError);
    rmSync(join(root, TEMPLATE_DIR), { recursive: true });
    await expect(run(root, { name: "Other" })).rejects.toThrow("[NEW_GAME]: templates/game not found");
  });
});
