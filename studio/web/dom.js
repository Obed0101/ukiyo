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

/** Formats numbers for readouts: up to 3 decimals without trailing zeros, invariant. */
export function formatNumber(/** @type {number} */ value) {
  if (!Number.isFinite(value)) return String(value);
  return String(Number(value.toFixed(3)));
}

/**
 * A calm empty state: one title, one sentence, at most one action. `command` renders as a copyable line.
 * @param {{ icon?: Node, title: string, text?: string, command?: string, action?: HTMLElement }} options
 * @returns {HTMLElement}
 */
export function emptyState({ icon, title, text, command, action }) {
  const copy = command
    ? h("button", {
      type: "button", class: "command", title: "Copy command", "aria-label": `Copy command: ${command}`,
      onClick: async (/** @type {Event} */ event) => {
        const button = /** @type {HTMLElement} */ (event.currentTarget);
        try {
          await navigator.clipboard.writeText(command);
          button.dataset.copied = "true";
          setTimeout(() => delete button.dataset.copied, 1200);
        } catch {
          // Clipboard blocked: the command stays selectable text.
        }
      },
    }, h("code", { text: command }))
    : null;
  return h("div", { class: "empty-state" }, icon, h("p", { class: "empty-title", text: title }), text ? h("p", { class: "empty-text", text }) : null, copy, action);
}
