import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { findRepoRoot } from "../src/engine.ts";
import studio from "../src/tools/studio.ts";

const repo = findRepoRoot(import.meta.dir);
let root: string;
let user: string;
let saved: string | undefined;

/** A copy of studio/ in a temp repo root and a temp user config dir, selected through the env var. */
beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "ukiyo-studio-tool-"));
  const dir = join(root, "studio");
  for (const sub of ["panels", "layouts", "themes"]) cpSync(join(repo, "studio", sub), join(dir, sub), { recursive: true });
  cpSync(join(repo, "studio/studio.json"), join(dir, "studio.json"));
  mkdirSync(join(dir, "src"), { recursive: true });
  writeFileSync(join(dir, "src/server.ts"), "");
  user = join(root, "config");
  mkdirSync(join(user, "themes"), { recursive: true });
  saved = process.env.UKIYO_STUDIO_CONFIG;
  process.env.UKIYO_STUDIO_CONFIG = user;
});
afterEach(() => {
  if (saved === undefined) delete process.env.UKIYO_STUDIO_CONFIG;
  else process.env.UKIYO_STUDIO_CONFIG = saved;
  rmSync(root, { recursive: true, force: true });
});

const run = (args: Record<string, unknown>) => studio.run(args, { root, log: () => {} } as never) as Promise<{ structuredContent: any; isError?: boolean }>;

describe("ukiyo_studio with a user layer", () => {
  test("list tags every item with its layer and check reports a broken user file", async () => {
    const ink = JSON.parse(readFileSync(join(root, "studio/themes/ink.json"), "utf8"));
    writeFileSync(join(user, "themes/mine.json"), JSON.stringify(ink));
    let listed = (await run({ action: "list" })).structuredContent;
    expect(listed.themes.mine.source).toBe("user");
    expect(listed.themes.ink.source).toBe("repo");
    expect(listed.panels[0].source).toBe("repo");
    expect(listed.userConfig).toBe(user);
    expect((await run({ action: "check" })).isError).toBeUndefined();

    writeFileSync(join(user, "themes/bad.json"), "{ nope");
    const checked = await run({ action: "check" });
    expect(checked.isError).toBe(true);
    expect(checked.structuredContent.problems[0]).toStartWith("[user] ");
    listed = (await run({ action: "list" })).structuredContent;
    expect(listed.problems).toHaveLength(1);
  });

  test("compose writes the repo studio.json until a user one exists, then the user's; target overrides", async () => {
    const repoFile = join(root, "studio/studio.json");
    const userFile = join(user, "studio.json");
    let result = (await run({ action: "compose", layout: "simple" })).structuredContent;
    expect(result.target).toBe("repo");
    expect(JSON.parse(readFileSync(repoFile, "utf8")).layout).toBe("simple");
    expect(existsSync(userFile)).toBe(false);

    result = (await run({ action: "compose", layout: "focus", target: "user" })).structuredContent;
    expect(result.target).toBe("user");
    expect(JSON.parse(readFileSync(userFile, "utf8"))).toEqual({ layout: "focus" });

    result = (await run({ action: "compose", layout: "wide" })).structuredContent;
    expect(result.target).toBe("user");
    expect(JSON.parse(readFileSync(repoFile, "utf8")).layout).toBe("simple");
    expect(result.composition.layout).toBe("wide");
  });
});
