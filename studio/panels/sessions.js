// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
/** @typedef {import("../web/types").GameEntry} GameEntry */
/** @typedef {import("../web/types").GameRun} GameRun */

const ACTIVE = new Set(["building", "starting", "running", "reloading", "stopping"]);

/**
 * Games: the ones the Studio can run from this repository (Run, Reload, Stop), and the development builds that are
 * running (each writes ~/.ukiyo/live/<pid>.json). The newest running build is followed unless the person pins one.
 * @type {PanelModule}
 */
export default {
  id: "sessions",
  title: "Games",
  icon: "activity",
  description: "Run a game from the repository, reload it at the same tick, stop it, and pick which running build the Studio follows.",
  mount(root, studio) {
    const { h, icon } = studio;
    /** @type {GameEntry[]} */
    let games = [];
    /** @type {GameRun[]} */
    let runs = [];
    let loadError = "";
    let signature = "";
    let timer = 0;
    let disposed = false;

    /** The latest run of a game, preferring one that is still active. @param {string} id */
    function runOf(id) {
      const mine = runs.filter((r) => r.game === id);
      return mine.find((r) => ACTIVE.has(r.state)) ?? mine[mine.length - 1] ?? null;
    }

    /** @param {() => Promise<unknown>} action */
    async function act(action) {
      try {
        await action();
        loadError = "";
      } catch (error) {
        loadError = error instanceof Error ? error.message : String(error);
      }
      await refresh();
    }

    /** @param {GameRun} run */
    function reloadSummary(run) {
      const r = run.reload;
      if (!r) return null;
      if (!r.ok) return `Reload not done: ${r.error ?? "failed"}`;
      const where = `tick ${r.fromTick}`;
      if (r.match) return `Reloaded at ${where}: the restored state is identical${r.sourceChanged ? " (code changed)" : ""}.`;
      return `Reloaded at ${where}: the state differs: ${(r.differences ?? []).slice(0, 3).join("; ")}`;
    }

    /** @param {GameEntry} game */
    function gameRow(game) {
      const run = runOf(game.id);
      const active = run && ACTIVE.has(run.state) ? run : null;
      const busy = active !== null && active.state !== "running";
      const buttons = h("span", { class: "row" });
      if (!active) {
        buttons.append(h("button", {
          type: "button", class: "button", "aria-label": `Run ${game.name}`, disabled: !game.bridge,
          onClick: () => act(() => studio.launcher.start(game.id)),
        }, icon("play"), "Run"));
      } else {
        buttons.append(
          h("button", {
            type: "button", class: "button", "aria-label": `Reload ${game.name} at the same tick`, disabled: busy,
            onClick: () => act(() => studio.launcher.reload(active.id)),
          }, icon("refresh-cw"), "Reload"),
          h("button", {
            type: "button", class: "button", "aria-label": `Stop ${game.name}`, disabled: active.state === "stopping",
            onClick: () => act(() => studio.launcher.stop(active.id)),
          }, icon("x"), "Stop"));
      }

      const lines = [];
      if (run) {
        const summary = run.state === "failed" ? run.detail : active ? `${run.state}: ${run.detail}` : run.reload ? "" : run.state === "stopped" ? "Stopped" : "";
        if (summary) lines.push(h("p", { class: "muted", text: summary }));
        const reload = reloadSummary(run);
        if (reload) lines.push(h("p", { class: run.reload && !run.reload.ok ? "panel-error" : "muted", text: reload }));
        const problems = run.state === "failed" ? (run.errors.length ? run.errors : run.output.slice(-8)) : run.reload && !run.reload.ok ? run.reload.errors ?? [] : [];
        if (problems.length) lines.push(h("pre", { class: "script mono", role: "alert", "aria-label": `${game.name} errors`, text: problems.join("\n") }));
      } else if (!game.bridge) {
        lines.push(h("p", { class: "muted", text: "No dev bridge in this host, so the Studio could not attach." }));
      }

      return h("li", null,
        h("div", { class: "row" },
          h("span", { class: "session-game", text: game.name }),
          h("span", { class: "mono muted", text: game.kind }),
          buttons),
        ...lines);
    }

    function render() {
      const sessions = studio.sessions;
      // Both lists are polled; rebuilding identical buttons would steal keyboard focus.
      const next = JSON.stringify([games, runs.map((r) => [r.id, r.state, r.detail, r.reload, r.errors.length]), sessions.map((s) => s.pid), studio.session?.pid ?? null, loadError]);
      if (next === signature) return;
      signature = next;
      const focused = /** @type {HTMLElement | null} */ (root.querySelector(":focus"));
      const focusKey = focused?.getAttribute("aria-label") ?? focused?.dataset.pid;
      root.replaceChildren();

      if (loadError) root.append(h("p", { class: "panel-error", role: "alert", text: loadError }));
      if (games.length > 0) {
        root.append(h("p", { class: "muted", text: "Run" }), h("ul", { class: "session-list", role: "list" }, ...games.map(gameRow)));
      }

      root.append(h("p", { class: "muted", text: "Running" }));
      if (sessions.length === 0) {
        root.append(h("p", { class: "empty" },
          games.length > 0 ? "Nothing is running. Run a game above; it appears here once it has started." : "No development build is running. Start one and it appears here within two seconds: ",
          games.length > 0 ? "" : h("code", { text: "dotnet run --project samples/LanternRun/Desktop" })));
      } else {
        const list = h("ul", { class: "session-list", role: "list" });
        for (const session of sessions) {
          const current = studio.session?.pid === session.pid;
          const started = new Date(session.startedAt);
          list.append(h("li", null,
            h("button", {
              type: "button",
              class: "session",
              "aria-pressed": String(current),
              dataset: { pid: String(session.pid) },
              onClick: () => studio.selectSession(current ? null : session.pid),
            },
            h("span", { class: "session-game", text: session.game }),
            h("span", { class: "mono muted", text: `${session.target} · pid ${session.pid} · ${started.toLocaleTimeString()}` }))));
        }
        root.append(list, h("p", { class: "muted", text: "Selecting a game pins it; select it again to follow the newest build." }));
      }

      if (focusKey) {
        const candidates = /** @type {NodeListOf<HTMLElement>} */ (root.querySelectorAll("button"));
        [...candidates].find((b) => b.getAttribute("aria-label") === focusKey || b.dataset.pid === focusKey)?.focus();
      }
    }

    async function refresh() {
      try {
        const listing = await studio.launcher.list();
        games = listing.games;
        runs = listing.runs;
      } catch (error) {
        loadError = error instanceof Error ? error.message : String(error);
      }
      if (disposed) return;
      render();
      clearTimeout(timer);
      timer = window.setTimeout(refresh, runs.some((r) => ACTIVE.has(r.state) && r.state !== "running") ? 700 : 2500);
    }

    studio.on("sessions", render);
    studio.on("session", render);
    render();
    refresh();
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  },
};
