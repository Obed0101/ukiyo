import { expect, test } from "bun:test";
import { parseArgs, selectSession, run } from "../src/cli.ts";
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { LiveSession } from "../src/bridge.ts";

test("CLI builds only explicit supported bridge commands", () => {
  expect(parseArgs(["live", "step", "--ticks", "60", "--pid", "12"]).request).toEqual({ cmd: "step", ticks: 60 });
  expect(parseArgs(["live", "record", "--on", "false"]).request).toEqual({ cmd: "record", on: false });
  expect(parseArgs(["live", "step"]).request.ticks).toBe(1);
});

test.each([
  [], ["live", "reload"], ["live", "step", "--ticks", "-1"], ["live", "step", "--ticks", "36001"],
  ["live", "step", "--ticks", "1.5"], ["live", "step", "--ticks", "1", "--ticks", "2"],
  ["live", "status", "--ticks", "1"], ["live", "record"], ["live", "record", "--on", "yes"],
  ["live", "input"], ["live", "capture"], ["live", "status", "--pid", "0"],
])("CLI rejects malformed arguments %j", (...args) => {
  expect(() => parseArgs(args as string[])).toThrow();
});

test("CLI refuses ambiguous session selection", () => {
  const sessions = [{ pid: 1 }, { pid: 2 }] as LiveSession[];
  expect(() => selectSession(sessions)).toThrow("multiple games");
  expect(selectSession(sessions, 2).pid).toBe(2);
  expect(() => selectSession(sessions, 3)).toThrow("no matching");
  expect(() => selectSession([])).toThrow("no matching");
});

test("CLI refuses an existing output before sending a command", async () => {
  const dir = mkdtempSync(join(tmpdir(), "ukiyo-cli-"));
  try {
    const file = join(dir, "keep.json");
    writeFileSync(file, "keep this");
    await expect(run(["live", "record", "--on", "false", "--out", file])).rejects.toThrow("output already exists");
    expect(readFileSync(file, "utf8")).toBe("keep this");
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("CLI process prints help and fails invalid commands without JSON stdout", async () => {
  const cli = join(import.meta.dir, "../src/cli.ts");
  const help = Bun.spawn([process.execPath, cli, "--help"], { stdout: "pipe", stderr: "pipe" });
  expect(await new Response(help.stdout).text()).toContain("ukiyo live");
  expect(await help.exited).toBe(0);
  const bad = Bun.spawn([process.execPath, cli, "live", "step", "--ticks", "-1"], { stdout: "pipe", stderr: "pipe" });
  expect(await new Response(bad.stdout).text()).toBe("");
  expect(await new Response(bad.stderr).text()).toContain("expected integer");
  expect(await bad.exited).toBe(1);
});
