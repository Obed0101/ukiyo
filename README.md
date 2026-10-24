<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/banner-dark.jpg">
  <source media="(prefers-color-scheme: light)" srcset=".github/assets/banner-light.jpg">
  <img alt="ukiyo" src=".github/assets/banner-dark.jpg" width="100%">
</picture>

<h3>The world is code. Agents build it. You watch every tick.</h3>

An agent-native game engine in C#. Your coding agent reads and changes the game's code while it runs,<br>
through skills and MCP. The Studio is the human window: watch, edit, take over.<br>
Assemble it like Arch Linux: simple, balanced or complete.<br>
<sub>Pre-alpha. 0.0.1 ships the core: one program on three targets. Sprites, physics, input, game UI, audio, lighting, several cameras, scenes and patches, skills, the MCP server and a first Studio are in the tree: they build and pass their tests. G1 parity passes; the Studio (<a href="docs/G3.md">G3</a>) is not closed (<a href="docs/G1.md">G1</a>).</sub>

<br>

[![status](https://img.shields.io/badge/status-pre--alpha_0.0.1-000?style=flat-square)](docs/G0.md)
[![.NET](https://img.shields.io/badge/.NET-10-000?style=flat-square)](https://dotnet.microsoft.com/)
[![renderers](https://img.shields.io/badge/renderers-wgpu_·_three.js_·_null-000?style=flat-square)](#how-it-works)
[![targets](https://img.shields.io/badge/targets-macOS_·_web_·_headless-000?style=flat-square)](#quickstart)
[![license](https://img.shields.io/badge/license-MIT-000?style=flat-square)](LICENSE)

[Website](https://ukiyoengine.top) · [Quickstart](#quickstart) · [How it works](#how-it-works) · [Roadmap](#roadmap) · [G0 report](docs/G0.md) · [Protocol](docs/protocol.md)

</div>

<br>

<div align="center">
  <img alt="The same Program.cs rendered on wgpu/Metal and on three.js/WebGL2, tick by tick" src=".github/assets/one-program-two-renderers.gif" width="720">
  <br>
  <sub>Same <code>Program.cs</code>. Left: native window, wgpu → Metal. Right: browser, .NET WebAssembly → three.js.<br>Every frame was read back from each renderer at the same tick. Nothing here is a mockup.</sub>
</div>

<br>

## Why ukiyo

Game engines were built around a person holding a mouse. The editor owns the truth, code gets attached to it, and
anything automated has to poke at it from the outside.

ukiyo starts from the other end.

- **The code is the world.** Scenes, entities, transforms and components are plain C#. There is no hidden editor state to reverse-engineer: what a person or an agent writes is exactly what runs.
- **Agents are native, not bolted on.** Fixed 60 Hz ticks, headless runs, structured snapshots, typed errors and build-stamped evidence are engine features. Tool protocols sit on top as thin adapters; they are not the product.
- **Your agent gets a whole engine, live.** It connects through skills and MCP and works on the game while it runs. Rendering, materials and textures, physics, audio, input and game UI, each with skills that teach an agent how to use it and checks that prove the result: code tests and visual tests on real frames.
- **Humans stay in the loop.** The Studio, on the roadmap, shows what agents are doing, lets you review their changes and take over at any point, without ever becoming the source of truth.
- **Take only what you need.** Like Arch Linux: start simple, balanced or complete, both for the Studio and for the engine, systems and skills your game is built from. Nothing you did not choose gets in the way.
- **An open library, not a store.** Models, materials, sounds, templates and whole game systems published as packages by the community. Free and open source, all of it: no marketplace cut, no locked assets.
- **One program, every target.** Renderers implement one contract. The same `Program.cs` runs on wgpu/Metal, in the browser through WebAssembly, and with no GPU at all.
- **Games ship alone.** A desktop export is a single native executable. A web export is a folder of static files. No editor, daemon or model at runtime.

## What 0.0.1 already proves

| | |
|---|---|
| One source, three targets | Every host prints the SHA-256 of the shared game files. They match on desktop, web, headless and both AOT builds. |
| Real renderers | wgpu-native on Metal (Apple M4 Pro) and three.js on WebGL2, plus a null renderer that never touches a GPU. |
| Identical simulation | Rotation at ticks 0, 60, 180 and 600 is bit-for-bit equal across all targets. |
| Nothing moves behind your back | Paused in the browser, 1.5 s of real time advance zero ticks and consecutive frames are pixel-identical. |
| One edit, everywhere | Changing only the game file (`CubeGame`, now `Shared/Game.cs`) changes every target the same way; renderer code is untouched. |
| Single binary | `rotating-cube` is one 10.3 MB NativeAOT executable with wgpu and SDL linked in. It runs copied alone into an empty folder. |
| Static web | The published site runs from any static file server, in IL or WebAssembly-AOT form. |

All of it is reproducible with one script and documented in the [G0 report](docs/G0.md), including what is still open.

## In the tree, not released yet

Written after 0.0.1. It builds with warnings as errors, its C# and JS tests pass, and LanternRun runs headless, on
Metal and in the browser with the same simulation and the same pixels, so the G1 checks pass; the Studio (G3) is
still open. [G1](docs/G1.md) and [G3](docs/G3.md) list what ran, what passed and what is missing.

- **2D layer.** Textures (PNG decoder, `CreateTexture`), sprites in world and screen space, sprite sheets and
  animations, on wgpu/Metal, three.js and a new CPU renderer that captures frames headless.
- **Input.** Keyboard and pointer latched per tick, deterministic input scripts, recording and replay.
- **Physics.** `Ukiyo.Physics`: a deterministic 2D rigid-body world with sensors, raycasts and layers.
- **NPC behaviour.** `Ukiyo.AI`: state machines, behavior trees, deterministic grid A* and steering, readable in snapshots.
- **Game UI.** `Ukiyo.UI`: immediate-mode HUDs and menus, pixel font, keyboard and pointer focus.
- **Audio.** `Ukiyo.Audio`: sounds as events from `Update`, a shared mixer, WAV assets and a seeded sound-effect
  synthesizer, played through SDL3 on desktop and Web Audio in the browser, recorded to WAV headless.
- **Dev bridge.** A running desktop build answers on localhost: pause, step, snapshot, capture, inject and record input.
- **Lighting and cameras.** Profile `G1Lit`: per-pixel Lambert with directional and point lights, and up to four
  camera passes per frame (minimap, split screen). See [lighting](docs/rendering-lighting.md) and
  [passes](docs/rendering-passes.md); samples `LitCube` and `Minimap`.
- **Scenes, prefabs and patches (G2).** Scenes as data, prefabs as functions, validated patches applied between
  ticks, a bounded patch history in snapshots. See [scenes-patches](docs/scenes-patches.md); sample `SceneHop`.
- **Imported art.** PNG sprites and glTF meshes compile into `Shared/gen/*.g.cs` through `tools/Ukiyo.Import`
  ([assets](docs/assets.md)); samples `TexturedQuad` and `ImportedMesh`.
- **MCP server and skills.** `tools/mcp` (Bun, no dependencies) gives agents headless runs, captures, tests, the
  live bridge, new-game scaffolding, `ukiyo_check` (layout conformance of a game, see
  [game layout](docs/game-layout.md)), asset import, sprite generation through OpenAI or Gemini image models (or
  offline), sound generation and the Studio; the same tools run as a CLI (`bun tools/mcp/src/cli.ts --help`).
  Eighteen skills in `skills/`: simple, balanced and complete presets for making games, and an engine preset for
  working on ukiyo itself (architecture, renderers, hosts, tooling), linked into `.claude/skills` and
  `.agents/skills` for agents in this repository.
- **LanternRun.** A small platformer in `samples/LanternRun` that uses the 2D layer, input, physics, UI and audio.

Samples: `RotatingCube` (the template), `LanternRun`, `TexturedQuad`, `LitCube`, `ImportedMesh`, `Minimap`,
`SceneHop`, `Sparks` (particles). Docs: [assets](docs/assets.md) · [conventions](docs/conventions.md) · [export](docs/export.md) ·
[game layout](docs/game-layout.md) · [protocol](docs/protocol.md) · [lighting](docs/rendering-lighting.md) ·
[passes](docs/rendering-passes.md) · [scenes and patches](docs/scenes-patches.md) · reports
[G0](docs/G0.md), [G1](docs/G1.md), [G3](docs/G3.md).

## Studio

`studio/` (Bun, plain ES modules) is the person's window onto a running game: its real frame, transport, values,
entities, input recording and activity, in simple, balanced or complete layouts. Panels, layouts and themes are
files that agents edit and open pages apply live; agents read what the person is looking at and send them notices
(`ukiyo_studio`). It never owns game state.

```sh
dotnet run --project samples/RotatingCube/Hosts/Desktop   # any development build announces itself in ~/.ukiyo/live
bun studio/src/server.ts                                  # → http://127.0.0.1:4100
```

Status: the base runs against a live Metal game ([G3](docs/G3.md)); controlled reload is not implemented yet.
On a Mac, `scripts/mac-app.sh` builds and installs `apps/mac` as Ukiyo.app, the Studio in a native window.
Details: [studio/README.md](studio/README.md).

## Quickstart

Apple Silicon Mac with the .NET 10 SDK (`10.0.401`), Xcode command line tools, cmake, ninja and [bun](https://bun.sh).

```sh
git clone https://github.com/Obed0101/ukiyo && cd ukiyo
scripts/fetch-native.sh                  # pinned wgpu-native + SDL3, static and shared
(cd web/three-adapter && bun install)    # pinned three.js
```

Run the same game three ways:

```sh
dotnet run --project samples/RotatingCube/Hosts/Headless   # no window, no GPU
dotnet run --project samples/RotatingCube/Hosts/Desktop    # native window, wgpu → Metal  (F5: pause · F6: step)

dotnet publish samples/RotatingCube/Hosts/Browser -c Release -o artifacts/web
python3 -m http.server --directory artifacts/web/wwwroot 8080   # open http://localhost:8080
```

Ship it:

```sh
dotnet publish samples/RotatingCube/Hosts/Export.Desktop -c Release -o artifacts/desktop
./artifacts/desktop/rotating-cube        # one file: no .NET, no dylibs, no editor
```

Verify everything, end to end (opens a window for a few seconds):

```sh
scripts/g0.sh
```

Prove an export runs alone, with no .NET and no dylibs on the path (opens a window briefly):

```sh
scripts/clean-run.sh RotatingCube        # see docs/export.md "Clean run"
```

## The shape of a game

This is the entire entry point. Every host compiles this exact file:

```csharp
return await GameApplication.RunAsync(new CubeGame(), PlatformBootstrap.Create(args));
```

The game owns its state and describes what to draw. Renderers only consume that description:

```csharp
public void Update(in TickInfo tick)
{
    _rotation = ExpectedRotation(tick.Tick + 1);
}

public void Extract(FrameBuilder frame)
{
    frame.Camera = FrameBuilder.LookAt(new Vector3(0f, 0.6f, 3.2f), Vector3.Zero, MathF.PI / 3f, 0.1f, 100f);
    frame.Draw(_mesh, _material, Pose.ToMatrix());
}
```

And every run tells you what it actually ran on:

```text
[ukiyo] target=desktop game=RotatingCube Game.cs=9846313B6743 Program.cs=138C7579F527
[ukiyo] renderer=WgpuRenderer backend=Metal device="Apple M4 Pro () · wgpu-native 29.0.1.1" profile=G0Unlit capture=True
```

## How it works

```text
                     Program.cs + your game  (C#)
                                 │
          ukiyo runtime ─ fixed 60 Hz ticks · snapshots · typed errors · evidence
                                 │
                   RenderPacket  (binary protocol v1)
                                 │
          ┌──────────────────────┼──────────────────────┐
     WgpuRenderer          ThreeJsRenderer          NullRenderer
     wgpu → Metal          .NET WASM → three.js     no GPU
     native window         browser canvas           tests and agents
```

- The simulation never depends on a renderer. It produces presentation data; renderers consume it.
- Renderers never animate on their own. In the browser, `requestAnimationFrame` only forwards time to C#.
- Every boundary fails loudly: stale handles, truncated packets, NaNs and unknown versions become typed errors, never dropped frames.
- Wire format, conventions and limits: [protocol](docs/protocol.md) · [conventions](docs/conventions.md).

```text
src/Ukiyo.Render              renderer contract, handles, validation, protocol, PNG, sprite geometry
src/Ukiyo.Core                fixed clock, IGame, runtime, snapshots, input, sprites, assets
src/Ukiyo.Render.*            Null · Software (CPU) · Wgpu · Three
src/Ukiyo.Platform.*          Headless · Desktop (SDL3 + Metal) · Browser (WASM)
src/Ukiyo.Physics             deterministic 2D physics            (G1)
src/Ukiyo.UI                  game UI: HUDs, menus, pixel font    (G1)
src/Ukiyo.AI                  NPC behaviour: FSM, BT, A*, steering
src/Ukiyo.Audio               mixer, WAV, sound synthesizer       (G1)
src/Ukiyo.DevBridge           live control of dev builds          (G1)
templates/game                the layout every new game copies    (docs/game-layout.md)
samples/RotatingCube          that template filled in: Shared/Program.cs and Shared/Game.cs (CubeGame)
samples/*                     RotatingCube, LanternRun, TexturedQuad, LitCube, ImportedMesh, Minimap, SceneHop, Sparks
src/Ukiyo.Particles          deterministic particles and VFX
apps/mac                      Ukiyo.app, the Studio as a native Mac window (scripts/mac-app.sh)
tools/Ukiyo.Import            art importer: sprites, atlases, glTF meshes
web/three-adapter             three.js presentation adapter, no game rules
tools/mcp                     MCP server for agents               (G1)
studio                        the Studio: live view, files as UI  (G3)
skills                        agent skills and presets
tests/Ukiyo.Tests             core, bridge, renderer, input, physics, UI tests
```

Host keys: desktop **F5** pause/resume, **F6** step one tick; browser **`** pause, **Shift+`** step. Every other key
goes to the game.

## Roadmap

- [x] **G0 · One program, two real renderers.** Metal and WebGL2 from the same C#, NativeAOT single binary, static web export.
- [x] **G1 · Runtime slice.** Input, 2D sprites, physics, game UI, audio, dev bridge, MCP and skills. Headless, Metal and WebGL2 match in simulation and pixels; NativeAOT export without the dev bridge ([report](docs/G1.md)). Not committed or released yet.
- [ ] **G2 · Authoring and data.** Scenes and prefabs as code plus data, validated patches, the agent CLI and its tool adapters. In the tree: scenes, prefabs, patches with a bounded history and replay (headless and live), glTF and sprite import, lit meshes, several cameras, the CLI and `ukiyo_check`. No G2 report yet, so the gate stays open.
- [ ] **G3 · Studio and reload.** A human window onto a running game in simple, balanced or complete layouts, a dev bridge from any editor, controlled reload. The Studio base has [live Metal evidence](docs/G3.md), including Watch, pointer input and recording → headless replay; controlled reload remains open.
- [ ] **G4 · Evidence and games.** Replays, visual conformance and the first real games.
- [ ] **G5 · The open library.** Packages for assets and game systems, published and shared by the community, free and open source.

Each gate closes only with evidence. Order and scope can still change.

## The name

**浮世** *ukiyo*, the floating world. Ukiyo-e masters carved a woodblock once and printed it again and again.
ukiyo carves your game once and prints it on every target.

## Contributing

It is early and the architecture is still moving. Issues and discussions are welcome; for anything larger than a
small fix, open an issue first. Run `dotnet test tests/Ukiyo.Tests` and `scripts/g0.sh` before sending changes to
the core.

### CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request to `dev` and `main`, on
`ubuntu-latest` and `macos-latest` (Apple Silicon):

- Release build. macOS builds all of `Ukiyo.slnx` after `scripts/fetch-native.sh` (cached). Linux leaves out only the
  `Desktop` and `Export.Desktop` host projects: they target `osx-arm64` and need the macOS wgpu-native/SDL3 builds in
  `native/`. The desktop platform and wgpu libraries, every Headless and Browser host, the importer and the tests
  still build there.
- `dotnet test tests/Ukiyo.Tests` and `bun test` in `tools/mcp`, `studio` and `web/three-adapter`. None of these
  needs native libraries, a GPU or a display; the Studio's `*.browser.ts` checks (Chrome plus a running Metal game) are
  not part of `bun test` and stay manual.
- Cross-platform determinism: `SnapshotGoldenTests` compares every sample's headless snapshots and packet digests with
  `tests/Ukiyo.Tests/golden/snapshots/<Sample>.txt`, so a golden recorded on one runner must come out byte for byte on
  the other, and runs again with `DOTNET_EnableHWIntrinsic=0` (System.Numerics without SIMD or FMA). On a mismatch
  the actual output is uploaded as an artifact. Intended changes: `UKIYO_UPDATE_GOLDEN=1 dotnet test tests/Ukiyo.Tests
  --filter SnapshotGoldenTests`, then review the diff.
- The golden CLI transcript, `bun scripts/cli-flow.ts` (help, status, scaffold, headless run, CPU capture, import
  check, removal), against `tools/mcp/test/golden/cli-flow.txt`. When a CLI or tool output changes on purpose,
  `bun scripts/cli-flow.ts --update` rewrites it; review the diff and commit the golden with the change.
- macOS only: the NativeAOT exports of RotatingCube, TexturedQuad, LitCube and SceneHop, published but not run,
  checked for system-only dependencies and no dev bridge. Running them (window, snapshot and hash parity with
  headless) is `scripts/aot-exports.sh` on a Mac with a display ([docs/export.md](docs/export.md)).

Nothing opens a window: desktop hosts are built, never run. GPU and browser evidence (`scripts/g0.sh`, `g1.sh`,
`ukiyo_live capture`) stays a local check on a Mac.

## Credits

Built on [wgpu-native](https://github.com/gfx-rs/wgpu-native), [SDL](https://github.com/libsdl-org/SDL),
[three.js](https://github.com/mrdoob/three.js) and [.NET](https://github.com/dotnet/runtime). The wordmark is set in
[Geist Pixel](https://github.com/vercel/geist-pixel-font) (SIL OFL).

## License

[MIT](LICENSE) © 2026 Obed González and the ukiyo contributors.

<br>

<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/assets/brand/ukiyo-mark-white.svg">
    <img alt="" src=".github/assets/brand/ukiyo-mark.svg" width="28">
  </picture>
</div>
