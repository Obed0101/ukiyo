// Shared browser shell for every ukiyo game: boots .NET WebAssembly, registers the three.js adapter and the frame
// coordinator, forwards input to C#, then runs the game's Program.cs. It never touches game state:
// requestAnimationFrame only forwards time, and input events are queued in C# and latched once per tick.
//
// Host keys (not sent to the game): ` (Backquote) pause/resume, Shift+` step one tick while paused.
import * as threeAdapter from "./ukiyo-three.js";

const POINTER_MOVE = 0;
const POINTER_DOWN = 1;
const POINTER_UP = 2;

export async function boot({ dotnet, canvas, status }) {
  const query = new URLSearchParams(location.search);
  const args = [];
  if (query.has("checkpoints")) args.push("--checkpoints", query.get("checkpoints"));
  if (query.has("paused")) args.push("--paused");
  if (query.has("fixed")) args.push("--fixed-size");
  if (query.has("input")) {
    // Same-origin input script for parity runs: ?input=input.json
    const response = await fetch(new URL(query.get("input"), location.href));
    if (!response.ok) throw new Error(`[INPUT]: could not load ${query.get("input")}: ${response.status}`);
    args.push("--input-json", await response.text());
  }

  const { setModuleImports, getAssemblyExports, runMain } = await dotnet.withApplicationArguments(...args).create();
  const exports = await getAssemblyExports("Ukiyo.Platform.Browser.dll");
  const host = exports.Ukiyo.Hosting.BrowserExports;
  const pixelSize = () => [Math.max(1, Math.round(canvas.clientWidth * devicePixelRatio)), Math.max(1, Math.round(canvas.clientHeight * devicePixelRatio))];
  const toPixels = (event) => {
    const rect = canvas.getBoundingClientRect();
    const [w, h] = pixelSize();
    return [((event.clientX - rect.left) / rect.width) * w, ((event.clientY - rect.top) / rect.height) * h];
  };

  // Web Audio: sounds arrive once as 16-bit PCM and are cached by id; plays are fire-and-forget voices.
  let audio = null;
  const sounds = new Map();
  const audioContext = () => (audio ??= new AudioContext({ latencyHint: "interactive" }));
  const unlockAudio = () => { if (audioContext().state === "suspended") audio.resume(); };
  addEventListener("pointerdown", unlockAudio);
  addEventListener("keydown", unlockAudio);

  setModuleImports("ukiyo-three", threeAdapter);
  setModuleImports("ukiyo-host", {
    canvasPixelSize: () => pixelSize().join("x"),
    setCanvasSize: (w, h) => { canvas.style.width = `${w / devicePixelRatio}px`; canvas.style.height = `${h / devicePixelRatio}px`; },
    startLoop: () => {
      let paused = false;
      addEventListener("keydown", (e) => {
        if (e.code === "Backquote") {
          if (e.shiftKey && paused) status.textContent = `paused · tick ${JSON.parse(host.Step(1)).tick}`;
          else if (!e.shiftKey) { paused = !paused; host.SetPaused(paused); }
          e.preventDefault();
          return;
        }
        if (!e.repeat && host.KeyEvent(e.code, true)) e.preventDefault();
      });
      addEventListener("keyup", (e) => { if (host.KeyEvent(e.code, false)) e.preventDefault(); });
      addEventListener("blur", () => host.ReleaseAllInput());
      canvas.addEventListener("pointermove", (e) => { const [x, y] = toPixels(e); host.PointerEvent(POINTER_MOVE, x, y, 0); });
      canvas.addEventListener("pointerdown", (e) => { canvas.setPointerCapture(e.pointerId); const [x, y] = toPixels(e); host.PointerEvent(POINTER_DOWN, x, y, e.button); });
      canvas.addEventListener("pointerup", (e) => { const [x, y] = toPixels(e); host.PointerEvent(POINTER_UP, x, y, e.button); });
      canvas.addEventListener("contextmenu", (e) => e.preventDefault());
      const frame = (t) => {
        const [w, h] = pixelSize();
        host.Frame(t, w, h);
        requestAnimationFrame(frame);
      };
      requestAnimationFrame(frame);
      status.textContent = "` pause · shift+` step · C# computes every tick";
    },
    audioLoad: (id, pcm16, sampleRate, channels) => {
      const ctx = audioContext();
      const bytes = pcm16 instanceof Uint8Array ? pcm16 : Uint8Array.from(pcm16); // byte[] may arrive as an Array
      const frames = bytes.length / 2 / channels;
      const buffer = ctx.createBuffer(channels, frames, sampleRate);
      const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      for (let c = 0; c < channels; c++) {
        const out = buffer.getChannelData(c);
        for (let i = 0; i < frames; i++) out[i] = view.getInt16((i * channels + c) * 2, true) / 32768;
      }
      sounds.set(id, buffer);
    },
    audioPlay: (id, volume, pan, pitch) => {
      const buffer = sounds.get(id);
      if (!buffer || !audio || audio.state !== "running") return; // no sound before the first user gesture
      const source = audio.createBufferSource();
      source.buffer = buffer;
      source.playbackRate.value = pitch;
      const gain = audio.createGain();
      gain.gain.value = volume;
      const panner = audio.createStereoPanner();
      panner.pan.value = pan;
      source.connect(gain).connect(panner).connect(audio.destination);
      source.start();
    },
    publishEvidence: (json) => {
      const node = document.createElement("script");
      node.type = "application/json";
      node.id = "ukiyo-evidence";
      node.textContent = json;
      document.body.appendChild(node);
      document.title = "ukiyo:evidence-ready";
      status.textContent = "evidence ready";
    },
  });

  // Exposed for agents and tests driving the page (DevTools, Playwright).
  globalThis.ukiyo = {
    snapshot: () => JSON.parse(host.Snapshot()),
    step: (n = 1) => JSON.parse(host.Step(n)),
    setPaused: (p) => host.SetPaused(p),
    key: (code, down) => host.KeyEvent(code, down),
    pointer: (kind, x, y, button = 0) => host.PointerEvent({ move: POINTER_MOVE, down: POINTER_DOWN, up: POINTER_UP }[kind] ?? POINTER_MOVE, x, y, button),
  };

  return runMain();
}
