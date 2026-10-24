// @ts-check
// The Hub: where the Studio starts. A plain list of the projects in this repository, newest-opened first, and the way
// to make a new one. No project is open here, so no panels and no game connection.
import { openDialog } from "./dialog.js";
import { clear, emptyState, h, relativeTime } from "./dom.js";
import { icon } from "./icons.js";
import { openMenu } from "./menu.js";

/** @typedef {import("./types").Project} Project */

/** Same rule as the ukiyo_new_game tool (tools/mcp/src/tools/new-game.ts); the server re-checks and its text wins. */
export const NAME_PATTERN = /^[A-Z][A-Za-z0-9]{1,39}$/;
const NAME_HINT = "Use PascalCase: a capital letter, then letters and digits, 2 to 40 characters.";

const FILTERS = [["all", "All"], ["game", "Games"], ["sample", "Samples"]];
let filter = "all";
let query = "";

/** @param {number} count @param {string} noun */
const plural = (count, noun) => `${count} ${noun}${count === 1 ? "" : "s"}`;

/**
 * @param {HTMLElement} root
 * @param {{
 *   projects: () => Project[], canReveal: () => boolean, open: (project: Project) => void,
 *   create: (input: { name: string, template: string }) => Promise<Project>,
 *   reveal: (project: Project) => Promise<void>,
 * }} hub
 * @returns {() => void} render, to call when the list changes
 */
export function mountHub(root, hub) {
  const render = () => {
    const focusedKey = /** @type {HTMLElement | null} */ (document.activeElement)?.closest?.("#hub")?.querySelector(":focus")?.getAttribute("data-key");
    const projects = hub.projects();
    const games = projects.filter((p) => p.kind === "game").length;
    const shown = projects
      .filter((p) => filter === "all" || p.kind === filter)
      .filter((p) => !query || `${p.name} ${p.path}`.toLowerCase().includes(query.toLowerCase()));

    const newButton = h("button", { type: "button", class: "button primary", onClick: () => newProjectDialog(hub) }, icon("plus"), "New project");
    const head = h("div", { class: "page-head" },
      h("div", null, h("h1", { class: "page-title", text: "Projects" }), h("p", { class: "page-summary muted", text: `${plural(games, "game")} · ${plural(projects.length - games, "sample")}` })),
      newButton);

    const children = [head];
    if (projects.length === 0) {
      children.push(emptyState({
        icon: icon("folder-plus"), title: "No projects yet", text: "Create a game to start.",
        action: h("button", { type: "button", class: "button primary", onClick: () => newProjectDialog(hub) }, icon("plus"), "New project"),
      }));
    } else {
      const search = /** @type {HTMLInputElement} */ (h("input", { type: "search", class: "field search", placeholder: "Search projects", "aria-label": "Search projects", value: query, "data-key": "search" }));
      search.addEventListener("input", () => {
        query = search.value;
        render();
        /** @type {HTMLInputElement | null} */ (root.querySelector("[data-key=search]"))?.focus();
      });
      const segmented = h("div", { class: "segmented", role: "group", "aria-label": "Show" }, ...FILTERS.map(([value, label]) =>
        h("button", { type: "button", class: "segment", "aria-pressed": String(filter === value), text: label, onClick: () => { filter = /** @type {string} */ (value); render(); } })));
      children.push(h("div", { class: "toolbar" }, segmented, h("label", { class: "search-wrap" }, icon("search"), search)));
      children.push(shown.length === 0
        ? emptyState({ icon: icon("search"), title: "No matches", text: query ? `Nothing named “${query}”.` : "Nothing in this view." })
        : h("ul", { class: "project-list", role: "list" }, ...shown.map((project) => row(project, hub))));
    }
    root.replaceChildren(h("div", { class: "page" }, ...children));
    if (focusedKey) /** @type {HTMLElement | null} */ (root.querySelector(`[data-key="${focusedKey}"]`))?.focus();
  };
  root.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    const rows = /** @type {HTMLElement[]} */ ([...root.querySelectorAll(".project-open")]);
    const index = rows.indexOf(/** @type {HTMLElement} */ (document.activeElement));
    if (index < 0 && event.key === "ArrowUp") return;
    event.preventDefault();
    rows[Math.max(0, Math.min(rows.length - 1, index + (event.key === "ArrowDown" ? 1 : -1)))]?.focus();
  });
  render();
  return render;
}

/** @param {Project} project @param {Parameters<typeof mountHub>[1]} hub */
function row(project, hub) {
  const more = h("button", { type: "button", class: "icon-button", "aria-label": `More actions for ${project.name}`, "aria-haspopup": "menu", onClick: () => {
    openMenu(more, [
      ...(hub.canReveal() ? [{ label: "Reveal in Finder", icon: "folder-open", onSelect: () => hub.reveal(project).catch(() => {}) }] : []),
      { label: "Copy path", icon: "copy", onSelect: () => navigator.clipboard?.writeText(project.dir).catch(() => {}) },
    ], { align: "end" });
  } }, icon("ellipsis"));
  return h("li", { class: "project-row" },
    h("button", { type: "button", class: "project-open", "data-key": project.id, onClick: () => hub.open(project) },
      h("span", { class: "project-name", text: project.name }),
      h("span", { class: "project-kind muted", text: `${project.template} · ${project.kind}` }),
      h("span", { class: "project-path muted mono", text: project.path }),
      h("span", { class: "project-time muted", text: relativeTime(project.lastOpened) || "—" })),
    more);
}

/** @param {Parameters<typeof mountHub>[1]} hub */
function newProjectDialog(hub) {
  let template = "2d";
  const name = /** @type {HTMLInputElement} */ (h("input", { type: "text", class: "field", id: "new-project-name", autocomplete: "off", spellcheck: "false", placeholder: "SkyLantern", maxlength: "40" }));
  const hint = h("p", { class: "field-hint muted", id: "new-project-hint", text: NAME_HINT });
  name.setAttribute("aria-describedby", "new-project-hint");
  const location = h("code", { class: "location mono", text: "games/" });
  const error = h("p", { class: "field-error", role: "alert", hidden: true });
  const templates = h("div", { class: "segmented", role: "group", "aria-label": "Template" });
  const draw = () => {
    clear(templates);
    for (const value of ["2d", "3d"]) {
      templates.append(h("button", { type: "button", class: "segment", "aria-pressed": String(template === value), text: value.toUpperCase(), onClick: () => { template = value; draw(); } }));
    }
  };
  draw();

  const cancel = h("button", { type: "button", class: "button", text: "Cancel", onClick: () => dialog.close() });
  const create = /** @type {HTMLButtonElement} */ (h("button", { type: "submit", class: "button primary", text: "Create project", form: "new-project-form" }));
  const form = h("form", { id: "new-project-form", class: "form", novalidate: true },
    h("div", { class: "form-row" }, h("label", { for: "new-project-name", text: "Name" }), name, hint),
    h("div", { class: "form-row" }, h("span", { class: "label", text: "Template" }), templates),
    h("div", { class: "form-row" }, h("span", { class: "label", text: "Location" }), location),
    error);

  const valid = () => NAME_PATTERN.test(name.value);
  const sync = () => {
    location.textContent = `games/${name.value}`;
    create.disabled = !valid();
    const bad = name.value.length > 0 && !valid();
    name.toggleAttribute("aria-invalid", bad);
    hint.classList.toggle("field-error", bad);
    error.hidden = true;
  };
  name.addEventListener("input", sync);
  sync();

  const dialog = openDialog({ title: "New project", content: form, actions: [cancel, create] });
  name.focus();
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!valid()) return;
    create.disabled = true;
    create.textContent = "Creating…";
    try {
      const project = await hub.create({ name: name.value, template });
      dialog.close();
      hub.open(project);
    } catch (failure) {
      error.textContent = failure instanceof Error ? failure.message : String(failure);
      error.hidden = false;
      create.textContent = "Create project";
      create.disabled = !valid();
    }
  });
}
