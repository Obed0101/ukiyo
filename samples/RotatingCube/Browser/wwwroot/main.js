// Browser shell: boots .NET WebAssembly, registers the Three adapter and the frame coordinator, then runs the
// shared Program.cs. This file never touches game state: requestAnimationFrame only forwards time to C#.
import { dotnet } from "./_framework/dotnet.js";
import * as threeAdapter from "./lib/ukiyo-three.js";

const canvas = document.getElementById("ukiyo-canvas");
const status = document.getElementById("status");
const query = new URLSearchParams(location.search);
const args = [];
if (query.has("checkpoints")) args.push("--checkpoints", query.get("checkpoints"));
if (query.has("paused")) args.push("--paused");
if (query.has("fixed")) args.push("--fixed-size");

const { setModuleImports, getAssemblyExports, runMain } = await dotnet.withApplicationArguments(...args).create();
const exports = await getAssemblyExports("Ukiyo.Platform.Browser.dll");
const host = exports.Ukiyo.Hosting.BrowserExports;
const pixelSize = () => [Math.max(1, Math.round(canvas.clientWidth * devicePixelRatio)), Math.max(1, Math.round(canvas.clientHeight * devicePixelRatio))];

setModuleImports("ukiyo-three", threeAdapter);
setModuleImports("ukiyo-host", {
  canvasPixelSize: () => pixelSize().join("x"),
  setCanvasSize: (w, h) => { canvas.style.width = `${w / devicePixelRatio}px`; canvas.style.height = `${h / devicePixelRatio}px`; },
  startLoop: () => {
    let paused = false;
    addEventListener("keydown", (e) => {
      if (e.key === " " || e.key === "p") { paused = !paused; host.SetPaused(paused); }
      if (e.key === "s" && paused) status.textContent = host.Step(1);
    });
    const frame = (t) => {
      const [w, h] = pixelSize();
      host.Frame(t, w, h);
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
    status.textContent = "space/P pause · S step · C# computes every tick";
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
globalThis.ukiyo = { snapshot: () => JSON.parse(host.Snapshot()), step: (n = 1) => JSON.parse(host.Step(n)), setPaused: (p) => host.SetPaused(p) };

runMain().catch((error) => { status.textContent = `error: ${error.message}`; console.error(error); });
