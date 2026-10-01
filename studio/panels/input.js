// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

/**
 * Records what the person plays (keys and pointer, per tick) and turns it into an input script: the same JSON
 * ukiyo_run {"input": …} and headless --input replay deterministically. This is how a human shows an agent a bug.
 * @type {PanelModule}
 */
export default {
  id: "input",
  title: "Input",
  icon: "keyboard",
  description: "Record the person's play as a replayable input script for agents and tests.",
  mount(root, studio) {
    const { h, icon } = studio;
    const record = h("button", { type: "button", class: "button", "aria-pressed": "false" });
    const copy = h("button", { type: "button", class: "button", disabled: true }, "Copy script");
    const save = h("button", { type: "button", class: "button", disabled: true }, "Save input.json");
    const summary = h("p", { class: "muted" });
    const output = h("pre", { class: "script mono", "aria-label": "Recorded input script" });
    root.classList.add("stack");
    root.append(h("div", { class: "row" }, record, copy, save), summary, output);

    let recording = false;
    let script = "";

    function render() {
      record.replaceChildren(icon("circle-dot"), recording ? "Stop recording" : "Record");
      record.setAttribute("aria-pressed", String(recording));
      record.toggleAttribute("disabled", !studio.session);
      copy.toggleAttribute("disabled", !script);
      save.toggleAttribute("disabled", !script);
      output.textContent = script;
      output.hidden = !script;
      if (!studio.session) summary.textContent = "Connect a game to record input.";
      else if (recording) summary.textContent = "Recording. Play in the game window, or in the Frame panel with Play on.";
      else if (!script) summary.textContent = "Record a play session; the script replays it exactly in headless runs and tests.";
    }

    record.addEventListener("click", async () => {
      try {
        const result = /** @type {{ recording: boolean, events: number, script: unknown }} */ (await studio.call("record", { on: !recording }));
        if (recording) {
          script = JSON.stringify(result.script, null, 2);
          summary.textContent = `${result.events} events recorded.`;
          studio.view({ lastRecording: { events: result.events } });
          studio.log("success", `recorded ${result.events} input events`);
        }
        recording = result.recording;
      } catch (error) {
        summary.textContent = error instanceof Error ? error.message : String(error);
      }
      render();
    });

    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(script);
        summary.textContent = "Script copied.";
      } catch {
        summary.textContent = "The browser blocked the clipboard; use Save instead.";
      }
    });

    save.addEventListener("click", () => {
      const url = URL.createObjectURL(new Blob([script], { type: "application/json" }));
      const link = h("a", { href: url, download: "input.json" });
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    });

    studio.on("session", () => {
      recording = false;
      render();
    });
    render();
  },
};
