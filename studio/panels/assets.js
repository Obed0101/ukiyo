// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { emptyState } from "../web/dom.js";
import { api, fetchImageUrl, formatBytes, projectName, useStyles } from "../web/panel-kit.js";

const KIND_ICON = /** @type {Record<string, string>} */ ({ folder: "folder", image: "file-image", scene: "file-braces", import: "file-braces", data: "file-braces", code: "file-code", audio: "file", file: "file" });

/**
 * The project's Shared/assets and art folders, read-only: browse, preview PNGs, see which imports are stale (the
 * importer's --check, never a build), reveal a file in Finder or copy its path. Nothing here changes a file.
 * @type {PanelModule}
 */
export default {
  id: "assets",
  title: "Assets",
  icon: "folder-open",
  description: "Browse the project's Shared/assets and art, with PNG previews and stale-import marks. Read-only.",
  mount(root, studio) {
    const { h, icon } = studio;
    useStyles();
    root.classList.add("pk");

    let project = /** @type {string | null} */ (null);
    let assetRoot = "";
    let path = "";
    let mode = /** @type {"list" | "grid"} */ ("list");
    /** @type {any} */
    let data = null;
    /** @type {{ root: string, path: string }[]} */
    let stale = [];
    let staleNote = "";
    let selected = /** @type {string | null} */ (null);
    let error = "";
    let generation = 0;
    let imageGeneration = 0;
    /** @type {string[]} */
    let urls = [];

    const crumbs = h("div", { class: "pk-crumbs", role: "navigation", "aria-label": "Folder" });
    const listButton = h("button", { type: "button", class: "segment", "aria-pressed": "true", "aria-label": "List", title: "List" }, icon("list"));
    const gridButton = h("button", { type: "button", class: "segment", "aria-pressed": "false", "aria-label": "Grid of images", title: "Grid" }, icon("layout-grid"));
    const toggle = h("div", { class: "segmented", role: "group", "aria-label": "View" }, listButton, gridButton);
    const bar = h("div", { class: "pk-bar" }, crumbs, toggle);
    const roots = h("div", { class: "segmented pk-roots", role: "group", "aria-label": "Asset folder" });
    const main = h("div", { class: "pk-scroll" });
    const detail = h("div", { class: "pk-detail", hidden: true });
    root.append(roots, bar, main, detail);

    function releaseUrls() {
      urls.forEach((url) => URL.revokeObjectURL(url));
      urls = [];
    }

    /** @param {string} file @param {HTMLElement} target */
    async function showImage(file, target) {
      const mine = imageGeneration;
      try {
        const url = await fetchImageUrl(new URLSearchParams({ project: project ?? "", root: assetRoot, path: file }).toString());
        if (mine !== imageGeneration) return URL.revokeObjectURL(url);
        urls.push(url);
        target.replaceChildren(h("img", { src: url, alt: file }));
      } catch {
        target.replaceChildren(icon("image"));
      }
    }

    const isStale = (/** @type {string} */ file) => stale.some((s) => s.root === assetRoot && s.path === file);

    async function load() {
      const mine = ++generation;
      releaseUrls();
      project = projectName(studio);
      error = "";
      if (!project) return render();
      try {
        const query = new URLSearchParams({ project, ...(assetRoot ? { root: assetRoot } : {}), path });
        const result = await api.get(`/api/assets?${query}`);
        if (mine !== generation) return;
        data = result;
        if (result.listing) assetRoot = result.listing.root;
      } catch (e) {
        if (mine !== generation) return;
        data = null;
        error = e instanceof Error ? e.message : String(e);
      }
      render();
      loadStale(mine);
    }

    /** @param {number} mine */
    async function loadStale(mine) {
      if (!project || !data?.listing) return;
      try {
        const result = await api.get(`/api/assets/stale?${new URLSearchParams({ project })}`);
        if (mine !== generation) return;
        stale = result.stale;
        staleNote = result.ran ? "" : result.reason ?? "";
        render();
      } catch {
        // Staleness is a hint; the listing stands without it.
      }
    }

    function renderCrumbs() {
      crumbs.replaceChildren();
      const parts = path.split("/").filter(Boolean);
      const add = (/** @type {string} */ label, /** @type {string} */ target, /** @type {boolean} */ current) => {
        if (crumbs.childElementCount) crumbs.append(icon("chevron-right"));
        crumbs.append(h("button", { type: "button", class: "pk-crumb", "aria-current": String(current), text: label, onClick: () => open(target) }));
      };
      add(assetRoot || "assets", "", parts.length === 0);
      parts.forEach((part, index) => add(part, parts.slice(0, index + 1).join("/"), index === parts.length - 1));
    }

    /** @param {string} target */
    function open(target) {
      path = target;
      selected = null;
      load();
    }

    function entryRow(/** @type {any} */ entry) {
      const row = h("button", { type: "button", class: "pk-row", "aria-selected": String(entry.path === selected), dataset: { path: entry.path } },
        icon(KIND_ICON[entry.kind] ?? "file"),
        h("span", { class: "pk-name", text: entry.name }),
        entry.kind === "import" && isStale(entry.path) ? h("span", { class: "pk-stale", text: "stale", title: "Generated output is missing or out of date: run ukiyo_import" }) : null,
        h("span", { class: "pk-meta", text: entry.kind === "folder" ? "folder" : `${entry.kind} · ${formatBytes(entry.size)}` }));
      row.addEventListener("click", () => (entry.kind === "folder" ? open(entry.path) : select(entry)));
      return row;
    }

    function entryTile(/** @type {any} */ entry) {
      const thumb = h("div", { class: "pk-thumb" }, icon("image"));
      const tile = h("button", { type: "button", class: "pk-tile", "aria-selected": String(entry.path === selected), title: entry.name, dataset: { path: entry.path } },
        thumb, h("span", { class: "pk-name", text: entry.name }));
      tile.addEventListener("click", () => select(entry));
      if (entry.path.toLowerCase().endsWith(".png") && entry.size <= 4 * 1024 * 1024) showImage(entry.path, thumb);
      return tile;
    }

    function render() {
      imageGeneration++;
      releaseUrls();
      roots.replaceChildren();
      toggle.hidden = !data?.listing;
      listButton.setAttribute("aria-pressed", String(mode === "list"));
      gridButton.setAttribute("aria-pressed", String(mode === "grid"));
      main.replaceChildren();
      renderCrumbs();
      renderDetail();
      if (!project) {
        bar.hidden = true;
        main.append(emptyState({ icon: icon("folder-open"), title: "No project", text: "Open a project or run a game to browse its assets." }));
        return;
      }
      bar.hidden = false;
      if (error) {
        main.append(emptyState({ icon: icon("folder-open"), title: "Assets unavailable", text: error }));
        return;
      }
      if (!data?.listing) {
        if (data) main.append(emptyState({ icon: icon("folder-open"), title: "No assets folder", text: `${project} has no Shared/assets or art folder yet.` }));
        return;
      }
      if (data.roots.length > 1) {
        for (const name of data.roots) {
          roots.append(h("button", { type: "button", class: "segment", "aria-pressed": String(name === assetRoot), text: name, onClick: () => {
            assetRoot = name;
            open("");
          } }));
        }
      }
      roots.hidden = data.roots.length < 2;
      const entries = data.listing.entries;
      if (entries.length === 0) {
        main.append(emptyState({ icon: icon("folder-open"), title: "This folder is empty" }));
        return;
      }
      if (mode === "grid") {
        const others = entries.filter((e) => !e.path.toLowerCase().endsWith(".png"));
        const images = entries.filter((e) => e.path.toLowerCase().endsWith(".png"));
        if (others.length) main.append(h("div", { class: "pk-list" }, ...others.map(entryRow)));
        if (images.length) main.append(h("div", { class: "pk-grid", style: others.length ? "margin-top: 8px" : null }, ...images.map(entryTile)));
      } else {
        main.append(h("div", { class: "pk-list" }, ...entries.map(entryRow)));
      }
      if (data.listing.truncated) main.append(h("p", { class: "pk-sub", text: "Only the first 500 entries are shown." }));
      if (staleNote) main.append(h("p", { class: "pk-sub", text: "Stale imports are not checked yet.", title: staleNote }));
    }

    function renderDetail() {
      detail.replaceChildren();
      detail.hidden = !selected;
      if (!selected) return;
      const entry = data?.listing?.entries.find((/** @type {any} */ e) => e.path === selected);
      if (!entry) return;
      if (entry.path.toLowerCase().endsWith(".png") && entry.size <= 4 * 1024 * 1024) {
        const preview = h("div", { class: "pk-preview" }, icon("image"));
        detail.append(preview);
        showImage(entry.path, preview);
      }
      const full = `${data.base}/${assetRoot}/${entry.path}`;
      detail.append(h("p", { class: "pk-sub mono", text: full }),
        h("div", { class: "pk-actions" },
          navigator.platform.startsWith("Mac")
            ? h("button", { type: "button", class: "button", onClick: async () => {
              try {
                await api.post("/api/assets/reveal", { project, root: assetRoot, path: entry.path });
              } catch (e) {
                studio.log("error", e instanceof Error ? e.message : String(e));
              }
            } }, icon("square-arrow-out-up-right"), "Reveal in Finder")
            : null,
          h("button", { type: "button", class: "button", onClick: () => navigator.clipboard.writeText(full).catch(() => studio.log("warn", "Clipboard blocked")) }, icon("copy"), "Copy path")));
    }

    /** @param {any} entry */
    function select(entry) {
      selected = entry.path;
      studio.view({ asset: { root: assetRoot, path: entry.path } });
      render();
    }

    listButton.addEventListener("click", () => {
      mode = "list";
      render();
    });
    gridButton.addEventListener("click", () => {
      mode = "grid";
      render();
    });

    const refresh = () => {
      if (projectName(studio) !== project) {
        assetRoot = "";
        path = "";
        selected = null;
        stale = [];
        load();
      }
    };
    studio.on("session", refresh);
    studio.on("project", refresh);
    render();
    load();
    return () => {
      generation++;
      imageGeneration++;
      releaseUrls();
    };
  },
};
