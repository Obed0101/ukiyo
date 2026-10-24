// HTTP side of the Assets panel. Mounted by server.ts after the token/origin/host guard, so every route here already
// needs the Studio token. A project is named (never a path); files are addressed as <root>/<path under the root>.
import { join, relative } from "node:path";
import { findGame, type GameInfo } from "../../tools/mcp/src/engine.ts";
import { checkImports, findSpecs } from "../../tools/mcp/src/importer.ts";
import { SECURITY_HEADERS } from "./guard.ts";
import { AssetError, ASSET_ROOTS, existingRoots, isAssetRoot, listFolder, previewPath, textPath, confine, type AssetRoot } from "./assets.ts";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...SECURITY_HEADERS } });
}

function game(repoRoot: string, name: string | null): GameInfo {
  if (!name) throw new AssetError("project is required", 400);
  try {
    return findGame(repoRoot, name);
  } catch {
    throw new AssetError(`no project ${JSON.stringify(name)}`, 404);
  }
}

function rootOf(value: string | null): AssetRoot {
  if (!isAssetRoot(value)) throw new AssetError(`root must be one of ${ASSET_ROOTS.join(", ")}`, 400);
  return value;
}

type StaleReport = { ran: boolean; reason?: string; stale: { root: AssetRoot; path: string }[]; at: number };
const STALE_TTL_MS = 30_000;
const staleCache = new Map<string, StaleReport>();

/** The importer's `--check` over the project's specs (tools/mcp checkImports, which never builds), cached briefly. */
async function staleReport(repoRoot: string, info: GameInfo): Promise<StaleReport> {
  const cached = staleCache.get(info.name);
  if (cached && Date.now() - cached.at < STALE_TTL_MS) return cached;
  const specs = findSpecs(info.dir);
  const check = (await checkImports(repoRoot, new Map([[info.name, specs]]), AbortSignal.timeout(60_000))).get(info.name);
  const report: StaleReport = { ran: false, stale: [], at: Date.now() };
  if (!check) report.reason = "no import specs";
  else if (!check.ran) report.reason = check.reason;
  else {
    report.ran = true;
    for (const result of check.results) {
      if (result.status !== "stale") continue;
      const absolute = join(repoRoot, result.spec);
      for (const root of ASSET_ROOTS) {
        const inside = relative(join(info.dir, root), absolute);
        if (!inside.startsWith("..")) report.stale.push({ root, path: inside.split("\\").join("/") });
      }
    }
  }
  staleCache.set(info.name, report);
  return report;
}

async function reveal(real: string): Promise<void> {
  if (process.platform !== "darwin") throw new AssetError("Reveal in Finder is only available on macOS", 501);
  const proc = Bun.spawn(["open", "-R", real], { stdout: "ignore", stderr: "pipe" });
  if ((await proc.exited) !== 0) throw new AssetError(`open -R failed: ${(await new Response(proc.stderr).text()).trim()}`, 500);
}

/** Returns a response for /api/assets*, or null when the path is not this module's. */
export async function assetRoutes(request: Request, url: URL, repoRoot: string): Promise<Response | null> {
  const { pathname, searchParams } = url;
  if (!pathname.startsWith("/api/assets")) return null;
  try {
    const key = `${request.method} ${pathname}`;
    if (key === "GET /api/assets") {
      const info = game(repoRoot, searchParams.get("project"));
      const roots = existingRoots(info.dir);
      const requested = searchParams.get("root");
      const root = requested ? rootOf(requested) : roots[0];
      if (!root) return json({ project: info.name, roots, listing: null });
      return json({ project: info.name, roots, base: relative(repoRoot, info.dir), listing: listFolder(info.dir, root, searchParams.get("path") ?? "") });
    }
    if (key === "GET /api/assets/file") {
      const info = game(repoRoot, searchParams.get("project"));
      const real = previewPath(info.dir, rootOf(searchParams.get("root")), searchParams.get("path") ?? "");
      return new Response(Bun.file(real), { headers: { "content-type": "image/png", "cache-control": "no-cache", ...SECURITY_HEADERS } });
    }
    if (key === "GET /api/assets/text") {
      const info = game(repoRoot, searchParams.get("project"));
      const real = textPath(info.dir, rootOf(searchParams.get("root")), searchParams.get("path") ?? "");
      return new Response(Bun.file(real), { headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...SECURITY_HEADERS } });
    }
    if (key === "GET /api/assets/stale") {
      const info = game(repoRoot, searchParams.get("project"));
      const { ran, reason, stale } = await staleReport(repoRoot, info);
      return json({ ran, reason, stale });
    }
    if (key === "POST /api/assets/reveal") {
      const input = (await request.json().catch(() => null)) as Record<string, unknown> | null;
      if (!input || typeof input !== "object") throw new AssetError("body must be a JSON object", 400);
      const info = game(repoRoot, typeof input.project === "string" ? input.project : null);
      const root = rootOf(typeof input.root === "string" ? input.root : null);
      await reveal(confine(info.dir, root, typeof input.path === "string" ? input.path : ""));
      return json({ ok: true });
    }
    return json({ error: "not found" }, 404);
  } catch (error) {
    if (error instanceof AssetError) return json({ error: error.message }, error.status);
    throw error;
  }
}
