import { describe, expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { parseHeadless } from "../src/engine.ts";
import { validate } from "../src/protocol.ts";
import live, { liveRequest } from "../src/tools/live.ts";
import capture from "../src/tools/capture.ts";
import { patchLogFile, patchReport, patchSchema, runSchema } from "../src/tools/run.ts";

const spawn = { id: "p1", tick: 12, ops: [{ op: "spawn", id: "bat-3", prefab: "drifter", position: [-5, -1], values: { speed: 3 } }] };
const refusedLater = { id: "p4", tick: 80, ops: [{ op: "dispose", id: "moth" }] };
const beyond = { id: "p9", tick: 500, scene: "meadow", ops: [{ op: "retarget", id: "lamp-west", position: [1, 1, 0] }] };

describe("patch schema", () => {
  test("accepts the documented patch shapes and keeps prefab values as given", () => {
    expect(validate(patchSchema, spawn)).toEqual(spawn);
    expect(validate(runSchema, { game: "SceneHop", patches: [spawn, refusedLater, beyond] })).toMatchObject({ patches: [spawn, refusedLater, beyond] });
  });

  test.each([
    [{ ...spawn, ops: [] }, "at least 1"],
    [{ ...spawn, ops: [{ op: "teleport", id: "x" }] }, "must be one of spawn, dispose, retarget"],
    [{ ...spawn, when: "now" }, "is not a known property"],
    [{ ...spawn, id: "has space" }, "must match"],
    [{ ...spawn, tick: -1 }, ">= 0"],
    [{ id: "p", ops: [{ op: "dispose", id: "x" }] }, "tick is required"],
    [{ ...spawn, ops: [{ op: "dispose", id: "x", position: [1] }] }, "at least 2"],
  ])("refuses %j before anything runs", (patch, message) => {
    expect(() => validate(runSchema, { game: "SceneHop", patches: [patch] })).toThrow(message);
  });

  test("ukiyo_capture inherits patches from ukiyo_run", () => {
    expect(capture.inputSchema.properties?.patches).toEqual(runSchema.properties!.patches);
    expect(capture.inputSchema.properties?.patchLog).toEqual(runSchema.properties!.patchLog);
  });
});

describe("patch log for a headless run", () => {
  test("inline patches are written to the run's work directory; a path stays inside the repository", () => {
    const root = mkdtempSync(join(tmpdir(), "ukiyo-root-"));
    const work = mkdtempSync(join(tmpdir(), "ukiyo-work-"));
    try {
      const inline = patchLogFile({ patches: [spawn, refusedLater] }, root, work)!;
      expect(inline.path).toBe(join(work, "patches.json"));
      expect(JSON.parse(readFileSync(inline.path, "utf8"))).toEqual([spawn, refusedLater]);
      expect(inline.patches).toEqual([{ id: "p1", tick: 12 }, { id: "p4", tick: 80 }]);

      writeFileSync(join(root, "log.json"), JSON.stringify([beyond]));
      expect(patchLogFile({ patchLog: "log.json" }, root, work)).toEqual({ path: join(root, "log.json"), patches: [{ id: "p9", tick: 500 }] });
      writeFileSync(join(root, "broken.json"), "[{");
      expect(patchLogFile({ patchLog: "broken.json" }, root, work)).toEqual({ path: join(root, "broken.json"), patches: null });

      expect(patchLogFile({}, root, work)).toBeNull();
      expect(() => patchLogFile({ patches: [spawn], patchLog: "log.json" }, root, work)).toThrow("not both");
      expect(() => patchLogFile({ patchLog: "../outside.json" }, root, work)).toThrow("outside the ukiyo repository");
      expect(() => patchLogFile({ patchLog: "missing.json" }, root, work)).toThrow("does not exist");
    } finally {
      rmSync(root, { recursive: true, force: true });
      rmSync(work, { recursive: true, force: true });
    }
  });

  test("the report reads applied and rejected patches from the last snapshot and lists the ones not reached", () => {
    const rejection = { id: "p4", tick: 80, code: "UnknownEntity", message: "[SCENE:UnknownEntity]: patch p4 op #0 (moth): no live entity to dispose" };
    const stdout = [
      "patches /tmp/run/patches.json submitted=3",
      `snapshot ${JSON.stringify({ tick: 0, scene: { active: "meadow", loadedAt: 0, patches: [], rejected: [] } })}`,
      `snapshot ${JSON.stringify({ tick: 120, scene: { active: "meadow", loadedAt: 120, patches: [{ id: "p1", tick: 12 }], rejected: [rejection] } })}`,
    ].join("\n");
    const report = patchReport(parseHeadless(stdout), [{ id: "p1", tick: 12 }, { id: "p4", tick: 80 }, { id: "p9", tick: 500 }]);

    expect(report).toEqual({
      log: "/tmp/run/patches.json",
      submitted: 3,
      applied: [{ id: "p1", tick: 12 }],
      rejected: [rejection],
      notReached: [{ id: "p9", tick: 500 }],
    });
    expect(patchReport(parseHeadless('snapshot {"tick":0}'), null)).toBeNull();
  });
});

describe("ukiyo_live patch", () => {
  test("the action is advertised and forwards exactly one validated patch", () => {
    const args = validate(live.inputSchema, { action: "patch", pid: 7, patch: spawn }) as Record<string, unknown>;
    expect(liveRequest(args)).toEqual({ cmd: "patch", patch: spawn });
    expect(() => liveRequest(validate(live.inputSchema, { action: "patch" }) as Record<string, unknown>)).toThrow("patch needs patch");
    expect(() => validate(live.inputSchema, { action: "patch", patch: [spawn] })).toThrow("must be an object");
    expect(() => validate(live.inputSchema, { action: "patch", patch: { ...spawn, ops: [{ op: "spawn" }] } })).toThrow("id is required");
  });

  test("other actions do not carry a patch", () => {
    expect(liveRequest(validate(live.inputSchema, { action: "step", ticks: 5 }) as Record<string, unknown>)).toEqual({ cmd: "step", ticks: 5 });
    expect(liveRequest({ action: "status", patch: spawn })).toEqual({ cmd: "status" });
  });
});
