---
name: ukiyo-engine
description: Use when changing the ukiyo engine itself (src/, web/, tools/, studio/) rather than a game. Architecture and boundaries, build rules (warnings as errors, NativeAOT, determinism), where each kind of change goes, how to add a module, and the evidence a change needs before it counts. Load before ukiyo-renderer-dev, ukiyo-platform-dev or ukiyo-tooling.
---

# Working on the ukiyo engine

Games are code on top of the engine (see ukiyo-core). This skill is for the engine: the runtime, renderers, hosts,
tools and Studio. The rule that shapes everything: **the simulation never depends on a renderer, a host or a tool.**
Data flows one way — game → `RenderPacket` → renderer — and every boundary fails loudly with a typed error.

## Map

```
src/Ukiyo.Render           contracts every renderer implements: IRenderer, IRenderCapture, RenderPacket,
                           ResourceBatch, handles, RenderValidation, PacketCodec (binary protocol v1), PNG, SpriteGeometry
src/Ukiyo.Core             IGame, GameRuntime (fixed 60 Hz, Step/AdvanceTime/RenderFrame/Capture), GameContext,
                           FrameBuilder, snapshots, input, sprites, audio contract, DevHooks, IPlatformHost
src/Ukiyo.Physics|UI|Audio optional systems; depend on Core, never on a host or renderer
src/Ukiyo.Render.*         Null · Software (CPU) · Wgpu (P/Invoke to wgpu-native) · Three (JSImport to web/three-adapter)
src/Ukiyo.Platform.*       Headless · Desktop (SDL3) · Browser (WASM): own the loop, events, audio device, dev session
src/Ukiyo.DevBridge        localhost control of dev builds; linked only when UkiyoDevBridge=true
web/three-adapter          JS half of the browser renderer and host (protocol.js mirrors PacketCodec.cs)
tools/mcp                  MCP server (Bun); studio/ the Studio (Bun + plain JS); skills/ agent skills
tests/Ukiyo.Tests          xUnit for everything in src/; Bun tests live next to their JS/TS
docs/                      protocol, conventions, gate reports (G0.md, G1.md, G3.md)
```

Dependency direction: Render ← Core ← (Physics, UI, Audio) ← Platform.* ← games. Render.* depend only on Render.
Nothing in `src/` references `tools/`, `studio/` or a sample.

## Build rules (they are errors, not advice)

- `Directory.Build.props`: .NET 10, `Nullable`, `TreatWarningsAsErrors`, `InvariantGlobalization`; everything under
  `src/` is `IsAotCompatible`, so trimming/AOT analyzers fail the build. No reflection over unknown types, no
  `dynamic`, no `Assembly.Load`, no runtime codegen. Use source-generated `LibraryImport` / `JSImport`, `Utf8JsonWriter`
  and `JsonDocument` instead of reflection-based serializers.
- Central package versions (`Directory.Packages.props`). New packages need a reason; engine code has almost none.
- Determinism: simulation code uses `Simulation.TickSeconds`, never wall-clock time; seeded randomness; stable
  iteration order; `DetMath` for trigonometry that must match across CoreCLR, NativeAOT and WebAssembly. Anything a
  host does with real time (frame pacing, audio queueing) stays in the host.
- Errors are typed and tagged: `RenderException(RenderErrorCode…)` with `[RENDER:Code]`, `ArgumentException` /
  `FormatException` / `InvalidDataException` with a `[AREA]:` prefix (`[RUNTIME]`, `[INPUT]`, `[WAV]`, `[HEADLESS]`…).
  Never swallow a failure as a dropped frame or a default value.
- Match the code around you: file-scoped namespaces, records for data, `sealed` by default, XML doc comments on public
  API that explain *why*, no `#region`, no TODOs.

## Where a change goes

| Change | Touch | Also update |
|---|---|---|
| New thing games draw | `Ukiyo.Render` contract + `RenderValidation` + every renderer | protocol (both codecs), docs/protocol.md, ukiyo-renderer-dev |
| New runtime feature (per-tick service) | `Ukiyo.Core` (`TickInfo` init property, `GameRuntime.Step`) | ukiyo-core skill, CoreTests |
| New optional system | new `src/Ukiyo.<Name>` project referencing Core | slnx, README layout, a skill, tests |
| New host behavior | `src/Ukiyo.Platform.*` | ukiyo-platform-dev, the other hosts if it is user-visible |
| Agent tooling | `tools/mcp/src/tools/<name>.ts` | ukiyo-tooling, server test tool list |
| Studio | `studio/panels`, `layouts`, `themes`, `web` | ukiyo-studio |

Adding a project: `src/Ukiyo.<Name>/Ukiyo.<Name>.csproj` with only `ProjectReference`s (props come from
`Directory.Build.props`), add it to `Ukiyo.slnx`, reference it from `tests/Ukiyo.Tests` and from the hosts or samples
that use it. A system that games opt into is referenced by the game's host csproj, never forced into every host.

## Evidence

A change counts when its check ran, not when it compiles:

```sh
dotnet build Ukiyo.slnx -c Release        # warnings are errors, AOT analyzers on
dotnet test tests/Ukiyo.Tests             # add a test for the behavior you changed
(cd web/three-adapter && bun test)        # when protocol or adapter changed
(cd tools/mcp && bun test) ; (cd studio && bun test)
scripts/g0.sh                             # cross-target parity must keep passing
```

Rendering changes also need a frame: CPU capture (`ukiyo_capture`) and, for GPU paths, a desktop capture through the
dev bridge (`ukiyo_live capture`) at the same tick. Report each check as run/failed/not run; a build is not a visual
check and a CPU frame is not a GPU frame. Gate reports in `docs/` record dates, machines and versions.

Skills describe the code as it is: when you change an API, change the skill that teaches it in the same commit.

Related: ukiyo-renderer-dev, ukiyo-platform-dev, ukiyo-tooling, ukiyo-studio, ukiyo-testing.
