// @ts-check
// The Studio shell. Two places share one header: the Hub (no project open) and a Project (the editor), with Settings
// as a third route. The shell owns the connection to the open project's running game (through the server's dev bridge
// proxy), the layout grid and the theme; panels own everything inside their box. Layout, theme and the panels
// themselves are files (studio/layouts, studio/themes, studio/panels, studio/studio.json): when one changes on disk
// the server says so and the shell re-applies it without a reload.
import { createApi } from "./api.js";
import { append, clear, emptyState, h } from "./dom.js";
import { createHeader } from "./header.js";
import { mountHub } from "./hub.js";
import { icon } from "./icons.js";
import { mountSettings } from "./settings.js";

/** @typedef {import("./types").LiveSession} LiveSession */
/** @typedef {import("./types").GameStatus} GameStatus */
/** @typedef {import("./types").GameSnapshot} GameSnapshot */
/** @typedef {import("./types").Frame} Frame */
/** @typedef {import("./types").PanelModule} PanelModule */
/** @typedef {import("./types").Studio} Studio */
/** @typedef {import("./types").Project} Project */
/** @typedef {import("./types").GameRun} GameRun */

const STATUS_INTERVAL_MS = 250;
const HIDDEN_INTERVAL_MS = 1000;
const SESSIONS_INTERVAL_MS = 2000;
const RUNS_INTERVAL_MS = 2000;
const RUNS_BUSY_INTERVAL_MS = 700;
const CACHE_KEY = "ukiyo.studio.cache.v1";
const PIN_KEY = "ukiyo.studio.session";
const TAB_KEY = "ukiyo.studio.tab";
const ACTIVE_RUN = new Set(["building", "starting", "running", "reloading", "stopping"]);

const token = document.querySelector('meta[name="ukiyo-studio-token"]')?.getAttribute("content") ?? "";
const api = createApi(token);
const prefersDark = matchMedia("(prefers-color-scheme: dark)");

const state = {
  /** @type {any} */ manifest: null,
  /** @type {"hub" | "project" | "settings"} */ route: "hub",
  /** @type {Project[]} */ projects: [],
  /** @type {Project | null} */ project: null,
  canReveal: false,
  /** @type {string | null} */ configDir: null,
  /** @type {GameRun[]} */ runs: [],
  stepping: false,
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
  /** @type {Record<string, string>} */ activeTabs: {},
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
  if (!session) throw new Error("No game is connected. Run the project first.");
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

/** The live build of the open project: matched by game name, the pinned pid first, else the newest. */
function chooseSession() {
  const mine = state.project ? state.sessions.filter((s) => s.game === state.project?.name) : [];
  const next = mine.find((s) => s.pid === state.pinned) ?? mine[0] ?? null;
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

// ── runs: what the Studio started for the open project ─────────────────────────────────────────────────────────

/** @type {Map<string, number>} */
const reloadsSeen = new Map();
let runsTimer = 0;

/** @returns {GameRun | null} the open project's run: an active one, else the latest. */
function currentRun() {
  if (!state.project) return null;
  const mine = state.runs.filter((r) => r.game === state.project?.id);
  return mine.find((r) => ACTIVE_RUN.has(r.state)) ?? mine[mine.length - 1] ?? null;
}

/** @param {GameRun} run */
function reloadSummary(run) {
  const r = run.reload;
  if (!r) return "";
  if (!r.ok) return `Reload not done: ${r.error ?? "failed"}`;
  const same = r.match ? "the restored state is identical" : `the state differs: ${(r.differences ?? []).slice(0, 3).join("; ")}`;
  return `Reloaded at tick ${r.fromTick}: ${same}${r.sourceChanged ? " (code changed)" : ""}.`;
}

async function pollRuns() {
  clearTimeout(runsTimer);
  if (state.project) {
    try {
      const listing = await api.get("/api/games");
      state.runs = listing.runs;
      for (const run of state.runs) {
        if (run.reloads > (reloadsSeen.get(run.id) ?? 0)) {
          if (reloadsSeen.has(run.id)) log(run.reload?.ok ? "success" : "warn", "studio", reloadSummary(run));
          reloadsSeen.set(run.id, run.reloads);
        } else if (!reloadsSeen.has(run.id)) {
          reloadsSeen.set(run.id, run.reloads);
        }
      }
    } catch {
      // The session poll reports the server being gone.
    }
    renderChrome();
  }
  const busy = currentRun()?.state;
  runsTimer = window.setTimeout(pollRuns, busy && busy !== "running" ? RUNS_BUSY_INTERVAL_MS : RUNS_INTERVAL_MS);
}

/** @param {string} what @param {() => Promise<unknown>} action */
async function launcherAction(what, action) {
  try {
    await action();
  } catch (error) {
    showNotice({ level: "error", from: "Studio", text: `${what}: ${error instanceof Error ? error.message : String(error)}`, at: new Date().toISOString() });
  }
  await pollRuns();
}

const headerActions = {
  hub: () => { location.hash = "#/"; },
  settings: () => { location.hash = "#/settings"; },
  back: () => { location.hash = returnTo; },
  run: () => state.project && launcherAction("Could not run", () => api.post("/api/run", { game: state.project?.id })),
  stop: () => { const run = currentRun(); if (run) launcherAction("Could not stop", () => api.post("/api/run/stop", { run: run.id })); },
  reload: () => { const run = currentRun(); if (run) launcherAction("Could not reload", () => api.post("/api/run/reload", { run: run.id })); },
  toggle: () => transport(state.status?.paused ? "resume" : "pause"),
  /** @param {number} ticks */
  step: (ticks) => transport("step", ticks),
};

/** @param {"pause" | "resume" | "step"} cmd @param {number} [ticks] */
async function transport(cmd, ticks) {
  if (state.stepping) return;
  state.stepping = true;
  renderChrome();
  try {
    await control(cmd, ticks);
  } catch (error) {
    showNotice({ level: "error", from: "Studio", text: error instanceof Error ? error.message : String(error), at: new Date().toISOString() });
  } finally {
    state.stepping = false;
    renderChrome();
  }
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
/** @type {Map<string, { section: HTMLElement, tab: { actions: HTMLElement } | null, cleanup: () => void }>} */
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

/** @param {string} text */
const capitalized = (text) => text[0]?.toUpperCase() + text.slice(1);

/** Area names of a layout in reading order. @param {{ areas: string[] }} layout */
function areasOf(layout) {
  const seen = /** @type {string[]} */ ([]);
  for (const row of layout.areas) for (const cell of row.trim().split(/\s+/)) if (cell !== "." && !seen.includes(cell)) seen.push(cell);
  return seen;
}

/** What is on screen: grid cells (a panel, or the tabs of a group), the dock and the hidden set. */
function plan() {
  const { composition, layouts } = state.manifest;
  const layout = layouts[composition.layout];
  const hidden = new Set(composition.hide);
  const cells = areasOf(layout)
    .map((area) => ({ area, tabbed: Boolean(layout.tabs?.[area]), ids: /** @type {string[]} */ (layout.tabs?.[area] ?? [area]).filter((id) => !hidden.has(id)) }))
    .filter((cell) => cell.ids.length > 0);
  const dock = composition.show.filter((/** @type {string} */ id) => !layout.panels.includes(id) && !hidden.has(id));
  return { layout, cells, dock, hidden };
}

/** Every panel id that is mounted for real (not a placeholder). */
function visiblePanels() {
  const { cells, dock, hidden } = plan();
  const known = new Set(state.manifest.panels.map((/** @type {{ id: string }} */ p) => p.id));
  return { grid: cells.flatMap((c) => c.ids).filter((id) => known.has(id)), dock, hidden };
}

/** @param {string} id @param {HTMLElement} target @param {{ actions: HTMLElement } | null} [tab] */
function mountPanel(id, target, tab = null) {
  mounted.get(id)?.cleanup();
  const module = modules.get(id)?.module;
  const titleId = `panel-title-${id}`;
  const body = tab ? target : h("div", { class: "panel-body" });
  const actions = tab ? tab.actions : h("div", { class: "panel-actions" });
  clear(body);
  clear(actions);
  target.dataset.version = String(modules.get(id)?.version ?? "");
  if (!tab) {
    clear(target);
    target.setAttribute("aria-labelledby", titleId);
    if (module?.chrome === "bare") target.dataset.chrome = "bare";
    else delete target.dataset.chrome;
    const header = h("header", { class: "panel-head" });
    append(header, module ? icon(safeIcon(module.icon)) : icon("x"), h("h2", { id: titleId, text: module?.title ?? id }), actions);
    append(target, header, body);
  }

  if (!module) {
    append(body, h("p", { class: "panel-error", role: "alert", text: state.panelErrors[id] ?? `panels/${id}.js did not load` }));
    mounted.set(id, { section: target, tab, cleanup: () => {} });
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
    get project() { return state.project; },
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
    launcher: {
      list: () => api.get("/api/games"),
      start: (game) => api.post("/api/run", { game }),
      stop: (run) => api.post("/api/run/stop", { run }),
      reload: (run) => api.post("/api/run/reload", { run }),
    },
    on(event, handler) {
      const off = on(event, handler);
      subscriptions.push(off);
      return off;
    },
    view(patch) {
      state.panelView[id] = { ...state.panelView[id], ...patch };
      reportView();
    },
    compose: (patch) => compose(patch),
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
    section: target,
    tab,
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

/** A panel the layout names that this Studio has no file for yet: a quiet box, never an error. @param {string} id @param {HTMLElement} body */
function placeholder(id, body) {
  body.replaceChildren(emptyState({ icon: icon("layout-panel-left"), title: capitalized(id), text: "This panel is not available in this Studio." }));
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
/** The project whose panels are mounted in the grid, or null. */
let gridProject = /** @type {string | null} */ (null);

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

function unmountGrid() {
  for (const { cleanup } of mounted.values()) cleanup();
  mounted.clear();
  clear(grid);
  gridProject = null;
}

/** A tab strip in the panel header style, one panel visible at a time, the choice remembered per layout and area. @param {string} area @param {string[]} ids @param {string} layoutName */
function tabGroup(area, ids, layoutName) {
  const storageKey = `${TAB_KEY}.${layoutName}.${area}`;
  let saved = state.activeTabs[storageKey];
  try {
    saved ??= localStorage.getItem(storageKey) ?? "";
  } catch {
    // Storage blocked: the choice lasts for this page only.
  }
  let active = ids.includes(saved) ? saved : /** @type {string} */ (ids[0]);

  const section = h("section", { class: "panel tabbed", dataset: { area } });
  section.style.gridArea = area;
  const tablist = h("div", { class: "tabs", role: "tablist", "aria-label": capitalized(area) });
  const actionsHost = h("div", { class: "panel-actions" });
  const bodies = h("div", { class: "tab-bodies" });
  section.append(h("header", { class: "panel-head tabs-head" }, tablist, actionsHost), bodies);

  /** @type {Map<string, { button: HTMLElement, body: HTMLElement, actions: HTMLElement }>} */
  const parts = new Map();
  for (const id of ids) {
    const module = modules.get(id)?.module;
    const button = h("button", {
      type: "button", role: "tab", class: "tab", id: `tab-${id}`, "aria-controls": `tabpanel-${id}`, dataset: { tab: id },
      onClick: () => select(id, true),
    }, icon(module ? safeIcon(module.icon) : "layout-panel-left"), module?.title ?? capitalized(id));
    const body = h("div", { class: "panel-body tab-panel", role: "tabpanel", id: `tabpanel-${id}`, "aria-labelledby": `tab-${id}`, dataset: { panel: id } });
    const actions = h("div", { class: "panel-actions-tab" });
    tablist.append(button);
    bodies.append(body);
    actionsHost.append(actions);
    parts.set(id, { button, body, actions });
  }

  /** @param {string} id @param {boolean} [remember] */
  function select(id, remember = false) {
    active = id;
    for (const [other, part] of parts) {
      const on = other === id;
      part.button.setAttribute("aria-selected", String(on));
      part.button.tabIndex = on ? 0 : -1;
      part.body.hidden = !on;
      part.actions.hidden = !on;
    }
    if (!remember) return;
    state.activeTabs[storageKey] = id;
    try {
      localStorage.setItem(storageKey, id);
    } catch {
      // Storage blocked.
    }
    reportView();
  }
  tablist.addEventListener("keydown", (event) => {
    const keys = ["ArrowLeft", "ArrowRight", "Home", "End"];
    if (!keys.includes(event.key)) return;
    event.preventDefault();
    const index = ids.indexOf(active);
    const next = event.key === "Home" ? 0 : event.key === "End" ? ids.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + ids.length) % ids.length;
    select(/** @type {string} */ (ids[next]), true);
    parts.get(/** @type {string} */ (ids[next]))?.button.focus();
  });

  const missing = new Set(state.manifest.layouts[layoutName].missing);
  for (const [id, part] of parts) {
    if (missing.has(id) || !state.manifest.panels.some((/** @type {{ id: string }} */ p) => p.id === id)) placeholder(id, part.body);
    else mountPanel(id, part.body, { actions: part.actions });
  }
  select(active);
  return section;
}

function buildGrid() {
  for (const { cleanup } of mounted.values()) cleanup();
  mounted.clear();
  clear(grid);
  if (!state.project) return;
  gridProject = state.project.id;

  const { layout, cells, dock } = plan();
  const layoutName = state.manifest.composition.layout;
  const live = new Set(cells.map((c) => c.area));
  const areas = layout.areas.map((/** @type {string} */ row) => row.trim().split(/\s+/).map((cell) => (live.has(cell) ? cell : ".")).join(" "));
  const rows = [layout.rows];
  if (dock.length) {
    const width = areas[0].split(" ").length;
    areas.push(Array(width).fill("dock").join(" "));
    rows.push("minmax(200px, 0.5fr)");
  }
  grid.style.gridTemplateColumns = layout.columns;
  grid.style.gridTemplateRows = rows.join(" ");
  grid.style.gridTemplateAreas = areas.map((/** @type {string} */ row) => `"${row}"`).join(" ");
  grid.dataset.layout = layoutName;

  const known = new Set(state.manifest.panels.map((/** @type {{ id: string }} */ p) => p.id));
  for (const cell of cells) {
    if (cell.tabbed) {
      grid.append(tabGroup(cell.area, cell.ids, layoutName));
      continue;
    }
    const id = /** @type {string} */ (cell.ids[0]);
    const section = h("section", { class: "panel", dataset: { panel: id } });
    section.style.gridArea = id;
    grid.append(section);
    if (!known.has(id)) {
      section.append(h("header", { class: "panel-head" }, icon("layout-panel-left"), h("h2", { text: capitalized(id) })), h("div", { class: "panel-body" }));
      placeholder(id, /** @type {HTMLElement} */ (section.lastElementChild));
    } else {
      mountPanel(id, section);
    }
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

/**
 * Writes the person's composition (their own config) and applies it. Settings and panels both come through here.
 * @param {import("./types").CompositionPatch} patch
 */
async function compose(patch) {
  try {
    await api.post("/api/composition", { ...patch, target: "user" });
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
  // A broken file in the person's own config is skipped by the server; say so instead of failing silently.
  state.fileError = manifest.problems?.length ? manifest.problems.map((/** @type {{ layer: string, message: string }} */ p) => `[${p.layer}] ${p.message}`).join("; ") : null;
  document.getElementById("user-css")?.setAttribute("href", `/user.css?v=${manifest.userCss ?? 0}`);
  const { composition, layouts } = manifest;
  const shape = JSON.stringify({ layout: layouts[composition.layout], composition: { ...composition, theme: undefined, auto: undefined } });
  applyTheme();
  await loadModules();
  settingsRender?.();
  if (!state.project) {
    lastShape = shape;
    return;
  }
  if (shape !== lastShape || gridProject !== state.project.id) {
    lastShape = shape;
    buildGrid();
    return;
  }
  // Same layout: remount only panels whose file changed (or that failed and may load now).
  for (const [id, entry] of mounted) {
    const version = String(modules.get(id)?.version ?? "");
    if (version && entry.section.dataset.version === version) continue;
    mountPanel(id, entry.section, entry.tab);
  }
  renderChrome();
}

// ── routes: Hub, Project, Settings ──────────────────────────────────────────────────────────────────────────────

const views = {
  hub: /** @type {HTMLElement} */ (document.getElementById("hub")),
  settings: /** @type {HTMLElement} */ (document.getElementById("settings")),
};
let hubRender = /** @type {(() => void) | null} */ (null);
let settingsRender = /** @type {(() => void) | null} */ (null);
let returnTo = "#/";
let lastHash = "#/";

async function refreshProjects() {
  const listing = await api.get("/api/projects");
  state.projects = listing.projects;
  state.canReveal = listing.canReveal;
  state.configDir = listing.configDir;
  hubRender?.();
  settingsRender?.();
}

/** @param {string} name */
function projectNamed(name) {
  return state.projects.find((p) => p.name === name && p.kind === "game") ?? state.projects.find((p) => p.name === name) ?? null;
}

/** @param {Project} project */
async function openProject(project) {
  await api.post("/api/projects/open", { id: project.id }).catch(() => {});
  location.hash = `#/p/${encodeURIComponent(project.name)}`;
}

/** @param {Project | null} project */
function setProject(project) {
  if (project?.id === state.project?.id) return;
  state.project = project;
  state.runs = [];
  state.pinned = null;
  emit("project", project);
  chooseSession();
  if (project) pollRuns();
}

/** @param {"hub" | "project" | "settings"} route */
function show(route) {
  state.route = route;
  views.hub.hidden = route !== "hub";
  views.settings.hidden = route !== "settings";
  grid.hidden = route !== "project";
  document.documentElement.dataset.route = route;
  document.title = route === "project" && state.project ? `${state.project.name} · ukiyo Studio` : route === "settings" ? "Settings · ukiyo Studio" : "ukiyo Studio";
  renderChrome();
  reportView();
}

async function route() {
  const hash = location.hash || "#/";
  const previous = lastHash;
  lastHash = hash;
  if (hash === "#/settings") {
    if (previous !== "#/settings") returnTo = previous;
    settingsRender ??= mountSettings(views.settings, {
      manifest: () => state.manifest,
      config: () => ({ configDir: state.configDir, canReveal: state.canReveal }),
      compose,
      panelTitle: (id) => modules.get(id)?.module.title ?? capitalized(id),
      revealConfig: () => api.post("/api/config/reveal", {}),
    });
    settingsRender();
    show("settings");
    return;
  }
  const match = hash.match(/^#\/p\/(.+)$/);
  if (match) {
    const name = decodeURIComponent(/** @type {string} */ (match[1]));
    let project = projectNamed(name);
    if (!project) {
      await refreshProjects().catch(() => {});
      project = projectNamed(name);
    }
    if (!project) {
      showNotice({ level: "error", from: "Studio", text: `There is no project named ${name}.`, at: new Date().toISOString() });
      location.hash = "#/";
      return;
    }
    setProject(project);
    if (state.manifest && gridProject !== project.id) buildGrid();
    show("project");
    return;
  }
  unmountGrid();
  setProject(null);
  show("hub");
  refreshProjects().catch(() => {});
}

hubRender = mountHub(views.hub, {
  projects: () => state.projects,
  canReveal: () => state.canReveal,
  open: (project) => { openProject(project); },
  create: async (input) => {
    const { project } = await api.post("/api/projects/new", input);
    await refreshProjects().catch(() => {});
    return project;
  },
  reveal: async (project) => { await api.post("/api/projects/reveal", { id: project.id }); },
});

addEventListener("hashchange", () => { route(); });

// ── chrome: header, status bar, notices ─────────────────────────────────────────────────────────────────────────

const chrome = {
  statusbar: /** @type {HTMLElement} */ (document.getElementById("statusbar")),
  notices: /** @type {HTMLElement} */ (document.getElementById("notices")),
};

const header = createHeader({
  start: /** @type {HTMLElement} */ (document.getElementById("header-start")),
  center: /** @type {HTMLElement} */ (document.getElementById("header-center")),
  end: /** @type {HTMLElement} */ (document.getElementById("header-end")),
}, headerActions);

/** @returns {import("./header.js").HeaderModel} */
function headerModel() {
  const project = state.project;
  const run = currentRun();
  const active = run && ACTIVE_RUN.has(run.state) ? run : null;
  const attached = Boolean(state.session);
  const busy = Boolean(active && (!attached || active.state !== "running"));
  const mode = busy ? "busy" : attached ? "attached" : "idle";
  const status = state.status;
  const stateText = busy
    ? (active?.detail || `${capitalized(active?.state ?? "")}…`)
    : attached
      ? (status ? `${status.paused ? "Paused" : "Running"} · tick ${status.tick}` : "Connecting…")
      : "";
  /** @type {{ label: string, lines: string[] } | null} */
  let problem = null;
  if (run && !active && run.state === "failed") problem = { label: "Build failed", lines: run.errors.length ? run.errors : run.output.slice(-8) };
  else if (run?.reload && !run.reload.ok && !busy) problem = { label: "Reload failed", lines: run.reload.errors?.length ? run.reload.errors : [run.reload.error ?? "failed"] };
  return {
    route: state.route, projectName: project?.name ?? null, online: state.serverOnline,
    mode, paused: status?.paused ?? false, owned: Boolean(active), canRun: Boolean(project?.bridge) && state.serverOnline,
    runHint: project && !project.bridge ? "This host does not link the dev bridge, so the Studio could not attach" : "",
    stateText, problem, stepping: state.stepping,
  };
}

function renderChrome() {
  header.render(headerModel());

  // One quiet line: what is rendering and at what size, dropped ticks, bridge latency. Errors anywhere.
  const items = [];
  if (state.route === "project") {
    const { session, status } = state;
    if (status) {
      items.push(h("span", { text: `${status.backend} · ${status.width}×${status.height}`, title: status.renderer }));
      if (status.droppedTicks > 0) items.push(h("span", { class: "statusbar-warn", text: `${status.droppedTicks} ticks dropped` }));
    } else {
      items.push(h("span", { text: session ? "Connecting to the game" : "Not running" }));
    }
    if (state.bridgeMs !== null && session) items.push(h("span", { class: "push", text: `${state.bridgeMs} ms`, title: "Bridge round trip" }));
  }
  clear(chrome.statusbar);
  for (const item of items) chrome.statusbar.append(item);
  if (state.fileError) chrome.statusbar.append(h("span", { class: "statusbar-error", role: "alert", text: state.fileError }));
  chrome.statusbar.hidden = chrome.statusbar.childElementCount === 0;
}

/** @param {import("./types").Notice} notice */
function showNotice(notice) {
  const close = h("button", { type: "button", class: "icon-button", "aria-label": "Dismiss" }, icon("x"));
  const card = h("div", { class: "notice", dataset: { level: notice.level }, role: notice.level === "error" ? "alert" : "status" },
    h("span", { class: "notice-from" }, icon("send"), notice.from),
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
      route: state.route,
      project: state.project ? { name: state.project.name, kind: state.project.kind, path: state.project.path } : null,
      layout: state.manifest.composition.layout,
      theme: state.themeName,
      panels: state.route === "project" ? [...inGrid, ...dock] : [],
      hidden: [...hidden],
      activeTabs: state.activeTabs,
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
    grid.hidden = false;
    grid.append(h("p", { class: "fatal", role: "alert", text: `The Studio files are invalid: ${message}. Fix the file named above; the page updates when it is valid.` }));
    renderChrome();
  }
  await refreshProjects().catch(() => {});
  // A game that is already running opens its own project, once, when the Studio starts at the Hub.
  if (!location.hash) {
    try {
      const { sessions } = await api.get("/api/sessions");
      const running = sessions.map((/** @type {LiveSession} */ s) => projectNamed(s.game)).find(Boolean);
      if (running) location.hash = `#/p/${encodeURIComponent(running.name)}`;
    } catch {
      // No sessions yet: the Hub it is.
    }
  }
  listen();
  await route();
  pollSessions();
  pollStatus();
}

start();
