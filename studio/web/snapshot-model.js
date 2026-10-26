// @ts-check
// Small readings of a snapshot shared by the Entities, Values, Hierarchy and Inspector panels. Pure functions.

/** Rotation about the vertical axis in degrees, from the snapshot's quaternion. @param {number[]} rotation */
export function angleOf(rotation) {
  return (2 * Math.atan2(rotation[2] ?? 0, rotation[3] ?? 1) * 180) / Math.PI;
}

/**
 * Value change since the previous snapshot of a different tick (0 when unknown or the same tick).
 * @param {{ value: number, before: number | undefined, tick: number, previousTick: number }} reading
 */
export function valueDelta({ value, before, tick, previousTick }) {
  return before === undefined || tick === previousTick ? 0 : value - before;
}

/** Position as "x, y" (and z only when it is not 0). @param {number[]} position @param {(n: number) => string} format */
export function positionText(position, format) {
  const [x = 0, y = 0, z = 0] = position;
  return `${format(x)}, ${format(y)}${z === 0 ? "" : `, ${format(z)}`}`;
}
