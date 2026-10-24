import { afterAll, beforeAll, beforeEach, describe, expect, test } from "bun:test";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { checkGame } from "../src/conformance.ts";
import { findGame, findRepoRoot, listGames } from "../src/engine.ts";
import { validate } from "../src/protocol.ts";
import check from "../src/tools/check.ts";

const repo = findRepoRoot(import.meta.dir);
const signal = new AbortController().signal;
const context = { root: repo, log: () => {}, signal };

describe("conformance of a game copied into a scratch repository", () => {
  let root: string;
  let dir: string;
  const run = () => checkGame(root, findGame(root, "RotatingCube"), { signal });
  const codes = async () => (await run()).findings.map((f) => `${f.level}:${f.code}`);
  const edit = (file: string, change: (text: string) => string) => writeFileSync(join(dir, file), change(readFileSync(join(dir, file), "utf8")));

  beforeAll(() => { root = mkdtempSync(join(tmpdir(), "ukiyo-check-")); });
  afterAll(() => rmSync(root, { recursive: true, force: true }));
  beforeEach(() => {
    dir = join(root, "games", "RotatingCube");
    rmSync(join(root, "games"), { recursive: true, force: true });
    mkdirSync(join(root, "games"), { recursive: true });
    cpSync(join(repo, "samples/RotatingCube"), dir, { recursive: true, filter: (src) => !/\/(bin|obj|lib)$/.test(src) });
  });

  test("a copy of the template-shaped sample conforms; assets and golden are skipped for a game with no specs under games/", async () => {
    const result = await run();
    expect(result.findings).toEqual([]);
    expect(result).toMatchObject({ ok: true, layout: "template", checks: { layout: "pass", hosts: "pass", shared: "pass", program: "pass", assets: "skipped", golden: "skipped" } });
  });

  test("a missing folder is MissingEntry; Www is required only with a Browser host", async () => {
    rmSync(join(dir, "Shared/Systems"), { recursive: true });
    const result = await run();
    expect(result.findings).toEqual([expect.objectContaining({ code: "MissingEntry", level: "error", path: "games/RotatingCube/Shared/Systems" })]);
    expect(result.ok).toBe(false);
    rmSync(join(dir, "Www"), { recursive: true });
    expect((await run()).findings.filter((f) => f.path.includes("Www")).length).toBe(2);
    rmSync(join(dir, "Hosts/Browser"), { recursive: true });
    const noBrowser = await run();
    expect(noBrowser.findings.filter((f) => f.path.includes("Www"))).toEqual([]);
    expect(noBrowser.findings.find((f) => f.code === "MissingHost")).toMatchObject({ level: "info" });
  });

  test("a host folder with an extra file, or a csproj that does not link Shared or links the wrong platform, is refused", async () => {
    writeFileSync(join(dir, "Hosts/Headless/notes.txt"), "x");
    expect(await codes()).toEqual(["error:HostLayout"]);
    rmSync(join(dir, "Hosts/Headless/notes.txt"));
    edit("Hosts/Desktop/RotatingCube.Desktop.csproj", (t) => t.replace("Platform/Desktop/**/*.cs", "Platform/Browser/**/*.cs"));
    const result = await run();
    expect(result.findings.map((f) => f.message)).toEqual(["must compile Platform/Desktop", "must not compile Platform/Browser"]);
    edit("Hosts/Headless/RotatingCube.Headless.csproj", (t) => t.replace('Include="$(UkiyoGameDir)Shared/**/*.cs"', 'Include="../../Shared/*.cs"'));
    expect((await codes()).filter((c) => c === "error:HostProject").length).toBe(3);
  });

  test("#if and host interop in Shared/ are PlatformLeak with file:line; a commented mention is fine", async () => {
    writeFileSync(join(dir, "Shared/Lib/Leak.cs"), "// #if DEBUG and DllImport in a comment are fine\nclass A\n{\n#if DEBUG\n    [System.Runtime.InteropServices.LibraryImport(\"x\")] static partial void F();\n#endif\n}\n");
    const result = await run();
    expect(result.findings.map((f) => [f.code, f.path])).toEqual([
      ["PlatformLeak", "games/RotatingCube/Shared/Lib/Leak.cs:4"],
      ["PlatformLeak", "games/RotatingCube/Shared/Lib/Leak.cs:5"],
    ]);
  });

  test("a spec with no importer build to check it is AssetsUnchecked (a warning), never a silent pass", async () => {
    mkdirSync(join(dir, "art"), { recursive: true });
    writeFileSync(join(dir, "art/x.import.json"), "{}");
    const result = await run();
    expect(result.checks.assets).toBe("unchecked");
    expect(result.findings).toEqual([expect.objectContaining({ code: "AssetsUnchecked", level: "warn" })]);
    expect(result.ok).toBe(true);
  });

  test("Program.cs must call GameApplication.RunAsync with PlatformBootstrap.Create(args) and have no directives", async () => {
    edit("Shared/Program.cs", (t) => t.replace("PlatformBootstrap.Create(args)", "new MyHost()"));
    expect(await codes()).toEqual(["error:ProgramShape"]);
    edit("Shared/Program.cs", (t) => `${t.replace("new MyHost()", "PlatformBootstrap.Create(args)")}\n#if DEBUG\n#endif\n`);
    expect((await run()).findings.map((f) => f.code)).toEqual(["PlatformLeak", "ProgramShape"]);
  });
});

describe("ukiyo_check on this repository", () => {
  test("RotatingCube conforms with golden present; the older-layout samples are legacy, not failures", async () => {
    const result = await check.run({ game: "RotatingCube", build: false }, context);
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toMatchObject({ ok: true, checked: 1, games: [{ game: "RotatingCube", checks: { golden: "pass", assets: "skipped" } }] });
    const legacy = await check.run({ game: "LitCube", build: false }, context);
    expect(legacy.isError).toBe(false);
    expect(legacy.structuredContent).toMatchObject({ legacy: ["LitCube"], games: [{ layout: "legacy", findings: [{ code: "LegacyLayout", level: "info" }] }] });
  });

  test("every game in the repository passes, and the schema refuses unknown input", async () => {
    const all = await check.run({ build: false }, context);
    expect(all.isError).toBe(false);
    expect((all.structuredContent!.games as unknown[]).length).toBe(listGames(repo).length);
    expect(() => validate(check.inputSchema, { game: "RotatingCube", fix: true })).toThrow("not a known property");
    await expect(check.run({ game: "Nope" }, context)).rejects.toThrow('no game "Nope"');
  });
});
