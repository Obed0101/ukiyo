// @ts-check
// The one header, same component in the Hub, a project and Settings. Left: where you are. Centre (project only): run
// and transport. Right: plain state text, then Settings. On the Mac the header is the titlebar row (studio.css).
import { h } from "./dom.js";
import { icon } from "./icons.js";
import { openMenu, openPopover } from "./menu.js";

/**
 * @typedef {{
 *   route: "hub" | "project" | "settings", projectName: string | null, online: boolean,
 *   mode: "idle" | "busy" | "attached", paused: boolean, owned: boolean, canRun: boolean, runHint: string,
 *   stateText: string, problem: { label: string, lines: string[] } | null, stepping: boolean,
 * }} HeaderModel
 * @typedef {{
 *   hub: () => void, settings: () => void, back: () => void, run: () => void, stop: () => void, reload: () => void,
 *   toggle: () => void, step: (ticks: number) => void,
 * }} HeaderActions
 */

const STEPS = [1, 10, 60];

/** @param {{ start: HTMLElement, center: HTMLElement, end: HTMLElement }} slots @param {HeaderActions} actions */
export function createHeader({ start, center, end }, actions) {
  let signature = "";
  /** @type {HTMLElement | null} */
  let stateNode = null;

  /** @param {string} label @param {string} name @param {() => void} onClick @param {{ disabled?: boolean, title?: string }} [extra] */
  const quiet = (label, name, onClick, extra = {}) =>
    h("button", { type: "button", class: "icon-button", "aria-label": label, title: extra.title ?? label, disabled: extra.disabled, onClick }, icon(name));

  /** @param {HeaderModel} model */
  function transport(model) {
    const busy = model.mode === "busy";
    const attached = model.mode === "attached";
    const primary = attached
      ? h("button", { type: "button", class: "button primary transport-main", "aria-label": model.paused ? "Resume the game" : "Pause the game", disabled: model.stepping, onClick: actions.toggle },
        icon(model.paused ? "play" : "pause"), model.paused ? "Resume" : "Pause")
      : h("button", { type: "button", class: "button primary transport-main", "aria-label": "Run the game", title: model.runHint || undefined, disabled: busy || !model.canRun, onClick: actions.run },
        icon("play"), "Run");
    const stepMenu = h("button", { type: "button", class: "icon-button step-more", "aria-label": "More step sizes", "aria-haspopup": "menu", disabled: !attached || model.stepping, onClick: () =>
      openMenu(stepMenu, STEPS.slice(1).map((n) => ({ label: `Step ${n} ticks`, onSelect: () => actions.step(n) })), { align: "center" }) }, icon("chevron-down"));
    const step = h("div", { class: "split" },
      h("button", { type: "button", class: "button split-main", "aria-label": "Step 1 tick", disabled: !attached || model.stepping, onClick: () => actions.step(1) }, icon("step-forward"), "+1"),
      stepMenu);
    return h("div", { class: "transport", role: "group", "aria-label": "Game controls" },
      primary, step,
      model.owned || busy ? quiet("Reload at the same tick", "refresh-cw", actions.reload, { disabled: busy }) : null,
      model.owned || busy ? quiet("Stop the game", "square", actions.stop, { disabled: model.stateText === "Stopping…" }) : null);
  }

  /** @param {HeaderModel} model */
  function build(model) {
    start.replaceChildren();
    center.replaceChildren();
    end.replaceChildren();
    const mark = h("span", { class: "mark", "aria-hidden": "true" });
    if (model.route === "hub") {
      start.append(mark, h("span", { class: "crumb-root", text: "ukiyo" }), h("span", { class: "crumb-sep muted", text: "/" }), h("h1", { class: "crumb-here", text: "Projects" }));
    } else if (model.route === "settings") {
      start.append(quiet("Back", "arrow-left", actions.back), h("h1", { class: "crumb-here", text: "Settings" }));
    } else {
      start.append(
        h("button", { type: "button", class: "crumb-link", onClick: actions.hub, title: "Back to Projects" }, "Projects"),
        icon("chevron-right"),
        h("h1", { class: "crumb-here", text: model.projectName ?? "" }));
      center.append(transport(model));
    }
    stateNode = null;
    if (model.route === "project") {
      if (model.problem) {
        const problem = model.problem;
        const button = h("button", { type: "button", class: "state state-problem", onClick: () =>
          openPopover(button, h("div", { class: "problem" }, h("p", { class: "problem-title", text: problem.label }), h("pre", { class: "script mono", text: problem.lines.join("\n") || "No output was captured." })), { align: "end" }) },
        problem.label);
        stateNode = button;
      } else {
        stateNode = h("span", { class: "state", role: "status" });
      }
      end.append(stateNode);
    }
    if (!model.online) end.append(h("span", { class: "state state-offline", role: "status", text: "Studio offline" }));
    if (model.route !== "settings") end.append(quiet("Settings", "settings", actions.settings, { title: "Settings (⌘,)" }));
  }

  return {
    /** @param {HeaderModel} model */
    render(model) {
      const next = JSON.stringify([model.route, model.projectName, model.online, model.mode, model.paused, model.owned, model.canRun, model.stepping, model.problem?.label, model.problem?.lines.length, model.stateText === "Stopping…"]);
      if (next !== signature) {
        signature = next;
        const focus = /** @type {HTMLElement | null} */ (document.activeElement)?.getAttribute("aria-label");
        build(model);
        if (focus) /** @type {HTMLElement | null} */ (document.querySelector(`.topbar [aria-label="${focus}"]:not([disabled])`))?.focus();
      }
      if (stateNode && !model.problem) {
        stateNode.textContent = model.stateText;
        stateNode.dataset.kind = model.mode;
      }
    },
  };
}
