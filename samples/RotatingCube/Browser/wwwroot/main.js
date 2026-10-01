// Browser entry for RotatingCube. The shared shell (web/three-adapter/ukiyo-host.js) boots .NET WebAssembly, wires
// the three.js adapter and input, and runs the shared Program.cs. Nothing here touches game state.
import { dotnet } from "./_framework/dotnet.js";
import { boot } from "./lib/ukiyo-host.js";

const status = document.getElementById("status");
boot({ dotnet, canvas: document.getElementById("ukiyo-canvas"), status }).catch((error) => {
  status.textContent = `error: ${error.message}`;
  console.error(error);
});
