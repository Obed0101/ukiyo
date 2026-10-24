// HTTP side of projects (src/projects.ts) and of the config folder buttons in Settings. Mounted by server.ts after the
// token/origin/host guard. Requests name a project by id from GET /api/projects, never by path.
import { mkdirSync } from "node:fs";
import { MAX_BODY_BYTES, SECURITY_HEADERS } from "./guard.ts";
import { ProjectError, canReveal, createProject, findProject, listProjects, readRecents, recordOpened, reveal } from "./projects.ts";

export type ProjectRouteOptions = {
  repoRoot: string;
  /** The recents file; defaults are decided by projects.ts. */
  recents?: string;
  /** The user config folder shown in Settings (layers.ts → userConfigDir). */
  configDir: () => string;
  /** Opens a path in Finder; replaced by tests. */
  reveal?: (path: string) => void;
  log: (level: "debug" | "info" | "warn" | "error", event: string, data?: Record<string, unknown>) => void;
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...SECURITY_HEADERS } });
}

async function readBody(request: Request): Promise<Record<string, unknown>> {
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) throw new ProjectError("[STUDIO]: body larger than 1 MB", 413);
  const value = raw ? JSON.parse(raw) : {};
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ProjectError("[STUDIO]: body must be a JSON object", 400);
  return value as Record<string, unknown>;
}

/** Returns a response for /api/projects* and /api/config/reveal, or null when the path is not theirs. */
export async function projectRoutes(options: ProjectRouteOptions, request: Request, pathname: string): Promise<Response | null> {
  const { repoRoot, log } = options;
  const recentsPath = options.recents;
  const show = options.reveal ?? ((path: string) => reveal(path));
  try {
    switch (`${request.method} ${pathname}`) {
      case "GET /api/projects":
        return json({ projects: listProjects(repoRoot, readRecents(recentsPath)), canReveal: canReveal(), configDir: options.configDir() });
      case "POST /api/projects/open": {
        const project = findProject(repoRoot, (await readBody(request)).id);
        recordOpened(project.id, { file: recentsPath });
        log("info", "studio.project.opened", { project: project.id });
        return json({ project: { ...project, lastOpened: new Date().toISOString() } });
      }
      case "POST /api/projects/new": {
        const input = await readBody(request);
        const project = await createProject({ repoRoot, name: input.name, template: input.template, log });
        recordOpened(project.id, { file: recentsPath });
        log("info", "studio.project.created", { project: project.id });
        return json({ project }, 201);
      }
      case "POST /api/projects/reveal": {
        show(findProject(repoRoot, (await readBody(request)).id).dir);
        return json({ ok: true });
      }
      case "POST /api/config/reveal": {
        const directory = options.configDir();
        mkdirSync(directory, { recursive: true });
        show(directory);
        return json({ ok: true });
      }
    }
  } catch (error) {
    if (error instanceof ProjectError) return json({ error: error.message }, error.status);
    if (error instanceof SyntaxError) return json({ error: `[STUDIO]: ${error.message}` }, 400);
    throw error;
  }
  return null;
}
