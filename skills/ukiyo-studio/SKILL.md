---
name: ukiyo-studio
description: Use when the person asks to change the ukiyo Studio (their window onto the running game) — add, remove or rewrite a panel, change the layout, theme, colors, fonts or density, add a feature or control — or when you need to know what they are looking at (focused panel, selected entity, tick) or tell them something. The Studio UI is files: panels/*.js, layouts/*.json, themes/*.json, studio.json; open pages update live.
---

# The ukiyo Studio

The Studio (`studio/`, started with `bun studio/src/server.ts`) shows the person the running game: the real frame,
transport, values, entities, input recording, activity. It is **a view, never the source of truth**: it reads and
steers the game only through the dev bridge. Everything it looks like is a file you can edit, and every open page
picks up the change without a reload.

## Change it the way the person asks

| They say | You change |
|---|---|
| "make it lighter / use our brand colors / bigger corners" | a theme in `studio/themes/<name>.json` (or a new one) and `studio.json` `"theme"` |
| "I only want the frame and play controls" | `studio.json` `"layout": "simple"`, or `hide` / `show` |
| "put the values on the left, wider" | a layout in `studio/layouts/<name>.json` (grid columns/rows/areas) |
| "show me X while it runs" (a readout, a graph, a control) | a new panel `studio/panels/<id>.js`, placed in a layout or in `show` |
| "this panel should also…" | that panel's file |
| global look of buttons, tables, spacing | `studio/web/studio.css` (shared classes) |

Prefer the smallest layer: options before code (`studio.json` → `panels.<id>`), a theme before CSS, a panel before
shell changes. Ask the person only when the request has two materially different readings.

## Panels

```js
// @ts-check  — studio/panels/fps.js
/** @type {import("../web/types").PanelModule} */
export default {
  id: "fps",                       // must equal the file name
  title: "Tick rate",
  icon: "activity",                // a name in studio/web/icons.js (lucide; add new ones there from lucide's node data)
  description: "Ticks advanced per second of wall time.",
  mount(root, studio) {
    const { h } = studio;
    const value = h("p", { class: "readout" });
    root.append(value);
    let last = null;
    studio.on("status", (status) => {           // also: session, sessions, snapshot, frame, log, notice
      if (!status) {
        value.textContent = "—";
        return;
      }
      const now = performance.now();
      if (last) value.textContent = `${(((status.tick - last.tick) * 1000) / (now - last.at)).toFixed(1)} ticks/s`;
      last = { tick: status.tick, at: now };
    });
    return () => {};                            // undo timers/listeners not registered with studio.on
  },
};
```

The `studio` object (typed in `studio/web/types.d.ts`) is the panel's only door: `session`, `status`, `snapshot`,
`frame`, `call(cmd, args)` (dev bridge: status, pause, resume, step, snapshot, capture, input, record), `control`
(pause/resume/step that updates every panel), `capture()`, `options` (from `studio.json`), `actions` (header slot for
icon buttons), `view(patch)` (tell agents what the person selected), `compose(patch)`, `log(level, text)`, `h`,
`icon`. Panels never fetch the server directly and never hold game state of their own beyond what they display.

Then place it: add its id to a layout's `areas` (rectangular, every row the same number of cells, one track per
column/row, no `repeat()`), or add it to `studio.json` `"show"` to put it in the dock row.

## Layouts, themes, composition

- `layouts/<name>.json`: `description`, `columns`, `rows`, `areas` (CSS grid-template). Below 900 px the Studio stacks
  panels in reading order, so order areas by importance.
- `themes/<name>.json`: `description`, `colorScheme` ("dark"/"light"), and **every** token in `THEME_TOKENS`
  (`studio/src/manifest.ts`): colors (`bg`, `panel`, `panel-raised`, `line`, `line-strong`, `fg`, `muted`, `subtle`,
  `accent`, `accent-fg`, `ok`, `warn`, `danger`, `screen*`, `selection*`), `radius-control`, `radius-panel`,
  `font-sans`, `font-mono`, `font-pixel`. Adding a token means adding it to `THEME_TOKENS`, every theme and the
  defaults at the top of `web/studio.css`. Keep text contrast ≥ 4.5:1 against `bg` and `panel`; the `screen` tokens
  stay dark (the game frame is a display).
- `studio.json`: `layout`, `theme` ("auto" follows the system using `auto.dark` / `auto.light`), `hide`, `show`,
  `panels` (per-panel options). The person's layout switch, theme menu and Panels panel write this same file.

## Tools

- `ukiyo_studio {"action":"view"}`: what the person sees now — layout, theme, visible panels, `focusedPanel`, `session`,
  `tick`, `paused`, `panelState` (e.g. `entities.selected` and its pose, `viewport.watching` / `playing`),
  `panelErrors`, `fileError`. Read it before acting on "this", "here" or "the one I selected".
- `ukiyo_studio {"action":"notify","text":"…","level":"success"}`: tell them what you changed or what to try.
- `ukiyo_studio {"action":"compose", …}` and `{"action":"check"}`: validated composition change; validate all files.

## Proving a Studio change

1. `ukiyo_studio check` (or `cd studio && bun test`): every file valid, every layout names existing panels.
2. If a Studio is open: `ukiyo_studio view` shows no `panelErrors` / `fileError` after your edit.
3. Visual claims need the page rendered in a browser (desktop and narrow width, both themes). A valid file is not a
   visual check; say which of these you could not run.

Related: ukiyo-live (the dev bridge underneath), ukiyo-tooling, ukiyo-engine.
