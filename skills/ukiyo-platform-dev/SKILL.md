---
name: ukiyo-platform-dev
description: Use when changing a ukiyo host (headless, desktop SDL3, browser WebAssembly) or the dev bridge — the frame loop, OS events to engine input, audio devices, host keys, command-line flags, native interop (SDL3 LibraryImport, JSImport/JSExport), export profiles, and adding a dev bridge command. Not for game code.
---

# Hosts and the dev bridge

A host (`IPlatformHost`, created by `PlatformBootstrap.Create(args)` in each `src/Ukiyo.Platform.*`) owns everything
that touches the real world: time, the window or page, OS input, the audio device, the renderer instance and the dev
session. The game and the runtime never see any of it.

## The loop every interactive host runs

```csharp
using var dev = DevHooks.Start(runtime, Target);        // null unless the dev bridge is linked
await runtime.StartAsync(renderer, config);
while (running) {
    PollEvents();                                       // OS → runtime.Input.Push(InputEvent…); host keys handled here
    dev?.Pump();                                        // bridge requests run here, between frames, on this thread
    runtime.AdvanceTime(elapsedSeconds);                // fixed 60 Hz ticks, no-op while paused
    runtime.RenderFrame(extent);                        // drawing-buffer size in pixels
    audio?.Pump();                                      // keep the device queue fed
}
```

- Input is pushed as it arrives and **latched once per tick** by the runtime; hosts never call into the game.
- Host keys are reserved and documented in README: desktop F5 pause / F6 step, browser Backquote / Shift+Backquote.
  Every other key goes to the game. Adding a host key is a user-visible change: README, both hosts, the skill.
- Resizes change the drawing-buffer extent passed to `RenderFrame`; the renderer gets `Resize` from the host.
- Headless has no loop: it steps to each checkpoint, prints machine-readable lines (`snapshot {json}`, `capture
  <path>`, `audio <path> seconds=…`) that `tools/mcp/src/engine.ts` parses. Changing a line format means changing that
  parser and its test (`tools/mcp/test/engine.test.ts`).

## Native interop

- SDL3 (`src/Ukiyo.Platform.Desktop/Sdl3.cs`): `[LibraryImport]`, blittable structs, event union read by offset.
  Offsets and constants come from the pinned SDL3 headers; when you write a binding without the header at hand, mark
  it unverified in docs/G1.md (current example: the audio stream bindings) and verify on a device.
- Browser (`src/Ukiyo.Platform.Browser`): `[JSImport(name, "ukiyo-host")]` for calls into `web/three-adapter/
  ukiyo-host.js`, `[JSExport]` for calls from JS (`Frame`, `SetPaused`, `Step`, `Snapshot`, `KeyEvent`,
  `PointerEvent`). Marshalled types are limited (numbers, bool, string, byte[], JSObject); keep signatures simple and
  make the JS side accept both `Uint8Array` and `Array` where marshalling is unverified.
- Both are AOT-safe by construction; never use `DllImport` with non-blittable marshalling or reflection-based JS
  interop.

## Flags and export profiles

- Headless flags are parsed in `HeadlessOptions.Parse`: validate every value and throw `ArgumentException` with a
  `[HEADLESS]:` message that names the expected format. Add the flag to the MCP tool that uses it.
- Development hosts set `<UkiyoDevBridge>true</UkiyoDevBridge>`; export profiles (`Export.Desktop`, published web)
  never do, so shipped games contain no listener. Keep it that way: dev tooling attaches only through `DevHooks`.

## Adding a dev bridge command

`src/Ukiyo.DevBridge/DevBridgeServer.cs`: newline-delimited JSON over TCP on 127.0.0.1, token from the 0600
discovery file `~/.ukiyo/live/<pid>.json`, requests executed in `Pump` on the host thread.

1. Add a `case` in `Execute` that validates its arguments, returns `Ok(id, json => …)` or `Error(id, "…")`. Responses
   must stay on one line (no pretty-printed JSON inside).
2. Add it to the unknown-command message, to `BRIDGE_COMMANDS` in `tools/mcp/src/bridge.ts`, to the `ukiyo_live`
   action enum, and to `BridgeCommand` in `studio/web/types.d.ts` if the Studio uses it.
3. Test it in `tests/Ukiyo.Tests/DevBridgeTests.cs` (a real loopback connection with the discovery token; the test
   pumps the session the way a host does) and document it in the ukiyo-live skill.

Never add a command that writes files outside the game's own process state, executes code, or works without the
token.

Related: ukiyo-engine, ukiyo-live (using the bridge), ukiyo-input, ukiyo-audio.
