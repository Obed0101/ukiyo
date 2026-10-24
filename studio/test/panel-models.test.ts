import { describe, expect, test } from "bun:test";
import { bounded, filterEntries, foldRepeats, levelOfOutput, type ConsoleEntry } from "../web/console-model.js";
import { flatIds, groupEntities, stepId } from "../web/hierarchy-model.js";
import { buildRetargetPatch, describePatchResult, nextPatchId, parseNumber, patchOutcome, patchTick } from "../web/patch-model.js";

const pose = { position: [0, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] };

describe("hierarchy grouping", () => {
  test("a scene game gets one group named after the scene, in scene-file order, patch spawns last", () => {
    const snapshot = { entities: { moth: pose, "bat-3": pose, "lamp-west": pose, "lamp-east": pose }, scene: { active: "meadow" } };
    const sceneEntities = [{ id: "lamp-west", prefab: "lantern" }, { id: "lamp-east", prefab: "lantern" }, { id: "moth", prefab: "drifter" }];
    const [group] = groupEntities(snapshot, { sceneEntities });
    expect(group!.scene).toBe("meadow");
    expect(group!.rows.map((r) => r.id)).toEqual(["lamp-west", "lamp-east", "moth", "bat-3"]);
    expect(group!.rows[0]!.prefab).toBe("lantern");
    expect(group!.rows[3]!.prefab).toBeNull();
  });

  test("without a scene the list is flat and sorted naturally", () => {
    const groups = groupEntities({ entities: { b10: pose, b2: pose, a: pose } });
    expect(groups).toHaveLength(1);
    expect(groups[0]!.scene).toBeNull();
    expect(groups[0]!.rows.map((r) => r.id)).toEqual(["a", "b2", "b10"]);
  });

  test("the filter matches id or prefab and keyboard stepping stays inside the shown order", () => {
    const snapshot = { entities: { a: pose, b: pose, c: pose }, scene: { active: "s" } };
    const groups = groupEntities(snapshot, { query: "LANT", sceneEntities: [{ id: "a", prefab: "lantern" }, { id: "c", prefab: "lantern" }, { id: "b", prefab: "moth" }] });
    const ids = flatIds(groups);
    expect(ids).toEqual(["a", "c"]);
    expect(stepId(ids, null, 1)).toBe("a");
    expect(stepId(ids, "a", 1)).toBe("c");
    expect(stepId(ids, "c", 1)).toBe("c");
    expect(stepId(ids, "a", -1)).toBe("a");
  });

  test("no entities gives no group", () => {
    expect(groupEntities({ entities: {} })).toEqual([]);
  });
});

describe("inspector patch request", () => {
  test("a retarget patch carries id, tick, scene guard and a 2D position when z is 0", () => {
    expect(buildRetargetPatch({ patchId: "p1", tick: 41, scene: "meadow", entity: "lamp-west", position: [2, 1, 0] })).toEqual({
      id: "p1", tick: 41, scene: "meadow", ops: [{ op: "retarget", id: "lamp-west", position: [2, 1] }],
    });
  });

  test("z is kept when it is not 0 and no scene means no guard", () => {
    const patch = buildRetargetPatch({ patchId: "p2", tick: 3, scene: null, entity: "e", position: [0, 0, 1.5] });
    expect(patch).toEqual({ id: "p2", tick: 3, ops: [{ op: "retarget", id: "e", position: [0, 0, 1.5] }] });
    expect("scene" in patch).toBe(false);
  });

  test("the target tick is now when paused and the next boundary when running", () => {
    expect(patchTick({ tick: 10, paused: true })).toBe(10);
    expect(patchTick({ tick: 10, paused: false })).toBe(11);
  });

  test("patch ids are unique and valid scene ids", () => {
    const a = nextPatchId();
    const b = nextPatchId();
    expect(a).not.toBe(b);
    expect(a).toMatch(/^[A-Za-z0-9_.-]{1,64}$/);
  });

  test("results and outcomes are read from what the game reported", () => {
    expect(describePatchResult({ id: "p", tick: 5, status: "queued", currentTick: 4, paused: false })).toMatchObject({ ok: true, text: "Queued for tick 5" });
    expect(describePatchResult({ status: "applied", tick: 4 })).toMatchObject({ text: "Applied at tick 4" });
    expect(describePatchResult(null).ok).toBe(false);
    const snapshot = { scene: { patches: [{ id: "a", tick: 7 }], rejected: [{ id: "r", tick: 8, code: "OutOfRange", message: "[SCENE:OutOfRange]: x" }] } };
    expect(patchOutcome(snapshot, "a")).toEqual({ state: "applied", tick: 7 });
    expect(patchOutcome(snapshot, "r")).toEqual({ state: "rejected", message: "[SCENE:OutOfRange]: x" });
    expect(patchOutcome(snapshot, "z")).toEqual({ state: "pending" });
  });

  test("parseNumber refuses blanks and non-finite input", () => {
    expect(parseNumber(" 2.5 ")).toBe(2.5);
    expect(parseNumber("")).toBeNull();
    expect(parseNumber("abc")).toBeNull();
    expect(parseNumber("Infinity")).toBeNull();
  });
});

describe("console model", () => {
  const entry = (text: string, level: ConsoleEntry["level"] = "info", source = "G"): ConsoleEntry => ({ key: text, at: "2026-01-01T00:00:00Z", source, level, text });

  test("identical consecutive lines fold with a count, non-adjacent ones do not", () => {
    const folded = foldRepeats([entry("a"), entry("a"), entry("a"), entry("b"), entry("a")]);
    expect(folded.map((e) => [e.text, e.count])).toEqual([["a", 3], ["b", 1], ["a", 1]]);
  });

  test("level filter keeps errors for Errors and errors plus warnings for Warnings; search is case-insensitive", () => {
    const all = [entry("fine"), entry("careful", "warn"), entry("boom", "error")];
    expect(filterEntries(all, { level: "errors", query: "" }).map((e) => e.text)).toEqual(["boom"]);
    expect(filterEntries(all, { level: "warnings", query: "" }).map((e) => e.text)).toEqual(["careful", "boom"]);
    expect(filterEntries(all, { level: "all", query: "CARE" }).map((e) => e.text)).toEqual(["careful"]);
  });

  test("stderr is an error and words decide stdout", () => {
    expect(levelOfOutput("stderr", "hello")).toBe("error");
    expect(levelOfOutput("stdout", "warning CS8618: x")).toBe("warn");
    expect(levelOfOutput("stdout", "Unhandled exception")).toBe("error");
    expect(levelOfOutput("stdout", "tick 5")).toBe("info");
  });

  test("the client list is bounded to the newest entries", () => {
    expect(bounded([1, 2, 3, 4, 5], 3)).toEqual([3, 4, 5]);
  });
});
