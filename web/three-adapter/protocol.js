// Decoder for the Ukiyo render protocol v1 (little-endian). Mirrors src/Ukiyo.Render/PacketCodec.cs; the
// layout is documented in docs/protocol.md. Every malformed input throws an Error whose message starts with
// "[RENDER:<Code>]" so the C# facade can map it back to a RenderException instead of dropping the frame.

export const RESOURCE_MAGIC = 0x42524b55; // "UKRB"
export const FRAME_MAGIC = 0x50524b55; // "UKRP"
export const VERSION = 1;
export const HEADER_SIZE = 16;
export const MAX_VERTICES = 1 << 20;
export const MAX_INDICES = 3 << 20;
export const MAX_INSTANCES = 1 << 16;

export const CommandKind = Object.freeze({ CreateMesh: 1, CreateMaterial: 2, Destroy: 3 });
export const ResourceKind = Object.freeze({ Mesh: 1, Material: 2 });

export class RenderError extends Error {
  constructor(code, message) {
    super(`[RENDER:${code}]: ${message}`);
    this.code = code;
  }
}

class Reader {
  constructor(bytes) {
    if (!(bytes instanceof Uint8Array)) throw new RenderError("InvalidPacket", "payload must be a Uint8Array");
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    this.offset = 0;
  }

  need(n) {
    if (this.offset + n > this.view.byteLength) {
      throw new RenderError("TruncatedPayload", `need ${n} bytes at offset ${this.offset}, have ${this.view.byteLength - this.offset}`);
    }
  }

  u8() { this.need(1); return this.view.getUint8(this.offset++); }
  u16() { this.need(2); const v = this.view.getUint16(this.offset, true); this.offset += 2; return v; }
  u32() { this.need(4); const v = this.view.getUint32(this.offset, true); this.offset += 4; return v; }
  f32() {
    this.need(4);
    const v = this.view.getFloat32(this.offset, true);
    this.offset += 4;
    if (!Number.isFinite(v)) throw new RenderError("NonFiniteValue", `non-finite float at offset ${this.offset - 4}`);
    return v;
  }

  floats(count) {
    const out = new Float32Array(count);
    for (let i = 0; i < count; i++) out[i] = this.f32();
    return out;
  }

  header(magic) {
    if (this.view.byteLength < HEADER_SIZE) throw new RenderError("TruncatedPayload", `${this.view.byteLength} bytes is shorter than the header`);
    if (this.u32() !== magic) throw new RenderError("InvalidPacket", "bad magic");
    const version = this.u16();
    if (version !== VERSION) throw new RenderError("UnsupportedVersion", `protocol v${version}, expected v${VERSION}`);
    this.u16();
  }

  expectLength() {
    const declared = this.u32();
    if (declared !== this.view.byteLength) throw new RenderError("TruncatedPayload", `declared ${declared} bytes, received ${this.view.byteLength}`);
  }

  count(max, what) {
    const value = this.u32();
    if (value > max) throw new RenderError("OutOfRange", `${what} count ${value} exceeds ${max}`);
    return value;
  }

  handle(expectedKind) {
    const kind = this.u32();
    const index = this.u32();
    const generation = this.u32();
    this.u32();
    if (kind !== expectedKind) throw new RenderError("WrongResourceKind", `handle of kind ${kind} where ${expectedKind} is required`);
    return { kind, index, generation };
  }

  end() {
    if (this.offset !== this.view.byteLength) throw new RenderError("InvalidPacket", `${this.view.byteLength - this.offset} trailing bytes`);
  }
}

/** Decodes a resource batch into create/destroy commands. Mesh data stays in typed arrays for direct upload. */
export function decodeResources(bytes) {
  const r = new Reader(bytes);
  r.header(RESOURCE_MAGIC);
  const count = r.u32();
  r.expectLength();
  const commands = [];
  for (let i = 0; i < count; i++) {
    const kind = r.u8();
    const resourceKind = r.u8();
    r.u16();
    const handle = { kind: resourceKind, index: r.u32(), generation: r.u32() };
    if (kind === CommandKind.CreateMesh) {
      if (resourceKind !== ResourceKind.Mesh) throw new RenderError("WrongResourceKind", "CreateMesh with a non-mesh handle");
      const vertexCount = r.count(MAX_VERTICES, "vertex");
      const indexCount = r.count(MAX_INDICES, "index");
      if (vertexCount === 0 || indexCount === 0 || indexCount % 3 !== 0) throw new RenderError("OutOfRange", `mesh ${vertexCount} vertices / ${indexCount} indices`);
      const interleaved = r.floats(vertexCount * 6);
      const indices = new Uint32Array(indexCount);
      for (let n = 0; n < indexCount; n++) {
        const index = r.u32();
        if (index >= vertexCount) throw new RenderError("OutOfRange", `index ${index} >= vertex count ${vertexCount}`);
        indices[n] = index;
      }
      commands.push({ kind, handle, vertexCount, interleaved, indices });
    } else if (kind === CommandKind.CreateMaterial) {
      if (resourceKind !== ResourceKind.Material) throw new RenderError("WrongResourceKind", "CreateMaterial with a non-material handle");
      commands.push({ kind, handle, baseColor: r.floats(4), useVertexColors: r.u32() !== 0 });
    } else if (kind === CommandKind.Destroy) {
      commands.push({ kind, handle });
    } else {
      throw new RenderError("InvalidPacket", `unknown resource command ${kind}`);
    }
  }
  r.end();
  return commands;
}

/** Decodes one frame packet. The 64-bit tick is combined from (lo, hi) as a BigInt, then exposed as a safe Number. */
export function decodeFrame(bytes) {
  const r = new Reader(bytes);
  r.header(FRAME_MAGIC);
  const sequence = r.u32();
  r.expectLength();
  const tick = BigInt(r.u32()) | (BigInt(r.u32()) << 32n);
  const viewport = { width: r.u32(), height: r.u32() };
  const clearColor = r.floats(4);
  const camera = { position: r.floats(3), rotation: r.floats(4), fovY: r.f32(), near: r.f32(), far: r.f32() };
  if (!(camera.fovY > 0 && camera.fovY < Math.PI) || !(camera.near > 0) || !(camera.far > camera.near)) {
    throw new RenderError("OutOfRange", "invalid camera projection");
  }
  const count = r.count(MAX_INSTANCES, "instance");
  const instances = [];
  for (let i = 0; i < count; i++) {
    instances.push({ mesh: r.handle(ResourceKind.Mesh), material: r.handle(ResourceKind.Material), world: r.floats(16) });
  }
  r.end();
  return { sequence, tick: Number(tick), viewport, clearColor, camera, instances };
}
