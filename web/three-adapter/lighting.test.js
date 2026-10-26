// G1Lit protocol tests. The lit golden files are produced by the C# encoder
// (tests/Ukiyo.Tests/LightingTests.Lit_cube_packets_match_the_golden_files_shared_with_the_js_decoder).
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { CommandKind, FLAG_LIGHTING, LightKind, MAX_LIGHTS, RenderError, decodeFrame, decodeResources } from "./protocol.js";

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

// Frame with 3 instances and no sprites: header 16 + tick 8 + viewport 8 + clear 16 + camera 40 + count 4 + 3 × 96.
const LIGHTING_OFFSET = 16 + 8 + 8 + 16 + 40 + 4 + 3 * 96;
const LIGHT_COUNT_OFFSET = LIGHTING_OFFSET + 12;
const FIRST_LIGHT_OFFSET = LIGHT_COUNT_OFFSET + 4;

describe("lit golden packets from the C# encoder", () => {
  test("lit cube resources: cube and floor carry normals, the lamp does not; two lit materials and one unlit", () => {
    const commands = decodeResources(fixture("lit-cube-resources.bin"));
    const meshes = commands.filter((c) => c.kind === CommandKind.CreateMesh);
    const materials = commands.filter((c) => c.kind === CommandKind.CreateMaterial);
    expect(meshes.map((m) => m.hasNormals)).toEqual([true, true, false]);
    expect(meshes.map((m) => m.stride)).toEqual([9, 9, 6]);
    expect(meshes[0].vertexCount).toBe(28);
    // Every cube normal is a unit axis: +X face first.
    expect(Array.from(meshes[0].interleaved.subarray(6, 9))).toEqual([1, 0, 0]);
    expect(materials.map((m) => m.lit)).toEqual([true, true, false]);
    expect(materials.map((m) => m.useVertexColors)).toEqual([true, false, false]);
  });

  test("lit cube tick 60 frame: flag 0x2, ambient, a directional key light and the orbiting point light", () => {
    const bytes = fixture("lit-cube-frame-tick60.bin");
    expect(new DataView(bytes.buffer, bytes.byteOffset).getUint16(6, true)).toBe(FLAG_LIGHTING);
    const frame = decodeFrame(bytes);
    expect(frame.tick).toBe(60);
    expect(frame.instances).toHaveLength(3);
    expect(frame.lighting.lights.map((l) => l.kind)).toEqual([LightKind.Directional, LightKind.Point]);
    expect(frame.lighting.lights[0].range).toBe(0);
    expect(frame.lighting.lights[1].range).toBeGreaterThan(0);
    expect(frame.lighting.ambient[2]).toBeGreaterThan(0);
  });

  test("G0 fixtures still decode with no lighting and no normals", () => {
    expect(decodeFrame(fixture("cube-frame-tick60.bin")).lighting).toBeNull();
    const [mesh, material] = decodeResources(fixture("cube-resources.bin"));
    expect(mesh.hasNormals).toBe(false);
    expect(material.lit).toBe(false);
  });
});

describe("malformed lit input fails with a typed error", () => {
  test(`more than ${MAX_LIGHTS} lights`, () => {
    const bytes = fixture("lit-cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setUint32(LIGHT_COUNT_OFFSET, MAX_LIGHTS + 1, true);
    expectCode(() => decodeFrame(bytes), "OutOfRange");
  });

  test("NaN light intensity", () => {
    const bytes = fixture("lit-cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setFloat32(FIRST_LIGHT_OFFSET + 28, Number.NaN, true);
    expectCode(() => decodeFrame(bytes), "NonFiniteValue");
  });

  test("unknown light kind", () => {
    const bytes = fixture("lit-cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setUint32(FIRST_LIGHT_OFFSET, 7, true);
    expectCode(() => decodeFrame(bytes), "InvalidPacket");
  });

  test("negative light color", () => {
    const bytes = fixture("lit-cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setFloat32(FIRST_LIGHT_OFFSET + 16, -1, true);
    expectCode(() => decodeFrame(bytes), "OutOfRange");
  });

  test("point light with zero range", () => {
    const bytes = fixture("lit-cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setFloat32(FIRST_LIGHT_OFFSET + 40 + 32, 0, true);
    expectCode(() => decodeFrame(bytes), "OutOfRange");
  });

  test("lighting flag set without a lighting section", () => {
    const bytes = fixture("cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setUint16(6, FLAG_LIGHTING, true);
    expectCode(() => decodeFrame(bytes), "TruncatedPayload");
  });

  test("unknown material flag bit", () => {
    const bytes = fixture("cube-resources.bin").slice();
    new DataView(bytes.buffer).setUint32(bytes.length - 4, 0x4, true);
    expectCode(() => decodeResources(bytes), "InvalidPacket");
  });

  test("zero-length normal", () => {
    const bytes = fixture("lit-cube-resources.bin").slice();
    // First mesh: 16-byte header + 12-byte command head + 8 bytes counts; its normal is floats 6..8 of vertex 0.
    const firstNormal = 16 + 12 + 8 + 6 * 4;
    const view = new DataView(bytes.buffer);
    for (let i = 0; i < 3; i++) view.setFloat32(firstNormal + i * 4, 0, true);
    expectCode(() => decodeResources(bytes), "OutOfRange");
  });
});
