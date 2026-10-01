// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { formatNumber } from "../web/dom.js";

/**
 * Entity poses from the snapshot. Selecting one tells agents which entity the person is looking at
 * (ukiyo_studio view → panelState.entities.selected), so "make this one jump higher" has a referent.
 * @type {PanelModule}
 */
export default {
  id: "entities",
  title: "Entities",
  icon: "list-tree",
  description: "Entity positions, rotations and scales from the snapshot; selection is shared with agents.",
  mount(root, studio) {
    const { h } = studio;
    const filter = /** @type {HTMLInputElement} */ (h("input", { class: "field", type: "search", placeholder: "Filter entities", "aria-label": "Filter entities" }));
    const list = h("div");
    root.classList.add("stack");
    root.append(filter, list);
    /** @type {string | null} */
    let selected = null;

    function render() {
      // Snapshots arrive several times a second; keep keyboard focus on the same entity across rebuilds.
      const focused = /** @type {HTMLElement | null} */ (list.querySelector(":focus"))?.dataset.entity;
      list.replaceChildren();
      const snapshot = studio.snapshot;
      if (!studio.session || !snapshot) {
        list.append(h("p", { class: "empty", text: studio.session ? "Waiting for the first snapshot." : "Entities appear when a game is connected." }));
        return;
      }
      const query = filter.value.trim().toLowerCase();
      const names = Object.keys(snapshot.entities).filter((name) => name.toLowerCase().includes(query)).sort();
      if (names.length === 0) {
        list.append(h("p", { class: "empty", text: query ? `No entity matches "${filter.value}".` : "This game reports no entities in its snapshot." }));
        return;
      }

      const body = h("tbody");
      for (const name of names) {
        const pose = snapshot.entities[name];
        if (!pose) continue;
        const [x, y, z] = pose.position;
        const angle = 2 * Math.atan2(pose.rotation[2], pose.rotation[3]);
        const isSelected = name === selected;
        const row = h("tr", { "aria-selected": String(isSelected), tabindex: "0", class: isSelected ? "selected" : "", dataset: { entity: name } },
          h("td", { text: name }),
          h("td", { class: "num", text: `${formatNumber(x)}, ${formatNumber(y)}${z === 0 ? "" : `, ${formatNumber(z)}`}` }),
          h("td", { class: "num", text: `${formatNumber((angle * 180) / Math.PI)}°` }));
        const choose = () => select(isSelected ? null : name);
        row.addEventListener("click", choose);
        row.addEventListener("keydown", (event) => {
          if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            choose();
          }
        });
        body.append(row);
      }
      list.append(h("table", { class: "table entity-table", "aria-label": "Entities" },
        h("thead", null, h("tr", null, h("th", { text: "Entity" }), h("th", { class: "num", text: "Position" }), h("th", { class: "num", text: "Angle (z)" }))),
        body));
      if (focused) [...body.querySelectorAll("tr")].find((row) => row.dataset.entity === focused)?.focus();
    }

    /** @param {string | null} name */
    function select(name) {
      selected = name;
      const pose = name ? studio.snapshot?.entities[name] : null;
      studio.view({ selected: name, pose: pose ?? null });
      render();
    }

    filter.addEventListener("input", render);
    studio.on("snapshot", render);
    studio.on("session", () => select(null));
    render();
  },
};
