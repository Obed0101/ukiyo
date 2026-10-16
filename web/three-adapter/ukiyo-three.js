// Ukiyo Three.js adapter: the JS side of ThreeJsRenderer. It keeps persistent maps from resource handles to
// Three geometries/materials and applies frame packets computed in C#. It never advances time, animates or
// decides anything about the game: with no packet, nothing moves.
import * as THREE from "three";
import { CommandKind, PassSequencer, RenderError, ResourceKind, TextureFilter, decodeFrame, decodeResources, requirePassInside } from "./protocol.js";
import { spriteDrawOrder, spriteQuad } from "./sprite-geometry.js";
import { createLitMaterial, createSceneLightUniforms, writeSceneLights } from "./lit-material.js";

const state = {
  sceneLights: createSceneLightUniforms(),
  renderer: null,
  scene: null,
  camera: null,
  geometries: new Map(),
  materials: new Map(),
  textures: new Map(),
  pool: [],
  spriteScene: null,
  spriteCamera: null,
  spriteMeshes: [],
  target: null,
  blitScene: null,
  lastSequence: 0,
  // Multi-camera frames: order checks, one sRGB target per pass id, and how many passes the last complete frame had.
  passes: new PassSequencer(),
  passTargets: [],
  lastFramePasses: 0,
};

const tableFor = (kind) => (kind === ResourceKind.Mesh ? state.geometries : kind === ResourceKind.Material ? state.materials : state.textures);
const nameFor = (kind) => (kind === ResourceKind.Mesh ? "mesh" : kind === ResourceKind.Material ? "material" : "texture");

const key = (handle) => `${handle.kind}:${handle.index}`;

function lookup(table, handle, what) {
  const entry = table.get(key(handle));
  if (!entry) throw new RenderError("UnknownHandle", `${what} ${handle.index}.${handle.generation} was never created`);
  if (entry.generation !== handle.generation) {
    throw new RenderError("StaleHandle", `${what} ${handle.index}.${handle.generation} is stale (live generation ${entry.generation})`);
  }
  return entry.value;
}

function requireReady() {
  if (!state.renderer) throw new RenderError("NotInitialized", "adapter used before initialize");
}

/** Creates the WebGL2 renderer on an existing canvas and reports what actually runs. */
export function initialize(canvasId) {
  const canvas = document.getElementById(canvasId);
  if (!(canvas instanceof HTMLCanvasElement)) throw new RenderError("BackendFailure", `canvas #${canvasId} not found`);
  // No multisampling, like the wgpu and CPU renderers: it softens sprite edges and bleeds neighboring atlas cells.
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: false, powerPreference: "high-performance" });
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  const gl = renderer.getContext();
  const debug = gl.getExtension("WEBGL_debug_renderer_info");
  state.renderer = renderer;
  renderer.autoClear = false;
  state.scene = new THREE.Scene();
  state.camera = new THREE.PerspectiveCamera();
  // Sprites arrive already placed in pixels and are converted to NDC here, so this camera is the identity.
  state.spriteScene = new THREE.Scene();
  state.spriteCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
  // Blending is straight alpha in linear space (docs/protocol.md). A WebGL canvas blends the encoded sRGB values,
  // so the frame is drawn into an SRGB8_ALPHA8 target, where the GPU decodes, blends in linear and re-encodes on
  // write (as wgpu's *Srgb surface does), then copied to the canvas.
  state.target = new THREE.WebGLRenderTarget(1, 1, { colorSpace: THREE.SRGBColorSpace, minFilter: THREE.NearestFilter, magFilter: THREE.NearestFilter, generateMipmaps: false });
  const blit = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.MeshBasicMaterial({ map: state.target.texture, depthTest: false, depthWrite: false, toneMapped: false }));
  blit.position.z = -0.5;
  blit.frustumCulled = false;
  state.blitScene = new THREE.Scene();
  state.blitScene.add(blit);
  return JSON.stringify({
    renderer: "ThreeJsRenderer",
    backend: typeof WebGL2RenderingContext !== "undefined" && gl instanceof WebGL2RenderingContext ? "WebGL2" : "WebGL1",
    device: debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER),
    three: THREE.REVISION,
  });
}

export function applyResources(bytes) {
  requireReady();
  for (const command of decodeResources(bytes)) {
    const id = key(command.handle);
    if (command.kind === CommandKind.CreateMesh) {
      if (state.geometries.has(id)) throw new RenderError("DuplicateHandle", `mesh slot ${command.handle.index} is live`);
      const buffer = new THREE.InterleavedBuffer(command.interleaved, command.stride);
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute("position", new THREE.InterleavedBufferAttribute(buffer, 3, 0));
      geometry.setAttribute("color", new THREE.InterleavedBufferAttribute(buffer, 3, 3));
      if (command.hasNormals) geometry.setAttribute("normal", new THREE.InterleavedBufferAttribute(buffer, 3, 6));
      geometry.userData.hasNormals = command.hasNormals;
      geometry.setIndex(new THREE.BufferAttribute(command.indices, 1));
      geometry.computeBoundingSphere();
      state.geometries.set(id, { generation: command.handle.generation, value: geometry });
    } else if (command.kind === CommandKind.CreateMaterial) {
      if (state.materials.has(id)) throw new RenderError("DuplicateHandle", `material slot ${command.handle.index} is live`);
      const [r, g, b] = command.baseColor;
      // Colors arrive linear; Three's working space is linear sRGB and output is encoded to sRGB.
      const material = command.lit
        ? createLitMaterial(command.baseColor, command.useVertexColors, state.sceneLights)
        : new THREE.MeshBasicMaterial({ color: new THREE.Color().setRGB(r, g, b, THREE.LinearSRGBColorSpace), vertexColors: command.useVertexColors, side: THREE.FrontSide });
      state.materials.set(id, { generation: command.handle.generation, value: material });
    } else if (command.kind === CommandKind.CreateTexture) {
      if (state.textures.has(id)) throw new RenderError("DuplicateHandle", `texture slot ${command.handle.index} is live`);
      // RGBA8 sRGB with straight alpha, rows top to bottom; uv v=0 samples the first row, as in the C# renderers.
      const texture = new THREE.DataTexture(command.rgba, command.width, command.height, THREE.RGBAFormat, THREE.UnsignedByteType);
      const filter = command.filter === TextureFilter.Linear ? THREE.LinearFilter : THREE.NearestFilter;
      texture.colorSpace = THREE.SRGBColorSpace;
      texture.magFilter = filter;
      texture.minFilter = filter;
      texture.generateMipmaps = false;
      texture.wrapS = THREE.ClampToEdgeWrapping;
      texture.wrapT = THREE.ClampToEdgeWrapping;
      texture.needsUpdate = true;
      const material = new THREE.MeshBasicMaterial({ map: texture, vertexColors: true, transparent: true, depthTest: false, depthWrite: false, side: THREE.DoubleSide });
      state.textures.set(id, { generation: command.handle.generation, value: { texture, material, dispose: () => { material.dispose(); texture.dispose(); } } });
    } else if (command.kind === CommandKind.Destroy) {
      const table = tableFor(command.handle.kind);
      lookup(table, command.handle, nameFor(command.handle.kind)).dispose();
      table.delete(id);
    }
  }
}

export function resize(width, height) {
  requireReady();
  // Drawing-buffer pixels come from C#; CSS size stays whatever the page layout says.
  state.renderer.setPixelRatio(1);
  state.renderer.setSize(width, height, false);
  state.target.setSize(width, height);
}

/**
 * Applies one C# frame packet and draws it. Returns the tick of the packet. A single-camera packet is drawn and
 * presented at once. A pass of a multi-camera frame is drawn into its own target at its rect size (exactly like a
 * single-camera frame of that size) and copied into the frame at its rect; the last pass presents the canvas.
 */
export function render(bytes) {
  requireReady();
  const frame = decodeFrame(bytes);
  if (frame.sequence <= state.lastSequence) throw new RenderError("InvalidPacket", `sequence ${frame.sequence} after ${state.lastSequence}`);
  const completesFrame = state.passes.accept(frame);
  state.lastSequence = frame.sequence;
  const size = state.renderer.getDrawingBufferSize(new THREE.Vector2());
  try {
    if (frame.pass) {
      renderPass(frame, size, completesFrame);
      return frame.tick;
    }
    if (state.target.width !== size.x || state.target.height !== size.y) state.target.setSize(size.x, size.y);
    drawCamera(frame, state.target, size.x, size.y);
    state.lastFramePasses = 0;
  } catch (error) {
    state.passes.abort();
    throw error;
  }
  state.renderer.setRenderTarget(null);
  state.renderer.render(state.blitScene, state.spriteCamera);
  return frame.tick;
}

/** One pass: its own target, then an exact 1:1 copy into the frame target at the rect (GL rows count from the bottom). */
function renderPass(frame, size, completesFrame) {
  const { pass } = frame;
  requirePassInside(pass, size.x, size.y);
  const frameTarget = state.target;
  if (frameTarget.width !== size.x || frameTarget.height !== size.y) frameTarget.setSize(size.x, size.y);
  if (pass.id === 0) {
    // Pixels no pass covers keep pass 0's clear color.
    state.renderer.setRenderTarget(frameTarget);
    const [r, g, b, a] = frame.clearColor;
    state.renderer.setClearColor(new THREE.Color().setRGB(r, g, b, THREE.LinearSRGBColorSpace), a);
    state.renderer.clear();
  }

  const passTarget = passTargetFor(pass.id, pass.width, pass.height);
  drawCamera(frame, passTarget.target, pass.width, pass.height);
  frameTarget.viewport.set(pass.x, size.y - pass.y - pass.height, pass.width, pass.height);
  state.renderer.setRenderTarget(frameTarget);
  state.renderer.render(passTarget.blitScene, state.spriteCamera);
  frameTarget.viewport.set(0, 0, size.x, size.y);
  state.renderer.setRenderTarget(null);
  if (completesFrame) {
    state.lastFramePasses = pass.count;
    state.renderer.render(state.blitScene, state.spriteCamera);
  }
}

/** A pass's sRGB target (kept for capturePass) and the quad that copies it into the frame target. */
function passTargetFor(id, width, height) {
  let entry = state.passTargets[id];
  if (!entry) {
    const target = new THREE.WebGLRenderTarget(width, height, { colorSpace: THREE.SRGBColorSpace, minFilter: THREE.NearestFilter, magFilter: THREE.NearestFilter, generateMipmaps: false });
    const blit = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.MeshBasicMaterial({ map: target.texture, depthTest: false, depthWrite: false, toneMapped: false }));
    blit.position.z = -0.5;
    blit.frustumCulled = false;
    const blitScene = new THREE.Scene();
    blitScene.add(blit);
    entry = { target, blitScene };
    state.passTargets[id] = entry;
  }
  if (entry.target.width !== width || entry.target.height !== height) entry.target.setSize(width, height);
  return entry;
}

/** Draws one camera into `target` (width × height is the drawing buffer this packet sees): clear, meshes, sprites. */
function drawCamera(frame, target, width, height) {
  const { camera, clearColor } = frame;
  const cam = state.camera;
  cam.position.set(camera.position[0], camera.position[1], camera.position[2]);
  cam.quaternion.set(camera.rotation[0], camera.rotation[1], camera.rotation[2], camera.rotation[3]);
  cam.fov = THREE.MathUtils.radToDeg(camera.fovY);
  cam.near = camera.near;
  cam.far = camera.far;
  cam.aspect = width / height;
  cam.updateProjectionMatrix();

  while (state.pool.length < frame.instances.length) {
    const mesh = new THREE.Mesh();
    mesh.matrixAutoUpdate = false;
    state.scene.add(mesh);
    state.pool.push(mesh);
  }
  state.pool.forEach((mesh, i) => {
    const instance = frame.instances[i];
    mesh.visible = instance !== undefined;
    if (!instance) return;
    mesh.geometry = lookup(state.geometries, instance.mesh, "mesh");
    mesh.material = lookup(state.materials, instance.material, "material");
    // Same draw-time checks as RenderValidation.RequireLitInputs in C#.
    if (mesh.material.userData.lit) {
      if (!mesh.geometry.userData.hasNormals) throw new RenderError("InvalidPacket", `lit material ${instance.material.index} drawn on mesh ${instance.mesh.index} without normals`);
      if (!frame.lighting) throw new RenderError("InvalidPacket", `lit material ${instance.material.index} drawn in frame ${frame.sequence} without a lighting section`);
    }
    // Field order M11..M44 from System.Numerics is the column-major array Three expects (docs/conventions.md).
    mesh.matrix.fromArray(instance.world);
    mesh.matrixWorldNeedsUpdate = true;
  });

  writeSceneLights(state.sceneLights, frame.lighting);
  state.renderer.setRenderTarget(target);
  // Three converts the clear color for the bound target when it is set, so it must be set after binding.
  state.renderer.setClearColor(new THREE.Color().setRGB(clearColor[0], clearColor[1], clearColor[2], THREE.LinearSRGBColorSpace), clearColor[3]);
  state.renderer.clear();
  state.renderer.render(state.scene, cam);
  if (frame.sprites.length > 0) {
    buildSprites(frame, width, height);
    state.renderer.render(state.spriteScene, state.spriteCamera);
  } else {
    state.spriteMeshes.forEach((mesh) => { mesh.visible = false; });
  }
}

/** One mesh per run of consecutive sprites sharing a texture, in the shared draw order. Geometry is rebuilt per frame. */
function buildSprites(frame, width, height) {
  const batches = [];
  for (const index of spriteDrawOrder(frame.sprites)) {
    const sprite = frame.sprites[index];
    const entry = lookup(state.textures, sprite.texture, "texture");
    let batch = batches[batches.length - 1];
    if (!batch || batch.entry !== entry) {
      batch = { entry, positions: [], uvs: [], colors: [] };
      batches.push(batch);
    }
    const quad = spriteQuad(sprite, frame.camera2D, width, height);
    for (const corner of [0, 1, 2, 0, 2, 3]) {
      const v = quad[corner];
      batch.positions.push((v.x / width) * 2 - 1, 1 - (v.y / height) * 2, -0.5);
      batch.uvs.push(v.u, v.v);
      batch.colors.push(sprite.color[0], sprite.color[1], sprite.color[2], sprite.color[3]);
    }
  }

  while (state.spriteMeshes.length < batches.length) {
    const mesh = new THREE.Mesh(new THREE.BufferGeometry());
    mesh.frustumCulled = false;
    state.spriteScene.add(mesh);
    state.spriteMeshes.push(mesh);
  }
  state.spriteMeshes.forEach((mesh, i) => {
    const batch = batches[i];
    mesh.visible = batch !== undefined;
    if (!batch) return;
    mesh.renderOrder = i;
    mesh.geometry.dispose();
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(batch.positions, 3));
    geometry.setAttribute("uv", new THREE.Float32BufferAttribute(batch.uvs, 2));
    geometry.setAttribute("color", new THREE.Float32BufferAttribute(batch.colors, 4));
    mesh.geometry = geometry;
    mesh.material = batch.entry.material;
  });
}

/** Reads back the frame just rendered (same task, so the drawing buffer is intact). Format: "WxH;<base64 png>". */
export function capture() {
  requireReady();
  const canvas = state.renderer.domElement;
  const url = canvas.toDataURL("image/png");
  return `${canvas.width}x${canvas.height};${url.slice(url.indexOf(",") + 1)}`;
}

/**
 * Pass `id` of the last multi-camera frame, alone at its rect size, read from its own target (the bytes the pass drew,
 * before later passes covered any of it). Format: "WxH;<base64 png>", like capture().
 */
export function capturePass(id) {
  requireReady();
  state.passes.requireComplete();
  if (!(id >= 0 && id < state.lastFramePasses)) throw new RenderError("OutOfRange", `capture of pass ${id}, but the last frame has ${state.lastFramePasses || 1} pass(es) and no pass section`);
  const { target } = state.passTargets[id];
  const { width, height } = target;
  const pixels = new Uint8Array(width * height * 4);
  // An SRGB8_ALPHA8 target reads back its stored (encoded) bytes, the same bytes the copy into the frame used.
  state.renderer.readRenderTargetPixels(target, 0, 0, width, height, pixels);
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  const image = context.createImageData(width, height);
  for (let y = 0; y < height; y++) {
    // GL rows start at the bottom; PNG rows at the top.
    image.data.set(pixels.subarray((height - 1 - y) * width * 4, (height - y) * width * 4), y * width * 4);
  }
  for (let i = 3; i < image.data.length; i += 4) image.data[i] = 255;
  context.putImageData(image, 0, 0);
  const url = canvas.toDataURL("image/png");
  return `${width}x${height};${url.slice(url.indexOf(",") + 1)}`;
}

export function dispose() {
  if (!state.renderer) return;
  state.passTargets.forEach((entry) => {
    entry.target.dispose();
    entry.blitScene.children.forEach((mesh) => { mesh.geometry.dispose(); mesh.material.dispose(); });
  });
  state.passTargets.length = 0;
  state.passes.abort();
  state.geometries.forEach((entry) => entry.value.dispose());
  state.materials.forEach((entry) => entry.value.dispose());
  state.textures.forEach((entry) => entry.value.dispose());
  state.spriteMeshes.forEach((mesh) => mesh.geometry.dispose());
  state.geometries.clear();
  state.materials.clear();
  state.textures.clear();
  state.pool.length = 0;
  state.spriteMeshes.length = 0;
  state.blitScene.children.forEach((mesh) => { mesh.geometry.dispose(); mesh.material.dispose(); });
  state.target.dispose();
  state.renderer.dispose();
  state.renderer = null;
}
