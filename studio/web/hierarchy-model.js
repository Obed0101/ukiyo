// @ts-check
// Which entities the Hierarchy lists and in what order. Pure, so the grouping is tested without a browser.

/**
 * @typedef {{ id: string, prefab: string | null }} HierarchyRow
 * @typedef {{ scene: string | null, rows: HierarchyRow[] }} HierarchyGroup
 */

const collator = new Intl.Collator("en", { numeric: true, sensitivity: "base" });

/**
 * Rows of the running game. With a scene (snapshot.scene.active) there is one group named after it, in the scene
 * file's spawn order when that is known, then entities a patch spawned in snapshot order. Without a scene the list is
 * flat and sorted by name, so it never reshuffles between snapshots.
 * @param {{ entities: Record<string, unknown>, scene?: { active?: string | null } | null }} snapshot
 * @param {{ query?: string, sceneEntities?: { id: string, prefab?: string }[] | null }} [options]
 * @returns {HierarchyGroup[]}
 */
export function groupEntities(snapshot, { query = "", sceneEntities = null } = {}) {
  const needle = query.trim().toLowerCase();
  const prefabs = new Map((sceneEntities ?? []).map((entity) => [entity.id, entity.prefab ?? null]));
  const ids = Object.keys(snapshot.entities);
  const scene = snapshot.scene?.active ?? null;

  let ordered;
  if (scene !== null && scene !== undefined) {
    const known = (sceneEntities ?? []).map((entity) => entity.id).filter((id) => ids.includes(id));
    const rest = ids.filter((id) => !known.includes(id));
    ordered = [...known, ...rest];
  } else {
    ordered = [...ids].sort(collator.compare);
  }

  const rows = ordered
    .filter((id) => id.toLowerCase().includes(needle) || (prefabs.get(id) ?? "").toLowerCase().includes(needle))
    .map((id) => ({ id, prefab: prefabs.get(id) ?? null }));
  return rows.length === 0 && needle === "" ? [] : [{ scene: scene ?? null, rows }];
}

/** The ids in the order the list shows them, for keyboard stepping. @param {HierarchyGroup[]} groups */
export function flatIds(groups) {
  return groups.flatMap((group) => group.rows.map((row) => row.id));
}

/** The id one step from `current` in the shown order (wraps nowhere; stays at the ends). @param {string[]} ids @param {string | null} current @param {1 | -1} step */
export function stepId(ids, current, step) {
  if (ids.length === 0) return null;
  const index = current === null ? -1 : ids.indexOf(current);
  if (index < 0) return ids[step === 1 ? 0 : ids.length - 1] ?? null;
  return ids[Math.min(ids.length - 1, Math.max(0, index + step))] ?? null;
}
