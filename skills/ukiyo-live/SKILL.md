---
name: ukiyo-live
description: Use when working with a game that is running — pausing, stepping tick by tick, reading snapshots, capturing the real GPU frame, injecting input or recording a player's input through the dev bridge (ukiyo_live).
---

# Driving a running game

Desktop development builds (`UkiyoDevBridge=true` in the Desktop csproj, the default for new games) start a dev
bridge: a TCP server on `127.0.0.1` speaking newline-delimited JSON, guarded by a random token. Each running game
writes a discovery file `~/.ukiyo/live/<pid>.json` (mode 0600) with port and token; it is removed on exit. Exported
builds do not contain the bridge. Disable it with `UKIYO_DEV_BRIDGE=0`; pin the port with `UKIYO_DEV_BRIDGE_PORT`.

Start a game: `dotnet run --project games/<Name>/Desktop` (or `samples/LanternRun/Desktop`). Then:

| `ukiyo_live` action | Does |
|---|---|
| `list` | running dev builds (pid, game, port) |
| `status` | tick, paused, renderer, viewport, dropped ticks |
| `pause` / `resume` | stop or continue the simulation (the window keeps drawing) |
| `step` + `ticks` | advance exactly N ticks while paused |
| `snapshot` | the game's snapshot JSON at the current tick |
| `capture` | the real frame from the GPU (wgpu → Metal) as PNG |
| `input` + `events` | queue key/pointer events (same JSON as input scripts, without `tick`); they apply on the next tick |
| `record` + `on` | start recording the player's input; `on:false` stops and returns it as an input script |

Requests run on the game's own thread between frames, so a snapshot or capture is always of a consistent tick.

## Patterns

- Reproduce a bug a human hit: `record on` → the human plays → `record off` → save the script → replay it headless with
  `ukiyo_run {"input": …}` → fix → replay again → turn the replay into a test (ukiyo-testing).
- Inspect a moment: `pause` → `step 1` repeatedly → `snapshot` / `capture` at each tick.
- Drive a menu: `pause`, `input` Enter down, `step 1`, `input` Enter up, `step 1`, `capture`.

The person at the window keeps control: F5 pauses/resumes, F6 steps. Tell them before you inject input into a game
they are playing.

If they use the Studio (`bun studio/src/server.ts`), it drives the same bridge: `ukiyo_studio view` tells you which
entity they selected and whether they are watching or playing, and `ukiyo_studio notify` is how you tell them what
you are about to do (see ukiyo-studio).
