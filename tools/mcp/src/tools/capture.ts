import type { Tool } from "../protocol.ts";
import { runHeadless, runSchema } from "./run.ts";

const capture: Tool = {
  name: "ukiyo_capture",
  title: "Capture frames",
  description:
    "Like ukiyo_run, but renders each checkpoint with the deterministic CPU renderer and returns the PNG frames (also saved to disk). This is visual evidence of what the game draws, without a GPU. Pair captures with snapshots when you report a visual change. Multi-camera games (minimap, split screen) also get `passes`: per checkpoint the composed frame and each camera alone at its rect size ({pass, primary, path}); single-camera results have no `passes` key.",
  inputSchema: { ...runSchema, properties: { ...runSchema.properties, checkpoints: { ...runSchema.properties!.checkpoints!, default: [60] }, size: { ...runSchema.properties!.size!, default: "640x360" } } },
  run: (args, context) => runHeadless(args, context, true),
  cli: { outputs: ["captures", "passes"] },
};

export default capture;
