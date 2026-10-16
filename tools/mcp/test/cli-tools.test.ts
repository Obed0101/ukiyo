import { afterAll, describe, expect, test } from "bun:test";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { CliUsageError, commandName, optionName, parseToolArgs, toolHelp } from "../src/cli-args.ts";
import { copyOutputs, exitCode, help, runTool, toolCommands, ToolFailure } from "../src/cli.ts";
import { text, ToolInputError, type Tool, type ToolContext } from "../src/protocol.ts";
import capture from "../src/tools/capture.ts";
import importTool from "../src/tools/import.ts";
import newGame, { TEMPLATE_DIR, templateFiles } from "../src/tools/new-game.ts";
import skills from "../src/tools/skills.ts";
import status from "../src/tools/status.ts";
import testTool from "../src/tools/test.ts";

const scratch = realpathSync(mkdtempSync(join(tmpdir(), "ukiyo-cli-tools-")));
afterAll(() => rmSync(scratch, { recursive: true, force: true }));

const file = (name: string, value: unknown) => {
  const path = join(scratch, name);
  writeFileSync(path, JSON.stringify(value));
  return path;
};

describe("argv → tool input, per command", () => {
  test("status takes nothing", () => {
    expect(parseToolArgs(status, [])).toEqual({ input: {} });
    expect(() => parseToolArgs(status, ["--verbose"])).toThrow("status: unknown option --verbose; options: none");
    expect(() => parseToolArgs(status, ["extra"])).toThrow("unexpected argument");
  });

  test("new-game: the name is positional, --template and --hosts are the schema's own options", () => {
    expect(parseToolArgs(newGame, ["SkyLantern", "--template", "3d", "--hosts", "Headless,Desktop"]).input)
      .toEqual({ name: "SkyLantern", template: "3d", hosts: ["Headless", "Desktop"] });
    expect(parseToolArgs(newGame, ["SkyLantern"]).input).toEqual({ name: "SkyLantern" });
    expect(() => parseToolArgs(newGame, [])).toThrow("new-game needs <name>; usage: ukiyo new-game <name> [options]");
    expect(() => parseToolArgs(newGame, ["SkyLantern", "--kind", "2d"])).toThrow("unknown option --kind; options: --template --hosts");
    expect(() => parseToolArgs(newGame, ["SkyLantern", "--template"])).toThrow("--template needs a value");
  });

  test("capture: lists are comma-separated, array-of-object options read a JSON file, --out is CLI-only", () => {
    const patches = [{ id: "p1", tick: 10, ops: [{ op: "dispose", id: "crate1" }] }];
    const input = [{ tick: 5, key: "Space", down: true }];
    const parsed = parseToolArgs(capture, ["LitCube", "--checkpoints", "0,60", "--size", "320x180", "--patches", file("patches.json", patches),
      "--input", file("input.json", input), "--audio", "--timeout-seconds", "120", "--out", "frames"]);
    expect(parsed).toEqual({ input: { game: "LitCube", checkpoints: [0, 60], size: "320x180", patches, input, audio: true, timeoutSeconds: 120 }, out: "frames" });
    expect(parseToolArgs(capture, ["LitCube", "--audio", "false"]).input).toEqual({ game: "LitCube", audio: false });
    expect(parseToolArgs(capture, ["LitCube", "--patch-log", "logs/a.json"]).input).toEqual({ game: "LitCube", patchLog: "logs/a.json" });
    expect(() => parseToolArgs(capture, ["LitCube", "--checkpoints", "0,x"])).toThrow("--checkpoints expects a number");
    expect(() => parseToolArgs(capture, ["LitCube", "--patches", join(scratch, "missing.json")])).toThrow("--patches FILE must be a readable JSON file");
    expect(() => parseToolArgs(capture, ["LitCube", "--size", "1x1", "--size", "2x2"])).toThrow("duplicate option --size");
  });

  test("test: --suite, --filter, --timeout-seconds", () => {
    expect(parseToolArgs(testTool, ["--filter", "FullyQualifiedName~Physics", "--suite", "all", "--timeout-seconds", "60"]).input)
      .toEqual({ filter: "FullyQualifiedName~Physics", suite: "all", timeoutSeconds: 60 });
    expect(() => parseToolArgs(testTool, ["--out", "x"])).toThrow("unknown option --out");
  });

  test("import: run by default, --check switches the action, positional paths are the specs", () => {
    expect(parseToolArgs(importTool, []).input).toEqual({ action: "run" });
    expect(parseToolArgs(importTool, ["--check", "samples/TexturedQuad", "art/a.import.json"]).input)
      .toEqual({ action: "check", specs: ["samples/TexturedQuad", "art/a.import.json"] });
    expect(parseToolArgs(importTool, ["--game", "TexturedQuad", "--check"]).input).toEqual({ action: "check", game: "TexturedQuad" });
    expect(() => parseToolArgs(importTool, ["--action", "check"])).toThrow("unknown option --action; options: --check --game --timeout-seconds");
  });

  test("skills: the action is positional", () => {
    expect(parseToolArgs(skills, ["list"]).input).toEqual({ action: "list" });
    expect(parseToolArgs(skills, ["install", "--preset", "engine", "--target", "claude", "--skills", "ukiyo-core,ukiyo-live", "--project-dir", "games/X"]).input)
      .toEqual({ action: "install", preset: "engine", target: "claude", skills: ["ukiyo-core", "ukiyo-live"], projectDir: "games/X" });
    expect(() => parseToolArgs(skills, [])).toThrow("skills needs <action>");
    expect(() => parseToolArgs(skills, ["list", "more"])).toThrow("unexpected argument \"more\"");
  });
});

describe("one generic mapping for every tool module", () => {
  const thing: Tool = {
    name: "ukiyo_thing_maker",
    title: "Make a thing",
    description: "test tool",
    inputSchema: {
      type: "object",
      required: ["name", "count"],
      additionalProperties: false,
      properties: {
        name: { type: "string" },
        count: { type: "integer", minimum: 1, maximum: 9 },
        dryRun: { type: "boolean", default: false },
        layout: { type: "object" },
      },
    },
    run: async (args) => ({ content: [text("made")], structuredContent: { made: args } }),
  };

  test("names and positional arguments come from the schema", () => {
    expect(commandName(thing)).toBe("thing-maker");
    expect(optionName("timeoutSeconds")).toBe("--timeout-seconds");
    expect(parseToolArgs(thing, ["lamp", "3", "--dry-run", "--layout", file("layout.json", { a: 1 })]).input)
      .toEqual({ name: "lamp", count: 3, dryRun: true, layout: { a: 1 } });
    expect(toolHelp(thing)).toContain("ukiyo thing-maker <name> <count> [options]   Make a thing (ukiyo_thing_maker)");
    expect(toolHelp(thing)).toContain("--layout FILE");
  });

  test("every discovered tool except run and live is a command", async () => {
    const commands = await toolCommands();
    for (const name of ["status", "new-game", "capture", "test", "import", "skills"]) expect(commands.has(name)).toBe(true);
    expect(commands.has("run")).toBe(false);
    expect(commands.has("live")).toBe(false);
    for (const [name, tool] of commands) expect(tool.name).toBe(`ukiyo_${name.replaceAll("-", "_")}`);
  });

  const context = (): ToolContext => ({ root: scratch, log: () => {}, signal: new AbortController().signal });

  test("runTool validates with the tool's schema and returns its structured result", async () => {
    expect(await runTool(thing, ["lamp", "3"], context)).toEqual({ made: { name: "lamp", count: 3, dryRun: false } });
    await expect(runTool(thing, ["lamp", "10"], context)).rejects.toThrow(ToolInputError);
    await expect(runTool(thing, ["lamp", "1.5"], context)).rejects.toThrow("must be an integer");
  });

  test("an isError result is a ToolFailure carrying its structured result; text-only results are wrapped", async () => {
    const failing: Tool = { ...thing, run: async () => ({ isError: true, content: [text("stale: 1")], structuredContent: { stale: 1 } }) };
    const error = await runTool(failing, ["lamp", "1"], context).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ToolFailure);
    expect((error as ToolFailure).message).toBe("stale: 1");
    expect((error as ToolFailure).result).toEqual({ stale: 1 });
    const textOnly: Tool = { ...thing, run: async () => ({ content: [text("hello")] }) };
    expect(await runTool(textOnly, ["lamp", "1"], context)).toEqual({ text: "hello" });
  });

  test("exit codes: 2 for usage and schema errors of generic commands, 1 for failures and for every run/live error", () => {
    expect(exitCode(["new-game"], new CliUsageError("x"))).toBe(2);
    expect(exitCode(["new-game", "bad"], new ToolInputError("x"))).toBe(2);
    expect(exitCode([], new CliUsageError("x"))).toBe(2);
    expect(exitCode(["import"], new ToolFailure("stale"))).toBe(1);
    expect(exitCode(["status"], new Error("ENOENT"))).toBe(1);
    expect(exitCode(["run", "X"], new ToolInputError("x"))).toBe(1);
    expect(exitCode(["live", "step"], new Error("expected integer"))).toBe(1);
  });

  test("--out copies the listed files and never overwrites", () => {
    const frames = join(scratch, "frames-src");
    mkdirSync(frames, { recursive: true });
    writeFileSync(join(frames, "frame-60.png"), "png");
    const out = join(scratch, "out");
    const copied = copyOutputs({ game: "X", captures: [join(frames, "frame-60.png")] }, ["captures"], out);
    expect(copied).toEqual({ game: "X", captures: [join(out, "frame-60.png")], out });
    expect(readFileSync(join(out, "frame-60.png"), "utf8")).toBe("png");
    const original = { captures: [join(frames, "frame-60.png")] };
    expect(() => copyOutputs(original, ["captures"], out)).toThrow("already exists");
    let refused: unknown;
    try { copyOutputs(original, ["captures"], out); } catch (error) { refused = error; }
    expect(refused).toBeInstanceOf(ToolFailure);
    expect((refused as ToolFailure).result).toEqual(original);
  });
});

describe("help", () => {
  test("lists run, live and every generated command; a command's --help is its block", async () => {
    const full = await help();
    expect(full).toContain("ukiyo live <list|status");
    expect(full).toContain("ukiyo run <Game> [options]");
    for (const tool of (await toolCommands()).values()) expect(full).toContain(toolHelp(tool));
    expect(full).toContain("UKIYO_ROOT");
    expect(await help("capture")).toBe(toolHelp(capture));
    expect(toolHelp(capture)).toContain("--out DIR");
    expect(toolHelp(importTool)).toContain("ukiyo import [specs…] [options]");
    expect(toolHelp(importTool)).toContain("--check");
    expect(toolHelp(newGame)).toContain("--hosts A,B,…");
  });
});

describe("CLI process on a throwaway repository", () => {
  const cli = join(import.meta.dir, "../src/cli.ts");
  const repoTemplate = new URL(`../../../${TEMPLATE_DIR}`, import.meta.url).pathname;
  const root = join(scratch, "repo");
  for (const path of templateFiles(repoTemplate)) {
    mkdirSync(dirname(join(root, TEMPLATE_DIR, path)), { recursive: true });
    cpSync(join(repoTemplate, path), join(root, TEMPLATE_DIR, path));
  }
  writeFileSync(join(root, "Ukiyo.slnx"), "<Solution>\n</Solution>\n");
  writeFileSync(join(root, "global.json"), "{}\n");
  writeFileSync(join(root, "Directory.Build.props"), "<Project><PropertyGroup><Version>0.0.1-test</Version></PropertyGroup></Project>\n");

  async function cliRun(...args: string[]) {
    const child = Bun.spawn([process.execPath, cli, ...args], { stdout: "pipe", stderr: "pipe", env: { ...process.env, UKIYO_ROOT: root } });
    const [stdout, stderr, code] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
    return { stdout, stderr, code };
  }

  test("new-game then status: JSON on stdout, exit 0; usage errors exit 2 without stdout; tool failures exit 1", async () => {
    const created = await cliRun("new-game", "TestGame", "--hosts", "Headless");
    expect(created.code).toBe(0);
    expect(JSON.parse(created.stdout)).toMatchObject({ name: "TestGame", template: "2d", hosts: ["Headless"] });
    expect(existsSync(join(root, "games/TestGame/Hosts/Headless/TestGame.Headless.csproj"))).toBe(true);
    expect(readFileSync(join(root, "Ukiyo.slnx"), "utf8")).toContain("games/TestGame/Hosts/Headless/TestGame.Headless.csproj");

    const state = await cliRun("status");
    expect(state.code).toBe(0);
    const games = JSON.parse(state.stdout).games as { name: string; hosts: string[] }[];
    expect(games).toEqual([expect.objectContaining({ name: "TestGame", dir: "games/TestGame", hosts: ["Headless"] })]);
    expect(JSON.parse(state.stdout).engine).toEqual({ version: "0.0.1-test", root });

    const again = await cliRun("new-game", "TestGame");
    expect(again).toMatchObject({ code: 2, stdout: "" });
    expect(again.stderr).toContain("[UKIYO]: [MCP:InvalidParams]: games/TestGame already exists");

    const badName = await cliRun("new-game", "lowercase");
    expect(badName).toMatchObject({ code: 2, stdout: "" });
    expect(badName.stderr).toContain("arguments.name must match");

    const unknown = await cliRun("new-game", "Other", "--kind", "3d");
    expect(unknown).toMatchObject({ code: 2, stdout: "" });

    // This repository has no skills/ folder, so listing them fails inside the tool.
    const failed = await cliRun("skills", "list");
    expect(failed).toMatchObject({ code: 1, stdout: "" });
    expect(failed.stderr).toContain("[UKIYO]: ");
  }, 30_000);
});
