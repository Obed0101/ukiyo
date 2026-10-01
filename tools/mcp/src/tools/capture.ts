import type { Tool } from "../protocol.ts";
import { runHeadless, runSchema } from "./run.ts";

const capture: Tool = {
  name: "ukiyo_capture",
  title: "Capture frames",
  description:
    "Like ukiyo_run, but renders each checkpoint with the deterministic CPU renderer and returns the PNG frames (also saved to disk). This is visual evidence of what the game draws, without a GPU. Pair captures with snapshots when you report a visual change.",
  inputSchema: { ...runSchema, properties: { ...runSchema.properties, checkpoints: { ...runSchema.properties!.checkpoints!, default: [60] }, size: { ...runSchema.properties!.size!, default: "640x360" } } },
  run: (args, context) => runHeadless(args, context, true),
};

export default capture;
