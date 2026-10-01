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
<sub>Pre-alpha. 0.0.1 ships the core: one program on three targets. Sprites, physics, input, game UI, audio, skills, the MCP server and a first Studio are in the tree: they build and pass their tests, cross-target parity is still open (<a href="docs/G1.md">G1</a>, <a href="docs/G3.md">G3</a>).</sub>

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
| One edit, everywhere | Changing only `CubeGame.cs` changes every target the same way; renderer code is untouched. |
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
- **Game UI.** `Ukiyo.UI`: immediate-mode HUDs and menus, pixel font, keyboard and pointer focus.
- **Audio.** `Ukiyo.Audio`: sounds as events from `Update`, a shared mixer, WAV assets and a seeded sound-effect
  synthesizer, played through SDL3 on desktop and Web Audio in the browser, recorded to WAV headless.
- **Dev bridge.** A running desktop build answers on localhost: pause, step, snapshot, capture, inject and record input.
- **MCP server and skills.** `tools/mcp` (Bun, no dependencies) gives agents headless runs, captures, tests, the
  live bridge, new-game scaffolding, sprite generation through OpenAI or Gemini image models (or offline), sound
  generation and the Studio. Sixteen skills in `skills/`: simple, balanced and complete presets for making games, and
  an engine preset for working on ukiyo itself (architecture, renderers, hosts, tooling), linked into `.claude/skills`
  and `.agents/skills` for agents in this repository.
- **Studio base.** `studio/` (Bun, plain ES modules): the running game's real frame, transport, values, entities,
  input recording and activity, in simple, balanced or complete layouts. Panels, layouts and themes are files that
  agents edit and open pages apply live; agents can read what the person is looking at and send them notices
  ([G3](docs/G3.md)).
- **LanternRun.** A small platformer in `samples/LanternRun` that uses all of the above.

## Quickstart

Apple Silicon Mac with the .NET 10 SDK (`10.0.401`), Xcode command line tools, cmake, ninja and [bun](https://bun.sh).

```sh
git clone https://github.com/Obed0101/ukiyo && cd ukiyo
scripts/fetch-native.sh                  # pinned wgpu-native + SDL3, static and shared
(cd web/three-adapter && bun install)    # pinned three.js
```

Run the same game three ways:

```sh
dotnet run --project samples/RotatingCube/Headless   # no window, no GPU
dotnet run --project samples/RotatingCube/Desktop    # native window, wgpu → Metal  (F5: pause · F6: step)

dotnet publish samples/RotatingCube/Browser -c Release -o artifacts/web
python3 -m http.server --directory artifacts/web/wwwroot 8080   # open http://localhost:8080
```

Ship it:

```sh
dotnet publish samples/RotatingCube/Export.Desktop -c Release -o artifacts/desktop
./artifacts/desktop/rotating-cube        # one file: no .NET, no dylibs, no editor
```

Verify everything, end to end (opens a window for a few seconds):

```sh
scripts/g0.sh
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
[ukiyo] target=desktop game=RotatingCube CubeGame.cs=9846313B6743 Program.cs=138C7579F527
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
src/Ukiyo.Audio               mixer, WAV, sound synthesizer       (G1)
src/Ukiyo.DevBridge           live control of dev builds          (G1)
samples/RotatingCube/Shared   the one Program.cs and CubeGame.cs
samples/LanternRun            2D platformer sample                (G1)
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
- [ ] **G2 · Authoring and data.** Scenes and prefabs as code plus data, validated patches, the agent CLI and its tool adapters.
- [ ] **G3 · Studio and reload.** A human window onto a running game in simple, balanced or complete layouts, a dev bridge from any editor, controlled reload. The Studio base is written, [not yet run](docs/G3.md).
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
