// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { emptyState, formatNumber } from "../web/dom.js";
import { loadSceneEntities, projectName, useStyles } from "../web/panel-kit.js";
import { buildRetargetPatch, describePatchResult, nextPatchId, parseNumber, patchOutcome, patchTick } from "../web/patch-model.js";
import { selection } from "../web/selection.js";
import { angleOf, valueDelta } from "../web/snapshot-model.js";

const AXES = ["x", "y", "z"];

/**
 * The selected entity: pose and, for games with a SceneRegistry, position fields that submit a real `retarget` scene
 * patch through the dev bridge and show what the game answered. Games without scenes are read-only (no inputs).
 * With nothing selected it shows the snapshot values with their change since the previous tick.
 * @type {PanelModule}
 */
export default {
  id: "inspector",
  title: "Inspector",
  icon: "sliders-horizontal",
  description: "Pose and values of the selected entity; position edits are scene patches the game applies at a tick boundary.",
  mount(root, studio) {
    const { h, icon } = studio;
    useStyles();
    root.classList.add("pk");
    const body = h("div", { class: "pk-scroll" });
    root.append(body);

    /** @type {{ id: string, prefab?: string, values?: Record<string, number> }[] | null} */
    let sceneEntities = null;
    let sceneKey = "";
    /** @type {Record<string, number>} */
    let previous = {};
    let previousTick = -1;
    /** @type {{ text: string, state: "" | "ok" | "error", patchId?: string }} */
    let status = { text: "", state: "" };
    /** @type {HTMLInputElement[]} */
    let inputs = [];
    let builtFor = "";

    const editable = () => Boolean(studio.session && studio.snapshot?.scene);

    function renderValues() {
      const snapshot = studio.snapshot;
      body.replaceChildren();
      const values = snapshot?.values ?? {};
      const names = Object.keys(values).sort((a, b) => a.localeCompare(b));
      if (!studio.session || !snapshot) {
        body.append(emptyState({ icon: icon("sliders-horizontal"), title: "Nothing to inspect", text: "Run a game and select an entity in the Hierarchy." }));
        return;
      }
      if (names.length === 0) {
        body.append(emptyState({ icon: icon("sliders-horizontal"), title: "No entity selected", text: "Select one in the Hierarchy. This game reports no values." }));
        return;
      }
      const rows = names.map((name) => {
        const value = values[name] ?? 0;
        return { name, value, delta: valueDelta({ value, before: previous[name], tick: snapshot.tick, previousTick }) };
      });
      const changed = rows.some((row) => row.delta !== 0);
      body.append(h("table", { class: "table" },
        h("thead", null, h("tr", null, h("th", { text: "Value" }), h("th", { class: "num", text: "Now" }), changed ? h("th", { class: "num", text: "Change" }) : null)),
        h("tbody", null, ...rows.map((row) => h("tr", null,
          h("td", { text: row.name }),
          h("td", { class: "num", text: formatNumber(row.value) }),
          changed ? h("td", { class: "num muted", text: row.delta === 0 ? "" : `${row.delta > 0 ? "+" : ""}${formatNumber(row.delta)}` }) : null)))));
      if (snapshot.tick !== previousTick) {
        previous = { ...values };
        previousTick = snapshot.tick;
      }
    }

    /** @param {string} name */
    function buildEntity(name) {
      body.replaceChildren();
      inputs = [];
      const pose = studio.snapshot?.entities[name];
      if (!pose) {
        body.append(emptyState({ icon: icon("sliders-horizontal"), title: `${name} is not in the game`, text: "It was disposed or the scene changed." }));
        return;
      }
      const prefab = sceneEntities?.find((entity) => entity.id === name);
      const scene = studio.snapshot?.scene?.active;
      const canEdit = editable();

      const props = h("div", { class: "pk-props" });
      props.append(h("span", { class: "pk-label", text: "Position" }));
      AXES.forEach((axis, index) => {
        const input = /** @type {HTMLInputElement} */ (h("input", {
          class: "pk-num", type: "text", inputmode: "decimal", "aria-label": `Position ${axis}`, readonly: !canEdit, dataset: { axis: String(index) },
        }));
        input.value = formatNumber(pose.position[index] ?? 0);
        if (canEdit) {
          input.addEventListener("input", () => {
            input.dataset.dirty = "true";
            input.removeAttribute("aria-invalid");
          });
          input.addEventListener("keydown", (event) => {
            if (event.key === "Enter") submit(name);
            if (event.key === "Escape") {
              delete input.dataset.dirty;
              input.value = formatNumber(studio.snapshot?.entities[name]?.position[index] ?? 0);
            }
          });
        }
        inputs.push(input);
        props.append(input);
      });
      props.append(h("span", { class: "pk-label", text: "Rotation" }),
        h("span", { class: "pk-num pk-static", style: "grid-column: span 3", text: `${formatNumber(angleOf(pose.rotation))}°`, "aria-label": "Rotation in degrees" }),
        h("span", { class: "pk-label", text: "Scale" }),
        ...AXES.map((axis, index) => h("span", { class: "pk-num pk-static", "aria-label": `Scale ${axis}`, text: formatNumber(pose.scale[index] ?? 1) })));

      const statusLine = h("p", { class: "pk-status", role: "status", "aria-live": "polite", dataset: { state: status.state }, text: status.text });
      const section = h("div", { class: "pk-section" },
        h("p", { class: "pk-title", text: name }),
        h("p", { class: "pk-sub", text: [prefab?.prefab, scene ? `scene ${scene}` : null].filter(Boolean).join(" · ") || "entity" }),
        props,
        canEdit ? statusLine : h("p", { class: "pk-status", text: "Read-only: this game has no scene registry, so there is nothing the Studio may patch." }));
      body.append(section);

      const values = prefab?.values ? Object.entries(prefab.values) : [];
      if (values.length) {
        body.append(h("div", { class: "pk-section" },
          h("p", { class: "pk-sub", text: "Values in the scene file (initial)" }),
          h("table", { class: "table" }, h("tbody", null, ...values.map(([key, value]) => h("tr", null, h("td", { text: key }), h("td", { class: "num", text: formatNumber(value) })))))));
      }
      builtFor = `${name}\0${canEdit}\0${prefab ? "p" : ""}`;
    }

    /** @param {string} name */
    async function submit(name) {
      const pose = studio.snapshot?.entities[name];
      if (!pose) return;
      const position = /** @type {[number, number, number]} */ ([...pose.position]);
      for (const input of inputs) {
        const value = parseNumber(input.value);
        if (value === null) {
          input.setAttribute("aria-invalid", "true");
          return setStatus("Enter a number in every position field.", "error");
        }
        position[Number(input.dataset.axis)] = value;
      }
      if (position.every((value, index) => value === pose.position[index])) return setStatus("Nothing changed.", "");
      try {
        // Ask the game for its tick right now: a stale status would aim at a tick that is already past.
        const live = /** @type {{ tick: number, paused: boolean }} */ (await studio.call("status"));
        const patchId = nextPatchId();
        const patch = buildRetargetPatch({ patchId, tick: patchTick(live), scene: studio.snapshot?.scene?.active ?? null, entity: name, position });
        const result = describePatchResult(await studio.call("patch", { patch }));
        setStatus(result.text, "ok", result.ok && result.status === "queued" ? patchId : undefined);
        for (const input of inputs) delete input.dataset.dirty;
        studio.log("info", `patch ${patchId}: ${name} → ${position.join(", ")}`);
        // A paused game shows nothing new until a frame is read back: show the effect of the edit.
        if (result.ok && result.status === "applied") studio.capture().catch(() => {});
      } catch (error) {
        setStatus(error instanceof Error ? error.message : String(error), "error");
      }
    }

    /** @param {string} text @param {"" | "ok" | "error"} state @param {string} [patchId] */
    function setStatus(text, state, patchId) {
      status = { text, state, patchId };
      const line = body.querySelector(".pk-status[role=status]");
      if (line instanceof HTMLElement) {
        line.textContent = text;
        line.dataset.state = state;
      }
    }

    function followQueuedPatch() {
      if (!status.patchId) return;
      const outcome = patchOutcome(studio.snapshot, status.patchId);
      if (outcome.state === "applied") setStatus(`Applied at tick ${outcome.tick}`, "ok");
      else if (outcome.state === "rejected") setStatus(outcome.message, "error");
    }

    function refreshInputs() {
      const name = selection.get();
      const pose = name ? studio.snapshot?.entities[name] : null;
      if (!name || !pose) return;
      inputs.forEach((input, index) => {
        if (document.activeElement === input || input.dataset.dirty) return;
        input.value = formatNumber(pose.position[index] ?? 0);
      });
    }

    function render() {
      const name = selection.get();
      if (!name) {
        builtFor = "";
        renderValues();
        return;
      }
      const prefab = sceneEntities?.find((entity) => entity.id === name);
      const key = `${name}\0${editable()}\0${prefab ? "p" : ""}`;
      const pose = studio.snapshot?.entities[name];
      if (key !== builtFor || !pose || body.querySelector(".empty-state")) buildEntity(name);
      else refreshInputs();
    }

    async function refreshScene() {
      const project = projectName(studio);
      const scene = studio.snapshot?.scene?.active;
      const key = project && scene ? `${project}\0${scene}` : "";
      if (key === sceneKey) return;
      sceneKey = key;
      sceneEntities = project && scene ? await loadSceneEntities(project, scene) : null;
      render();
    }

    studio.on("snapshot", () => {
      followQueuedPatch();
      render();
      refreshScene();
    });
    studio.on("session", () => {
      previous = {};
      previousTick = -1;
      sceneKey = "";
      sceneEntities = null;
      status = { text: "", state: "" };
      builtFor = "";
      render();
    });
    const stop = selection.subscribe(() => {
      status = { text: "", state: "" };
      builtFor = "";
      render();
    });
    render();
    refreshScene();
    return stop;
  },
};
