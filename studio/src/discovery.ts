// A running Studio announces itself the same way games do: ~/.ukiyo/studio/<pid>.json, owner-only, holding the port
// and token. Agents (the MCP tool ukiyo_studio) read it to see what the person is looking at and to send them notices.
import { chmodSync, closeSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, rmSync, writeSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export type StudioSession = { pid: number; port: number; token: string; url: string; startedAt: string; file: string };

export const STUDIO_LIVE_DIR = join(homedir(), ".ukiyo", "studio");

export function writeDiscovery(session: Omit<StudioSession, "file">, directory = STUDIO_LIVE_DIR): string {
  mkdirSync(directory, { recursive: true, mode: 0o700 });
  const file = join(directory, `${session.pid}.json`);
  // Created 0600 from the first byte; chmod covers a stale file left by a recycled pid.
  const fd = openSync(file, "w", 0o600);
  try {
    writeSync(fd, `${JSON.stringify(session, null, 2)}\n`);
  } finally {
    closeSync(fd);
  }
  chmodSync(file, 0o600);
  return file;
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

/** Running Studios, newest first. Files of dead processes are removed. */
export function studioSessions(directory = STUDIO_LIVE_DIR): StudioSession[] {
  if (!existsSync(directory)) return [];
  const sessions: StudioSession[] = [];
  for (const name of readdirSync(directory).filter((f) => f.endsWith(".json"))) {
    const file = join(directory, name);
    try {
      const session = { ...JSON.parse(readFileSync(file, "utf8")), file } as StudioSession;
      if (alive(session.pid)) sessions.push(session);
      else rmSync(file, { force: true });
    } catch {
      // Being written; next time.
    }
  }
  return sessions.sort((a, b) => b.startedAt.localeCompare(a.startedAt));
}

/** Calls a running Studio's API with its token. */
export async function studioRequest(session: StudioSession, path: string, body?: unknown, timeoutMs = 5_000): Promise<unknown> {
  const response = await fetch(`http://127.0.0.1:${session.port}${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: { "x-ukiyo-token": session.token, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(timeoutMs),
  });
  const payload = (await response.json()) as { error?: string };
  if (!response.ok) throw new Error(`[STUDIO]: ${path} → ${response.status} ${payload.error ?? ""}`.trim());
  return payload;
}
