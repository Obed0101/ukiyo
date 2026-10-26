// Real-time DOM dump for a browser evidence page, used by web-evidence.sh for the real-GPU leg (WEB_EVIDENCE_GL=metal).
// Chrome's --dump-dom with --virtual-time-budget lets virtual time run ahead while the page waits on the WebAssembly
// compile; on ANGLE Metal that dumped the page before .NET started in 4 of 5 runs of the AOT build. This starts headless
// Chrome with a DevTools port on a fresh profile, polls the page in real time until its title is "ukiyo:evidence-ready"
// (or its status line reports an error, or the timeout passes), writes the DOM like --dump-dom and stops Chrome.
// The served page is the published build, unchanged.
// Usage: bun scripts/web-evidence-realtime.ts <url> <out.html> [chrome flags…]
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const TIMEOUT_MS = 180_000;
const POLL_MS = 100;
const READY_TITLE = "ukiyo:evidence-ready";

const [url, out, ...flags] = process.argv.slice(2);
if (!url || !out) {
  console.error("usage: bun scripts/web-evidence-realtime.ts <url> <out.html> [chrome flags…]");
  process.exit(2);
}

type Target = { type: string; url: string; webSocketDebuggerUrl: string };

/** One DevTools connection to the page; evaluate returns the expression's value. */
async function connect(webSocketUrl: string) {
  const socket = new WebSocket(webSocketUrl);
  await new Promise<void>((resolve, reject) => { socket.onopen = () => resolve(); socket.onerror = () => reject(new Error(`[EVIDENCE]: DevTools socket ${webSocketUrl} failed`)); });
  const pending = new Map<number, (message: { result?: { result?: { value?: unknown } }; error?: unknown }) => void>();
  socket.onmessage = (event) => {
    const message = JSON.parse(String(event.data));
    pending.get(message.id)?.(message);
    pending.delete(message.id);
  };
  let nextId = 1;
  const evaluate = (expression: string) => new Promise<unknown>((resolve, reject) => {
    const id = nextId++;
    pending.set(id, (message) => message.error ? reject(new Error(`[EVIDENCE]: ${JSON.stringify(message.error)}`)) : resolve(message.result?.result?.value));
    socket.send(JSON.stringify({ id, method: "Runtime.evaluate", params: { expression, returnByValue: true } }));
  });
  return { evaluate, close: () => socket.close() };
}

const profile = mkdtempSync(join(tmpdir(), "ukiyo-evidence-"));
const chrome = Bun.spawn([CHROME, "--headless=new", ...flags, `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check",
  "--remote-debugging-port=0", url], { stdout: "ignore", stderr: "ignore" });
const deadline = Date.now() + TIMEOUT_MS;
try {
  // Chrome writes the port it picked to DevToolsActivePort in the profile.
  const portFile = join(profile, "DevToolsActivePort");
  while (!existsSync(portFile) || !readFileSync(portFile, "utf8").includes("\n")) {
    if (Date.now() > deadline) throw new Error("[EVIDENCE]: Chrome never opened its DevTools port");
    await Bun.sleep(POLL_MS);
  }
  const port = readFileSync(portFile, "utf8").split("\n")[0];
  let page: Target | undefined;
  while (!page) {
    if (Date.now() > deadline) throw new Error("[EVIDENCE]: no page target");
    const targets = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()) as Target[];
    page = targets.find((t) => t.type === "page" && t.url.startsWith(url.split("?")[0]!));
    if (!page) await Bun.sleep(POLL_MS);
  }
  const devtools = await connect(page.webSocketDebuggerUrl);
  for (;;) {
    const state = String(await devtools.evaluate(`document.title + "\\n" + (document.getElementById("status")?.textContent ?? "")`));
    const [title, status = ""] = state.split("\n");
    if (title === READY_TITLE || status.startsWith("error") || Date.now() > deadline) break;
    await Bun.sleep(POLL_MS);
  }
  const html = await devtools.evaluate(`(document.doctype ? "<!DOCTYPE html>\\n" : "") + document.documentElement.outerHTML`);
  devtools.close();
  writeFileSync(out, String(html));
} finally {
  chrome.kill();
  await chrome.exited;
  rmSync(profile, { recursive: true, force: true });
}
