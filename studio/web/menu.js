// @ts-check
// Small anchored popups: a menu of actions (arrow keys, Enter, Esc) and a free-form popover. One open at a time;
// both close on outside click, Esc or scroll, and give focus back to their anchor.
import { h } from "./dom.js";
import { icon } from "./icons.js";

/** @type {(() => void) | null} */
let closeCurrent = null;

/**
 * @param {HTMLElement} anchor
 * @param {HTMLElement} surface
 * @param {{ align?: "start" | "end" | "center" }} [options]
 */
function present(anchor, surface, { align = "start" } = {}) {
  closeCurrent?.();
  surface.style.position = "fixed";
  surface.style.visibility = "hidden";
  document.body.append(surface);
  const a = anchor.getBoundingClientRect();
  const s = surface.getBoundingClientRect();
  const left = align === "end" ? a.right - s.width : align === "center" ? a.left + (a.width - s.width) / 2 : a.left;
  surface.style.left = `${Math.max(8, Math.min(left, innerWidth - s.width - 8))}px`;
  surface.style.top = `${Math.min(a.bottom + 4, innerHeight - s.height - 8)}px`;
  surface.style.visibility = "";
  anchor.setAttribute("aria-expanded", "true");

  const close = () => {
    if (closeCurrent !== close) return;
    closeCurrent = null;
    surface.remove();
    anchor.removeAttribute("aria-expanded");
    document.removeEventListener("pointerdown", outside, true);
    document.removeEventListener("keydown", keys, true);
    removeEventListener("blur", close);
    removeEventListener("resize", close);
  };
  /** @param {Event} event */
  const outside = (event) => {
    if (!surface.contains(/** @type {Node} */ (event.target)) && !anchor.contains(/** @type {Node} */ (event.target))) close();
  };
  /** @param {KeyboardEvent} event */
  const keys = (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      close();
      anchor.focus();
    }
  };
  closeCurrent = close;
  document.addEventListener("pointerdown", outside, true);
  document.addEventListener("keydown", keys, true);
  addEventListener("blur", close);
  addEventListener("resize", close);
  return close;
}

/**
 * @param {HTMLElement} anchor
 * @param {{ label: string, icon?: string, disabled?: boolean, onSelect: () => void }[]} items
 * @param {{ align?: "start" | "end" | "center" }} [options]
 */
export function openMenu(anchor, items, options) {
  const buttons = items.map((item) => h("button", {
    type: "button", role: "menuitem", class: "menu-item", disabled: item.disabled,
    onClick: () => {
      close();
      item.onSelect();
    },
  }, item.icon ? icon(item.icon) : null, item.label));
  const surface = h("div", { class: "menu", role: "menu" }, ...buttons);
  surface.addEventListener("keydown", (event) => {
    const enabled = /** @type {HTMLElement[]} */ (buttons.filter((b) => !b.hasAttribute("disabled")));
    const index = enabled.indexOf(/** @type {HTMLElement} */ (document.activeElement));
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const step = event.key === "ArrowDown" ? 1 : -1;
      enabled[(index + step + enabled.length) % enabled.length]?.focus();
    }
  });
  const close = present(anchor, surface, options);
  /** @type {HTMLElement | undefined} */ (buttons.find((b) => !b.hasAttribute("disabled")))?.focus();
  return close;
}

/** @param {HTMLElement} anchor @param {Node} content @param {{ align?: "start" | "end" | "center" }} [options] */
export function openPopover(anchor, content, options) {
  const surface = h("div", { class: "popover", role: "dialog" }, content);
  return present(anchor, surface, options);
}
