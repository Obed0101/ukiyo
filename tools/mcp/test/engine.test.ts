import { describe, expect, test } from "bun:test";
import { parseHeadless } from "../src/engine.ts";

describe("headless output parsing", () => {
  test("reads snapshots, captures and the audio report; keeps engine info lines", () => {
    const stdout = [
      "[ukiyo] target=headless game=LanternRun",
      'snapshot {"tick":0,"entities":{}}',
      'snapshot {"tick":60,"entities":{}}',
      "capture /tmp/frames/headless-tick-000060.png",
      "audio /tmp/run.wav seconds=1 sounds=3 peak=0.6123 rms=0.0412",
      "noise that is not protocol",
    ].join("\n");
    const output = parseHeadless(stdout);

    expect(output.snapshots.map((s) => s.tick)).toEqual([0, 60]);
    expect(output.captures).toEqual(["/tmp/frames/headless-tick-000060.png"]);
    expect(output.audio).toEqual({ path: "/tmp/run.wav", seconds: 1, sounds: 3, peak: 0.6123, rms: 0.0412 });
    expect(output.info).toEqual(["[ukiyo] target=headless game=LanternRun"]);
  });

  test("no audio line means no audio report", () => {
    expect(parseHeadless('snapshot {"tick":0}').audio).toBeNull();
  });
});
