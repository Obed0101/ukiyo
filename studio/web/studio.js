// @ts-check
// The Studio shell. It owns the connection to the selected game (through the server's dev bridge proxy), the layout
// grid and the theme; panels own everything inside their box. Layout, theme and the panels themselves are files
// (studio/layouts, studio/themes, studio/panels, studio/studio.json): when one changes on disk the server says so
// and the shell re-applies it without a reload.
import { createApi } from "./api.js";
import { append, clear, h } from "./dom.js";
import { icon } from "./icons.js";

/** @typedef {import("./types").LiveSession} LiveSession */
/** @typedef {import("./types").GameStatus} GameStatus */
/** @typedef {import("./types").GameSnapshot} GameSnapshot */
/** @typedef {import("./types").Frame} Frame */
/** @typedef {import("./types").PanelModule} PanelModule */
/** @typedef {import("./types").Studio} Studio */

const STATUS_INTERVAL_MS = 250;
const HIDDEN_INTERVAL_MS = 1000;
const SESSIONS_INTERVAL_MS = 2000;
const CACHE_KEY = "ukiyo.studio.cache.v1";
const PIN_KEY = "ukiyo.studio.session";
const LAYOUT_ORDER = ["simple", "balanced", "complete"];

const token = document.querySelector('meta[name="ukiyo-studio-token"]')?.getAttribute("content") ?? "";
const api = createApi(token);
const prefersDark = matchMedia("(prefers-color-scheme: dark)");

const state = {
  /** @type {any} */ manifest: null,
  /** @type {LiveSession[]} */ sessions: [],
  /** @type {LiveSession | null} */ session: null,
  /** @type {number | null} */ pinned: readPin(),
  /** @type {GameStatus | null} */ status: null,
  /** @type {GameSnapshot | null} */ snapshot: null,
  /** @type {Frame | null} */ frame: null,
  /** @type {number | null} */ bridgeMs: null,
  serverOnline: false,
  /** @type {string | null} */ focusedPanel: null,
  /** @type {Record<string, Record<string, unknown>>} */ panelView: {},
  /** @type {Record<string, string>} */ panelErrors: {},
  /** @type {string | null} */ fileError: null,
  themeName: "",
};

// ── events ──────────────────────────────────────────────────────────────────────────────────────────────────────

/** @type {Map<string, Set<(value: any) => void>>} */
const listeners = new Map();

/** @param {string} event @param {unknown} value */
function emit(event, value) {
  for (const handler of listeners.get(event) ?? []) {
    try {
      handler(value);
    } catch (error) {
      console.error(`[STUDIO]: ${event} handler failed`, error);
    }
  }
}

/** @param {string} event @param {(value: any) => void} handler */
function on(event, handler) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event)?.add(handler);
  return () => listeners.get(event)?.delete(handler);
}

/** @type {import("./types").LogEntry[]} */
const history = [];

/** @param {"info" | "success" | "warn" | "error"} level @param {string} source @param {string} text */
function log(level, source, text) {
  const entry = { at: new Date().toISOString(), level, source, text };
  history.push(entry);
  if (history.length > 500) history.shift();
  emit("log", entry);
}

// ── game connection ─────────────────────────────────────────────────────────────────────────────────────────────

const QUIET = new Set(["status", "snapshot"]);

/**
 * @param {import("./types").BridgeCommand} cmd
 * @param {Record<string, unknown>} [args]
 */
async function call(cmd, args = {}) {
  const session = state.session;
  if (!session) throw new Error("No game is connected. Start a development build first.");
  const response = await api.post("/api/bridge", { pid: session.pid, cmd, ...args });
  state.bridgeMs = response.ms ?? null;
  if (!response.ok) {
    log("error", "bridge", `${cmd}: ${response.error}`);
    throw new Error(response.error);
  }
  if (!QUIET.has(cmd)) log("info", "bridge", `${cmd}${args.ticks !== undefined ? ` ${args.ticks}` : ""} · ${response.ms} ms`);
  return response.result;
}

/** @param {GameStatus | null} status */
function setStatus(status) {
  state.status = status;
  emit("status", status);
  renderChrome();
}

/** @param {GameSnapshot | null} snapshot */
function setSnapshot(snapshot) {
  state.snapshot = snapshot;
  emit("snapshot", snapshot);
}

/** @param {"pause" | "resume" | "step"} cmd @param {number} [ticks] */
async function control(cmd, ticks) {
  const result = await call(cmd, cmd === "step" ? { ticks: ticks ?? 1 } : {});
  if (cmd === "step") {
    setSnapshot(/** @type {GameSnapshot} */ (result));
    setStatus(/** @type {GameStatus} */ (await call("status")));
  } else {
    setStatus(/** @type {GameStatus} */ (result));
    await refreshSnapshot();
  }
}

async function refreshSnapshot() {
  if (!state.session) return;
  setSnapshot(/** @type {GameSnapshot} */ (await call("snapshot")));
}

let capturing = /** @type {Promise<Frame | null> | null} */ (null);

/** Reads the real frame back from the game's renderer. Concurrent callers share one request. */
function capture() {
  capturing ??= (async () => {
    try {
      const session = state.session;
      const result = /** @type {{ tick: number, width: number, height: number, backend: string, png: string }} */ (await call("capture"));
      if (!session || state.session?.pid !== session.pid) return null;
      state.frame = { tick: result.tick, width: result.width, height: result.height, backend: result.backend, url: `data:image/png;base64,${result.png}`, pid: session.pid };
      emit("frame", state.frame);
      return state.frame;
    } finally {
      capturing = null;
    }
  })();
  return capturing;
}

function readPin() {
  try {
    const value = Number(localStorage.getItem(PIN_KEY));
    return Number.isInteger(value) && value > 0 ? value : null;
  } catch {
    return null;
  }
}

/** @param {number | null} pid */
function selectSession(pid) {
  state.pinned = pid;
  try {
    if (pid === null) localStorage.removeItem(PIN_KEY);
    else localStorage.setItem(PIN_KEY, String(pid));
  } catch {
    // Storage blocked: the choice lasts for this page only.
  }
  chooseSession();
}

function chooseSession() {
  const next = state.sessions.find((s) => s.pid === state.pinned) ?? state.sessions[0] ?? null;
  if (next?.pid === state.session?.pid) return;
  state.session = next;
  state.frame = null;
  setSnapshot(null);
  setStatus(null);
  emit("frame", null);
  emit("session", next);
  if (next) log("info", "studio", `connected to ${next.game} (${next.target}, pid ${next.pid})`);
  reportView();
  if (next) refreshSnapshot().catch(() => {});
}

async function pollSessions() {
  try {
    const { sessions } = await api.get("/api/sessions");
    state.sessions = sessions;
    state.serverOnline = true;
    emit("sessions", sessions);
    chooseSession();
  } catch {
    state.serverOnline = false;
  }
  renderChrome();
  setTimeout(pollSessions, SESSIONS_INTERVAL_MS);
}

async function pollStatus() {
  if (state.session) {
    try {
      const status = /** @type {GameStatus} */ (await call("status"));
      const moved = status.tick !== state.status?.tick;
      setStatus(status);
      if (moved && (listeners.get("snapshot")?.size ?? 0) > 0) await refreshSnapshot();
    } catch (error) {
      // The game exited or stalled; the session poll drops it if its process is gone.
      if (state.status) log("warn", "bridge", error instanceof Error ? error.message : String(error));
      setStatus(null);
    }
  }
  setTimeout(pollStatus, document.hidden ? HIDDEN_INTERVAL_MS : STATUS_INTERVAL_MS);
}

// ── theme and layout ────────────────────────────────────────────────────────────────────────────────────────────

/** Swaps tokens without every element transitioning at once (better-ui: suppress transitions on theme switch). */
function applyTheme() {
  const { composition, themes } = state.manifest;
  const name = composition.theme === "auto" ? (prefersDark.matches ? composition.auto.dark : composition.auto.light) : composition.theme;
  const theme = themes[name];
  if (!theme) return;
  const root = document.documentElement;
  const guard = document.createElement("style");
  guard.textContent = "*,*::before,*::after{transition:none !important}";
  document.head.append(guard);
  for (const [key, value] of Object.entries(theme.tokens)) root.style.setProperty(`--${key}`, String(value));
  root.style.colorScheme = theme.colorScheme;
  root.dataset.theme = name;
  void root.offsetHeight;
  requestAnimationFrame(() => guard.remove());
  state.themeName = name;
  try {
    localStorage.setItem(CACHE_KEY, JSON.stringify({ vars: theme.tokens, colorScheme: theme.colorScheme, theme: name }));
  } catch {
    // No cache: the next load paints defaults first.
  }
}

/** @type {Map<string, { version: number, module: PanelModule }>} */
const modules = new Map();
/** @type {Map<string, { section: HTMLElement, cleanup: () => void }>} */
const mounted = new Map();

/** @param {{ id: string, version: number }} entry */
async function loadPanel(entry) {
  const cached = modules.get(entry.id);
  if (cached && cached.version === entry.version) return cached.module;
  const imported = await import(`/panels/${entry.id}.js?v=${entry.version}`);
  /** @type {PanelModule} */
  const module = imported.default;
  if (!module || module.id !== entry.id || typeof module.mount !== "function" || typeof module.title !== "string") {
    throw new TypeError(`panels/${entry.id}.js must export default { id: "${entry.id}", title, icon, description, mount(root, studio) }`);
  }
  modules.set(entry.id, { version: entry.version, module });
  return module;
}

function visiblePanels() {
  const { composition, layouts } = state.manifest;
  const layout = layouts[composition.layout];
  const hidden = new Set(composition.hide);
  const grid = layout.panels.filter((/** @type {string} */ id) => !hidden.has(id));
  const dock = composition.show.filter((/** @type {string} */ id) => !layout.panels.includes(id) && !hidden.has(id));
  return { layout, grid, dock, hidden };
}

/** @param {string} id @param {HTMLElement} section */
function mountPanel(id, section) {
  mounted.get(id)?.cleanup();
  const module = modules.get(id)?.module;
  const header = h("header", { class: "panel-head" });
  const actions = h("div", { class: "panel-actions" });
  const body = h("div", { class: "panel-body" });
  clear(section);
  const titleId = `panel-title-${id}`;
  section.setAttribute("aria-labelledby", titleId);
  section.dataset.version = String(modules.get(id)?.version ?? "");
  append(header, module ? icon(safeIcon(module.icon)) : icon("x"), h("h2", { id: titleId, text: module?.title ?? id }), actions);
  append(section, header, body);

  if (!module) {
    append(body, h("p", { class: "panel-error", role: "alert", text: state.panelErrors[id] ?? `panels/${id}.js did not load` }));
    mounted.set(id, { section, cleanup: () => {} });
    return;
  }

  /** @type {(() => void)[]} */
  const subscriptions = [];
  /** @type {Studio} */
  const studio = {
    id,
    get options() {
      return state.manifest.composition.panels[id] ?? {};
    },
    actions,
    h,
    icon,
    get session() { return state.session; },
    get sessions() { return state.sessions; },
    get status() { return state.status; },
    get snapshot() { return state.snapshot; },
    get frame() { return state.frame; },
    call: (cmd, args) => call(cmd, args),
    control,
    capture,
    refreshSnapshot,
    selectSession,
    on(event, handler) {
      const off = on(event, handler);
      subscriptions.push(off);
      return off;
    },
    view(patch) {
      state.panelView[id] = { ...state.panelView[id], ...patch };
      reportView();
    },
    compose,
    log: (level, text) => log(level, id, text),
    get history() { return history.slice(); },
    get manifest() { return state.manifest; },
    panelTitle: (other) => modules.get(other)?.module.title ?? other,
  };

  let cleanup = () => {};
  try {
    const result = module.mount(body, studio);
    if (typeof result === "function") cleanup = result;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    state.panelErrors[id] = `mount failed: ${message}`;
    append(body, h("p", { class: "panel-error", role: "alert", text: `panels/${id}.js: ${message}` }));
    log("error", id, message);
  }
  mounted.set(id, {
    section,
    cleanup() {
      for (const off of subscriptions) off();
      try {
        cleanup();
      } catch (error) {
        console.error(`[STUDIO]: ${id} cleanup failed`, error);
      }
    },
  });
}

/** @param {string} name */
function safeIcon(name) {
  try {
    icon(name);
    return name;
  } catch {
    return "layout-panel-left";
  }
}

const grid = /** @type {HTMLElement} */ (document.getElementById("grid"));

async function loadModules() {
  state.panelErrors = {};
  const { grid: inGrid, dock } = visiblePanels();
  const needed = new Set([...inGrid, ...dock]);
  await Promise.all(state.manifest.panels.map(async (/** @type {{ id: string, version: number }} */ entry) => {
    try {
      await loadPanel(entry);
    } catch (error) {
      if (!needed.has(entry.id)) return;
      const message = error instanceof Error ? error.message : String(error);
      state.panelErrors[entry.id] = message;
      modules.delete(entry.id);
      log("error", "studio", `panels/${entry.id}.js: ${message}`);
    }
  }));
}

function buildGrid() {
  for (const { cleanup } of mounted.values()) cleanup();
  mounted.clear();
  clear(grid);

  const { layout, grid: inGrid, dock, hidden } = visiblePanels();
  const areas = layout.areas.map((/** @type {string} */ row) => row.trim().split(/\s+/).map((cell) => (hidden.has(cell) ? "." : cell)).join(" "));
  const rows = [layout.rows];
  if (dock.length) {
    const width = areas[0].split(" ").length;
    areas.push(Array(width).fill("dock").join(" "));
    rows.push("minmax(200px, 0.5fr)");
  }
  grid.style.gridTemplateColumns = layout.columns;
  grid.style.gridTemplateRows = rows.join(" ");
  grid.style.gridTemplateAreas = areas.map((/** @type {string} */ row) => `"${row}"`).join(" ");
  grid.dataset.layout = state.manifest.composition.layout;

  for (const id of inGrid) {
    const section = h("section", { class: "panel", dataset: { panel: id } });
    section.style.gridArea = id;
    grid.append(section);
    mountPanel(id, section);
  }
  if (dock.length) {
    const strip = h("div", { class: "dock" });
    strip.style.gridArea = "dock";
    grid.append(strip);
    for (const id of dock) {
      const section = h("section", { class: "panel", dataset: { panel: id } });
      strip.append(section);
      mountPanel(id, section);
    }
  }
  renderChrome();
  reportView();
}

/** @param {import("./types").CompositionPatch} patch */
async function compose(patch) {
  try {
    await api.post("/api/composition", patch);
    // The file watcher announces the change; applying it here too keeps this page instant.
    await reload();
  } catch (error) {
    log("error", "studio", `could not change the layout: ${error instanceof Error ? error.message : String(error)}`);
    throw error;
  }
}

let lastShape = "";
let reloading = Promise.resolve();

/** Reloads run one after another: a local compose and the file watcher's event often arrive together. */
function reload() {
  reloading = reloading.catch(() => {}).then(applyManifest);
  return reloading;
}

async function applyManifest() {
  const manifest = await api.get("/api/manifest");
  state.manifest = manifest;
  state.fileError = null;
  const { composition, layouts } = manifest;
  const shape = JSON.stringify({ layout: layouts[composition.layout], composition: { ...composition, theme: undefined, auto: undefined } });
  applyTheme();
  await loadModules();
  if (shape !== lastShape) {
    lastShape = shape;
    buildGrid();
    return;
  }
  // Same layout: remount only panels whose file changed (or that failed and may load now).
  for (const [id, entry] of mounted) {
    const version = String(modules.get(id)?.version ?? "");
    if (version && entry.section.dataset.version === version) continue;
    mountPanel(id, entry.section);
  }
  renderChrome();
}

// ── chrome: top bar, status bar, notices ────────────────────────────────────────────────────────────────────────

const chrome = {
  game: /** @type {HTMLElement} */ (document.getElementById("game")),
  state: /** @type {HTMLElement} */ (document.getElementById("state")),
  tick: /** @type {HTMLElement} */ (document.getElementById("tick")),
  layouts: /** @type {HTMLElement} */ (document.getElementById("layouts")),
  theme: /** @type {HTMLSelectElement} */ (document.getElementById("theme")),
  statusbar: /** @type {HTMLElement} */ (document.getElementById("statusbar")),
  notices: /** @type {HTMLElement} */ (document.getElementById("notices")),
};

function renderChrome() {
  const { session, status } = state;
  chrome.game.textContent = session ? session.game : "No game connected";
  chrome.game.title = session ? `${session.game} · ${session.target} · pid ${session.pid}` : "";

  const live = Boolean(session && status && !status.paused);
  const label = !state.serverOnline ? "Studio server offline" : !session ? "Waiting for a dev build" : !status ? "Connecting" : status.paused ? "Paused" : "Running";
  chrome.state.dataset.state = !state.serverOnline ? "offline" : !session || !status ? "idle" : live ? "live" : "paused";
  chrome.state.textContent = label;
  chrome.tick.textContent = status ? String(status.tick) : "—";

  if (state.manifest) renderLayoutSwitch();

  const items = [];
  if (status) {
    items.push(`${status.renderer} · ${status.backend}`, `${status.width}×${status.height}`, `dropped ${status.droppedTicks}`);
  }
  if (state.bridgeMs !== null && session) items.push(`bridge ${state.bridgeMs} ms`);
  if (state.manifest) items.push(`layout ${state.manifest.composition.layout} · theme ${state.themeName} · studio/studio.json`);
  clear(chrome.statusbar);
  for (const item of items) chrome.statusbar.append(h("span", { text: item }));
  if (state.fileError) chrome.statusbar.append(h("span", { class: "statusbar-error", role: "alert", text: state.fileError }));
}

let layoutSignature = "";

function renderLayoutSwitch() {
  const { layouts, themes, composition } = state.manifest;
  const names = Object.keys(layouts).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  const signature = JSON.stringify([names, composition.layout, Object.keys(themes), composition.theme]);
  if (signature === layoutSignature) return;
  layoutSignature = signature;

  clear(chrome.layouts);
  for (const name of names) {
    const button = h("button", {
      type: "button",
      class: "segment",
      "aria-pressed": String(name === composition.layout),
      title: layouts[name].description,
      text: name[0].toUpperCase() + name.slice(1),
      onClick: () => compose({ layout: name }).catch(() => {}),
    });
    chrome.layouts.append(button);
  }

  clear(chrome.theme);
  chrome.theme.append(h("option", { value: "auto", text: "Theme: system", selected: composition.theme === "auto" }));
  for (const name of Object.keys(themes).sort()) {
    chrome.theme.append(h("option", { value: name, text: `Theme: ${name}`, selected: composition.theme === name }));
  }
}

/** @param {string} name */
function rank(name) {
  const index = LAYOUT_ORDER.indexOf(name);
  return index < 0 ? LAYOUT_ORDER.length : index;
}

chrome.theme.addEventListener("change", () => compose({ theme: chrome.theme.value }).catch(() => {}));

/** @param {import("./types").Notice} notice */
function showNotice(notice) {
  const close = h("button", { type: "button", class: "icon-button", "aria-label": "Dismiss" }, icon("x"));
  const card = h("div", { class: "notice", dataset: { level: notice.level }, role: notice.level === "error" ? "alert" : "status" },
    h("span", { class: "notice-from" }, icon("send", { size: 14 }), notice.from),
    h("p", { text: notice.text }),
    close);
  const dismiss = () => card.remove();
  close.addEventListener("click", dismiss);
  chrome.notices.append(card);
  if (notice.level === "info" || notice.level === "success") setTimeout(dismiss, 8000);
}

// ── what the person sees, for agents (ukiyo_studio view) ─────────────────────────────────────────────────────────

let viewTimer = /** @type {ReturnType<typeof setTimeout> | undefined} */ (undefined);

function reportView() {
  clearTimeout(viewTimer);
  viewTimer = setTimeout(() => {
    if (!state.manifest) return;
    const { grid: inGrid, dock, hidden } = visiblePanels();
    api.post("/api/view", {
      layout: state.manifest.composition.layout,
      theme: state.themeName,
      panels: [...inGrid, ...dock],
      hidden: [...hidden],
      focusedPanel: state.focusedPanel,
      session: state.session ? { pid: state.session.pid, game: state.session.game, target: state.session.target } : null,
      tick: state.status?.tick ?? null,
      paused: state.status?.paused ?? null,
      frameTick: state.frame?.tick ?? null,
      window: { width: innerWidth, height: innerHeight },
      panelState: state.panelView,
      panelErrors: state.panelErrors,
      fileError: state.fileError,
    }).catch(() => {});
  }, 300);
}

/** @param {Event} event */
function trackFocus(event) {
  const panel = /** @type {HTMLElement | null} */ (/** @type {Element} */ (event.target)?.closest?.("[data-panel]"));
  const id = panel?.dataset.panel ?? null;
  if (id === state.focusedPanel) return;
  state.focusedPanel = id;
  reportView();
}

document.addEventListener("focusin", trackFocus);
document.addEventListener("pointerdown", trackFocus);
addEventListener("resize", reportView);
on("frame", reportView);
prefersDark.addEventListener("change", () => state.manifest && (applyTheme(), renderChrome(), reportView()));

// ── start ───────────────────────────────────────────────────────────────────────────────────────────────────────

function listen() {
  const events = api.events();
  events.addEventListener("hello", () => {
    state.serverOnline = true;
    renderChrome();
  });
  events.addEventListener("manifest", (event) => {
    const { files } = JSON.parse(/** @type {MessageEvent} */ (event).data);
    log("info", "files", `changed: ${files.join(", ")}`);
    reload().catch((error) => log("error", "studio", error instanceof Error ? error.message : String(error)));
  });
  events.addEventListener("manifest-error", (event) => {
    const { error } = JSON.parse(/** @type {MessageEvent} */ (event).data);
    state.fileError = error;
    log("error", "files", error);
    renderChrome();
    reportView();
  });
  events.addEventListener("notice", (event) => {
    const notice = JSON.parse(/** @type {MessageEvent} */ (event).data);
    showNotice(notice);
    emit("notice", notice);
    log(notice.level, notice.from, notice.text);
  });
  events.addEventListener("error", () => {
    state.serverOnline = false;
    renderChrome();
  });
}

async function start() {
  try {
    await reload();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    state.fileError = message;
    clear(grid);
    grid.append(h("p", { class: "fatal", role: "alert", text: `The Studio files are invalid: ${message}. Fix the file named above; the page updates when it is valid.` }));
    renderChrome();
  }
  listen();
  pollSessions();
  pollStatus();
}

start();
