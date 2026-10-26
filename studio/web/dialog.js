// @ts-check
// The one dialog component: a native <dialog> opened modally, so focus is trapped, the page behind is inert and Esc
// closes it. Content and actions are the caller's; this only adds the frame and returns focus to where it came from.
import { h } from "./dom.js";

/**
 * @param {{ title: string, label?: string, content: Node, actions: HTMLElement[], onClose?: () => void }} options
 * @returns {{ close: () => void, element: HTMLDialogElement }}
 */
export function openDialog({ title, content, actions, onClose }) {
  const opener = /** @type {HTMLElement | null} */ (document.activeElement);
  const titleId = `dialog-title-${Math.random().toString(36).slice(2, 8)}`;
  const dialog = /** @type {HTMLDialogElement} */ (h("dialog", { class: "dialog", "aria-labelledby": titleId },
    h("h2", { class: "dialog-title", id: titleId, text: title }),
    h("div", { class: "dialog-body" }, content),
    h("div", { class: "dialog-actions" }, ...actions)));
  dialog.addEventListener("close", () => {
    dialog.remove();
    opener?.focus?.();
    onClose?.();
  });
  dialog.addEventListener("click", (event) => {
    if (event.target === dialog) dialog.close();
  });
  document.body.append(dialog);
  dialog.showModal();
  return { close: () => dialog.close(), element: dialog };
}
