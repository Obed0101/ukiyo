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
export const MAX_SPRITES = 1 << 16;
export const MAX_TEXTURE_SIZE = 4096;
export const FLAG_SPRITES = 0x1;
export const FLAG_LIGHTING = 0x2;
export const MAX_LIGHTS = 8;
// Multi-camera frames (docs/rendering-passes.md): the packet is one pass; a 32-byte pass section follows every other one.
export const FLAG_PASSES = 0x4;
export const MAX_PASSES = 4;

// Wire command 5 is CreateMesh with per-vertex normals (G1Lit); it decodes to a CreateMesh command with hasNormals.
export const CommandKind = Object.freeze({ CreateMesh: 1, CreateMaterial: 2, Destroy: 3, CreateTexture: 4, CreateMeshWithNormals: 5 });
export const ResourceKind = Object.freeze({ Mesh: 1, Material: 2, Texture: 3 });
export const TextureFilter = Object.freeze({ Nearest: 1, Linear: 2 });
export const MaterialFlags = Object.freeze({ VertexColors: 0x1, Lit: 0x2 });
export const LightKind = Object.freeze({ Directional: 1, Point: 2 });

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

  i32() { this.need(4); const v = this.view.getInt32(this.offset, true); this.offset += 4; return v; }

  bytes(count) {
    this.need(count);
    const start = this.view.byteOffset + this.offset;
    this.offset += count;
    return new Uint8Array(this.view.buffer.slice(start, start + count));
  }

  /** Returns the header flags; any bit outside allowedFlags is an error, never ignored. */
  header(magic, allowedFlags = 0) {
    if (this.view.byteLength < HEADER_SIZE) throw new RenderError("TruncatedPayload", `${this.view.byteLength} bytes is shorter than the header`);
    if (this.u32() !== magic) throw new RenderError("InvalidPacket", "bad magic");
    const version = this.u16();
    if (version !== VERSION) throw new RenderError("UnsupportedVersion", `protocol v${version}, expected v${VERSION}`);
    const flags = this.u16();
    if ((flags & ~allowedFlags) !== 0) throw new RenderError("InvalidPacket", `unknown flags 0x${flags.toString(16)}`);
    return flags;
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
    if (kind === CommandKind.CreateMesh || kind === CommandKind.CreateMeshWithNormals) {
      if (resourceKind !== ResourceKind.Mesh) throw new RenderError("WrongResourceKind", "CreateMesh with a non-mesh handle");
      const hasNormals = kind === CommandKind.CreateMeshWithNormals;
      const stride = hasNormals ? 9 : 6;
      const vertexCount = r.count(MAX_VERTICES, "vertex");
      const indexCount = r.count(MAX_INDICES, "index");
      if (vertexCount === 0 || indexCount === 0 || indexCount % 3 !== 0) throw new RenderError("OutOfRange", `mesh ${vertexCount} vertices / ${indexCount} indices`);
      const interleaved = r.floats(vertexCount * stride);
      if (hasNormals) {
        for (let v = 0; v < vertexCount; v++) {
          const at = v * stride + 6;
          if (interleaved[at] === 0 && interleaved[at + 1] === 0 && interleaved[at + 2] === 0) throw new RenderError("OutOfRange", `vertex ${v} normal has zero length`);
        }
      }
      const indices = new Uint32Array(indexCount);
      for (let n = 0; n < indexCount; n++) {
        const index = r.u32();
        if (index >= vertexCount) throw new RenderError("OutOfRange", `index ${index} >= vertex count ${vertexCount}`);
        indices[n] = index;
      }
      commands.push({ kind: CommandKind.CreateMesh, handle, vertexCount, stride, hasNormals, interleaved, indices });
    } else if (kind === CommandKind.CreateMaterial) {
      if (resourceKind !== ResourceKind.Material) throw new RenderError("WrongResourceKind", "CreateMaterial with a non-material handle");
      const baseColor = r.floats(4);
      const flags = r.u32();
      if ((flags & ~(MaterialFlags.VertexColors | MaterialFlags.Lit)) !== 0) throw new RenderError("InvalidPacket", `unknown material flags 0x${flags.toString(16)}`);
      commands.push({ kind, handle, baseColor, useVertexColors: (flags & MaterialFlags.VertexColors) !== 0, lit: (flags & MaterialFlags.Lit) !== 0 });
    } else if (kind === CommandKind.CreateTexture) {
      if (resourceKind !== ResourceKind.Texture) throw new RenderError("WrongResourceKind", "CreateTexture with a non-texture handle");
      const width = r.count(MAX_TEXTURE_SIZE, "texture width");
      const height = r.count(MAX_TEXTURE_SIZE, "texture height");
      const filter = r.u32();
      if (width === 0 || height === 0) throw new RenderError("OutOfRange", `texture ${width}x${height}`);
      if (filter !== TextureFilter.Nearest && filter !== TextureFilter.Linear) throw new RenderError("InvalidPacket", `unknown texture filter ${filter}`);
      commands.push({ kind, handle, width, height, filter, rgba: r.bytes(width * height * 4) });
    } else if (kind === CommandKind.Destroy) {
      if (resourceKind < ResourceKind.Mesh || resourceKind > ResourceKind.Texture) throw new RenderError("WrongResourceKind", `destroy of unknown kind ${resourceKind}`);
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
  const flags = r.header(FRAME_MAGIC, FLAG_SPRITES | FLAG_LIGHTING | FLAG_PASSES);
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
  let camera2D = { center: new Float32Array([0, 0]), viewHeight: 10 };
  const sprites = [];
  if (flags & FLAG_SPRITES) {
    camera2D = { center: r.floats(2), viewHeight: r.f32() };
    r.f32();
    if (!(camera2D.viewHeight > 0)) throw new RenderError("OutOfRange", `2D camera view height ${camera2D.viewHeight}`);
    const spriteCount = r.count(MAX_SPRITES, "sprite");
    for (let i = 0; i < spriteCount; i++) {
      const texture = r.handle(ResourceKind.Texture);
      const space = r.u32();
      if (space !== 1 && space !== 2) throw new RenderError("InvalidPacket", `unknown sprite space ${space}`);
      sprites.push({
        texture,
        space,
        layer: r.i32(),
        position: r.floats(2),
        size: r.floats(2),
        pivot: r.floats(2),
        rotation: r.f32(),
        uv: r.floats(4),
        color: r.floats(4),
      });
    }
  }
  const lighting = flags & FLAG_LIGHTING ? readLighting(r) : null;
  const pass = flags & FLAG_PASSES ? readPass(r, viewport) : null;
  r.end();
  return { sequence, tick: Number(tick), viewport, clearColor, camera, instances, camera2D, sprites, lighting, pass };
}

/** Pass section; the same checks as RenderValidation.Validate(FramePass, drawing buffer) in C#. */
function readPass(r, viewport) {
  const id = r.u32();
  const count = r.u32();
  const primary = r.u32();
  r.u32();
  const pass = { id, count, primary, x: r.u32(), y: r.u32(), width: r.u32(), height: r.u32() };
  if (count < 1 || count > MAX_PASSES) throw new RenderError("OutOfRange", `pass count ${count} outside 1..${MAX_PASSES}`);
  if (id >= count || primary >= count) throw new RenderError("OutOfRange", `pass id ${id} or primary ${primary} outside 0..${count - 1}`);
  // u32 values above int32 are negative in C#; both sides reject them.
  if ([pass.x, pass.y, pass.width, pass.height].some((v) => v > 0x7fffffff)) throw new RenderError("OutOfRange", `pass ${id} rect has a negative value`);
  if (viewport.width > 0 && viewport.height > 0) requirePassInside(pass, viewport.width, viewport.height);
  return pass;
}

/** The rect must be at least 1×1 and inside the drawing buffer (RenderValidation.RequirePassInside). */
export function requirePassInside(pass, width, height) {
  if (pass.width < 1 || pass.height < 1 || pass.x + pass.width > width || pass.y + pass.height > height) {
    throw new RenderError("OutOfRange", `pass ${pass.id} rect ${pass.x},${pass.y} ${pass.width}x${pass.height} is empty or outside the ${width}x${height} drawing buffer`);
  }
}

/**
 * Frame assembly rules, mirroring PassSequencer in src/Ukiyo.Render/Passes.cs: a frame without a pass section is
 * complete; a multi-camera frame is passes 0, 1, … as consecutive packets (sequence + 1, same tick, count, primary and
 * drawing buffer). accept() returns true when the frame is complete and throws InvalidPacket on anything else.
 */
export class PassSequencer {
  constructor() {
    this.openFrame = null;
  }

  get open() {
    return this.openFrame !== null;
  }

  accept(frame) {
    const pass = frame.pass;
    const open = this.openFrame;
    if (open) {
      const expected = open.pass;
      if (!pass || pass.id !== expected.id + 1 || frame.sequence !== open.sequence + 1 || frame.tick !== open.tick
        || pass.count !== expected.count || pass.primary !== expected.primary
        || frame.viewport.width !== open.viewport.width || frame.viewport.height !== open.viewport.height) {
        this.openFrame = null;
        throw new RenderError("InvalidPacket", `frame of ${expected.count} passes at tick ${open.tick} expects pass ${expected.id + 1} (sequence ${open.sequence + 1}) next; got ${describe(frame)}`);
      }
    } else if (pass && pass.id !== 0) {
      throw new RenderError("InvalidPacket", `pass ${pass.id} of ${pass.count} without the passes before it (${describe(frame)})`);
    }
    if (!pass || pass.id === pass.count - 1) {
      this.openFrame = null;
      return true;
    }
    this.openFrame = frame;
    return false;
  }

  abort() {
    this.openFrame = null;
  }

  requireComplete() {
    if (this.openFrame) throw new RenderError("InvalidPacket", `capture while the frame at tick ${this.openFrame.tick} is incomplete (pass ${this.openFrame.pass.id} of ${this.openFrame.pass.count} received)`);
  }
}

const describe = (frame) => (frame.pass
  ? `pass ${frame.pass.id} of ${frame.pass.count} (sequence ${frame.sequence}, tick ${frame.tick})`
  : `a single-camera packet (sequence ${frame.sequence}, tick ${frame.tick})`);

/** G1Lit lighting section; the same limits and checks as RenderValidation.Validate(SceneLighting) in C#. */
function readLighting(r) {
  const ambient = r.floats(3);
  if (ambient.some((v) => v < 0)) throw new RenderError("OutOfRange", "ambient light has a negative component");
  const count = r.count(MAX_LIGHTS, "light");
  const lights = [];
  for (let i = 0; i < count; i++) {
    const kind = r.u32();
    if (kind !== LightKind.Directional && kind !== LightKind.Point) throw new RenderError("InvalidPacket", `unknown light kind ${kind}`);
    const vector = r.floats(3);
    const color = r.floats(3);
    const intensity = r.f32();
    const range = r.f32();
    r.u32();
    if (color.some((v) => v < 0) || intensity < 0) throw new RenderError("OutOfRange", `light ${i} has a negative color or intensity`);
    if (kind === LightKind.Directional && ((vector[0] === 0 && vector[1] === 0 && vector[2] === 0) || range !== 0)) {
      throw new RenderError("OutOfRange", `directional light ${i} needs a non-zero direction and range 0`);
    }
    if (kind === LightKind.Point && !(range > 0)) throw new RenderError("OutOfRange", `point light ${i} range ${range} must be positive`);
    lights.push({ kind, vector, color, intensity, range });
  }
  return { ambient, lights };
}
