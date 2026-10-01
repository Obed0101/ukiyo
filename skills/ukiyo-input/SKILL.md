---
name: ukiyo-input
description: Use for keyboard and pointer input, movement axes, menus driven by keys, and for scripting input — deterministic input scripts for headless runs, tests and replays, and recording a player's input.
---

# ukiyo input

Hosts push raw events into `runtime.Input` (an `InputCollector`) whenever they arrive. Right before each tick the
runtime latches them into an immutable `InputState` that the game reads as `tick.Input`. Every tick sees a consistent
snapshot, so input is deterministic and replayable.

## Reading input in Update

```csharp
var input = tick.Input;
input.IsDown(Key.Right);            // held this tick
input.WasPressed(Key.Space);        // went down since the last tick (edge, true for one tick)
input.WasReleased(Key.Space);
input.Axis(Key.Left, Key.Right);                         // -1, 0 or 1
input.Axis(Key.Left, Key.Right, Key.A, Key.D);           // arrows or WASD, clamped
input.IsDown(PointerButton.Left); input.WasPressed(PointerButton.Left);
input.Pointer;                       // drawing-buffer pixels, top-left origin
input.Viewport;                      // RenderExtent the pointer refers to
```

A press and release inside the same frame still reports `WasPressed` and `WasReleased` on that tick.
Keys: `A`–`Z`, `D0`–`D9`, `Space`, `Enter`, `Escape`, `Tab`, `Backspace`, `Left`, `Right`, `Up`, `Down`, `LeftShift`,
`RightShift`, `LeftControl`, `RightControl`, `LeftAlt`, `RightAlt`, `F1`–`F12`.

Reserved by hosts (never reach the game): desktop **F5** pause/resume, **F6** step one tick while paused; browser
**`** (Backquote) pause, **Shift+`** step. When the window loses focus every held key is released.

## Input scripts (agents, tests, replays)

```json
[
  {"tick": 1,  "key": "Enter", "down": true},
  {"tick": 2,  "key": "Enter", "down": false},
  {"tick": 30, "key": "Right", "down": true},
  {"tick": 40, "pointer": [320, 180]},
  {"tick": 41, "button": "Left", "down": true, "pointer": [320, 180]}
]
```

Keys accept engine names or browser `KeyboardEvent.code` values (`"ArrowLeft"`, `"KeyA"`, `"Digit1"`). Events are
fed before the tick with the same number is simulated.

- Headless CLI: `--input script.json`; MCP: `ukiyo_run {"game":…,"input":[…]}`.
- C#: `runtime.Script = InputScript.Parse(json);` or `runtime.Input.Push(InputEvent.KeyDown(Key.Space)); runtime.Step(1);`
- Record: `runtime.Input.Recording = true;` … `new InputScript(runtime.Input.Recorded).ToJson()`; live builds: `ukiyo_live {"action":"record","on":true}` then `on:false` returns the script.

Hold a key with a down event and a later up event; a key that is never released stays held.

## Clicking UI from a script

UI coordinates live on a virtual canvas. Convert with `ui.CanvasToScreen(rect.Center, viewport)` (see ukiyo-ui) or
compute it: canvas 320×180 on a 1280×720 buffer is scale 4, no offset.
