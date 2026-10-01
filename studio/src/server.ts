#!/usr/bin/env bun
// ukiyo Studio server: serves the Studio page, proxies the dev bridge of running games, watches the Studio's own files
// and pushes changes to open pages, and holds what the person is looking at for agents. Local only (127.0.0.1), token
// protected, no dependencies.
//
//   bun studio/src/server.ts            → http://127.0.0.1:4100
//   UKIYO_STUDIO_PORT=4200              (0 = any free port)
import { randomBytes } from "node:crypto";
import { existsSync, rmSync, watch, type FSWatcher } from "node:fs";
import { join, resolve } from "node:path";
import { BRIDGE_COMMANDS, bridgeRequest, liveSessions, publicSessions } from "../../tools/mcp/src/bridge.ts";
import { writeDiscovery } from "./discovery.ts";
import { MAX_BODY_BYTES, SECURITY_HEADERS, refuse, safeName } from "./guard.ts";
import { STUDIO_DIR, StudioFileError, readManifest, writeComposition, type CompositionPatch } from "./manifest.ts";

const VERSION = "0.1.0";
const REPO_ROOT = resolve(STUDIO_DIR, "..");
const token = randomBytes(16).toString("hex");

/** Fonts come from the geist package the site already installs; without it the Studio falls back to system fonts. */
const FONT_DIRS = [process.env.UKIYO_STUDIO_FONTS, join(REPO_ROOT, "site/node_modules/geist/dist/fonts")].filter(Boolean) as string[];
const FONTS: Record<string, string> = {
  "geist.woff2": "geist-sans/Geist-Variable.woff2",
  "geist-mono.woff2": "geist-mono/GeistMono-Variable.woff2",
  "geist-pixel-square.woff2": "geist-pixel/GeistPixel-Square.woff2",
};

const MIME: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".woff2": "font/woff2",
};

function log(level: "debug" | "info" | "warn" | "error", event: string, data: Record<string, unknown> = {}) {
  process.stderr.write(`${JSON.stringify({ time: new Date().toISOString(), level, source: "ukiyo-studio", event, ...data })}\n`);
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": MIME[".json"]!, "cache-control": "no-store", ...SECURITY_HEADERS } });
}

function fail(status: number, error: string): Response {
  return json({ error }, status);
}

async function file(path: string, cache = "no-cache"): Promise<Response> {
  if (!existsSync(path)) return fail(404, "not found");
  const extension = path.slice(path.lastIndexOf("."));
  return new Response(Bun.file(path), { headers: { "content-type": MIME[extension] ?? "application/octet-stream", "cache-control": cache, ...SECURITY_HEADERS } });
}

async function body(request: Request): Promise<Record<string, unknown>> {
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) throw new RangeError("[STUDIO]: body larger than 1 MB");
  const value = raw ? JSON.parse(raw) : {};
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new TypeError("[STUDIO]: body must be a JSON object");
  return value as Record<string, unknown>;
}

// ── live state ────────────────────────────────────────────────────────────────────────────────────────────────────

/** What the person is looking at, as reported by the open page(s). Agents read it through ukiyo_studio view. */
let view: { state: Record<string, unknown>; updatedAt: string | null } = { state: {}, updatedAt: null };

const encoder = new TextEncoder();
const streams = new Set<ReadableStreamDefaultController<Uint8Array>>();

function broadcast(event: string, data: unknown) {
  const chunk = encoder.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
  for (const stream of streams) {
    try {
      stream.enqueue(chunk);
    } catch {
      streams.delete(stream);
    }
  }
}

function events(): Response {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  let heartbeat: ReturnType<typeof setInterval>;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
      streams.add(c);
      c.enqueue(encoder.encode(`retry: 1000\nevent: hello\ndata: ${JSON.stringify({ version: VERSION })}\n\n`));
      heartbeat = setInterval(() => {
        try {
          c.enqueue(encoder.encode(": keep-alive\n\n"));
        } catch {
          clearInterval(heartbeat);
        }
      }, 15_000);
    },
    cancel() {
      clearInterval(heartbeat);
      streams.delete(controller);
    },
  });
  return new Response(stream, { headers: { "content-type": "text/event-stream", "cache-control": "no-store", connection: "keep-alive", ...SECURITY_HEADERS } });
}

// ── file watching: editing a panel, layout, theme or studio.json updates every open page ─────────────────────────────

const watchers: FSWatcher[] = [];
let pending = new Set<string>();
let flush: ReturnType<typeof setTimeout> | undefined;

function changed(where: string, name: string | null) {
  if (!name || name.endsWith(".tmp") || name.startsWith(".")) return;
  if (where === "." && name !== "studio.json") return;
  pending.add(where === "." ? name : `${where}/${name}`);
  clearTimeout(flush);
  flush = setTimeout(() => {
    const files = [...pending].sort();
    pending = new Set();
    try {
      readManifest();
      broadcast("manifest", { files });
      log("info", "studio.files.changed", { files });
    } catch (error) {
      // A half-written or invalid file: tell the page (and the agent reading the log) instead of applying it.
      const message = error instanceof Error ? error.message : String(error);
      broadcast("manifest-error", { files, error: message });
      log("warn", "studio.files.invalid", { files, error: message });
    }
  }, 120);
}

for (const where of [".", "panels", "layouts", "themes"]) {
  const directory = join(STUDIO_DIR, where);
  if (existsSync(directory)) watchers.push(watch(directory, (_event, name) => changed(where, name)));
}

// ── routes ────────────────────────────────────────────────────────────────────────────────────────────────────────

async function page(): Promise<Response> {
  const html = (await Bun.file(join(STUDIO_DIR, "web/index.html")).text()).replace("__UKIYO_STUDIO_TOKEN__", token);
  return new Response(html, { headers: { "content-type": MIME[".html"]!, "cache-control": "no-store", ...SECURITY_HEADERS } });
}

async function bridge(request: Request): Promise<Response> {
  const input = await body(request);
  const cmd = String(input.cmd ?? "");
  if (!(BRIDGE_COMMANDS as readonly string[]).includes(cmd)) return fail(400, `cmd must be one of ${BRIDGE_COMMANDS.join(", ")}`);
  const sessions = liveSessions();
  const session = typeof input.pid === "number" ? sessions.find((s) => s.pid === input.pid) : sessions[0];
  if (!session) return fail(404, typeof input.pid === "number" ? `no running dev build with pid ${input.pid}` : "no running dev build");

  const forward: Record<string, unknown> = { cmd };
  if (cmd === "step") forward.ticks = input.ticks ?? 1;
  if (cmd === "input") forward.events = input.events;
  if (cmd === "record") forward.on = input.on === true;
  const started = performance.now();
  try {
    const response = await bridgeRequest(session, forward, 10_000);
    const ms = Math.round(performance.now() - started);
    return json({ ...response, pid: session.pid, ms });
  } catch (error) {
    return fail(502, error instanceof Error ? error.message : String(error));
  }
}

async function route(request: Request, url: URL): Promise<Response> {
  const { pathname } = url;
  const get = request.method === "GET";

  if (get && (pathname === "/" || pathname === "/index.html")) return page();
  if (get && pathname.startsWith("/web/")) {
    const name = pathname.slice(5);
    return safeName(name) ? file(join(STUDIO_DIR, "web", name)) : fail(404, "not found");
  }
  if (get && pathname.startsWith("/panels/")) {
    const name = pathname.slice(8);
    return safeName(name) && name.endsWith(".js") ? file(join(STUDIO_DIR, "panels", name)) : fail(404, "not found");
  }
  if (get && pathname.startsWith("/fonts/")) {
    const relative = FONTS[pathname.slice(7)];
    const found = relative && FONT_DIRS.map((d) => join(d, relative)).find(existsSync);
    return found ? file(found, "max-age=86400") : fail(404, "font not installed (cd site && bun install)");
  }
  if (get && pathname === "/brand/mark.svg") return file(join(REPO_ROOT, ".github/assets/brand/ukiyo-mark.svg"), "max-age=86400");

  switch (`${request.method} ${pathname}`) {
    case "GET /api/health":
      return json({ ok: true, version: VERSION, pages: streams.size });
    case "GET /api/manifest":
      try {
        return json(readManifest());
      } catch (error) {
        return fail(error instanceof StudioFileError ? 422 : 500, error instanceof Error ? error.message : String(error));
      }
    case "GET /api/sessions":
      return json({ sessions: publicSessions(liveSessions()) });
    case "POST /api/bridge":
      return bridge(request);
    case "GET /api/events":
      return events();
    case "GET /api/view":
      return json({ ...view, pages: streams.size });
    case "POST /api/view": {
      const state = await body(request);
      view = { state, updatedAt: new Date().toISOString() };
      return json({ ok: true });
    }
    case "POST /api/composition": {
      const patch = (await body(request)) as CompositionPatch;
      try {
        const composition = writeComposition(patch);
        log("info", "studio.composition.written", { patch });
        return json({ composition });
      } catch (error) {
        return fail(error instanceof StudioFileError ? 422 : 500, error instanceof Error ? error.message : String(error));
      }
    }
    case "POST /api/notice": {
      const notice = await body(request);
      const text = typeof notice.text === "string" ? notice.text.trim().slice(0, 500) : "";
      if (!text) return fail(400, "notice needs text");
      const level = ["info", "success", "warn", "error"].includes(notice.level as string) ? notice.level : "info";
      const from = typeof notice.from === "string" ? notice.from.slice(0, 40) : "agent";
      broadcast("notice", { text, level, from, at: new Date().toISOString() });
      return json({ ok: true, delivered: streams.size });
    }
  }
  return fail(404, "not found");
}

function start(port: number) {
  return Bun.serve({
    hostname: "127.0.0.1",
    port,
    idleTimeout: 255, // the event stream sends a keep-alive every 15 s
    async fetch(request) {
      const url = new URL(request.url);
      const refusal = refuse(request, url, { port: server.port ?? port, token });
      if (refusal) {
        log("warn", "studio.request.refused", { path: url.pathname, reason: refusal.reason });
        return fail(refusal.status, refusal.reason);
      }
      try {
        return await route(request, url);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log("error", "studio.request.failed", { path: url.pathname, message });
        return fail(error instanceof SyntaxError || error instanceof TypeError || error instanceof RangeError ? 400 : 500, message);
      }
    },
  });
}

const requested = Number(process.env.UKIYO_STUDIO_PORT ?? 4100);
let server: ReturnType<typeof start>;
try {
  server = start(requested);
} catch (error) {
  if ((error as { code?: string }).code !== "EADDRINUSE" || requested === 0) throw error;
  log("warn", "studio.port.busy", { port: requested });
  server = start(0);
}

const url = `http://127.0.0.1:${server.port}/`;
const discovery = writeDiscovery({ pid: process.pid, port: server.port!, token, url, startedAt: new Date().toISOString() });
log("info", "studio.ready", { url, discovery });
process.stdout.write(`ukiyo Studio → ${url}\n`);

function shutdown() {
  for (const watcher of watchers) watcher.close();
  rmSync(discovery, { force: true });
  server.stop(true);
  process.exit(0);
}
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
