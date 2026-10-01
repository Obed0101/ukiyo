// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

/**
 * Which panels are on screen. Toggling one writes studio/studio.json (hide/show), the same file agents edit, so a
 * person and an agent always see one composition. Panels outside the layout's grid go to a dock row at the bottom.
 * @type {PanelModule}
 */
export default {
  id: "composer",
  title: "Panels",
  icon: "layout-panel-left",
  description: "Show or hide panels; the choice is saved to studio/studio.json.",
  mount(root, studio) {
    const { h } = studio;
    const { panels, layouts, composition } = studio.manifest;
    const layout = layouts[composition.layout];
    const inLayout = new Set(layout?.panels ?? []);
    const hidden = new Set(composition.hide);
    const shown = new Set(composition.show);
    const error = h("p", { class: "panel-error", role: "alert" });

    const list = h("ul", { class: "toggle-list", role: "list" });
    for (const { id } of panels) {
      const visible = inLayout.has(id) ? !hidden.has(id) : shown.has(id) && !hidden.has(id);
      const checkbox = /** @type {HTMLInputElement} */ (h("input", { type: "checkbox", id: `compose-${id}`, checked: visible, disabled: id === studio.id }));
      checkbox.addEventListener("change", () => toggle(id, checkbox.checked));
      list.append(h("li", null,
        checkbox,
        h("label", { for: `compose-${id}` }, studio.panelTitle(id), h("span", { class: "muted", text: inLayout.has(id) ? "" : " · dock" }))));
    }
    root.classList.add("stack");
    root.append(list, error, h("p", { class: "muted" }, "Saved to ", h("code", { class: "mono", text: "studio/studio.json" }), ". Agents read and edit the same file."));

    /** @param {string} id @param {boolean} visible */
    async function toggle(id, visible) {
      const hide = new Set(composition.hide);
      const show = new Set(composition.show);
      if (visible) {
        hide.delete(id);
        if (!inLayout.has(id)) show.add(id);
      } else {
        show.delete(id);
        if (inLayout.has(id)) hide.add(id);
      }
      try {
        await studio.compose({ hide: [...hide], show: [...show] });
      } catch (failure) {
        error.textContent = failure instanceof Error ? failure.message : String(failure);
      }
    }
  },
};
