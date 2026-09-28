// Ukiyo Three.js adapter: the JS side of ThreeJsRenderer. It keeps persistent maps from resource handles to
// Three geometries/materials and applies frame packets computed in C#. It never advances time, animates or
// decides anything about the game: with no packet, nothing moves.
import * as THREE from "three";
import { CommandKind, RenderError, ResourceKind, decodeFrame, decodeResources } from "./protocol.js";

const state = {
  renderer: null,
  scene: null,
  camera: null,
  geometries: new Map(),
  materials: new Map(),
  pool: [],
  lastSequence: 0,
};

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
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: "high-performance" });
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  const gl = renderer.getContext();
  const debug = gl.getExtension("WEBGL_debug_renderer_info");
  state.renderer = renderer;
  state.scene = new THREE.Scene();
  state.camera = new THREE.PerspectiveCamera();
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
    } else if (command.kind === CommandKind.Destroy) {
      const table = command.handle.kind === ResourceKind.Mesh ? state.geometries : state.materials;
      lookup(table, command.handle, command.handle.kind === ResourceKind.Mesh ? "mesh" : "material").dispose();
      table.delete(id);
    }
  }
}

export function resize(width, height) {
  requireReady();
  // Drawing-buffer pixels come from C#; CSS size stays whatever the page layout says.
  state.renderer.setPixelRatio(1);
  state.renderer.setSize(width, height, false);
}

/** Applies one C# frame packet and draws it. Returns the tick that was presented. */
export function render(bytes) {
  requireReady();
  const frame = decodeFrame(bytes);
  if (frame.sequence <= state.lastSequence) throw new RenderError("InvalidPacket", `sequence ${frame.sequence} after ${state.lastSequence}`);
  state.lastSequence = frame.sequence;

  const { camera, clearColor } = frame;
  state.renderer.setClearColor(new THREE.Color().setRGB(clearColor[0], clearColor[1], clearColor[2], THREE.LinearSRGBColorSpace), clearColor[3]);
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

  state.renderer.render(state.scene, cam);
  return frame.tick;
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
  state.geometries.clear();
  state.materials.clear();
  state.pool.length = 0;
  state.renderer.dispose();
  state.renderer = null;
}
