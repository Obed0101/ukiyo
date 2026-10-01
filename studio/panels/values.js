// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { formatNumber } from "../web/dom.js";

/**
 * The named numbers the game puts in its snapshot (GameSnapshot.Values: score, lives, state…), with the change since
 * the previous snapshot. Options: { "pin": ["score", "lives"] } shows those first, in that order.
 * @type {PanelModule}
 */
export default {
  id: "values",
  title: "Values",
  icon: "gauge",
  description: "Snapshot values (score, lives, state…) with their last change.",
  mount(root, studio) {
    const { h } = studio;
    const pin = Array.isArray(studio.options.pin) ? studio.options.pin.map(String) : [];
    /** @type {Record<string, number>} */
    let previous = {};
    let previousTick = -1;

    function render() {
      const snapshot = studio.snapshot;
      root.replaceChildren();
      if (!studio.session) {
        root.append(h("p", { class: "empty", text: "Values appear when a game is connected." }));
        return;
      }
      const values = snapshot?.values ?? {};
      const names = Object.keys(values).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
      if (!snapshot) {
        root.append(h("p", { class: "empty", text: "Waiting for the first snapshot." }));
        return;
      }
      if (names.length === 0) {
        root.append(h("p", { class: "empty" }, "This game reports no values. Add them in ", h("code", { text: "Snapshot()" }), ": ", h("code", { text: "Values = { [\"score\"] = _score }" }), "."));
        return;
      }

      const body = h("tbody");
      for (const name of names) {
        const value = values[name] ?? 0;
        const before = previous[name];
        const delta = before === undefined || snapshot.tick === previousTick ? 0 : value - before;
        body.append(h("tr", null,
          h("td", { text: name }),
          h("td", { class: "num", text: formatNumber(value) }),
          h("td", { class: "num muted", text: delta === 0 ? "" : `${delta > 0 ? "+" : ""}${formatNumber(delta)}` })));
      }
      root.append(h("table", { class: "table" },
        h("thead", null, h("tr", null, h("th", { text: "Name" }), h("th", { class: "num", text: "Value" }), h("th", { class: "num", text: "Change" }))),
        body));
      if (snapshot.tick !== previousTick) {
        previous = { ...values };
        previousTick = snapshot.tick;
      }
    }

    /** @param {string} name */
    function rank(name) {
      const index = pin.indexOf(name);
      return index < 0 ? pin.length : index;
    }

    studio.on("snapshot", render);
    studio.on("session", () => {
      previous = {};
      previousTick = -1;
      render();
    });
    render();
  },
};
