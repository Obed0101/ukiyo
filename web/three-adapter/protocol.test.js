// JS side of the bridge tests. The golden files are produced by the C# encoder (tests/Ukiyo.Tests,
// BridgeTests.Cube_packets_match_the_golden_files_shared_with_the_js_decoder), so both sides agree on bytes.
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { CommandKind, RenderError, decodeFrame, decodeResources } from "./protocol.js";

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

describe("golden packets from the C# encoder", () => {
  test("cube resource batch decodes to one mesh (24 face + 4 marker vertices) and one material", () => {
    const commands = decodeResources(fixture("cube-resources.bin"));
    expect(commands.map((c) => c.kind)).toEqual([CommandKind.CreateMesh, CommandKind.CreateMaterial]);
    expect(commands[0].vertexCount).toBe(28);
    expect(commands[0].indices.length).toBe(42);
    expect(commands[1].useVertexColors).toBe(true);
    expect(Array.from(commands[1].baseColor)).toEqual([1, 1, 1, 1]);
  });

  test("tick 60 frame decodes with one instance and a rigid world matrix", () => {
    const frame = decodeFrame(fixture("cube-frame-tick60.bin"));
    expect(frame.tick).toBe(60);
    expect(frame.viewport).toEqual({ width: 640, height: 480 });
    expect(frame.instances).toHaveLength(1);
    const m = frame.instances[0].world;
    // Column-major: translation at 12..14 (cube at origin), homogeneous row [3, 7, 11, 15] = [0, 0, 0, 1].
    expect([m[12], m[13], m[14], m[15]]).toEqual([0, 0, 0, 1]);
    expect([m[3], m[7], m[11]]).toEqual([0, 0, 0]);
  });
});

describe("malformed input fails with a typed error", () => {
  test("truncated frame", () => {
    const bytes = fixture("cube-frame-tick60.bin");
    expectCode(() => decodeFrame(bytes.subarray(0, bytes.length - 4)), "TruncatedPayload");
  });

  test("unsupported version", () => {
    const bytes = fixture("cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setUint16(4, 2, true);
    expectCode(() => decodeFrame(bytes), "UnsupportedVersion");
  });

  test("bad magic", () => {
    const bytes = fixture("cube-frame-tick60.bin").slice();
    bytes[0] ^= 0xff;
    expectCode(() => decodeFrame(bytes), "InvalidPacket");
  });

  test("NaN inside the world matrix", () => {
    const bytes = fixture("cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setFloat32(bytes.length - 4, Number.NaN, true);
    expectCode(() => decodeFrame(bytes), "NonFiniteValue");
  });

  test("index outside the vertex range", () => {
    const bytes = fixture("cube-resources.bin").slice();
    // First mesh command: 16-byte header + 12-byte command head + 8 bytes counts + 28 vertices * 24 bytes.
    const firstIndexOffset = 16 + 12 + 8 + 28 * 24;
    new DataView(bytes.buffer).setUint32(firstIndexOffset, 999, true);
    expectCode(() => decodeResources(bytes), "OutOfRange");
  });

  test("resource batch passed as a frame", () => {
    expectCode(() => decodeFrame(fixture("cube-resources.bin")), "InvalidPacket");
  });
});
