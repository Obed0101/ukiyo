// HTTP side of the launcher (src/launcher.ts). Mounted by server.ts after the token/origin/host guard, so every route
// here already needs the Studio token; POST bodies are JSON from this origin. Requests name a game by id, never by path.
import { MAX_BODY_BYTES, SECURITY_HEADERS } from "./guard.ts";
import { Launcher, LauncherError, type Run } from "./launcher.ts";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...SECURITY_HEADERS } });
}

async function readBody(request: Request): Promise<Record<string, unknown>> {
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) throw new LauncherError("[STUDIO]: body larger than 1 MB", 413);
  const value = raw ? JSON.parse(raw) : {};
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new LauncherError("[STUDIO]: body must be a JSON object", 400);
  return value as Record<string, unknown>;
}

/** The run a request means: its `run` id, else the only run of `game`/`pid` that is running. */
function pick(launcher: Launcher, input: Record<string, unknown>): Run {
  if (typeof input.run === "string") {
    const run = launcher.run(input.run);
    if (!run) throw new LauncherError(`[LAUNCH]: no run ${JSON.stringify(input.run)}`, 404);
    return run;
  }
  const candidates = launcher.runs().filter((r) => r.state === "running" && (typeof input.pid !== "number" || r.pid === input.pid) && (typeof input.game !== "string" || r.game === input.game));
  if (candidates.length === 1) return candidates[0]!;
  throw new LauncherError(
    candidates.length === 0
      ? "[LAUNCH]: the Studio has not started a running game that matches (it can only reload and stop games it launched)"
      : `[LAUNCH]: ${candidates.length} games match; name one with "run" or "pid"`,
    candidates.length === 0 ? 404 : 409,
  );
}

/** Returns a response for /api/games and /api/run*, or null when the path is not the launcher's. */
export async function runRoutes(launcher: Launcher, request: Request, pathname: string, log: (event: string, data: Record<string, unknown>) => void): Promise<Response | null> {
  try {
    switch (`${request.method} ${pathname}`) {
      case "GET /api/games":
        return json({ games: launcher.games(), runs: launcher.runs(), dotnet: launcher.dotnet });
      case "POST /api/run": {
        const input = await readBody(request);
        if (typeof input.game !== "string") throw new LauncherError('[LAUNCH]: body needs "game": one of the ids from GET /api/games', 400);
        const run = launcher.start(input.game);
        log("studio.run.started", { run: run.id, game: run.game });
        return json({ run }, 202);
      }
      case "POST /api/run/stop": {
        const run = await launcher.stop(pick(launcher, await readBody(request)).id);
        log("studio.run.stopped", { run: run.id, game: run.game });
        return json({ run });
      }
      case "POST /api/run/reload": {
        const input = await readBody(request);
        const { run, done } = launcher.beginReload(pick(launcher, input).id);
        log("studio.run.reload", { run: run.id, game: run.game });
        // wait:true answers when the relaunch is attached (or failed); otherwise poll GET /api/games.
        return input.wait === true ? json({ run: await done }) : json({ run }, 202);
      }
    }
  } catch (error) {
    if (error instanceof LauncherError) return json({ error: error.message }, error.status);
    if (error instanceof SyntaxError) return json({ error: `[STUDIO]: ${error.message}` }, 400);
    throw error;
  }
  return null;
}
