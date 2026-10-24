// @ts-check
// The scene patch the Inspector submits and how its outcome is read back. Pure, so the request shape is tested
// without a browser or a game (docs/scenes-patches.md).

let counter = 0;
const session = Date.now().toString(36);

/** A patch id unique for this page (ids are unique per game session; 1-64 chars of A-Za-z0-9_.-). */
export function nextPatchId() {
  counter += 1;
  return `studio-${session}-${counter}`;
}

/**
 * The tick a patch targets: now when the game is paused between steps (applied immediately), else the next boundary.
 * @param {{ tick: number, paused: boolean }} status
 */
export function patchTick({ tick, paused }) {
  return paused ? tick : tick + 1;
}

/**
 * One `retarget` op as the dev bridge takes it ({ cmd: "patch", patch }). z is kept from the entity's pose and only
 * sent when it is not 0 (the scene JSON accepts [x, y] or [x, y, z]).
 * @param {{ patchId: string, tick: number, scene: string | null, entity: string, position: [number, number, number] }} change
 */
export function buildRetargetPatch({ patchId, tick, scene, entity, position }) {
  const [x, y, z] = position;
  return {
    id: patchId,
    tick,
    ...(scene ? { scene } : {}),
    ops: [{ op: "retarget", id: entity, position: z === 0 ? [x, y] : [x, y, z] }],
  };
}

/**
 * @param {unknown} result the bridge's answer to a patch
 * @returns {{ ok: true, status: string, tick: number, text: string } | { ok: false, text: string }}
 */
export function describePatchResult(result) {
  const r = /** @type {{ status?: string, tick?: number, currentTick?: number } | null} */ (result);
  if (!r || typeof r.status !== "string") return { ok: false, text: "The game gave no patch result." };
  const tick = typeof r.tick === "number" ? r.tick : 0;
  const text = r.status === "applied" ? `Applied at tick ${tick}` : r.status === "queued" ? `Queued for tick ${tick}` : `${r.status} (tick ${tick})`;
  return { ok: true, status: r.status, tick, text };
}

/**
 * What a queued patch became, read from the snapshot's scene section (last 16 applied and refused).
 * @param {{ scene?: { patches?: { id: string, tick: number }[], rejected?: { id: string, tick: number, code?: string, message?: string }[] } | null } | null} snapshot
 * @param {string} patchId
 * @returns {{ state: "applied", tick: number } | { state: "rejected", message: string } | { state: "pending" }}
 */
export function patchOutcome(snapshot, patchId) {
  const applied = snapshot?.scene?.patches?.find((patch) => patch.id === patchId);
  if (applied) return { state: "applied", tick: applied.tick };
  const refused = snapshot?.scene?.rejected?.find((patch) => patch.id === patchId);
  if (refused) return { state: "rejected", message: refused.message ?? refused.code ?? "refused" };
  return { state: "pending" };
}

/** Parses a typed number; null for blank or non-finite. @param {string} text */
export function parseNumber(text) {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : null;
}
