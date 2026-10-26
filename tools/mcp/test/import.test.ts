import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { findRepoRoot, listGames } from "../src/engine.ts";
import {
  assertSpecInsideRoot,
  findSpecs,
  importerBuildState,
  parseImportError,
  parseImportStdout,
  parseStale,
  projectClosure,
  resolveSpecTargets,
  specResult,
} from "../src/importer.ts";
import { ToolInputError, type ToolResult } from "../src/protocol.ts";
import importTool from "../src/tools/import.ts";

const repo = findRepoRoot(import.meta.dir);
const context = { root: repo, log: () => {}, signal: new AbortController().signal };
const call = (args: Record<string, unknown>) => importTool.run({ timeoutSeconds: 300, ...args }, context) as Promise<ToolResult & { structuredContent: Record<string, any> }>;

function write(path: string, body: string) {
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, body);
}

describe("spec discovery and path validation (fixture repository)", () => {
  let root: string;
  beforeAll(() => {
    root = mkdtempSync(join(tmpdir(), "ukiyo-import-root-"));
    write(join(root, "samples/Fake/Shared/Game.cs"), "");
    write(join(root, "samples/Fake/art/b.import.json"), "{}");
    write(join(root, "samples/Fake/art/a.import.json"), "{}");
    write(join(root, "samples/Fake/art/meshes/ship.import.json"), "{}");
    write(join(root, "samples/Fake/art/bin/skipped.import.json"), "{}");
    write(join(root, "samples/Fake/art/obj/skipped.import.json"), "{}");
    write(join(root, "samples/Fake/art/node_modules/skipped.import.json"), "{}");
    write(join(root, "samples/Fake/art/notes.json"), "{}");
    write(join(root, "samples/Bare/Shared/Game.cs"), "");
  });
  afterAll(() => rmSync(root, { recursive: true, force: true }));

  test("finds nested *.import.json in ordinal order and skips bin, obj and node_modules", () => {
    expect(findSpecs(join(root, "samples/Fake")).map((p) => relative(root, p))).toEqual([
      "samples/Fake/art/a.import.json",
      "samples/Fake/art/b.import.json",
      "samples/Fake/art/meshes/ship.import.json",
    ]);
  });

  test("a game, a folder and a file resolve to one deduplicated spec list", () => {
    const specs = resolveSpecTargets(root, { game: "fake", specs: ["samples/Fake/art/meshes", join(root, "samples/Fake/art/a.import.json"), "samples/Fake/art/missing.import.json"] });
    expect(specs.map((p) => relative(root, p))).toEqual([
      "samples/Fake/art/a.import.json",
      "samples/Fake/art/b.import.json",
      "samples/Fake/art/meshes/ship.import.json",
      "samples/Fake/art/missing.import.json",
    ]);
  });

  test.each([
    [{}, "pass a game"],
    [{ specs: ["../outside.import.json"] }, "outside the ukiyo repository"],
    [{ specs: ["/etc"] }, "outside the ukiyo repository"],
    [{ game: "Bare" }, "Bare has no *.import.json"],
    [{ game: "Nope" }, 'no game "Nope"'],
    [{ specs: ["samples/Bare"] }, "no *.import.json under samples/Bare"],
  ])("refuses %j", (input, message) => {
    expect(() => resolveSpecTargets(root, input)).toThrow(ToolInputError);
    expect(() => resolveSpecTargets(root, input)).toThrow(message);
  });

  test("whenEmpty all sweeps every spec of every game; without it, or with nothing to sweep, it is refused", () => {
    expect(resolveSpecTargets(root, { whenEmpty: "all" }).map((p) => relative(root, p))).toEqual([
      "samples/Fake/art/a.import.json",
      "samples/Fake/art/b.import.json",
      "samples/Fake/art/meshes/ship.import.json",
    ]);
    const empty = mkdtempSync(join(tmpdir(), "ukiyo-import-empty-"));
    try {
      write(join(empty, "samples/Bare/Shared/Game.cs"), "");
      expect(() => resolveSpecTargets(empty, { whenEmpty: "all" })).toThrow("no *.import.json in any game");
    } finally {
      rmSync(empty, { recursive: true, force: true });
    }
  });

  test("a spec whose output or inputs leave the repository is refused before the importer runs", () => {
    const spec = join(root, "samples/Fake/art/escape.import.json");
    writeFileSync(spec, JSON.stringify({ output: "../../../../elsewhere/Art.g.cs", inputs: ["a.png"] }));
    expect(() => assertSpecInsideRoot(root, spec)).toThrow('"output" ../../../../elsewhere/Art.g.cs resolves outside');
    writeFileSync(spec, JSON.stringify({ output: "../Shared/gen/Art.g.cs", inputs: ["a.png", "/etc/passwd.png"] }));
    expect(() => assertSpecInsideRoot(root, spec)).toThrow('"inputs" /etc/passwd.png resolves outside');
    writeFileSync(spec, JSON.stringify({ output: "../Shared/gen/Art.g.cs", inputs: ["a.png"], kind: "anything else passes through" }));
    expect(() => assertSpecInsideRoot(root, spec)).not.toThrow();
    writeFileSync(spec, "not json: the importer reports it as InvalidSpec");
    expect(() => assertSpecInsideRoot(root, spec)).not.toThrow();
  });
});

describe("importer output and error mapping", () => {
  test("reads up-to-date and wrote lines with their short content hash, keeps other [import] lines", () => {
    const { outputs, info } = parseImportStdout([
      "[import] up to date samples/A/Shared/gen/AArt.g.cs (60x40, 5 frames, 1 animations, content b2320091ad58)",
      "[import] wrote samples/A/Shared/gen/Ship.g.cs (1 mesh, 24 vertices, content 0123456789ab)",
      "[import] note: something new",
      "unrelated",
    ].join("\n"));
    expect(outputs).toEqual([
      { state: "up-to-date", output: "samples/A/Shared/gen/AArt.g.cs", summary: "60x40, 5 frames, 1 animations, content b2320091ad58", contentHash: "b2320091ad58" },
      { state: "wrote", output: "samples/A/Shared/gen/Ship.g.cs", summary: "1 mesh, 24 vertices, content 0123456789ab", contentHash: "0123456789ab" },
    ]);
    expect(info).toEqual(["[import] note: something new"]);
  });

  test("typed errors keep their code; Stale is split into output, reason and regenerate command", () => {
    expect(parseImportError("[IMPORT:InvalidPng]: art/x.png: interlaced PNG\n")).toEqual({ code: "InvalidPng", message: "[IMPORT:InvalidPng]: art/x.png: interlaced PNG" });
    expect(parseImportError("Unhandled exception")).toBeNull();
    expect(parseStale("[IMPORT:Stale]: samples/A/Shared/gen/AArt.g.cs is out of date; run: dotnet run --project tools/Ukiyo.Import -- samples/A/art/a.import.json")).toEqual({
      output: "samples/A/Shared/gen/AArt.g.cs",
      reason: "out of date",
      regenerate: "dotnet run --project tools/Ukiyo.Import -- samples/A/art/a.import.json",
    });
  });

  test.each([
    [{ code: 0, stdout: "[import] wrote o.g.cs (x, content aaaaaaaaaaaa)", stderr: "", timedOut: false }, "wrote", undefined],
    [{ code: 0, stdout: "[import] up to date o.g.cs (x, content aaaaaaaaaaaa)", stderr: "", timedOut: false }, "up-to-date", undefined],
    [{ code: 0, stdout: "", stderr: "", timedOut: false }, "ok", undefined],
    [{ code: 1, stdout: "", stderr: "[IMPORT:Stale]: o.g.cs is missing; run: dotnet run --project tools/Ukiyo.Import -- s.import.json", timedOut: false }, "stale", "Stale"],
    [{ code: 1, stdout: "", stderr: "[IMPORT:DuplicateName]: two frames named hero", timedOut: false }, "error", "DuplicateName"],
    [{ code: 2, stdout: "", stderr: "usage: ukiyo-import [--check] <spec.import.json | folder>...", timedOut: false }, "error", "Usage"],
    [{ code: 134, stdout: "", stderr: "Unhandled exception. System.NullReferenceException", timedOut: false }, "error", "Crash"],
    [{ code: 143, stdout: "", stderr: "", timedOut: true }, "error", "Timeout"],
  ])("exit %j → %s", (run, status, code) => {
    const result = specResult("s.import.json", run);
    expect(result.status).toBe(status as typeof result.status);
    expect(result.error?.code).toBe(code as string | undefined);
  });
});

describe("importer build freshness (fixture projects)", () => {
  let root: string;
  beforeAll(() => {
    root = mkdtempSync(join(tmpdir(), "ukiyo-importer-build-"));
    write(join(root, "tools/Ukiyo.Import/Ukiyo.Import.csproj"), '<Project><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>');
    write(join(root, "tools/Ukiyo.Import/Program.cs"), "");
    write(join(root, "src/Lib/Lib.csproj"), "<Project />");
    write(join(root, "src/Lib/Png.cs"), "");
    write(join(root, "tools/Ukiyo.Import/bin/Debug/ukiyo-import.dll"), "");
    write(join(root, "Directory.Build.props"), "<Project />");
    const old = new Date("2026-01-01T00:00:00Z");
    for (const file of ["tools/Ukiyo.Import/Ukiyo.Import.csproj", "tools/Ukiyo.Import/Program.cs", "src/Lib/Lib.csproj", "src/Lib/Png.cs", "Directory.Build.props"]) utimesSync(join(root, file), old, old);
  });
  afterAll(() => rmSync(root, { recursive: true, force: true }));

  test("follows ProjectReference transitively", () => {
    expect(projectClosure(join(root, "tools/Ukiyo.Import/Ukiyo.Import.csproj")).map((p) => relative(root, p))).toEqual(["tools/Ukiyo.Import/Ukiyo.Import.csproj", "src/Lib/Lib.csproj"]);
  });

  test("a build newer than every source is usable; a newer referenced source or a missing build is not", () => {
    const dll = join(root, "tools/Ukiyo.Import/bin/Debug/ukiyo-import.dll");
    expect(importerBuildState(root, dll)).toEqual({ built: true, dll });
    expect(importerBuildState(root, null)).toEqual({ built: false, reason: "the importer is not built" });
    const future = new Date(Date.now() + 60_000);
    utimesSync(join(root, "src/Lib/Png.cs"), future, future);
    expect(importerBuildState(root, dll)).toEqual({ built: false, reason: "src/Lib/Png.cs is newer than the importer build" });
  });
});

describe("ukiyo_import against the real importer", () => {
  let scratch: string;
  beforeAll(() => {
    mkdirSync(join(repo, "artifacts"), { recursive: true });
    scratch = mkdtempSync(join(repo, "artifacts", "mcp-import-test-"));
  });
  afterAll(() => rmSync(scratch, { recursive: true, force: true }));

  test("check on samples/TexturedQuad reports its generated atlas up to date with the full content hash", async () => {
    const result = await call({ action: "check", game: "TexturedQuad" });
    expect(result.isError).toBe(false);
    const expectedHash = readFileSync(join(repo, "samples/TexturedQuad/Shared/gen/QuadArt.g.cs"), "utf8").match(/ContentHash = "([0-9a-f]{64})"/)?.[1];
    expect(result.structuredContent.specs).toEqual([
      expect.objectContaining({
        spec: "samples/TexturedQuad/art/quad.import.json",
        status: "up-to-date",
        outputs: [expect.objectContaining({ state: "up-to-date", output: "samples/TexturedQuad/Shared/gen/QuadArt.g.cs", contentHash: expectedHash })],
      }),
    ]);
  }, 180_000);

  test("check with no target covers every spec in the repository; run with no target is refused", async () => {
    const all = await call({ action: "check" });
    expect(all.isError).toBe(false);
    const specs = (all.structuredContent.specs as { spec: string }[]).map((s) => s.spec);
    expect(specs).toContain("samples/TexturedQuad/art/quad.import.json");
    expect(specs.length).toBe(listGames(repo).flatMap((g) => findSpecs(g.dir)).length);
    await expect(call({ action: "run" })).rejects.toThrow("pass a game");
  }, 180_000);

  test("check reports a missing output as stale, run writes it, and check then passes", async () => {
    const art = relative(scratch, join(repo, "samples/TexturedQuad/art"));
    const spec = join(scratch, "copy.import.json");
    writeFileSync(spec, JSON.stringify({ class: "CopyArt", namespace: "Ukiyo.Tests.Copy", output: "gen/CopyArt.g.cs", inputs: [`${art}/wisp_float.png`, `${art}/glass.png`], extrude: 1 }));
    const specPath = relative(repo, spec);

    const stale = await call({ action: "check", specs: [specPath] });
    expect(stale.isError).toBe(true);
    expect(stale.content[0]).toMatchObject({ text: expect.stringContaining("[IMPORT:Stale]") });
    expect(stale.structuredContent.stale).toEqual([expect.objectContaining({ spec: specPath, output: `${relative(repo, scratch)}/gen/CopyArt.g.cs`, reason: "missing" })]);

    const wrote = await call({ action: "run", specs: [specPath] });
    expect(wrote.isError).toBe(false);
    expect(wrote.structuredContent.wrote).toEqual([`${relative(repo, scratch)}/gen/CopyArt.g.cs`]);

    const fresh = await call({ action: "check", specs: [relative(repo, scratch)] });
    expect(fresh.isError).toBe(false);
    // Same pixels, frames and extrusion as TexturedQuad's atlas: the content hash ignores class and namespace.
    const quadHash = readFileSync(join(repo, "samples/TexturedQuad/Shared/gen/QuadArt.g.cs"), "utf8").match(/ContentHash = "([0-9a-f]{64})"/)?.[1];
    expect(fresh.structuredContent.specs[0].outputs[0].contentHash).toBe(quadHash);
  }, 180_000);

  test("an importer error comes back as an error result with its typed code, and other specs still run", async () => {
    const result = await call({ action: "check", specs: ["samples/TexturedQuad/art/missing.import.json", "samples/TexturedQuad/art/quad.import.json"] });
    expect(result.isError).toBe(true);
    expect(result.structuredContent.errors).toEqual([{ spec: "samples/TexturedQuad/art/missing.import.json", code: "MissingFile", message: expect.stringContaining("[IMPORT:MissingFile]") }]);
    expect(result.structuredContent.specs[1].status).toBe("up-to-date");
  }, 180_000);
});
