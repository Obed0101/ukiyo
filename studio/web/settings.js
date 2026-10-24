// @ts-check
// Settings: a route inside the shell, not a modal. Plain sections in one narrow column. Every change is a composition
// patch written to the person's own config (target "user"), the same files an agent or a text editor changes.
import { h } from "./dom.js";
import { icon } from "./icons.js";

const LAYOUT_ORDER = ["editor", "simple", "focus", "balanced", "wide", "complete"];
const RICE_URL = "https://github.com/Obed0101/ukiyo/tree/main/studio/examples/rice";

/** @param {string} text */
const title = (text) => text[0]?.toUpperCase() + text.slice(1);

/**
 * @param {HTMLElement} root
 * @param {{
 *   manifest: () => any, config: () => { configDir: string | null, canReveal: boolean },
 *   compose: (patch: import("./types").CompositionPatch) => Promise<void>,
 *   panelTitle: (id: string) => string, revealConfig: () => Promise<void>,
 * }} settings
 * @returns {() => void} render, to call when the manifest changes
 */
export function mountSettings(root, settings) {
  let failure = "";

  /** @param {import("./types").CompositionPatch} patch */
  const change = async (patch) => {
    try {
      failure = "";
      await settings.compose(patch);
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
      render();
    }
  };

  const render = () => {
    const manifest = settings.manifest();
    if (!manifest) return;
    const focused = /** @type {HTMLElement | null} */ (root.querySelector(":focus"));
    const focusKey = focused?.dataset.key;
    root.replaceChildren(h("div", { class: "page" },
      failure ? h("p", { class: "field-error", role: "alert", text: failure }) : null,
      appearance(manifest, change),
      layouts(manifest, change),
      panels(manifest, settings, change),
      config(settings)));
    if (focusKey) /** @type {HTMLElement | null} */ (root.querySelector(`[data-key="${focusKey}"]`))?.focus();
  };
  render();
  return render;
}

/** @param {string} heading @param {string | null} note @param {...Node | null} body */
function section(heading, note, ...body) {
  return h("section", { class: "settings-section", "aria-label": heading },
    h("h2", { class: "settings-heading", text: heading }),
    note ? h("p", { class: "muted settings-note", text: note }) : null,
    ...body);
}

/** @param {string} key @param {boolean} selected @param {string} name @param {string} description @param {Node | null} lead @param {() => void} onSelect */
function choice(key, selected, name, description, lead, onSelect) {
  return h("button", { type: "button", role: "radio", class: "choice", "aria-checked": String(selected), "data-key": key, onClick: onSelect },
    lead,
    h("span", { class: "choice-text" }, h("span", { class: "choice-name", text: name }), description ? h("span", { class: "choice-description muted", text: description }) : null),
    selected ? icon("check") : null);
}

/** @param {any} manifest @param {(patch: import("./types").CompositionPatch) => Promise<void>} change */
function appearance(manifest, change) {
  const { composition, themes } = manifest;
  const auto = composition.theme === "auto";
  const names = Object.keys(themes).sort();
  const byScheme = (/** @type {string} */ scheme) => names.filter((n) => themes[n].colorScheme === scheme);

  const swatches = (/** @type {string} */ name) => h("span", { class: "swatches", "aria-hidden": "true" },
    ...["bg", "panel", "accent"].map((token) => {
      const swatch = h("span", { class: "swatch" });
      swatch.style.background = themes[name].tokens[token];
      return swatch;
    }));

  const toggle = h("button", { type: "button", role: "switch", class: "switch-row", "aria-checked": String(auto), "data-key": "match-system",
    onClick: () => change({ theme: auto ? composition.auto[matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light"] : "auto" }) },
  h("span", { class: "choice-text" }, h("span", { class: "choice-name", text: "Match system" }), h("span", { class: "choice-description muted", text: "Follow light and dark from the operating system." })),
  h("span", { class: "switch", "aria-hidden": "true" }));

  /** @param {"light" | "dark"} scheme */
  const picker = (scheme) => {
    const select = /** @type {HTMLSelectElement} */ (h("select", { class: "select", id: `auto-${scheme}`, "data-key": `auto-${scheme}` },
      ...byScheme(scheme).map((n) => h("option", { value: n, text: title(n), selected: composition.auto[scheme] === n }))));
    select.addEventListener("change", () => change({ auto: { ...composition.auto, [scheme]: select.value } }));
    return h("div", { class: "setting-row" }, h("label", { for: `auto-${scheme}`, text: `${title(scheme)} theme` }), select);
  };

  return section("Appearance", null, toggle,
    auto
      ? h("div", { class: "setting-rows" }, picker("light"), picker("dark"))
      : h("div", { class: "choices", role: "radiogroup", "aria-label": "Theme" },
        ...names.map((name) => choice(`theme-${name}`, composition.theme === name, title(name), themes[name].description, swatches(name), () => change({ theme: name })))));
}

/** @param {any} manifest @param {(patch: import("./types").CompositionPatch) => Promise<void>} change */
function layouts(manifest, change) {
  const { composition, layouts: all } = manifest;
  const rank = (/** @type {string} */ n) => (LAYOUT_ORDER.includes(n) ? LAYOUT_ORDER.indexOf(n) : LAYOUT_ORDER.length);
  const names = Object.keys(all).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  return section("Layout", null, h("div", { class: "choices", role: "radiogroup", "aria-label": "Layout" },
    ...names.map((name) => choice(`layout-${name}`, composition.layout === name, title(name), all[name].description, null, () => change({ layout: name })))));
}

/** @param {any} manifest @param {Parameters<typeof mountSettings>[1]} settings @param {(patch: import("./types").CompositionPatch) => Promise<void>} change */
function panels(manifest, settings, change) {
  const { composition, layouts: all, panels: available } = manifest;
  const inLayout = new Set(all[composition.layout]?.panels ?? []);
  const hidden = new Set(composition.hide);
  const shown = new Set(composition.show);
  /** @param {string} id @param {boolean} visible */
  const toggle = (id, visible) => {
    const hide = new Set(hidden);
    const show = new Set(shown);
    if (visible) {
      hide.delete(id);
      if (!inLayout.has(id)) show.add(id);
    } else {
      show.delete(id);
      if (inLayout.has(id)) hide.add(id);
    }
    return change({ hide: [...hide], show: [...show] });
  };
  return section("Panels", "Panels outside the current layout appear in a strip below it.", h("ul", { class: "checks", role: "list" },
    ...available.map(({ /** @type {string} */ id }) => {
      const visible = inLayout.has(id) ? !hidden.has(id) : shown.has(id) && !hidden.has(id);
      const box = /** @type {HTMLInputElement} */ (h("input", { type: "checkbox", id: `panel-${id}`, checked: visible, "data-key": `panel-${id}` }));
      box.addEventListener("change", () => toggle(id, box.checked));
      return h("li", { class: "check-row" }, box, h("label", { for: `panel-${id}` }, h("span", { class: "choice-name", text: settings.panelTitle(id) })));
    })));
}

/** @param {Parameters<typeof mountSettings>[1]} settings */
function config(settings) {
  const { configDir, canReveal } = settings.config();
  const copy = h("button", { type: "button", class: "button", onClick: async () => {
    try {
      await navigator.clipboard.writeText(configDir ?? "");
      copy.dataset.copied = "true";
      copy.lastChild && (copy.lastChild.textContent = "Copied");
      setTimeout(() => { delete copy.dataset.copied; copy.lastChild && (copy.lastChild.textContent = "Copy path"); }, 1200);
    } catch {
      // Clipboard blocked: the path stays selectable.
    }
  } }, icon("copy"), "Copy path");
  return section("Config", null,
    h("p", { class: "config-path mono", text: configDir ?? "No user config folder on this system." }),
    h("div", { class: "row" },
      canReveal && configDir ? h("button", { type: "button", class: "button", onClick: () => settings.revealConfig().catch(() => {}) }, icon("folder-open"), "Reveal in Finder") : null,
      configDir ? copy : null),
    h("p", { class: "muted settings-note" }, "Your own layouts, themes, panels and CSS go in this folder and override the defaults. ",
      h("a", { href: RICE_URL, target: "_blank", rel: "noreferrer noopener", text: "examples/rice" }), " in the README is a complete example."));
}
