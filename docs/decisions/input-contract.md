# Input contract: latched raw keys (UKIYO-39)

Decided 2026-10-24 by the engineering lead on the owner's instruction, on evidence from the shipped code.

## Context

UKIYO-20..23 specified quantized actions (`MoveX/MoveY` -1000..1000, a Jump edge, `Update(in TickInfo, in InputState)`).
What shipped (6e56e71) and passes every G1 parity check is raw keys and pointer latched once per tick into
`tick.Input` (`InputState`), with `Axis()` helpers and `Update(in TickInfo)` unchanged. LanternRun, the Studio recording
format, the dev bridge input commands, `InputScript` and the golden snapshots all use the shipped form.

## Options

1. Keep latched raw keys; close the gaps (edge cases, tests, docs).
2. Add an opt-in quantized action layer on top of the latched state.
3. Replace with the original quantized contract.

## Decision

Option 1. Hosts push raw `InputEvent`s (key down/up, pointer move/down/up) into `runtime.Input`; before each tick the
runtime latches them into an immutable `InputState`. The `InputScript` JSON event list is the only replay and agent
format. A quantized layer (option 2) can be added later as a pure function of `InputState` without touching hosts or
scripts; it is not needed by any sample, so it is not built now.

Why: option 3 breaks LanternRun, recordings and the bridge for no determinism gain. Raw events are already
deterministic because the simulation only ever sees `InputState`, and scripts are the simplest thing for an agent to
write (`{"tick":10,"key":"Space","down":true}`).

## Defined behaviour (tested in `InputTests`)

- Events apply in arrival order at the latch before a tick; scripted events for tick N are fed before tick N runs.
- `WasPressed`/`WasReleased` are edges since the previous tick. Down and up inside one tick report both edges;
  `IsDown` shows the final state (a tap can be seen only through the edges).
- A key down while already held (OS or browser key repeat) is not a new press. Desktop drops SDL repeats and the browser
  drops `e.repeat`; the collector also ignores them.
- A key up for a key that is not held is ignored.
- Focus loss (SDL focus lost, browser blur, tab hidden, pointer cancel) calls `ReleaseAll`: held keys and buttons get
  ordinary release events, delivered with the next latched tick, so they show as `WasReleased` and can be pressed again.
- Several ticks in one host frame: live events land on the first tick of the frame; the recording stores them with that
  tick, so replaying the script reproduces the run exactly. Splitting a script run into different `Step` chunks does not
  change what any tick sees.
- Host-reserved keys (desktop F5/F6, browser Backquote) never reach the game.

## Consequences

- Desktop (SDL3) and browser (DOM, `KeyboardEvent.code`) both push into the same `InputCollector`; the same script
  gives the same snapshots on every host (headless goldens, `scripts/` parity gates).
- Games derive movement with `Axis()`; any deadzone or quantization is the game's business and must stay deterministic.
- UKIYO-20..23 are satisfied by this contract, not by the original quantized text; UKIYO-24 physics decisions are
  unaffected.
