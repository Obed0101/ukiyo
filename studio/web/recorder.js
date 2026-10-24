// @ts-check
// Input recording shared by the Frame panel's Record button and the Input panel: one recording per page, so both show
// the same state. The bridge does the recording (dev bridge command "record"); this only holds the last result.

/** @type {{ recording: boolean, script: string, events: number }} */
const state = { recording: false, script: "", events: 0 };
/** @type {Set<() => void>} */
const listeners = new Set();

export const recorder = {
  get state() {
    return state;
  },
  /** @param {() => void} listener @returns {() => void} */
  subscribe(listener) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  /** Starts or stops. Returns the number of events when this call stopped a recording. @param {import("./types").Studio} studio */
  async toggle(studio) {
    const result = /** @type {{ recording: boolean, events: number, script: unknown }} */ (await studio.call("record", { on: !state.recording }));
    let stopped = null;
    if (state.recording) {
      state.script = JSON.stringify(result.script, null, 2);
      state.events = result.events;
      stopped = result.events;
    }
    state.recording = result.recording;
    for (const listener of [...listeners]) listener();
    return stopped;
  },
  /** A new game session: the old recording is not running any more. */
  reset() {
    state.recording = false;
    for (const listener of [...listeners]) listener();
  },
};
