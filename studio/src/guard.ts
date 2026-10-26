// Request checks for the Studio server. It listens on 127.0.0.1 only, but a web page in any browser tab can still try
// to reach localhost, so every API call must carry the session token (served inside the Studio page, which other
// origins cannot read), the Host header must be this server (DNS rebinding) and writes must come from this origin.
import { timingSafeEqual } from "node:crypto";

export type GuardOptions = { port: number; token: string };

export const MAX_BODY_BYTES = 1 << 20;

export function allowedHosts(port: number): string[] {
  return [`127.0.0.1:${port}`, `localhost:${port}`];
}

function sameToken(given: string | null, token: string): boolean {
  if (!given) return false;
  const a = Buffer.from(given);
  const b = Buffer.from(token);
  return a.length === b.length && timingSafeEqual(a, b);
}

/** Returns why the request must be refused (with an HTTP status), or null when it may proceed. */
export function refuse(request: Request, url: URL, { port, token }: GuardOptions): { status: number; reason: string } | null {
  const host = request.headers.get("host");
  if (!host || !allowedHosts(port).includes(host)) return { status: 421, reason: `host ${host ?? "(none)"} is not this Studio` };

  const origin = request.headers.get("origin");
  if (origin !== null && !allowedHosts(port).some((h) => origin === `http://${h}`)) return { status: 403, reason: `origin ${origin} may not use the Studio` };

  if (!url.pathname.startsWith("/api/")) return null;

  // EventSource cannot send headers, so the event stream (and only it) takes the token from the query string.
  const given = request.headers.get("x-ukiyo-token") ?? (url.pathname === "/api/events" ? url.searchParams.get("token") : null);
  if (!sameToken(given, token)) return { status: 401, reason: "missing or wrong Studio token" };

  if (request.method === "POST") {
    if (!(request.headers.get("content-type") ?? "").startsWith("application/json")) return { status: 415, reason: "POST bodies must be application/json" };
    const length = Number(request.headers.get("content-length") ?? "0");
    if (length > MAX_BODY_BYTES) return { status: 413, reason: "body larger than 1 MB" };
  }
  return null;
}

/** Static names are single path segments: no slashes, no dot-dot, no hidden files. */
export function safeName(name: string): boolean {
  return /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/.test(name) && !name.includes("..");
}

export const SECURITY_HEADERS: Record<string, string> = {
  "content-security-policy":
    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer",
  "cross-origin-resource-policy": "same-origin",
};
