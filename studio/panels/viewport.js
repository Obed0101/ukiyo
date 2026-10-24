// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */
import { emptyState } from "../web/dom.js";

// KeyboardEvent.code values the engine understands (Ukiyo.Core KeyNames.FromWebCode). Anything else stays in the page.
// Tab is deliberately left out: it always moves focus out of the frame, so keyboard users are never trapped in play.
const GAME_KEYS = /^(Key[A-Z]|Digit[0-9]|F([1-9]|1[0-2])|Space|Enter|NumpadEnter|Escape|Backspace|Arrow(Left|Right|Up|Down)|Shift(Left|Right)|Control(Left|Right)|Alt(Left|Right))$/;
const BUTTONS = /** @type {const} */ (["Left", "Middle", "Right"]);

/**
 * The real frame, read back from the game's renderer (GPU on desktop) through the dev bridge — never a re-render.
 * "Watch" captures continuously; "Play" sends this panel's keyboard and pointer input to the game.
 * Options: { "watchFps": 4 } (1–15).
 * @type {PanelModule}
 */
export default {
  id: "viewport",
  title: "Frame",
  icon: "monitor",
  description: "The game's real rendered frame, captured on demand or continuously, with keyboard and pointer passthrough.",
  mount(root, studio) {
    const { h, icon } = studio;
    const fps = Math.min(15, Math.max(1, Number(studio.options.watchFps) || 4));

    const captureButton = h("button", { type: "button", class: "icon-button", "aria-label": "Capture the current frame", title: "Capture" }, icon("camera"));
    const watchButton = h("button", { type: "button", class: "icon-button", "aria-pressed": "false", "aria-label": `Watch: capture ${fps} frames per second`, title: "Watch" }, icon("radio"));
    const playButton = h("button", { type: "button", class: "icon-button", "aria-pressed": "false", "aria-label": "Play: send keyboard and pointer to the game", title: "Play" }, icon("keyboard"));
    studio.actions.append(captureButton, watchButton, playButton);

    const image = /** @type {HTMLImageElement} */ (h("img", { alt: "", draggable: "false" }));
    const message = h("div", { class: "empty-host" });
    const screen = h("div", { class: "screen", tabindex: "0", "aria-label": "Game frame" }, message);
    const caption = h("p", { class: "screen-caption" });
    root.classList.add("stack");
    root.append(screen, caption);

    let watching = false;
    let playing = false;
    let timer = /** @type {ReturnType<typeof setTimeout> | undefined} */ (undefined);
    let lastTick = -1;
    let watchGeneration = 0;

    let captureError = "";
    let autoCaptured = false;

    async function captureOnce() {
      try {
        const frame = await studio.capture();
        captureError = "";
        render();
        return frame;
      } catch (error) {
        captureError = `Capture failed: ${error instanceof Error ? error.message : String(error)}`;
      }
      render();
      return null;
    }

    async function watchLoop(generation = watchGeneration) {
      if (!watching || generation !== watchGeneration) return;
      const status = studio.status;
      // A paused game shows the same frame forever; only capture again when the tick moved.
      if (studio.session && status && status.tick !== lastTick) {
        const frame = await captureOnce();
        if (generation !== watchGeneration) return;
        if (frame) lastTick = frame.tick;
      }
      if (watching) timer = setTimeout(() => watchLoop(generation), 1000 / fps);
    }

    /** Games the Studio can run, so the empty state can offer the one-click action instead of a command. */
    /** @type {{ games: import("../web/types").GameEntry[], runs: import("../web/types").GameRun[] }} */
    let launch = { games: [], runs: [] };
    let launchTimer = 0;
    let launchError = "";
    let disposed = false;

    async function pollLaunch() {
      clearTimeout(launchTimer);
      if (disposed || studio.session) return;
      try {
        const next = await studio.launcher.list();
        const changed = JSON.stringify([next.games, next.runs.map((r) => [r.id, r.state, r.detail])]) !== JSON.stringify([launch.games, launch.runs.map((r) => [r.id, r.state, r.detail])]);
        launch = next;
        if (changed && !studio.session) render();
      } catch {
        // The Games panel reports a failing launcher; here the empty state just keeps its plain text.
      }
      launchTimer = window.setTimeout(pollLaunch, launch.runs.some((r) => r.state === "building" || r.state === "starting") ? 700 : 3000);
    }

    /** The empty state with no game attached: starting, or the one game to run. */
    function showNoGame() {
      const starting = launch.runs.find((r) => r.state === "building" || r.state === "starting");
      if (starting) {
        showMessage({ icon: "monitor", title: `Starting ${starting.name}`, text: starting.detail || "Building…" });
        return;
      }
      const game = launch.games.find((g) => g.bridge && g.name === "LanternRun") ?? launch.games.find((g) => g.bridge);
      if (!game) {
        showMessage({ icon: "unplug", title: "No game running", text: "Start a development build and it connects here." });
        return;
      }
      const run = h("button", {
        type: "button", class: "button primary", "aria-label": `Run ${game.name}`,
        onClick: async () => {
          launchError = "";
          try {
            await studio.launcher.start(game.id);
          } catch (error) {
            launchError = error instanceof Error ? error.message : String(error);
          }
          pollLaunch();
          render();
        },
      }, icon("play"), `Run ${game.name}`);
      showMessage({
        icon: "unplug", title: "No game running",
        text: launchError || (launch.games.length > 1 ? "Run a game, or choose another in Games." : "Run a game and it connects here."),
        action: run,
      });
    }

    /** @param {{ icon?: string, title: string, text?: string, command?: string, retry?: boolean, action?: HTMLElement }} options */
    function showMessage({ icon: name, title, text, command, retry, action: given }) {
      const action = given ?? (retry ? h("button", { type: "button", class: "button", onClick: () => captureOnce() }, icon("camera"), "Capture frame") : undefined);
      message.replaceChildren(emptyState({ icon: name ? icon(name) : undefined, title, text, command, action }));
      screen.replaceChildren(message);
    }

    function render() {
      const frame = studio.frame;
      captureButton.toggleAttribute("disabled", !studio.session);
      watchButton.toggleAttribute("disabled", !studio.session);
      playButton.toggleAttribute("disabled", !studio.session);
      if (!studio.session) {
        showNoGame();
        caption.textContent = "";
        if (!launchTimer) pollLaunch();
        return;
      }
      if (studio.status && !studio.status.capture) {
        showMessage({ icon: "monitor", title: "This renderer cannot show frames", text: `${studio.status.renderer} cannot read frames back. Agents can use ukiyo_capture for CPU frames.` });
        return;
      }
      if (captureError) {
        showMessage({ icon: "monitor", title: "Capture failed", text: captureError.replace(/^Capture failed: /, ""), retry: true });
        return;
      }
      if (!frame) {
        // The first frame of a newly attached game is read once on its own; later ones are the person's call.
        if (!autoCaptured) {
          autoCaptured = true;
          captureOnce();
        }
        showMessage({ icon: "monitor", title: "No frame yet", text: "Capture reads the real frame from the running game.", retry: true });
        caption.textContent = "";
        return;
      }
      if (image.src !== frame.url) image.src = frame.url;
      image.width = frame.width;
      image.height = frame.height;
      image.alt = `${studio.session.game} at tick ${frame.tick}`;
      if (image.parentElement !== screen) screen.replaceChildren(image);
      const behind = studio.status ? studio.status.tick - frame.tick : 0;
      caption.textContent = `tick ${frame.tick} · ${frame.width}×${frame.height}${behind > 0 ? ` · ${behind} ticks behind` : ""}`;
      caption.title = `${frame.backend} readback`;
    }

    captureButton.addEventListener("click", () => captureOnce());
    watchButton.addEventListener("click", () => {
      watching = !watching;
      watchGeneration++;
      watchButton.setAttribute("aria-pressed", String(watching));
      clearTimeout(timer);
      lastTick = -1;
      if (watching) watchLoop();
      studio.view({ watching });
    });
    playButton.addEventListener("click", () => {
      playing = !playing;
      if (!playing) releaseInput();
      playButton.setAttribute("aria-pressed", String(playing));
      if (playing) screen.focus();
      studio.view({ playing });
      studio.log("info", playing ? "play on: keys and clicks in the frame go to the game" : "play off");
    });

    /** @param {import("../web/types").InputEvent[]} events */
    function send(events) {
      studio.call("input", { events }).catch((error) => studio.log("error", error instanceof Error ? error.message : String(error)));
    }

    /** Maps a click on the scaled image to drawing-buffer pixels. @param {PointerEvent} event @param {boolean} [outside] */
    function pointerAt(event, outside = false) {
      const frame = studio.frame;
      if (!frame || image.parentElement !== screen) return null;
      // The image is letterboxed with object-fit: contain; find the drawn rectangle inside its box.
      const box = image.getBoundingClientRect();
      const scale = Math.min(box.width / frame.width, box.height / frame.height);
      if (!(scale > 0)) return null;
      const left = box.left + (box.width - frame.width * scale) / 2;
      const top = box.top + (box.height - frame.height * scale) / 2;
      const x = (event.clientX - left) / scale;
      const y = (event.clientY - top) / scale;
      if (!outside && (x < 0 || y < 0 || x > frame.width || y > frame.height)) return null;
      return /** @type {[number, number]} */ ([Math.round(x), Math.round(y)]);
    }

    const held = new Set();
    /** @type {Map<number, { button: "Left" | "Middle" | "Right", at: [number, number] }>} */
    const pointers = new Map();

    function releaseInput() {
      const events = /** @type {import("../web/types").InputEvent[]} */ ([...held].map((code) => ({ key: code, down: false })));
      held.clear();
      for (const [id, { button }] of pointers) {
        // Cancellation is not a click. Release outside the drawing buffer so GameUi cannot activate the button.
        events.push({ button, down: false, pointer: [-1, -1] });
        pointers.delete(id);
        if (screen.hasPointerCapture(id)) screen.releasePointerCapture(id);
      }
      if (events.length && studio.session) send(events);
    }
    /** @param {KeyboardEvent} event @param {boolean} down */
    function key(event, down) {
      if (!playing || !GAME_KEYS.test(event.code) || event.metaKey) return;
      event.preventDefault();
      if (down && (event.repeat || held.has(event.code))) return;
      if (down) held.add(event.code);
      else held.delete(event.code);
      send([{ key: event.code, down }]);
    }

    /** @param {PointerEvent} event @param {boolean} down */
    function pointer(event, down) {
      if (!playing) return;
      const previous = pointers.get(event.pointerId);
      const at = pointerAt(event, !down) ?? (!down ? previous?.at : null);
      const button = down ? BUTTONS[event.button] : previous?.button;
      if (!at || !button) return;
      event.preventDefault();
      if (down) {
        screen.focus();
        pointers.set(event.pointerId, { button, at });
        screen.setPointerCapture(event.pointerId);
      } else {
        pointers.delete(event.pointerId);
        if (screen.hasPointerCapture(event.pointerId)) screen.releasePointerCapture(event.pointerId);
      }
      send([{ button, down, pointer: at }]);
    }

    screen.addEventListener("keydown", (event) => key(event, true));
    screen.addEventListener("keyup", (event) => key(event, false));
    screen.addEventListener("pointerdown", (event) => pointer(event, true));
    screen.addEventListener("pointerup", (event) => pointer(event, false));
    screen.addEventListener("pointercancel", () => releaseInput());
    screen.addEventListener("lostpointercapture", () => releaseInput());
    screen.addEventListener("pointermove", (event) => {
      if (!playing) return;
      const at = pointerAt(event);
      if (!at) return;
      const previous = pointers.get(event.pointerId);
      if (previous) previous.at = at;
      send([{ pointer: at }]);
    });
    screen.addEventListener("blur", releaseInput);
    window.addEventListener("blur", releaseInput);

    studio.on("frame", render);
    studio.on("session", () => {
      // The shell has already switched sessions: never forward old releases to the new game.
      held.clear();
      pointers.clear();
      playing = false;
      playButton.setAttribute("aria-pressed", "false");
      studio.view({ playing });
      watchGeneration++;
      clearTimeout(timer);
      lastTick = -1;
      captureError = "";
      autoCaptured = false;
      render();
      if (watching) watchLoop();
      if (!studio.session) pollLaunch();
    });
    studio.on("status", render);
    render();

    return () => {
      disposed = true;
      clearTimeout(launchTimer);
      watching = false;
      watchGeneration++;
      clearTimeout(timer);
      releaseInput();
      window.removeEventListener("blur", releaseInput);
    };
  },
};
