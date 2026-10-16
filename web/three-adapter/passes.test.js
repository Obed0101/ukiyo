// Multi-camera pass tests. The minimap golden files are produced by the C# encoder
// (tests/Ukiyo.Tests/PassTests.Minimap_packets_match_the_golden_files_shared_with_the_js_decoder).
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { FLAG_LIGHTING, FLAG_PASSES, FLAG_SPRITES, MAX_PASSES, PassSequencer, RenderError, decodeFrame, decodeResources } from "./protocol.js";

const fixture = (name) => new Uint8Array(readFileSync(new URL(`./fixtures/${name}`, import.meta.url)));
const expectCode = (fn, code) => {
  try {
    fn();
  } catch (error) {
    expect(error).toBeInstanceOf(RenderError);
    expect(error.code).toBe(code);
    return;
  }
  throw new Error(`expected [RENDER:${code}]`);
};
const flags = (bytes) => new DataView(bytes.buffer, bytes.byteOffset).getUint16(6, true);
// The pass section is always last: u32 id, count, primary, pad, x, y, width, height.
const PASS_SECTION = 32;
const mutate = (name, word, value) => {
  const bytes = fixture(name).slice();
  new DataView(bytes.buffer).setUint32(bytes.length - PASS_SECTION + word * 4, value, true);
  return bytes;
};

// MinimapGame.MinimapRect at 640×480: side 480·17/50 = 163, margin 480·3/100 = 14.
const MAP_RECT = { x: 640 - 163 - 14, y: 14, width: 163, height: 163 };

describe("minimap golden packets from the C# encoder", () => {
  test("pass 0: the whole buffer, primary, lit, no sprites", () => {
    const bytes = fixture("minimap-frame-tick60-pass0.bin");
    expect(flags(bytes)).toBe(FLAG_LIGHTING | FLAG_PASSES);
    const frame = decodeFrame(bytes);
    expect(frame.tick).toBe(60);
    expect(frame.pass).toEqual({ id: 0, count: 2, primary: 0, x: 0, y: 0, width: 640, height: 480 });
    expect(frame.sprites).toHaveLength(0);
  });

  test("pass 1: the minimap rect, the next sequence, its own lighting and four frame sprites in pass-local pixels", () => {
    const main = decodeFrame(fixture("minimap-frame-tick60-pass0.bin"));
    const bytes = fixture("minimap-frame-tick60-pass1.bin");
    expect(flags(bytes)).toBe(FLAG_SPRITES | FLAG_LIGHTING | FLAG_PASSES);
    const map = decodeFrame(bytes);
    expect(map.sequence).toBe(main.sequence + 1);
    expect(map.tick).toBe(main.tick);
    expect(map.pass).toEqual({ id: 1, count: 2, primary: 0, ...MAP_RECT });
    expect(map.sprites).toHaveLength(4);
    expect(map.sprites.every((s) => s.space === 2)).toBe(true);
    expect(Array.from(map.sprites[0].position)).toEqual([0, 0]);
    expect(map.sprites[0].size[0]).toBe(MAP_RECT.width);
    expect(map.lighting.ambient[0]).toBeGreaterThan(main.lighting.ambient[0]);
    expect(map.clearColor[0]).not.toBe(main.clearColor[0]);
  });

  test("resources: one cube and one floor with normals, lit materials, the 1×1 frame texture", () => {
    const commands = decodeResources(fixture("minimap-resources.bin"));
    expect(commands.filter((c) => c.kind === 1).map((c) => c.hasNormals)).toEqual([true, true]);
    expect(commands.filter((c) => c.kind === 2).every((c) => c.lit)).toBe(true);
    expect(commands.filter((c) => c.kind === 4).map((c) => [c.width, c.height])).toEqual([[1, 1]]);
  });

  test("single-camera fixtures (G0 cube, lit cube) decode with no pass", () => {
    for (const name of ["cube-frame-tick60.bin", "lit-cube-frame-tick60.bin"]) {
      expect(flags(fixture(name)) & FLAG_PASSES).toBe(0);
      expect(decodeFrame(fixture(name)).pass).toBeNull();
    }
  });
});

describe("malformed pass sections fail with a typed error", () => {
  test(`more than ${MAX_PASSES} passes, or none`, () => {
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 1, MAX_PASSES + 1)), "OutOfRange");
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 1, 0)), "OutOfRange");
  });

  test("id or primary outside the pass count", () => {
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 0, 2)), "OutOfRange");
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 2, 7)), "OutOfRange");
  });

  test("rect outside the drawing buffer, empty, or negative as int32", () => {
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 6, 10_000)), "OutOfRange");
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 7, 0)), "OutOfRange");
    expectCode(() => decodeFrame(mutate("minimap-frame-tick60-pass1.bin", 4, 0x80000000)), "OutOfRange");
  });

  test("an unknown frame flag next to the pass flag", () => {
    const bytes = fixture("minimap-frame-tick60-pass1.bin").slice();
    new DataView(bytes.buffer).setUint16(6, FLAG_SPRITES | FLAG_LIGHTING | FLAG_PASSES | 0x8, true);
    expectCode(() => decodeFrame(bytes), "InvalidPacket");
  });
});

describe("frame assembly (PassSequencer mirrors src/Ukiyo.Render/Passes.cs)", () => {
  const pass = (sequence, id, { count = 2, tick = 1, primary = 0 } = {}) => ({ sequence, tick, viewport: { width: 640, height: 480 }, pass: { id, count, primary, x: 0, y: 0, width: 10, height: 10 } });
  const single = (sequence) => ({ sequence, tick: 1, viewport: { width: 640, height: 480 }, pass: null });

  test("a single-camera packet and the last pass complete a frame", () => {
    const sequencer = new PassSequencer();
    expect(sequencer.accept(single(1))).toBe(true);
    expect(sequencer.accept(pass(2, 0))).toBe(false);
    expect(sequencer.open).toBe(true);
    expect(sequencer.accept(pass(3, 1))).toBe(true);
    expect(sequencer.open).toBe(false);
  });

  test("the golden minimap frame assembles in order", () => {
    const sequencer = new PassSequencer();
    expect(sequencer.accept(decodeFrame(fixture("minimap-frame-tick60-pass0.bin")))).toBe(false);
    expect(sequencer.accept(decodeFrame(fixture("minimap-frame-tick60-pass1.bin")))).toBe(true);
  });

  test("out of order, interrupted or inconsistent passes are InvalidPacket", () => {
    const cases = [
      [pass(1, 1)],
      [pass(1, 0), single(2)],
      [pass(1, 0), pass(3, 1)],
      [pass(1, 0), pass(2, 1, { tick: 2 })],
      [pass(1, 0, { count: 3 }), pass(2, 1)],
      [pass(1, 0), pass(2, 1, { primary: 1 })],
      [pass(1, 0), pass(2, 0)],
    ];
    for (const packets of cases) {
      const sequencer = new PassSequencer();
      packets.slice(0, -1).forEach((p) => sequencer.accept(p));
      expectCode(() => sequencer.accept(packets[packets.length - 1]), "InvalidPacket");
      // The next frame starts clean.
      expect(sequencer.accept(single(100))).toBe(true);
    }
  });

  test("capture while a frame is incomplete is InvalidPacket", () => {
    const sequencer = new PassSequencer();
    sequencer.accept(pass(1, 0));
    expectCode(() => sequencer.requireComplete(), "InvalidPacket");
    sequencer.accept(pass(2, 1));
    sequencer.requireComplete();
  });
});
