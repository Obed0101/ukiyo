// @ts-check
/** @typedef {import("../web/types").PanelModule} PanelModule */

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
    const message = h("p", { class: "empty" });
    const screen = h("div", { class: "screen", tabindex: "0", "aria-label": "Game frame" }, message);
    const caption = h("p", { class: "screen-caption" });
    root.classList.add("stack");
    root.append(screen, caption);

    let watching = false;
    let playing = false;
    let timer = /** @type {ReturnType<typeof setTimeout> | undefined} */ (undefined);
    let lastTick = -1;

    let captureError = "";

    async function captureOnce() {
      try {
        await studio.capture();
        captureError = "";
      } catch (error) {
        captureError = `Capture failed: ${error instanceof Error ? error.message : String(error)}`;
      }
      render();
    }

    async function watchLoop() {
      if (!watching) return;
      const status = studio.status;
      // A paused game shows the same frame forever; only capture again when the tick moved.
      if (studio.session && status && status.tick !== lastTick) {
        lastTick = status.tick;
        await captureOnce();
      }
      timer = setTimeout(watchLoop, 1000 / fps);
    }

    /** @param {string} text */
    function showMessage(text) {
      message.textContent = text;
      screen.replaceChildren(message);
    }

    function render() {
      const frame = studio.frame;
      captureButton.toggleAttribute("disabled", !studio.session);
      watchButton.toggleAttribute("disabled", !studio.session);
      playButton.toggleAttribute("disabled", !studio.session);
      if (!studio.session) {
        showMessage("No game connected. Start a development build, for example: dotnet run --project samples/LanternRun/Desktop");
        caption.textContent = "";
        return;
      }
      if (studio.status && !studio.status.capture) {
        showMessage(`${studio.status.renderer} cannot read frames back. Use ukiyo_capture for CPU frames.`);
        return;
      }
      if (captureError) {
        showMessage(captureError);
        return;
      }
      if (!frame) {
        showMessage("No frame yet. Capture reads the real frame from the running game.");
        caption.textContent = "";
        return;
      }
      if (image.src !== frame.url) image.src = frame.url;
      image.width = frame.width;
      image.height = frame.height;
      image.alt = `${studio.session.game} at tick ${frame.tick}`;
      if (image.parentElement !== screen) screen.replaceChildren(image);
      const behind = studio.status ? studio.status.tick - frame.tick : 0;
      caption.textContent = `tick ${frame.tick} · ${frame.width}×${frame.height} · ${frame.backend} readback${behind > 0 ? ` · ${behind} ticks behind` : ""}`;
    }

    captureButton.addEventListener("click", () => captureOnce());
    watchButton.addEventListener("click", () => {
      watching = !watching;
      watchButton.setAttribute("aria-pressed", String(watching));
      clearTimeout(timer);
      lastTick = -1;
      if (watching) watchLoop();
      studio.view({ watching });
    });
    playButton.addEventListener("click", () => {
      playing = !playing;
      playButton.setAttribute("aria-pressed", String(playing));
      if (playing) screen.focus();
      studio.view({ playing });
      studio.log("info", playing ? "play on: keys and clicks in the frame go to the game" : "play off");
    });

    /** @param {import("../web/types").InputEvent[]} events */
    function send(events) {
      studio.call("input", { events }).catch((error) => studio.log("error", error instanceof Error ? error.message : String(error)));
    }

    /** Maps a click on the scaled image to drawing-buffer pixels. @param {PointerEvent} event */
    function pointerAt(event) {
      const frame = studio.frame;
      if (!frame || image.parentElement !== screen) return null;
      // The image is letterboxed with object-fit: contain; find the drawn rectangle inside its box.
      const box = image.getBoundingClientRect();
      const scale = Math.min(box.width / frame.width, box.height / frame.height);
      const left = box.left + (box.width - frame.width * scale) / 2;
      const top = box.top + (box.height - frame.height * scale) / 2;
      const x = (event.clientX - left) / scale;
      const y = (event.clientY - top) / scale;
      if (x < 0 || y < 0 || x > frame.width || y > frame.height) return null;
      return /** @type {[number, number]} */ ([Math.round(x), Math.round(y)]);
    }

    const held = new Set();
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
      const at = pointerAt(event);
      const button = BUTTONS[event.button];
      if (!at || !button) return;
      send([{ button, down, pointer: at }]);
    }

    screen.addEventListener("keydown", (event) => key(event, true));
    screen.addEventListener("keyup", (event) => key(event, false));
    screen.addEventListener("pointerdown", (event) => pointer(event, true));
    screen.addEventListener("pointerup", (event) => pointer(event, false));
    screen.addEventListener("blur", () => {
      // Releasing focus must not leave a key stuck down in the game.
      if (held.size) send([...held].map((code) => ({ key: code, down: false })));
      held.clear();
    });

    studio.on("frame", render);
    studio.on("session", () => {
      lastTick = -1;
      captureError = "";
      render();
    });
    studio.on("status", render);
    render();

    return () => {
      watching = false;
      clearTimeout(timer);
    };
  },
};
