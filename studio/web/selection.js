// @ts-check
// The selected entity, shared by every panel that shows entities. One ES module is one instance per page, so the
// Hierarchy and the Inspector agree without going through the shell. Panels still tell agents with studio.view().

/** @type {string | null} */
let selected = null;
/** @type {Set<(name: string | null) => void>} */
const listeners = new Set();

export const selection = {
  get() {
    return selected;
  },
  /** @param {string | null} name */
  set(name) {
    if (name === selected) return;
    selected = name;
    for (const listener of [...listeners]) listener(name);
  },
  /** @param {(name: string | null) => void} listener @returns {() => void} */
  subscribe(listener) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};
