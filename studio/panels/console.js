// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { bounded, filterEntries, foldRepeats, levelOfOutput } from "../web/console-model.js";
import { emptyState } from "../web/dom.js";
import { api, useStyles } from "../web/panel-kit.js";

const LIMIT = 2000;
const POLL_MS = 1000;
const FILTERS = /** @type {const} */ ([["all", "All"], ["warnings", "Warnings"], ["errors", "Errors"]]);

/**
 * One stream: what Studio-launched games print (stdout and stderr, kept by the server in a bounded ring), what the
 * Studio did (the Activity log) and bridge errors. Identical consecutive lines fold into one with a count; the view
 * follows the newest line until you scroll up.
 * @type {PanelModule}
 */
export default {
  id: "console",
  title: "Console",
  icon: "terminal",
  description: "Output of games the Studio launched, Studio activity and bridge errors in one stream.",
  mount(root, studio) {
    const { h, icon } = studio;
    useStyles();
    /** @type {import("../web/console-model.js").ConsoleEntry[]} */
    let entries = [];
    let level = /** @type {"all" | "warnings" | "errors"} */ ("all");
    let lastSeq = 0;
    let follow = true;
    let timer = 0;
    let disposed = false;

    const search = /** @type {HTMLInputElement} */ (h("input", { class: "field", type: "search", placeholder: "Search", "aria-label": "Search the console" }));
    const buttons = FILTERS.map(([id, label]) => h("button", { type: "button", class: "segment", "aria-pressed": String(id === level), text: label, onClick: () => {
      level = id;
      buttons.forEach((button, index) => button.setAttribute("aria-pressed", String(FILTERS[index]?.[0] === level)));
      render();
    } }));
    const segmented = h("div", { class: "segmented", role: "group", "aria-label": "Level" }, ...buttons);
    const clearButton = h("button", { type: "button", class: "icon-button", "aria-label": "Clear the console", title: "Clear" }, icon("trash-2"));
    studio.actions.append(clearButton);
    const scroller = h("div", { class: "pk-scroll pk-console", role: "log", "aria-live": "off", tabindex: "0" });
    root.classList.add("pk");
    root.append(h("div", { class: "pk-bar" }, segmented, search), scroller);

    /** @param {import("../web/console-model.js").ConsoleEntry} entry */
    function add(entry) {
      entries.push(entry);
      entries = bounded(entries, LIMIT);
    }

    for (const [index, entry] of studio.history.entries()) add({ key: `h${index}`, at: entry.at, source: entry.source, level: entry.level, text: entry.text });
    let historyCount = 0;
    studio.on("log", (entry) => {
      add({ key: `l${historyCount++}`, at: entry.at, source: entry.source, level: entry.level, text: entry.text });
      render();
    });

    function render() {
      scroller.replaceChildren();
      const folded = foldRepeats(filterEntries(entries, { level, query: search.value }));
      if (folded.length === 0) {
        scroller.append(emptyState({ icon: icon("terminal"), title: entries.length ? "No lines match" : "Nothing printed yet", text: entries.length ? undefined : "Game output and Studio activity appear here." }));
        return;
      }
      for (const entry of folded) {
        scroller.append(h("div", { class: "pk-line", dataset: { level: entry.level } },
          h("span", { class: "pk-time", text: new Date(entry.at).toLocaleTimeString([], { hour12: false }) }),
          h("span", { class: "pk-text" }, h("span", { class: "pk-source", text: `${entry.source} ` }), entry.text),
          entry.count > 1 ? h("span", { class: "pk-count", text: `×${entry.count}`, title: `${entry.count} identical lines` }) : null));
      }
      if (follow) scroller.scrollTop = scroller.scrollHeight;
    }

    scroller.addEventListener("scroll", () => {
      follow = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 24;
    });
    search.addEventListener("input", render);

    async function poll() {
      clearTimeout(timer);
      if (disposed) return;
      try {
        const page = await api.get(`/api/console?since=${lastSeq}`);
        if (page.last < lastSeq) lastSeq = 0; // the Studio server restarted
        else lastSeq = page.last;
        if (page.lines.length) {
          for (const line of page.lines) add({ key: `s${line.seq}`, at: line.at, source: line.source, level: levelOfOutput(line.stream, line.text), text: line.text });
          render();
        }
      } catch {
        // The server is away; the stream resumes when it is back.
      }
      timer = window.setTimeout(poll, POLL_MS);
    }

    clearButton.addEventListener("click", async () => {
      entries = [];
      follow = true;
      render();
      try {
        await api.post("/api/console/clear");
      } catch {
        // Local clear already done.
      }
    });

    render();
    poll();
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  },
};
