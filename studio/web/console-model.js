// @ts-check
// How the Console panel reads, filters and folds its lines. Pure functions over plain entries.

/**
 * @typedef {"info" | "success" | "warn" | "error"} Level
 * @typedef {{ key: string, at: string, source: string, level: Level, text: string }} ConsoleEntry
 * @typedef {ConsoleEntry & { count: number }} FoldedEntry
 */

export const LEVEL_FILTERS = /** @type {const} */ (["all", "warnings", "errors"]);

/** Level of a line a game printed: stderr is an error; otherwise the words in the line decide. @param {"stdout" | "stderr"} stream @param {string} text @returns {Level} */
export function levelOfOutput(stream, text) {
  if (stream === "stderr") return "error";
  if (/\b(error|exception|fatal|unhandled)\b|\berror [A-Z]+\d+/i.test(text)) return "error";
  if (/\bwarn(ing)?\b/i.test(text)) return "warn";
  return "info";
}

/** @param {ConsoleEntry} entry @param {"all" | "warnings" | "errors"} filter */
export function passesLevel(entry, filter) {
  if (filter === "errors") return entry.level === "error";
  if (filter === "warnings") return entry.level === "error" || entry.level === "warn";
  return true;
}

/** @param {ConsoleEntry[]} entries @param {{ level: "all" | "warnings" | "errors", query: string }} filter */
export function filterEntries(entries, { level, query }) {
  const needle = query.trim().toLowerCase();
  return entries.filter((entry) => passesLevel(entry, level) && (needle === "" || `${entry.source} ${entry.text}`.toLowerCase().includes(needle)));
}

/** Folds identical consecutive lines (same source, level and text) into one with a count. @param {ConsoleEntry[]} entries @returns {FoldedEntry[]} */
export function foldRepeats(entries) {
  /** @type {FoldedEntry[]} */
  const folded = [];
  for (const entry of entries) {
    const last = folded[folded.length - 1];
    if (last && last.source === entry.source && last.level === entry.level && last.text === entry.text) last.count += 1;
    else folded.push({ ...entry, count: 1 });
  }
  return folded;
}

/** Keeps the newest `limit` entries. @template T @param {T[]} entries @param {number} limit */
export function bounded(entries, limit) {
  return entries.length > limit ? entries.slice(entries.length - limit) : entries;
}
