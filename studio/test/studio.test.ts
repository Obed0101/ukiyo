import { describe, expect, test } from "bun:test";
import { cpSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { refuse, safeName } from "../src/guard.ts";
import { STUDIO_DIR, THEME_TOKENS, checkLayout, checkStudio, layoutPanels, readManifest, writeComposition } from "../src/manifest.ts";

/** A throwaway copy of the Studio files, so composition writes never touch the real studio.json. */
function scratchStudio(): string {
  const dir = mkdtempSync(join(tmpdir(), "ukiyo-studio-"));
  for (const sub of ["panels", "layouts", "themes"]) cpSync(join(STUDIO_DIR, sub), join(dir, sub), { recursive: true });
  cpSync(join(STUDIO_DIR, "studio.json"), join(dir, "studio.json"));
  return dir;
}

describe("studio files", () => {
  test("the shipped panels, layouts, themes and studio.json are valid", () => {
    expect(checkStudio()).toEqual([]);
  });

  test("simple, balanced and complete exist and only name existing panels", () => {
    const manifest = readManifest();
    const panelIds = manifest.panels.map((p) => p.id);
    for (const name of ["simple", "balanced", "complete"]) {
      const layout = manifest.layouts[name];
      expect(layout).toBeDefined();
      for (const id of layout!.panels) expect(panelIds).toContain(id);
    }
    // Complete shows every panel that ships.
    expect([...manifest.layouts.complete!.panels].sort()).toEqual([...panelIds].sort());
  });

  test("every theme defines every token and nothing else", () => {
    const manifest = readManifest();
    for (const theme of Object.values(manifest.themes)) {
      expect(Object.keys(theme.tokens).sort()).toEqual([...THEME_TOKENS].sort());
    }
  });

  test("the stylesheet's defaults cover every theme token", () => {
    const css = readFileSync(join(STUDIO_DIR, "web/studio.css"), "utf8");
    for (const token of THEME_TOKENS) expect(css).toContain(`--${token}:`);
  });

  test("every panel file names itself and declares title, icon and mount", () => {
    for (const file of readdirSync(join(STUDIO_DIR, "panels"))) {
      const source = readFileSync(join(STUDIO_DIR, "panels", file), "utf8");
      const id = file.replace(/\.js$/, "");
      expect(source).toContain(`id: "${id}"`);
      expect(source).toMatch(/title: "[^"]+"/);
      expect(source).toMatch(/icon: "[a-z-]+"/);
      expect(source).toMatch(/\bmount\(root, studio\)/);
      const iconName = source.match(/icon: "([a-z-]+)"/)?.[1];
      expect(readFileSync(join(STUDIO_DIR, "web/icons.js"), "utf8")).toContain(`${iconName?.includes("-") ? `"${iconName}"` : iconName}:`);
    }
  });
});

describe("layout checks", () => {
  const base = { description: "x", columns: "1fr 1fr", rows: "auto 1fr" };

  test("accepts rectangular areas and lists panels in reading order", () => {
    const layout = checkLayout("t", { ...base, areas: ["a a", "b c"] });
    expect(layoutPanels(layout)).toEqual(["a", "b", "c"]);
  });

  test.each([
    [["a b", "b a"], "not a rectangle"],
    [["a b", "c"], "same number of cells"],
    [["a Bad", "c d"], "not a panel id"],
  ])("rejects areas %j", (areas, message) => {
    expect(() => checkLayout("t", { ...base, areas })).toThrow(message);
  });

  test("track counts must match the grid", () => {
    expect(() => checkLayout("t", { ...base, columns: "minmax(0, 1fr)", areas: ["a b", "c d"] })).toThrow("columns must list 2 tracks");
  });

  test("CSS values cannot break out of their property", () => {
    expect(() => checkLayout("t", { ...base, columns: "1fr; background: red", areas: ["a b", "c d"] })).toThrow("CSS grid track list");
  });
});

describe("composition", () => {
  test("writes a validated patch and keeps the rest of studio.json", () => {
    const dir = scratchStudio();
    try {
      const composition = writeComposition({ layout: "complete", panels: { values: { pin: ["score"] } } }, dir);
      expect(composition.layout).toBe("complete");
      expect(composition.theme).toBe("auto");
      const saved = JSON.parse(readFileSync(join(dir, "studio.json"), "utf8"));
      expect(saved.panels.values.pin).toEqual(["score"]);
      expect(saved.$comment).toBeString();

      writeComposition({ panels: { values: null } }, dir);
      expect(JSON.parse(readFileSync(join(dir, "studio.json"), "utf8")).panels.values).toBeUndefined();
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  test.each([
    [{ layout: "nope" }, "layout must be one of"],
    [{ theme: "neon" }, 'theme must be "auto"'],
    [{ hide: ["ghost"] }, "hide must list panel ids"],
  ])("rejects %j without writing", (patch, message) => {
    const dir = scratchStudio();
    try {
      const before = readFileSync(join(dir, "studio.json"), "utf8");
      expect(() => writeComposition(patch as never, dir)).toThrow(message);
      expect(readFileSync(join(dir, "studio.json"), "utf8")).toBe(before);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  test("a broken studio.json is reported with its file name", () => {
    const dir = scratchStudio();
    try {
      writeFileSync(join(dir, "studio.json"), "{ not json");
      expect(checkStudio(dir)[0]).toContain("studio.json");
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("request guard", () => {
  const options = { port: 4100, token: "a".repeat(32) };
  const at = (path: string, init: RequestInit & { headers?: Record<string, string> } = {}) => {
    const request = new Request(`http://127.0.0.1:4100${path}`, init);
    return refuse(request, new URL(request.url), options);
  };
  const host = { host: "127.0.0.1:4100" };

  test("serves the page to this host and the API with the token", () => {
    expect(at("/", { headers: host })).toBeNull();
    expect(at("/api/manifest", { headers: { ...host, "x-ukiyo-token": options.token } })).toBeNull();
    expect(at(`/api/events?token=${options.token}`, { headers: host })).toBeNull();
  });

  test("refuses other hosts (DNS rebinding), other origins and missing tokens", () => {
    expect(at("/", { headers: { host: "evil.test:4100" } })?.status).toBe(421);
    expect(at("/api/manifest", { headers: { ...host, origin: "https://evil.test", "x-ukiyo-token": options.token } })?.status).toBe(403);
    expect(at("/api/manifest", { headers: host })?.status).toBe(401);
    expect(at("/api/manifest", { headers: { ...host, "x-ukiyo-token": "b".repeat(32) } })?.status).toBe(401);
    // Only the event stream takes the token from the query string.
    expect(at(`/api/manifest?token=${options.token}`, { headers: host })?.status).toBe(401);
  });

  test("writes must be JSON", () => {
    const headers = { ...host, "x-ukiyo-token": options.token, "content-type": "text/plain" };
    expect(at("/api/notice", { method: "POST", headers, body: "hi" })?.status).toBe(415);
  });

  test("static names cannot escape their folder", () => {
    expect(safeName("studio.js")).toBe(true);
    for (const bad of ["../server.ts", "a/b.js", ".hidden", "..", "x..js"]) expect(safeName(bad)).toBe(false);
  });
});
