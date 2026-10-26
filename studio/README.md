# ukiyo Studio

The person's window onto a running game. It shows the real frame the game renders, its tick, values and entities;
pauses, steps and plays it; records play as input scripts agents can replay. It never owns game state: the game is
code, and the Studio only looks at it through the dev bridge.

```sh
dotnet run --project samples/LanternRun/Desktop   # any development build; it announces itself in ~/.ukiyo/live
bun studio/src/server.ts                          # → http://127.0.0.1:4100
```

It runs against a live Metal game; running games from the Studio and controlled reload are described below.

## The UI is files

| What | Where | Live? |
|---|---|---|
| Panels | `panels/<id>.js`, one module each (`export default { id, title, icon, description, mount(root, studio) }`) | yes: saving a panel remounts it |
| Layouts | `layouts/<name>.json`: CSS grid `columns`, `rows`, `areas` naming panel ids | yes |
| Themes | `themes/<name>.json`: every token in `THEME_TOKENS` (`src/manifest.ts`) | yes |
| Composition | `studio.json`: active layout and theme, hidden/extra panels, per-panel options | yes |
| Shell, shared styles | `web/studio.js`, `web/studio.css`, `web/dom.js`, `web/icons.js` | reload the page |

Adding a panel is adding a file: drop `panels/<id>.js`, then name it in a layout's `areas` or add it to `show` in
`studio.json`. People change the composition in Settings (gear in the header, Cmd+, in the Mac app: theme, layout,
panels); agents edit the same files or call `ukiyo_studio compose`. Either way it is one file, visible in `git diff`.

The panel contract and the `studio` object every panel receives are typed in `web/types.d.ts`. Panels talk to the
game only through `studio.call` / `studio.control` / `studio.capture`, never to the server directly.

## Panels and layouts

| Panel | Shows |
|---|---|
| `viewport` (Frame) | The game's real rendered frame, on demand or continuously, with keyboard and pointer passthrough |
| `hierarchy` | Entities grouped by scene; selection is shared with the Inspector and with agents |
| `inspector` | Pose and values of the selected entity; position edits are scene patches applied at a tick boundary |
| `console` | Output of games the Studio launched, Studio activity and bridge errors in one stream |
| `assets` | `Shared/assets` and art, with PNG previews and stale-import marks (read-only) |
| `values` | Snapshot values with their last change |
| `entities` | Positions, rotations and scales from the snapshot |
| `input` | Records play as a replayable input script |
| `log` (Activity) | Bridge commands, file changes, agent notices and errors |

Layouts: `editor` (default: Hierarchy, Frame and Inspector, with Console and Assets as tabs below), `simple`, `focus`,
`balanced`, `wide` and `complete`. A layout groups panels into tabs with `"tabs": {"bottom": ["console", "assets"]}`.

## Your own config (the rice)

Your dotfiles layer over the repo without forking it. The directory is `$UKIYO_STUDIO_CONFIG`, else
`$XDG_CONFIG_HOME/ukiyo/studio`, else `~/.config/ukiyo/studio`, with the same shape as `studio/`:
`studio.json`, `themes/`, `layouts/`, `panels/` and an optional `user.css` (loaded after `web/studio.css`).

- A user file replaces the repo file of the same name; new names are added. User `studio.json` fields override the
  repo's (`hide`/`show` replace, `auto` and each panel's options merge). A user theme may say `"extends": "ink"` and
  list only the tokens it changes.
- Everything live-reloads, including a config directory created while the Studio runs. Theme colors are set inline by
  the active theme, so recolor with a theme, not `user.css`; `user.css` is for everything else.
- A broken user file never stops the Studio: it is skipped (the repo file of that name stays in effect), shown in the
  status bar and reported by `ukiyo_studio list`/`check` as `[user] …`. A broken repo file is `[repo] …` and fatal, as before.
- Where compose writes: the person's own UI (Settings) writes the user `studio.json`, creating it, so
  the repo stays clean. `ukiyo_studio compose` writes the user `studio.json` when it exists (a repo write would be
  shadowed by it), otherwise the repo's, as before; `target: "user" | "repo"` forces one. The reply names the file.

Start from [`examples/rice/`](examples/rice): `cp -R studio/examples/rice ~/.config/ukiyo/studio`. Shipped extras:
themes `graphite`, `contrast`, `linen` (all text at least 4.5:1, checked in `test/studio.test.ts`), layouts `focus`
and `wide`. Layering lives in `src/layers.ts`.

## Run and reload games

The Studio opens on the **Hub**, the list of the runnable projects of this repository (`samples/*/Desktop`,
`games/*/Desktop`, `games/*/Hosts/Desktop`, one csproj each, with the dev bridge), newest opened first. Opening one
shows the editor (`#/p/<Name>`); **New project** scaffolds `games/<Name>` with the same code as `ukiyo_new_game`.
Recents are kept in `~/.ukiyo/studio-recent.json` (`UKIYO_STUDIO_RECENT`), outside the repo and the config folder.
The header's **Run** starts `dotnet run --project <csproj>`
(`UKIYO_DOTNET`, `~/.dotnet/dotnet`, then PATH). Build state and compiler errors appear in the header; when the game
announces itself in `~/.ukiyo/live` the Studio follows it. **Stop** ends it; quitting the Studio stops the games it
started. **Reload** rebuilds (a failing build leaves the game running), relaunches and restores the same tick. Only
games the Studio started can be stopped or reloaded.

How reload works: an always-on `ReplayLog` in `GameRuntime` records latched input, patch submissions and viewport
changes. Reload builds first, pauses the old game at a tick boundary, exports its snapshot and history, stops it and
relaunches `dotnet run --no-build` with `UKIYO_RESTORE=<log file>`. The new process replays tick 0 to the old tick
before it announces itself and stays paused; the Studio compares the snapshots and resumes if the old game was running.

- Preserved: the snapshot at that tick, pending queued input, patches and their refusals, paused or running.
- Not preserved: audio (silent during replay); window size (the new process keeps its own, so state that depends on
  the pointer's viewport position may differ). Sessions with more than 2,000,000 log entries are refused rather than
  guessed. If the code changed, the replay still runs; the report flags `sourceChanged` and lists the first
  differences.

API (same token, Host and origin guard as every call): `GET /api/games`, `POST /api/run {game}`,
`POST /api/run/stop {run|pid|game}`, `POST /api/run/reload {run|pid|game, wait?}`. A game is named by its id
(`sample/LanternRun`); the server only ever spawns a csproj it discovered under the repository, never a path or
command from the request. Agents reload with `ukiyo_live {"action":"reload"}`, which needs a running Studio.

## What agents get

- `ukiyo_studio view`: what the person is looking at — layout, theme, focused panel, selected entity and its pose,
  tick, paused, whether they are watching or playing, panel load errors.
- `ukiyo_studio notify`: a message shown to the person in the Studio.
- `ukiyo_studio compose | list | check`: composition changes and validation of every Studio file in both layers; `list` tags each panel, layout and theme `repo` or `user`.
- The `ukiyo-studio` skill explains how to change panels, themes and layouts.

## Security

The server binds 127.0.0.1. Every API call needs the token embedded in the page (other origins cannot read it); the
Host header must be this server (DNS rebinding); POSTs must be JSON from this origin. Agents find the token in
`~/.ukiyo/studio/<pid>.json` (mode 0600), as they do for games in `~/.ukiyo/live`. The bridge proxy only forwards the
eight bridge commands; the run endpoints take game ids, not paths or commands.

## Design

Operate surface, same world as the site (`site/DESIGN.md`): ink/paper palette by system setting, hairline structure,
seal vermilion only for live state and focus, Geist Sans for UI, Geist Mono for data, Geist Pixel only for the
ticking number. The game frame is a dark screen in every theme, scaled with nearest-neighbour. No page-load
choreography; presses scale to 0.96; notices rise 8 px in 200 ms; reduced motion keeps only opacity. Below 900 px
panels stack in the layout's reading order. Fonts come from `site/node_modules/geist` when installed, otherwise
system fonts.

**Mac app**: `scripts/mac-app.sh` installs the Studio as `/Applications/Ukiyo.app` (native window, starts and stops the
server itself); see [apps/mac](../apps/mac/README.md).
