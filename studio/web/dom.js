// @ts-check
// Tiny DOM helper shared by the shell and panels. No framework: panels are plain functions over real elements, so an
// agent can read and change any of them without a build step.

/**
 * h("button", { class: "btn", onClick: fn, "aria-label": "Step" }, icon("step-forward"), "Step")
 * Props: class, text, on<Event> handlers, dataset (object), any other key becomes an attribute (false/null skip it).
 * @param {string} tag
 * @param {Record<string, unknown> | null} [props]
 * @param {...(Node | string | number | null | undefined | false)} children
 * @returns {HTMLElement}
 */
export function h(tag, props, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") element.className = String(value);
    else if (key === "text") element.textContent = String(value);
    else if (key === "dataset") Object.assign(element.dataset, value);
    else if (key.startsWith("on") && typeof value === "function") element.addEventListener(key.slice(2).toLowerCase(), /** @type {EventListener} */ (value));
    else element.setAttribute(key, value === true ? "" : String(value));
  }
  append(element, ...children);
  return element;
}

/**
 * @param {Element} element
 * @param {...(Node | string | number | null | undefined | false)} children
 */
export function append(element, ...children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    element.append(typeof child === "number" ? String(child) : child);
  }
}

/** @param {Element} element */
export function clear(element) {
  element.replaceChildren();
}

/** Formats numbers for readouts: integers as-is, others to 3 decimals, invariant. */
export function formatNumber(/** @type {number} */ value) {
  if (!Number.isFinite(value)) return String(value);
  return Number.isInteger(value) ? String(value) : value.toFixed(3);
}
