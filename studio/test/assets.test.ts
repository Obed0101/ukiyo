import { afterEach, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { AssetError, classify, confine, listFolder, previewPath, PREVIEW_MAX_BYTES } from "../src/assets.ts";
import { assetRoutes } from "../src/assets-routes.ts";

const scratch: string[] = [];
afterEach(() => {
  for (const dir of scratch.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function project(files: Record<string, string | Uint8Array>): { repo: string; game: string; outside: string } {
  const repo = mkdtempSync(join(tmpdir(), "ukiyo-assets-"));
  scratch.push(repo);
  const game = join(repo, "samples", "Demo");
  mkdirSync(join(repo, "Ukiyo-slnx-marker"), { recursive: true });
  for (const [path, content] of Object.entries(files)) {
    mkdirSync(dirname(join(game, path)), { recursive: true });
    writeFileSync(join(game, path), content);
  }
  const outside = join(repo, "secret.txt");
  writeFileSync(outside, "secret");
  return { repo, game, outside };
}

function refusal(run: () => unknown): AssetError {
  try {
    run();
  } catch (error) {
    if (error instanceof AssetError) return error;
    throw error;
  }
  throw new Error("expected an AssetError");
}

describe("asset path confinement", () => {
  test("rejects .. segments, absolute paths and NUL bytes before touching the disk", () => {
    const { game } = project({ "Shared/assets/a.png": "x" });
    expect(refusal(() => confine(game, "Shared/assets", "../../secret.txt")).status).toBe(400);
    expect(refusal(() => confine(game, "Shared/assets", "sub/../../x")).status).toBe(400);
    expect(refusal(() => confine(game, "Shared/assets", "..\\x")).status).toBe(400);
    expect(refusal(() => confine(game, "Shared/assets", "/etc/passwd")).status).toBe(400);
    expect(refusal(() => confine(game, "Shared/assets", "a.png\0.txt")).status).toBe(400);
  });

  test("refuses a symlinked file and a symlinked folder that point outside the root", () => {
    const { game, outside, repo } = project({ "Shared/assets/a.png": "x" });
    symlinkSync(outside, join(game, "Shared/assets/leak.png"));
    symlinkSync(repo, join(game, "Shared/assets/up"));
    expect(refusal(() => confine(game, "Shared/assets", "leak.png")).status).toBe(403);
    expect(refusal(() => confine(game, "Shared/assets", "up/secret.txt")).status).toBe(403);
    expect(refusal(() => previewPath(game, "Shared/assets", "leak.png")).status).toBe(403);
  });

  test("a listing hides links that leave the root and keeps real entries", () => {
    const { game, outside } = project({ "Shared/assets/a.png": "x", "Shared/assets/sub/b.json": "{}" });
    symlinkSync(outside, join(game, "Shared/assets/leak.png"));
    const names = listFolder(game, "Shared/assets", "").entries.map((e) => e.name);
    expect(names).toEqual(["sub", "a.png"]);
  });

  test("a missing root is a 404 and allows nothing", () => {
    const { game } = project({ "Shared/assets/a.png": "x" });
    expect(refusal(() => confine(game, "art", "")).status).toBe(404);
  });

  test("previews only PNG files under the size cap", () => {
    const { game } = project({ "Shared/assets/a.png": "x", "Shared/assets/b.json": "{}", "Shared/assets/big.png": new Uint8Array(PREVIEW_MAX_BYTES + 1) });
    expect(previewPath(game, "Shared/assets", "a.png")).toContain("a.png");
    expect(refusal(() => previewPath(game, "Shared/assets", "b.json")).status).toBe(415);
    expect(refusal(() => previewPath(game, "Shared/assets", "big.png")).status).toBe(413);
  });

  test("classifies by name", () => {
    expect(classify("hero.PNG")).toBe("image");
    expect(classify("scenes/meadow.json")).toBe("scene");
    expect(classify("hero.import.json")).toBe("import");
    expect(classify("notes.md")).toBe("data");
    expect(classify("blob.bin")).toBe("file");
  });
});

describe("asset routes", () => {
  test("a traversal through the HTTP route answers 400 and an unknown project 404", async () => {
    const { repo } = project({ "Shared/assets/a.png": "x" });
    mkdirSync(join(repo, "samples/Demo/Shared"), { recursive: true });
    writeFileSync(join(repo, "samples/Demo/Shared/Program.cs"), "");
    mkdirSync(join(repo, "samples/Demo/Headless"), { recursive: true });
    const get = (query: string) => assetRoutes(new Request(`http://127.0.0.1/api/assets/file?${query}`), new URL(`http://127.0.0.1/api/assets/file?${query}`), repo);
    expect((await get("project=Demo&root=Shared/assets&path=../../../secret.txt"))!.status).toBe(400);
    expect((await get("project=Nope&root=Shared/assets&path=a.png"))!.status).toBe(404);
    expect((await get("project=Demo&root=Shared&path=assets/a.png"))!.status).toBe(400);
    expect((await get("project=Demo&root=Shared/assets&path=a.png"))!.status).toBe(200);
  });
});
