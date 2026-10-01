// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

/**
 * Running development builds (each writes ~/.ukiyo/live/<pid>.json). The newest is followed automatically unless the
 * person pins one here.
 * @type {PanelModule}
 */
export default {
  id: "sessions",
  title: "Games",
  icon: "activity",
  description: "Running development builds; pick which one the Studio follows.",
  mount(root, studio) {
    const { h } = studio;
    let signature = "";

    function render() {
      const sessions = studio.sessions;
      // The list is polled every 2 s; rebuilding identical buttons would steal keyboard focus.
      const next = JSON.stringify([sessions.map((s) => s.pid), studio.session?.pid ?? null]);
      if (next === signature) return;
      signature = next;
      const focusedPid = /** @type {HTMLElement | null} */ (root.querySelector(":focus"))?.dataset.pid;
      root.replaceChildren();
      if (sessions.length === 0) {
        root.append(h("p", { class: "empty" },
          "No development build is running. Start one and it appears here within two seconds: ",
          h("code", { text: "dotnet run --project samples/LanternRun/Desktop" })));
        return;
      }
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
      if (focusedPid) /** @type {HTMLElement | null} */ (root.querySelector(`[data-pid="${focusedPid}"]`))?.focus();
    }

    studio.on("sessions", render);
    studio.on("session", render);
    render();
  },
};
