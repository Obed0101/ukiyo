// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

/**
 * Pause, resume and step the running game. Steps are exact ticks (60 per second); stepping always leaves it paused,
 * like F6 on desktop. Options (studio.json → panels.transport): { "steps": [1, 10, 60] }.
 * @type {PanelModule}
 */
export default {
  id: "transport",
  title: "Transport",
  icon: "play",
  description: "Pause, resume and step the game tick by tick.",
  mount(root, studio) {
    const { h, icon } = studio;
    const steps = Array.isArray(studio.options.steps) ? studio.options.steps.filter((n) => Number.isInteger(n) && n > 0 && n <= 36000) : [1, 10, 60];

    const toggle = h("button", { type: "button", class: "button primary" });
    const stepButtons = steps.map((ticks) =>
      h("button", { type: "button", class: "button", "aria-label": `Step ${ticks} tick${ticks === 1 ? "" : "s"}`, onClick: () => run("step", ticks) },
        icon("step-forward"), `+${ticks}`));
    const seconds = h("span", { class: "readout muted" });
    const error = h("span", { class: "panel-error", role: "alert" });
    root.classList.add("row");
    root.append(toggle, ...stepButtons, seconds, error);

    let busy = false;

    /** @param {"pause" | "resume" | "step"} cmd @param {number} [ticks] */
    async function run(cmd, ticks) {
      if (busy) return;
      busy = true;
      error.textContent = "";
      render();
      try {
        await studio.control(cmd, ticks);
      } catch (failure) {
        error.textContent = failure instanceof Error ? failure.message : String(failure);
      } finally {
        busy = false;
        render();
      }
    }

    function render() {
      const status = studio.status;
      const connected = Boolean(studio.session && status);
      const paused = status?.paused ?? false;
      toggle.replaceChildren(icon(paused ? "play" : "pause"), paused ? "Resume" : "Pause");
      toggle.setAttribute("aria-label", paused ? "Resume the game" : "Pause the game");
      toggle.onclick = () => run(paused ? "resume" : "pause");
      toggle.toggleAttribute("disabled", !connected || busy);
      for (const button of stepButtons) button.toggleAttribute("disabled", !connected || busy);
      seconds.textContent = status ? `${(status.tick / 60).toFixed(2)} s simulated` : "";
    }

    studio.on("status", render);
    studio.on("session", render);
    render();
  },
};
