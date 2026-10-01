// Client of the dev bridge (src/Ukiyo.DevBridge), shared by the MCP server and the Studio. Running development builds
// announce themselves with a discovery file in ~/.ukiyo/live (mode 0600) holding the port and the session token; each
// request is one line of JSON with that token, answered by one line.
import { existsSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { connect } from "node:net";
import { homedir } from "node:os";
import { join } from "node:path";

export type LiveSession = { pid: number; port: number; token: string; game: string; target: string; startedAt: string; file: string };

export type BridgeResponse = { ok: boolean; result?: unknown; error?: string };

/** Commands the bridge understands (DevBridgeServer.Execute). */
export const BRIDGE_COMMANDS = ["status", "pause", "resume", "step", "snapshot", "capture", "input", "record"] as const;

export const LIVE_DIR = join(homedir(), ".ukiyo", "live");

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

/** Sessions whose process is still running, newest first. Discovery files of dead processes are removed. */
export function liveSessions(directory = LIVE_DIR): LiveSession[] {
  if (!existsSync(directory)) return [];
  const sessions: LiveSession[] = [];
  for (const file of readdirSync(directory).filter((f) => f.endsWith(".json"))) {
    const path = join(directory, file);
    try {
      const session = { ...JSON.parse(readFileSync(path, "utf8")), file: path } as LiveSession;
      if (alive(session.pid)) sessions.push(session);
      else rmSync(path, { force: true });
    } catch {
      // A file being written right now; skip it this time.
    }
  }
  return sessions.sort((a, b) => b.startedAt.localeCompare(a.startedAt));
}

/** The session list without secrets, for anything shown to agents or people. */
export function publicSessions(sessions: LiveSession[]) {
  return sessions.map(({ token: _token, file: _file, ...rest }) => rest);
}

let nextId = 1;

export function bridgeRequest(session: LiveSession, request: Record<string, unknown>, timeoutMs = 30_000): Promise<BridgeResponse> {
  return new Promise((resolve, reject) => {
    const socket = connect({ host: "127.0.0.1", port: session.port });
    let buffer = "";
    const timer = setTimeout(() => {
      socket.destroy();
      reject(new Error(`[LIVE]: no answer from ${session.game} (pid ${session.pid}) in ${timeoutMs} ms — is the window paused in a debugger?`));
    }, timeoutMs);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(`${JSON.stringify({ ...request, id: nextId++, token: session.token })}\n`));
    socket.on("data", (chunk: string) => {
      buffer += chunk;
      const newline = buffer.indexOf("\n");
      if (newline < 0) return;
      clearTimeout(timer);
      socket.end();
      try {
        resolve(JSON.parse(buffer.slice(0, newline)) as BridgeResponse);
      } catch (error) {
        reject(error);
      }
    });
    socket.on("error", (error) => {
      clearTimeout(timer);
      reject(new Error(`[LIVE]: ${error.message}`));
    });
  });
}
