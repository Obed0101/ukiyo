import { afterEach, describe, expect, test } from "bun:test";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  checkLayered,
  compositionTarget,
  readLayeredManifest,
  resolvePanel,
  userConfigDir,
  watchLayers,
  writeLayeredComposition,
} from "../src/layers.ts";
import { STUDIO_DIR, readManifest } from "../src/manifest.ts";

const temporary: string[] = [];
afterEach(() => {
  for (const dir of temporary.splice(0)) rmSync(dir, { recursive: true, force: true });
});

/** A throwaway repo copy and an empty user config dir; nothing here touches the real ~/.config. */
function scratch(): { repo: string; user: string } {
  const root = mkdtempSync(join(tmpdir(), "ukiyo-layers-"));
  temporary.push(root);
  const repo = join(root, "repo");
  for (const sub of ["panels", "layouts", "themes"]) cpSync(join(STUDIO_DIR, sub), join(repo, sub), { recursive: true });
  cpSync(join(STUDIO_DIR, "studio.json"), join(repo, "studio.json"));
  const user = join(root, "user");
  mkdirSync(join(user, "themes"), { recursive: true });
  mkdirSync(join(user, "layouts"), { recursive: true });
  mkdirSync(join(user, "panels"), { recursive: true });
  return { repo, user };
}

const write = (path: string, value: unknown) => writeFileSync(path, typeof value === "string" ? value : JSON.stringify(value));
const wait = (ms: number) => new Promise((done) => setTimeout(done, ms));

describe("user config location", () => {
  test("UKIYO_STUDIO_CONFIG beats XDG_CONFIG_HOME beats ~/.config", () => {
    expect(userConfigDir({ UKIYO_STUDIO_CONFIG: "/tmp/a", XDG_CONFIG_HOME: "/tmp/x" })).toBe("/tmp/a");
    expect(userConfigDir({ XDG_CONFIG_HOME: "/tmp/x" })).toBe("/tmp/x/ukiyo/studio");
    expect(userConfigDir({ XDG_CONFIG_HOME: "relative" })).toMatch(/\.config\/ukiyo\/studio$/);
    expect(userConfigDir({})).toMatch(/\.config\/ukiyo\/studio$/);
  });
});

describe("layer precedence", () => {
  test("with no user directory the result is the repo manifest", () => {
    const { repo } = scratch();
    const layered = readLayeredManifest(repo, join(tmpdir(), "ukiyo-no-such-dir"));
    expect(layered.userDir).toBeNull();
    expect(layered.problems).toEqual([]);
    expect(layered.composition).toEqual(readManifest(repo).composition);
    expect(new Set(Object.values(layered.sources.themes))).toEqual(new Set(["repo"]));
  });

  test("a user theme, layout and panel replace the repo file of the same name; new names are added", () => {
    const { repo, user } = scratch();
    const ink = JSON.parse(readFileSync(join(repo, "themes/ink.json"), "utf8"));
    write(join(user, "themes/ink.json"), { ...ink, description: "mine", tokens: { ...ink.tokens, accent: "#00ff00" } });
    write(join(user, "themes/mine.json"), { ...ink, tokens: ink.tokens });
    write(join(user, "layouts/simple.json"), { description: "mine", columns: "1fr", rows: "1fr", areas: ["viewport"] });
    write(join(user, "panels/transport.js"), 'export default { id: "transport", title: "T", icon: "activity", mount() {} };\n');
    write(join(user, "panels/clock.js"), 'export default { id: "clock", title: "Clock", icon: "activity", mount() {} };\n');

    const m = readLayeredManifest(repo, user);
    expect(m.problems).toEqual([]);
    expect(m.themes.ink!.tokens.accent).toBe("#00ff00");
    expect(m.sources.themes).toMatchObject({ ink: "user", paper: "repo", mine: "user" });
    expect(m.layouts.simple!.description).toBe("mine");
    expect(m.sources.layouts).toMatchObject({ simple: "user", balanced: "repo" });
    expect(m.sources.panels).toMatchObject({ transport: "user", clock: "user", viewport: "repo" });
    expect(m.panels.map((p) => p.id)).toContain("clock");
    expect(resolvePanel("transport", repo, user)!.layer).toBe("user");
    expect(resolvePanel("viewport", repo, user)!.layer).toBe("repo");
    expect(resolvePanel("ghost", repo, user)).toBeNull();
  });

  test("user studio.json fields override the repo's; the rest is inherited", () => {
    const { repo, user } = scratch();
    write(join(repo, "studio.json"), { layout: "balanced", theme: "auto", panels: { values: { pin: ["a"], keep: 1 } }, hide: ["log"] });
    write(join(user, "studio.json"), { layout: "simple", auto: { dark: "ink", light: "paper" }, panels: { values: { pin: ["b"] } } });
    const m = readLayeredManifest(repo, user);
    expect(m.problems).toEqual([]);
    expect(m.composition.layout).toBe("simple");
    expect(m.composition.theme).toBe("auto");
    expect(m.composition.hide).toEqual(["log"]);
    expect(m.composition.panels.values).toEqual({ pin: ["b"], keep: 1 });
    expect(m.sources.composition).toEqual(["repo", "user"]);
  });

  test("a user theme can extend another and list only what it changes", () => {
    const { repo, user } = scratch();
    write(join(user, "themes/tweak.json"), { extends: "ink", description: "ink, red", tokens: { accent: "#ff0000" } });
    const m = readLayeredManifest(repo, user);
    expect(m.problems).toEqual([]);
    expect(m.themes.tweak!.colorScheme).toBe("dark");
    expect(m.themes.tweak!.tokens.accent).toBe("#ff0000");
    expect(m.themes.tweak!.tokens.bg).toBe(m.themes.ink!.tokens.bg);
  });

  test("user.css is reported with a version", () => {
    const { repo, user } = scratch();
    expect(readLayeredManifest(repo, user).userCss).toBeNull();
    write(join(user, "user.css"), "body{}");
    expect(readLayeredManifest(repo, user).userCss).toBeNumber();
  });
});

describe("invalid user files", () => {
  test("are reported with their layer, skipped, and never break the Studio", () => {
    const { repo, user } = scratch();
    write(join(user, "themes/broken.json"), "{ nope");
    write(join(user, "themes/ink.json"), { description: "x", colorScheme: "dark", tokens: { bg: "#000" } });
    write(join(user, "layouts/bad.json"), { description: "x", columns: "1fr", rows: "1fr", areas: ["ghostpanel"] });
    write(join(user, "panels/oops.js"), "export default {");
    write(join(user, "studio.json"), { layout: "does-not-exist" });

    const m = readLayeredManifest(repo, user);
    expect(m.problems).toHaveLength(5);
    expect(m.problems.every((p) => p.layer === "user")).toBe(true);
    expect(Object.keys(m.themes)).not.toContain("broken");
    expect(m.sources.themes.ink).toBe("repo"); // the repo theme stays in effect
    expect(Object.keys(m.layouts)).not.toContain("bad");
    expect(m.panels.map((p) => p.id)).not.toContain("oops");
    expect(m.composition.layout).toBe(readManifest(repo).composition.layout);

    const check = checkLayered(repo, user);
    expect(check).toHaveLength(5);
    expect(check.every((line) => line.startsWith("[user] "))).toBe(true);
  });

  test("a user panel with a syntax error does not shadow the repo panel", () => {
    const { repo, user } = scratch();
    write(join(user, "panels/log.js"), "export default {");
    expect(resolvePanel("log", repo, user)!.layer).toBe("repo");
  });

  test("a broken repo file is still fatal and tagged repo", () => {
    const { repo, user } = scratch();
    write(join(repo, "themes/ink.json"), "{ nope");
    const check = checkLayered(repo, user);
    expect(check).toHaveLength(1);
    expect(check[0]).toStartWith("[repo] ");
  });

  test("a clean user layer reports nothing", () => {
    const { repo, user } = scratch();
    expect(checkLayered(repo, user)).toEqual([]);
  });
});

describe("composition target", () => {
  test("agents write the repo file until a user studio.json exists; the UI creates it", () => {
    const { repo, user } = scratch();
    expect(compositionTarget({ dir: repo, userDir: user, from: "agent" })).toBe("repo");
    expect(compositionTarget({ dir: repo, userDir: user, from: "ui" })).toBe("user");
    expect(compositionTarget({ dir: repo, userDir: null, from: "ui" })).toBe("repo");
    write(join(user, "studio.json"), {});
    expect(compositionTarget({ dir: repo, userDir: user, from: "agent" })).toBe("user");
    expect(compositionTarget({ dir: repo, userDir: user, from: "agent", target: "repo" })).toBe("repo");
  });

  test("an agent write without a user studio.json changes the repo file and leaves the user dir alone", () => {
    const { repo, user } = scratch();
    const result = writeLayeredComposition({ layout: "complete" }, { dir: repo, userDir: user, from: "agent" });
    expect(result.target).toBe("repo");
    expect(JSON.parse(readFileSync(join(repo, "studio.json"), "utf8")).layout).toBe("complete");
    expect(existsSync(join(user, "studio.json"))).toBe(false);
  });

  test("the UI's write creates a minimal user studio.json and leaves the repo file untouched", () => {
    const { repo, user } = scratch();
    const before = readFileSync(join(repo, "studio.json"), "utf8");
    const result = writeLayeredComposition({ theme: "graphite", layout: "focus" }, { dir: repo, userDir: user, from: "ui" });
    expect(result.target).toBe("user");
    expect(result.composition).toMatchObject({ theme: "graphite", layout: "focus" });
    expect(JSON.parse(readFileSync(join(user, "studio.json"), "utf8"))).toEqual({ theme: "graphite", layout: "focus" });
    expect(readFileSync(join(repo, "studio.json"), "utf8")).toBe(before);
  });

  test("once a user studio.json exists, agent writes go there and can name user layouts and themes", () => {
    const { repo, user } = scratch();
    write(join(user, "studio.json"), { hide: ["log"] });
    write(join(user, "layouts/mine.json"), { description: "m", columns: "1fr", rows: "1fr", areas: ["viewport"] });
    const result = writeLayeredComposition({ layout: "mine", panels: { values: { pin: ["x"] } } }, { dir: repo, userDir: user, from: "agent" });
    expect(result.target).toBe("user");
    expect(JSON.parse(readFileSync(join(user, "studio.json"), "utf8"))).toEqual({ hide: ["log"], layout: "mine", panels: { values: { pin: ["x"] } } });
    writeLayeredComposition({ panels: { values: null } }, { dir: repo, userDir: user, from: "agent" });
    expect(JSON.parse(readFileSync(join(user, "studio.json"), "utf8")).panels).toEqual({});
  });

  test("an invalid patch is rejected without writing either file", () => {
    const { repo, user } = scratch();
    write(join(user, "studio.json"), { hide: [] });
    const before = readFileSync(join(user, "studio.json"), "utf8");
    expect(() => writeLayeredComposition({ layout: "nope" }, { dir: repo, userDir: user, from: "ui" })).toThrow("layout must be one of");
    expect(readFileSync(join(user, "studio.json"), "utf8")).toBe(before);
    expect(() => writeLayeredComposition({ layout: "nope" }, { dir: repo, userDir: user, from: "agent", target: "repo" })).toThrow("layout must be one of");
  });

  test("a repo-targeted write cannot name a user-only layout", () => {
    const { repo, user } = scratch();
    write(join(user, "layouts/mine.json"), { description: "m", columns: "1fr", rows: "1fr", areas: ["viewport"] });
    expect(() => writeLayeredComposition({ layout: "mine" }, { dir: repo, userDir: user, from: "agent", target: "repo" })).toThrow("layout must be one of");
  });
});

describe("live reload", () => {
  test("changes in the user layer are announced, including a directory created later", async () => {
    const root = mkdtempSync(join(tmpdir(), "ukiyo-watch-"));
    temporary.push(root);
    const { repo } = scratch();
    const user = join(root, "later");
    const seen: string[] = [];
    const stop = watchLayers({ dir: repo, userDir: user }, (label) => seen.push(label));
    try {
      mkdirSync(join(user, "themes"), { recursive: true });
      await wait(2600); // the watcher rescans every 2 s
      write(join(user, "themes/new.json"), "{}");
      write(join(user, "user.css"), "body{}");
      write(join(user, "README.md"), "ignored");
      write(join(repo, "studio.json"), "{}");
      await wait(400);
      expect(seen).toContain("user:themes");
      expect(seen).toContain("user:themes/new.json");
      expect(seen).toContain("user:user.css");
      expect(seen).toContain("studio.json");
      expect(seen).not.toContain("user:README.md");
    } finally {
      stop();
    }
  });

  test("the server applies a user file edit live and reports an invalid one without dying", async () => {
    const root = mkdtempSync(join(tmpdir(), "ukiyo-server-"));
    temporary.push(root);
    const user = join(root, "config");
    mkdirSync(join(user, "themes"), { recursive: true });
    const server = Bun.spawn(["bun", join(STUDIO_DIR, "src/server.ts")], {
      env: { ...process.env, HOME: root, UKIYO_STUDIO_CONFIG: user, UKIYO_STUDIO_PORT: "0" },
      stdout: "pipe",
      stderr: "ignore",
    });
    try {
      const reader = server.stdout.getReader();
      let output = "";
      while (!output.includes("\n")) output += new TextDecoder().decode((await reader.read()).value);
      const base = output.match(/http:\/\/127\.0\.0\.1:\d+/)![0];
      const token = (await (await fetch(`${base}/`)).text()).match(/ukiyo-studio-token" content="([0-9a-f]+)"/)![1]!;
      const get = async (path: string) => (await fetch(`${base}${path}`, { headers: { "x-ukiyo-token": token } })).json() as Promise<any>;

      const events = await fetch(`${base}/api/events?token=${token}`);
      const stream = events.body!.getReader();
      const next = async (needle: string) => {
        let text = "";
        const deadline = Date.now() + 5000;
        while (Date.now() < deadline && !text.includes(needle)) {
          const chunk = await Promise.race([stream.read(), wait(5000).then(() => ({ value: undefined }))]);
          text += new TextDecoder().decode(chunk.value);
        }
        return text;
      };
      await next("event: hello");

      const ink = (await get("/api/manifest")).themes.ink;
      write(join(user, "themes/ink.json"), { ...ink, description: "user ink" });
      expect(await next("event: manifest")).toContain("user:themes/ink.json");
      expect((await get("/api/manifest")).sources.themes.ink).toBe("user");

      write(join(user, "themes/ink.json"), "{ broken");
      const reported = await next("user:themes/ink.json");
      expect(reported).toContain("[user]");
      const after = await get("/api/manifest");
      expect(after.sources.themes.ink).toBe("repo");
      expect(after.problems[0].layer).toBe("user");
      stream.cancel();
    } finally {
      server.kill();
    }
  }, 30_000);
});

describe("shipped themes and layouts", () => {
  test("the extra themes and layouts ship and the repo alone is valid", () => {
    const m = readManifest();
    for (const name of ["graphite", "contrast", "linen"]) expect(m.themes[name]).toBeDefined();
    for (const name of ["focus", "wide"]) expect(m.layouts[name]).toBeDefined();
  });

  test("the example rice is a valid user layer on top of the repo", () => {
    const m = readLayeredManifest(STUDIO_DIR, join(STUDIO_DIR, "examples/rice"));
    expect(m.problems).toEqual([]);
    expect(m.sources.panels.clock).toBe("user");
    expect(m.sources.themes.midnight).toBe("user");
    expect(m.composition.layout).toBe("rice");
    expect(m.composition.show).toEqual(["clock"]);
  });
});
