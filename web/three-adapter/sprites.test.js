// 2D layer of the protocol: the cube golden frame plus an appended sprite section, laid out exactly as
// PacketCodec.Encode writes it (docs/protocol.md, "2D section"). Geometry is checked against the C# expectations
// in tests/Ukiyo.Tests/SpriteProtocolTests.cs.
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { CommandKind, FLAG_SPRITES, RenderError, ResourceKind, decodeFrame, decodeResources } from "./protocol.js";
import { SpriteSpace, spriteDrawOrder, spriteQuad } from "./sprite-geometry.js";

const fixture = (name) => new Uint8Array(readFileSync(new URL(`./fixtures/${name}`, import.meta.url)));

function withSprites(sprites, camera = [4, 5, 12]) {
  const base = fixture("cube-frame-tick60.bin");
  const out = new Uint8Array(base.length + 16 + 4 + sprites.length * 84);
  out.set(base);
  const view = new DataView(out.buffer);
  view.setUint16(6, FLAG_SPRITES, true);
  view.setUint32(12, out.length, true);
  let o = base.length;
  const f32 = (v) => { view.setFloat32(o, v, true); o += 4; };
  const u32 = (v) => { view.setUint32(o, v, true); o += 4; };
  f32(camera[0]); f32(camera[1]); f32(camera[2]); f32(0);
  u32(sprites.length);
  for (const s of sprites) {
    u32(ResourceKind.Texture); u32(0); u32(1); u32(0);
    u32(s.space); view.setInt32(o, s.layer, true); o += 4;
    [...s.position, ...s.size, ...s.pivot, s.rotation, ...s.uv, ...s.color].forEach(f32);
  }
  return out;
}

const sprite = (overrides = {}) => ({
  space: SpriteSpace.Screen,
  layer: 0,
  position: [10, 20],
  size: [16, 16],
  pivot: [0, 0],
  rotation: 0,
  uv: [0, 0, 1, 1],
  color: [1, 0.5, 0.25, 1],
  ...overrides,
});

describe("2D section", () => {
  test("decodes the 2D camera and every sprite field", () => {
    const frame = decodeFrame(withSprites([sprite({ space: SpriteSpace.World, layer: -3 }), sprite()]));
    expect(Array.from(frame.camera2D.center)).toEqual([4, 5]);
    expect(frame.camera2D.viewHeight).toBe(12);
    expect(frame.sprites).toHaveLength(2);
    expect(frame.sprites[0].layer).toBe(-3);
    expect(frame.sprites[0].texture).toEqual({ kind: ResourceKind.Texture, index: 0, generation: 1 });
    expect(Array.from(frame.sprites[1].color)).toEqual([1, 0.5, 0.25, 1]);
    expect(frame.instances).toHaveLength(1);
  });

  test("frames without the flag decode with no sprites (G0 bytes unchanged)", () => {
    expect(decodeFrame(fixture("cube-frame-tick60.bin")).sprites).toEqual([]);
  });

  test("unknown flag bits are rejected", () => {
    const bytes = fixture("cube-frame-tick60.bin").slice();
    new DataView(bytes.buffer).setUint16(6, 0x8, true);
    expect(() => decodeFrame(bytes)).toThrow(RenderError);
  });

  test("sprite quads match the C# geometry", () => {
    const screen = spriteQuad(sprite(), { center: [0, 0], viewHeight: 10 }, 200, 100);
    expect([screen[0].x, screen[0].y, screen[2].x, screen[2].y]).toEqual([10, 20, 26, 36]);
    const world = spriteQuad(sprite({ space: SpriteSpace.World, position: [0, 0], size: [2, 2], pivot: [0.5, 0.5] }), { center: [0, 0], viewHeight: 10 }, 200, 100);
    expect([world[0].x, world[0].y, world[2].x, world[2].y]).toEqual([90, 40, 110, 60]);
  });

  test("draw order is world, then screen, then layer, then submission", () => {
    const sprites = [sprite(), sprite({ space: SpriteSpace.World, layer: 5 }), sprite({ space: SpriteSpace.World, layer: 1 }), sprite()];
    expect(spriteDrawOrder(sprites)).toEqual([2, 1, 0, 3]);
  });
});

describe("texture resources", () => {
  test("decodes size, filter and a copy of the pixels", () => {
    const pixels = [255, 0, 0, 255, 0, 0, 255, 0];
    const bytes = new Uint8Array(16 + 12 + 12 + pixels.length);
    const view = new DataView(bytes.buffer);
    view.setUint32(0, 0x42524b55, true);
    view.setUint16(4, 1, true);
    view.setUint32(8, 1, true);
    view.setUint32(12, bytes.length, true);
    bytes[16] = CommandKind.CreateTexture;
    bytes[17] = ResourceKind.Texture;
    view.setUint32(20, 0, true);
    view.setUint32(24, 1, true);
    view.setUint32(28, 2, true);
    view.setUint32(32, 1, true);
    view.setUint32(36, 1, true);
    bytes.set(pixels, 40);
    const [command] = decodeResources(bytes);
    expect([command.width, command.height, command.filter]).toEqual([2, 1, 1]);
    expect(Array.from(command.rgba)).toEqual(pixels);
  });
});
