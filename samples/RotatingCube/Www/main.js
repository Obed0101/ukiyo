// Browser entry for RotatingCube. The shared shell (lib/ukiyo-host.js, synced from web/three-adapter at build) boots .NET
// WebAssembly, wires the three.js adapter and input, and runs the shared Program.cs. Nothing here touches game state.
import { dotnet } from "./_framework/dotnet.js";
import { boot } from "./lib/ukiyo-host.js";

const status = document.getElementById("status");
const canvas = document.getElementById("ukiyo-canvas");
canvas.focus();
boot({ dotnet, canvas, status }).catch((error) => {
  status.textContent = `error: ${error.message}`;
  console.error(error);
});
