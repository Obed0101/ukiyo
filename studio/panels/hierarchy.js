// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { emptyState } from "../web/dom.js";
import { flatIds, groupEntities, stepId } from "../web/hierarchy-model.js";
import { loadSceneEntities, projectName, useStyles } from "../web/panel-kit.js";
import { selection } from "../web/selection.js";

/**
 * The entities of the running game: grouped under the active scene when the game has a SceneRegistry (with each
 * entity's prefab, read from the scene file), otherwise a flat sorted list. Selection is shared with the Inspector and
 * reported to agents (ukiyo_studio view → panelState.hierarchy.selected). Up/Down move, Enter selects.
 * @type {PanelModule}
 */
export default {
  id: "hierarchy",
  title: "Hierarchy",
  icon: "folder-tree",
  description: "The running game's entities, grouped by scene; selection is shared with the Inspector and with agents.",
  mount(root, studio) {
    const { h, icon } = studio;
    useStyles();
    const filter = /** @type {HTMLInputElement} */ (h("input", { class: "field", type: "search", placeholder: "Filter", "aria-label": "Filter entities" }));
    const list = h("div", { class: "pk-scroll", role: "listbox", "aria-label": "Entities" });
    root.classList.add("pk");
    root.append(filter, list);

    /** @type {{ id: string, prefab?: string }[] | null} */
    let sceneEntities = null;
    let sceneKey = "";

    function report() {
      const name = selection.get();
      studio.view({ selected: name, pose: (name && studio.snapshot?.entities[name]) || null });
    }

    function render() {
      const focused = /** @type {HTMLElement | null} */ (list.querySelector(":focus"))?.dataset.entity;
      list.replaceChildren();
      const snapshot = studio.snapshot;
      filter.disabled = !snapshot;
      if (!studio.session || !snapshot) {
        list.append(emptyState({ icon: icon("folder-tree"), title: "No entities yet", text: studio.session ? "Waiting for the first snapshot." : "Entities appear when a game is running." }));
        return;
      }
      const groups = groupEntities(snapshot, { query: filter.value, sceneEntities });
      if (groups.length === 0 || groups[0]?.rows.length === 0) {
        list.append(emptyState({ icon: icon("folder-tree"), title: filter.value ? `Nothing matches "${filter.value}"` : "This game reports no entities" }));
        return;
      }
      const selected = selection.get();
      for (const group of groups) {
        if (group.scene) list.append(h("div", { class: "pk-group" }, icon("layers"), group.scene, ` · ${group.rows.length}`));
        for (const row of group.rows) {
          const item = h("div", { class: "pk-row", role: "option", tabindex: "0", "aria-selected": String(row.id === selected), dataset: { entity: row.id } },
            h("span", { class: "pk-name", text: row.id }),
            row.prefab ? h("span", { class: "pk-meta", text: row.prefab }) : null);
          item.addEventListener("click", () => selection.set(row.id));
          list.append(item);
        }
      }
      if (focused) /** @type {HTMLElement | undefined} */ ([...list.querySelectorAll("[data-entity]")].find((el) => /** @type {HTMLElement} */ (el).dataset.entity === focused))?.focus();
    }

    list.addEventListener("keydown", (event) => {
      const key = event.key;
      if (key !== "ArrowDown" && key !== "ArrowUp" && key !== "Enter" && key !== " ") return;
      event.preventDefault();
      const current = /** @type {HTMLElement | null} */ (document.activeElement)?.dataset?.entity ?? null;
      if (key === "Enter" || key === " ") {
        if (current) selection.set(current);
        return;
      }
      const snapshot = studio.snapshot;
      if (!snapshot) return;
      const next = stepId(flatIds(groupEntities(snapshot, { query: filter.value, sceneEntities })), current, key === "ArrowDown" ? 1 : -1);
      if (!next) return;
      /** @type {HTMLElement | undefined} */ ([...list.querySelectorAll("[data-entity]")].find((el) => /** @type {HTMLElement} */ (el).dataset.entity === next))?.focus();
    });

    async function refreshScene() {
      const project = projectName(studio);
      const scene = studio.snapshot?.scene?.active;
      const key = project && scene ? `${project}\0${scene}` : "";
      if (key === sceneKey) return;
      sceneKey = key;
      sceneEntities = project && scene ? await loadSceneEntities(project, scene) : null;
      render();
    }

    const stops = [
      selection.subscribe(() => {
        report();
        render();
      }),
      studio.on("snapshot", () => {
        render();
        refreshScene();
      }),
      studio.on("session", () => {
        selection.set(null);
        sceneKey = "";
        sceneEntities = null;
        render();
      }),
    ];
    filter.addEventListener("input", render);
    render();
    refreshScene();
    return () => stops.forEach((stop) => stop());
  },
};
