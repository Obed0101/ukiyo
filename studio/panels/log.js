// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

/**
 * What happened, newest first: bridge commands, Studio file changes (an agent editing a panel or the layout shows up
 * here), notices agents sent, and errors. Options: { "limit": 300 }.
 * @type {PanelModule}
 */
export default {
  id: "log",
  title: "Activity",
  icon: "scroll-text",
  description: "Bridge commands, Studio file changes, agent notices and errors.",
  mount(root, studio) {
    const { h, icon } = studio;
    const limit = Math.min(2000, Math.max(50, Number(studio.options.limit) || 300));
    const list = h("ol", { class: "log", reversed: true, "aria-live": "off" });
    const clearButton = h("button", { type: "button", class: "icon-button", "aria-label": "Clear the activity log", title: "Clear" }, icon("x"));
    studio.actions.append(clearButton);
    root.append(list);
    const empty = h("p", { class: "empty", text: "Nothing yet. Commands, file changes and agent notices appear here." });
    root.append(empty);

    /** @param {import("../web/types").LogEntry} entry */
    function add(entry) {
      empty.remove();
      const time = new Date(entry.at).toLocaleTimeString([], { hour12: false });
      list.prepend(h("li", { class: "log-entry", dataset: { level: entry.level } },
        h("span", { class: "mono muted", text: time }),
        h("span", { class: "log-source", text: entry.source }),
        h("span", { class: "log-text", text: entry.text })));
      while (list.childElementCount > limit) list.lastElementChild?.remove();
    }

    for (const entry of studio.history) add(entry);
    studio.on("log", add);

    clearButton.addEventListener("click", () => {
      list.replaceChildren();
      root.append(empty);
    });
  },
};
