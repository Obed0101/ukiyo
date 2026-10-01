// Ukiyo Three.js adapter: the JS side of ThreeJsRenderer. It keeps persistent maps from resource handles to
// Three geometries/materials and applies frame packets computed in C#. It never advances time, animates or
// decides anything about the game: with no packet, nothing moves.
import * as THREE from "three";
import { CommandKind, RenderError, ResourceKind, TextureFilter, decodeFrame, decodeResources } from "./protocol.js";
import { spriteDrawOrder, spriteQuad } from "./sprite-geometry.js";

const state = {
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
      const buffer = new THREE.InterleavedBuffer(command.interleaved, 6);
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute("position", new THREE.InterleavedBufferAttribute(buffer, 3, 0));
      geometry.setAttribute("color", new THREE.InterleavedBufferAttribute(buffer, 3, 3));
      geometry.setIndex(new THREE.BufferAttribute(command.indices, 1));
      geometry.computeBoundingSphere();
      state.geometries.set(id, { generation: command.handle.generation, value: geometry });
    } else if (command.kind === CommandKind.CreateMaterial) {
      if (state.materials.has(id)) throw new RenderError("DuplicateHandle", `material slot ${command.handle.index} is live`);
      const [r, g, b] = command.baseColor;
      // Colors arrive linear; Three's working space is linear sRGB and output is encoded to sRGB.
      const material = new THREE.MeshBasicMaterial({ color: new THREE.Color().setRGB(r, g, b, THREE.LinearSRGBColorSpace), vertexColors: command.useVertexColors, side: THREE.FrontSide });
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

/** Applies one C# frame packet and draws it. Returns the tick that was presented. */
export function render(bytes) {
  requireReady();
  const frame = decodeFrame(bytes);
  if (frame.sequence <= state.lastSequence) throw new RenderError("InvalidPacket", `sequence ${frame.sequence} after ${state.lastSequence}`);
  state.lastSequence = frame.sequence;

  const { camera, clearColor } = frame;
  const cam = state.camera;
  cam.position.set(camera.position[0], camera.position[1], camera.position[2]);
  cam.quaternion.set(camera.rotation[0], camera.rotation[1], camera.rotation[2], camera.rotation[3]);
  cam.fov = THREE.MathUtils.radToDeg(camera.fovY);
  cam.near = camera.near;
  cam.far = camera.far;
  const size = state.renderer.getDrawingBufferSize(new THREE.Vector2());
  cam.aspect = size.x / size.y;
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
    // Field order M11..M44 from System.Numerics is the column-major array Three expects (docs/conventions.md).
    mesh.matrix.fromArray(instance.world);
    mesh.matrixWorldNeedsUpdate = true;
  });

  if (state.target.width !== size.x || state.target.height !== size.y) state.target.setSize(size.x, size.y);
  state.renderer.setRenderTarget(state.target);
  // Three converts the clear color for the bound target when it is set, so it must be set after binding.
  state.renderer.setClearColor(new THREE.Color().setRGB(clearColor[0], clearColor[1], clearColor[2], THREE.LinearSRGBColorSpace), clearColor[3]);
  state.renderer.clear();
  state.renderer.render(state.scene, cam);
  if (frame.sprites.length > 0) {
    buildSprites(frame, size.x, size.y);
    state.renderer.render(state.spriteScene, state.spriteCamera);
  } else {
    state.spriteMeshes.forEach((mesh) => { mesh.visible = false; });
  }
  state.renderer.setRenderTarget(null);
  state.renderer.render(state.blitScene, state.spriteCamera);
  return frame.tick;
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

export function dispose() {
  if (!state.renderer) return;
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
