import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { findRepoRoot } from "../src/engine.ts";
import { declaredProfile, sceneRegistry, sharedSourceLines } from "../src/game-source.ts";
import { buildImporter } from "../src/importer.ts";
import status from "../src/tools/status.ts";

const repo = findRepoRoot(import.meta.dir);

describe("game facts from shared source", () => {
  let root: string;
  const game = (name: string, files: Record<string, string>) => {
    for (const [file, body] of Object.entries(files)) {
      const path = join(root, "samples", name, "Shared", file);
      mkdirSync(join(path, ".."), { recursive: true });
      writeFileSync(path, body);
    }
    return sharedSourceLines(root, join(root, "samples", name));
  };
  beforeAll(() => { root = mkdtempSync(join(tmpdir(), "ukiyo-source-")); });
  afterAll(() => rmSync(root, { recursive: true, force: true }));

  test("a Profile override names its profile and where it is declared", () => {
    const lines = game("Lit", { "Game.cs": "class G : IGame\n{\n    public RenderProfile Profile => RenderProfile.G1Lit;\n}\n" });
    expect(declaredProfile(lines)).toEqual({ profile: "G1Lit", basis: "source", declaredAt: "samples/Lit/Shared/Game.cs:3" });
  });

  test("an explicit interface getter on the following lines is read too", () => {
    const lines = game("Getter", { "Game.cs": "RenderProfile IGame.Profile\n{\n    get { return RenderProfile.G1Lit; }\n}\n" });
    expect(declaredProfile(lines).profile).toBe("G1Lit");
  });

  test("no override is the IGame default; a commented-out override does not count", () => {
    const lines = game("Plain", { "Game.cs": "// public RenderProfile Profile => RenderProfile.G1Lit;\n/// RenderProfile Profile => RenderProfile.G1Lit\nclass G {}\n" });
    expect(declaredProfile(lines)).toEqual({ profile: "G0Unlit", basis: "source", declaredAt: null, note: "no Profile override in Shared/: the IGame default" });
  });

  test("a computed profile is unknown, not guessed", () => {
    const lines = game("Computed", { "Game.cs": "public RenderProfile Profile => _settings.Lit ? Lit : Unlit;\n" });
    expect(declaredProfile(lines)).toMatchObject({ profile: "unknown", declaredAt: "samples/Computed/Shared/Game.cs:1" });
  });

  test("scene registry: new SceneRegistry<…> wins over a field declaration; absent means false", () => {
    const lines = game("Scenes", { "Game.cs": "private SceneRegistry<Mote> _scenes = null!;\nvoid Init() { _scenes = new SceneRegistry<Mote>(context); }\n" });
    expect(sceneRegistry(lines)).toEqual({ registry: true, at: "samples/Scenes/Shared/Game.cs:2" });
    const targetTyped = game("TargetTyped", { "Game.cs": "private SceneRegistry<Mote> _scenes = null!;\nvoid Init() { _scenes = new(context); }\n" });
    expect(sceneRegistry(targetTyped)).toEqual({ registry: true, at: "samples/TargetTyped/Shared/Game.cs:1" });
    expect(sceneRegistry(game("NoScenes", { "Game.cs": "class G {}\n" }))).toEqual({ registry: false, at: null });
  });
});

describe("ukiyo_status on this repository", () => {
  test("reports LitCube as G1Lit, SceneHop with a registry, and checks TexturedQuad's import spec with a current importer build", async () => {
    const built = await buildImporter(repo, { timeoutMs: 170_000 });
    expect(built.ok).toBe(true);
    const result = await status.run({}, { root: repo, log: () => {}, signal: new AbortController().signal });
    const games = (result.structuredContent?.games ?? []) as Record<string, any>[];
    const byName = Object.fromEntries(games.map((g) => [g.name, g]));

    expect(byName.LitCube.profile).toMatchObject({ profile: "G1Lit", basis: "source", declaredAt: expect.stringMatching(/^samples\/LitCube\/Shared\/.+\.cs:\d+$/) });
    expect(byName.RotatingCube.profile).toMatchObject({ profile: "G0Unlit", declaredAt: null });
    expect(byName.SceneHop.scenes).toMatchObject({ registry: true });
    expect(byName.LitCube.scenes).toEqual({ registry: false, at: null });
    expect(byName.LitCube.imports).toEqual({ specs: [] });
    expect(byName.TexturedQuad.imports.specs).toEqual(["samples/TexturedQuad/art/quad.import.json"]);
    expect(byName.TexturedQuad.imports.check).toMatchObject({
      ran: true,
      results: [{ spec: "samples/TexturedQuad/art/quad.import.json", status: "up-to-date", outputs: [{ output: "samples/TexturedQuad/Shared/gen/QuadArt.g.cs", contentHash: expect.stringMatching(/^[0-9a-f]{64}$/) }] }],
    });
  }, 180_000);
});
