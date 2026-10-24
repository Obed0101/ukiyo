// @ts-check
// What the engine-editor panels (hierarchy, inspector, console, assets) share: the server API with the page's token,
// the stylesheet they add to the Studio's, and which project they are looking at.
import { createApi } from "./api.js";

const token = document.querySelector('meta[name="ukiyo-studio-token"]')?.getAttribute("content") ?? "";
export const api = createApi(token);

let styled = false;
/** Adds panels.css once. The shell's studio.css is not edited for these panels. */
export function useStyles() {
  if (styled) return;
  styled = true;
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = "/web/panels.css";
  document.head.append(link);
}

/** The project the Studio is working on: the hub's choice when it exposes one, else the attached game. @param {import("./types").Studio} studio @returns {string | null} */
export function projectName(studio) {
  return studio.project?.name ?? studio.session?.game ?? null;
}

/**
 * Fetches a PNG through the token header and returns an object URL (an <img src> cannot send headers).
 * @param {string} query
 * @returns {Promise<string>}
 */
export async function fetchImageUrl(query) {
  const response = await fetch(`/api/assets/file?${query}`, { headers: { "x-ukiyo-token": token }, cache: "no-store" });
  if (!response.ok) throw new Error(`${response.status}`);
  return URL.createObjectURL(await response.blob());
}

/** @param {number} bytes */
export function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** @typedef {{ id: string, prefab?: string, position?: number[], values?: Record<string, number> }} SceneEntity */
/** @type {Map<string, Promise<SceneEntity[] | null>>} */
const sceneCache = new Map();

/**
 * Entities of a scene as its JSON file declares them (initial data, not the live world), or null when the project has
 * no such file. Cached per project and scene; a reload of the page or a changed scene name asks again.
 * @param {string} project @param {string} scene
 * @returns {Promise<SceneEntity[] | null>}
 */
export function loadSceneEntities(project, scene) {
  const key = `${project}\0${scene}`;
  let pending = sceneCache.get(key);
  if (!pending) {
    const query = new URLSearchParams({ project, root: "Shared/assets", path: `scenes/${scene}.json` });
    pending = api
      .get(`/api/assets/text?${query}`)
      .then((data) => (Array.isArray(data?.entities) ? /** @type {SceneEntity[]} */ (data.entities) : null))
      .catch(() => null);
    sceneCache.set(key, pending);
  }
  return pending;
}
