import { afterEach, describe, expect, test } from "bun:test";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { writeLayeredComposition } from "../src/layers.ts";
import { STUDIO_DIR, checkLayout } from "../src/manifest.ts";
import { projectRoutes } from "../src/project-routes.ts";

const REPO = resolve(STUDIO_DIR, "..");
const temporary: string[] = [];
afterEach(() => {
  for (const dir of temporary.splice(0)) rmSync(dir, { recursive: true, force: true });
});

/** A throwaway repository root (templates plus one sample), a recents file and a config dir; nothing real is touched. */
function scratch() {
  const root = mkdtempSync(join(tmpdir(), "ukiyo-projects-"));
  temporary.push(root);
  cpSync(join(REPO, "templates"), join(root, "templates"), { recursive: true });
  cpSync(join(REPO, "Ukiyo.slnx"), join(root, "Ukiyo.slnx"));
  cpSync(join(REPO, "samples/LanternRun"), join(root, "samples/LanternRun"), { recursive: true });
  return { root, recents: join(root, "recents.json"), config: join(root, "config") };
}

function routes(root: string, recents: string, config: string, revealed: string[] = []) {
  const options = { repoRoot: root, recents, configDir: () => config, reveal: (path: string) => void revealed.push(path), log: () => {} };
  return (method: string, path: string, body?: unknown) =>
    projectRoutes(options, new Request(`http://127.0.0.1${path}`, { method, body: body === undefined ? undefined : JSON.stringify(body) }), path);
}

describe("/api/projects", () => {
  test("lists the samples with their kind, sorted by name when nothing was opened", async () => {
    const { root, recents, config } = scratch();
    const response = await routes(root, recents, config)("GET", "/api/projects");
    const body = await response!.json();
    expect(response!.status).toBe(200);
    expect(body.configDir).toBe(config);
    expect(body.projects.map((p: { name: string }) => p.name)).toContain("LanternRun");
    expect(body.projects.every((p: { lastOpened: string | null }) => p.lastOpened === null)).toBe(true);
  });

  test("opening a project records it and puts it first", async () => {
    const { root, recents, config } = scratch();
    const call = routes(root, recents, config);
    const opened = await (await call("POST", "/api/projects/open", { id: "sample/LanternRun" }))!.json();
    expect(opened.project.name).toBe("LanternRun");
    const listing = await (await call("GET", "/api/projects"))!.json();
    expect(listing.projects[0].id).toBe("sample/LanternRun");
    expect(listing.projects[0].lastOpened).toBeString();
    expect(JSON.parse(readFileSync(recents, "utf8"))["sample/LanternRun"]).toBeString();
  });

  test("an unknown project id is a 404, not a path lookup", async () => {
    const { root, recents, config } = scratch();
    const response = await routes(root, recents, config)("POST", "/api/projects/open", { id: "../../etc" });
    expect(response!.status).toBe(404);
  });

  test("reveal opens the project folder; config reveal creates the folder first", async () => {
    const { root, recents, config } = scratch();
    const revealed: string[] = [];
    const call = routes(root, recents, config, revealed);
    await call("POST", "/api/projects/reveal", { id: "sample/LanternRun" });
    await call("POST", "/api/config/reveal", {});
    expect(revealed[0]).toBe(join(root, "samples/LanternRun"));
    expect(revealed[1]).toBe(config);
    expect(existsSync(config)).toBe(true);
  });

  test("paths that are not projects fall through", async () => {
    const { root, recents, config } = scratch();
    expect(await routes(root, recents, config)("GET", "/api/other")).toBeNull();
  });
});

describe("new project", () => {
  test("creates the game and answers with the project", async () => {
    const { root, recents, config } = scratch();
    const response = await routes(root, recents, config)("POST", "/api/projects/new", { name: "SkyLantern", template: "2d" });
    expect(response!.status).toBe(201);
    expect((await response!.json()).project.name).toBe("SkyLantern");
    expect(existsSync(join(root, "games/SkyLantern"))).toBe(true);
  });

  test("a duplicate name returns the tool's real error text with 422", async () => {
    const { root, recents, config } = scratch();
    const call = routes(root, recents, config);
    await call("POST", "/api/projects/new", { name: "SkyLantern", template: "2d" });
    const response = await call("POST", "/api/projects/new", { name: "SkyLantern", template: "3d" });
    expect(response!.status).toBe(422);
    const { error } = await response!.json();
    expect(error).toContain("games/SkyLantern already exists");
    expect(error).not.toContain("[MCP:InvalidParams]");
  });

  test("a name that is not PascalCase is refused and nothing is written", async () => {
    const { root, recents, config } = scratch();
    const response = await routes(root, recents, config)("POST", "/api/projects/new", { name: "sky lantern", template: "2d" });
    expect(response!.status).toBe(422);
    expect((await response!.json()).error).toBeString();
    expect(existsSync(join(root, "games"))).toBe(false);
  });
});

describe("layout tab groups", () => {
  const base = { description: "x", columns: "1fr 1fr", rows: "1fr auto", areas: ["main side", "bottom bottom"] };

  test("accepts a tab group on an area and expands it to panels", () => {
    const layout = checkLayout("t", { ...base, tabs: { bottom: ["console", "assets"] } });
    expect(layout.tabs).toEqual({ bottom: ["console", "assets"] });
  });

  test.each([
    [{ ghost: ["a", "b"] }, "no such area"],
    [{ bottom: ["only"] }, "two or more"],
    [{ bottom: ["main", "x"] }, "also an area"],
    [{ bottom: ["a", "a"] }, "listed twice"],
    [["a", "b"], "area names to panel id lists"],
  ])("rejects tabs %j", (tabs, message) => {
    expect(() => checkLayout("t", { ...base, tabs })).toThrow(message);
  });
});

describe("settings write to the user config", () => {
  test("a user-target compose lands in the user studio.json and leaves the repo file alone", () => {
    const root = mkdtempSync(join(tmpdir(), "ukiyo-settings-"));
    temporary.push(root);
    const repo = join(root, "repo");
    for (const sub of ["panels", "layouts", "themes"]) cpSync(join(STUDIO_DIR, sub), join(repo, sub), { recursive: true });
    cpSync(join(STUDIO_DIR, "studio.json"), join(repo, "studio.json"));
    const user = join(root, "user");
    mkdirSync(user, { recursive: true });
    const before = readFileSync(join(repo, "studio.json"), "utf8");

    const result = writeLayeredComposition({ theme: "auto", layout: "focus", hide: ["values"] }, { dir: repo, userDir: user, from: "ui", target: "user" });
    expect(result.target).toBe("user");
    const written = JSON.parse(readFileSync(join(user, "studio.json"), "utf8"));
    expect(written).toMatchObject({ theme: "auto", layout: "focus", hide: ["values"] });
    expect(readFileSync(join(repo, "studio.json"), "utf8")).toBe(before);
  });

  test("a layout that names a panel without a file is accepted and reported as missing", () => {
    const layout = checkLayout("t", { description: "x", columns: "1fr", rows: "1fr", areas: ["ghostpanel"] });
    expect(layout.areas).toEqual(["ghostpanel"]);
  });
});
