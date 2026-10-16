---
name: ukiyo-hosts
description: Use when running or shipping a ukiyo game on a host — headless, the desktop dev host (JIT + dylibs, dev bridge), the browser host (WebAssembly + three.js), the NativeAOT single-binary export or the static web export — or when a host csproj, a host's command-line flags, the web adapter sync into wwwroot/lib or whether the dev bridge is linked is in question. Not for changing host code (ukiyo-platform-dev).
---

# Hosts and export profiles

A game is one `Shared/` folder plus one folder per host, each holding only a csproj (Browser also `wwwroot/`). Every
host compiles the same `Shared/Program.cs`; `PlatformBootstrap.Create(args)` resolves to the bootstrap of the platform
project that host references (`src/Ukiyo.Platform.{Headless,Desktop,Browser}`). Game code never knows which one runs.

| Host folder | Build | Renderer | Dev bridge | Run |
|---|---|---|---|---|
| `Headless` | `Microsoft.NET.Sdk`, JIT | `NullRenderer`; CPU `SoftwareRenderer` with `--capture` | no | `dotnet run --project <game>/Headless -- <flags>` or `ukiyo_run` |
| `Desktop` (dev) | JIT, `osx-arm64`, `SelfContained=false`; copies `libwgpu_native.dylib` + `libSDL3.dylib` from `native/osx-arm64/lib` next to the exe | wgpu → Metal | **yes** (`UkiyoDevBridge=true`) | `dotnet run --project <game>/Desktop` |
| `Browser` | `Microsoft.NET.Sdk.WebAssembly` (IL; `-p:RunAOTCompilation=true` for WASM AOT) | three.js, WebGL2 | no | `dotnet run` (SDK dev server `WasmAppHost`, random port) or publish (below) |
| `Export.Desktop` | `PublishAot=true`; wgpu-native and SDL3 linked statically (`DirectPInvoke` + `.a`); only system frameworks | wgpu → Metal | **never** | `dotnet publish <game>/Export.Desktop -c Release -o artifacts/desktop` |

Check any host instead of trusting this table:
`dotnet msbuild <game>/<Host> -getProperty:UkiyoDevBridge -getProperty:PublishAot -getItem:ProjectReference`
(Desktop shows `"UkiyoDevBridge": "true"` and a `Ukiyo.DevBridge.csproj` reference; Export.Desktop shows
`"PublishAot": "true"` and no bridge). Desktop needs `scripts/fetch-native.sh` once (the csproj fails with "Native
dependencies missing" otherwise); it is macOS arm64 only.

## How a host gets the game

```xml
<UkiyoSharedSource Include="../Shared/**/*.cs" />
<Compile Include="@(UkiyoSharedSource)" Link="Shared/%(RecursiveDir)%(Filename)%(Extension)" />
<ProjectReference Include="../../../src/Ukiyo.Platform.Headless/Ukiyo.Platform.Headless.csproj" />
<ProjectReference Include="../../../src/Ukiyo.Physics/Ukiyo.Physics.csproj" />   <!-- + UI, Audio if the game uses them -->
```

- Games from `ukiyo_new_game` and `samples/TexturedQuad` glob `Shared/**/*.cs`; RotatingCube, LanternRun, LitCube and
  SceneHop glob `Shared/*.cs`, so a subfolder (`Shared/gen/`, `Shared/Levels/`) is **not compiled** there.
- Every host lists the same optional systems (`Ukiyo.Physics`, `Ukiyo.UI`, `Ukiyo.Audio`). One missing reference
  breaks only that host's build. Paths are relative (`../../../src/`): a game builds only inside the repository.
- `Directory.Build.targets` embeds `Shared/assets/**` as `assets/<path>` in every host and hashes each
  `UkiyoSharedSource` file (SHA-256) into the assembly. Every run prints it, and every snapshot carries it as `"source"`:
  `[ukiyo] target=headless game=TexturedQuad Program.cs=067A82A56A30 QuadArt.g.cs=348884E7F07A …`. Same hashes on
  two targets = same game code.

## Command-line flags (from each host's `Parse`)

Unknown flags are ignored without an error, and a flag missing its value is skipped: check the defaults in the output.
`--checkpoints` takes comma-separated ticks, sorted; a non-number throws `FormatException`.

| Host | Flags |
|---|---|
| Headless | `--checkpoints 0,60,180,600` (default) · `--input script.json` · `--capture dir` (CPU frames `headless-tick-NNNNNN.png` + `.json`) · `--size 1280x720` (default; bad value → `[HEADLESS]:` error) · `--audio run.wav` |
| Desktop, Export.Desktop (same `DesktopHost`) | `--capture dir` (GPU readback at each checkpoint, `desktop-tick-NNNNNN.png` + `.json`, then exit) · `--checkpoints` · `--frames N` (exit after N presented frames, prints a snapshot) · `--resize-test` · `--input script.json`. Keys: F5 pause, F6 step one tick (prints a snapshot) |
| Browser (URL query → args in `web/three-adapter/ukiyo-host.js`) | `?checkpoints=0,60` → `--checkpoints` · `?paused` → `--paused` · `?fixed` → `--fixed-size` · `?input=input.json` (same origin; fetched and passed as `--input-json`). Keys: `` ` `` pause, Shift+`` ` `` step |

Headless stdout is a contract the MCP tools parse: `[ukiyo] target=…`, `[ukiyo] renderer=…`, one `snapshot {json}` per
checkpoint, `capture <path>`, `audio <path> seconds=… sounds=… peak=… rms=…`.

## The dev bridge exists only in dev hosts

A host csproj with `<UkiyoDevBridge>true</UkiyoDevBridge>` gets `src/Ukiyo.DevBridge` plus a module initializer that
sets `DevHooks.Factory`; the desktop loop calls `DevHooks.Start`, which is `null` everywhere else. Only Desktop dev hosts
set it. Headless and Browser never start a bridge (`ukiyo_live` works only with a running Desktop dev build). Never set
it in an export, and never call bridge types from `Shared/`. Proof for a published binary: `strings <binary> | grep -c
DevBridge` prints 0 (docs/G1.md recorded this for RotatingCube).

## Static web export: no daemon

```sh
(cd web/three-adapter && bun install)                       # once: the build copies three from node_modules
dotnet publish <game>/Browser -c Release -o artifacts/web     # add -p:RunAOTCompilation=true for WASM AOT
python3 -m http.server --directory artifacts/web/wwwroot 8080 # any static server; open http://localhost:8080
```

`artifacts/web/wwwroot` is the whole game. It must run from a plain static server: no ukiyo process, Studio, MCP
server or bridge behind it, no requests to localhost services, no server-side code. Anything the game needs is
compiled in or embedded from `Shared/assets`.

## How `web/three-adapter` reaches `wwwroot`

`<UkiyoBrowserHost>true</UkiyoBrowserHost>` runs `UkiyoSyncWebAdapter` before every build: it copies `protocol.js`,
`ukiyo-three.js`, `sprite-geometry.js`, `lit-material.js`, `ukiyo-host.js` into `wwwroot/lib/` and three's
`three.module.js` + `three.core.js` into `wwwroot/lib/three/` (error "three is not installed" without `bun install`).
`wwwroot/index.html` maps `"three"` to `./lib/three/three.module.js`; `wwwroot/main.js` calls `boot` from
`./lib/ukiyo-host.js`. Check: build the Browser host, then `ls <game>/Browser/wwwroot/lib`.

- Never edit `wwwroot/lib/`: the next build overwrites it. Change `web/three-adapter/` (an engine change).
- `.gitignore` covers `samples/*/Browser/wwwroot/lib/` only. Under `games/` the copies show as untracked: do not
  commit them (`git status --short games/<Name>`).

## Adding a NativeAOT export to a game

`ukiyo_new_game` does not create one. Copy `samples/RotatingCube/Export.Desktop/RotatingCube.Export.Desktop.csproj`
to `games/<Name>/Export.Desktop/<Name>.Export.Desktop.csproj`, then change `AssemblyName` and `RootNamespace`, use
the recursive `Shared/**/*.cs` glob above, and add the same optional `ProjectReference`s as the game's Desktop host.
Keep `PublishAot`, `DirectPInvoke`, `NativeLibrary` and the framework `LinkerArg`; do not add `UkiyoDevBridge`.
Prove it: `dotnet publish games/<Name>/Export.Desktop -c Release -o artifacts/<name>`, then `otool -L
artifacts/<name>/<assembly>` lists only `/System/Library/Frameworks/*` and `/usr/lib/*`, and the binary runs alone:
`./<assembly> --capture /tmp/cap` opens a window and writes GPU frames (needs a visible window on the Mac).

## Never

- `#if`, `OperatingSystem.Is…`, `RuntimeInformation` or host types (`Sdl3`, `JSImport`, `DevBridgeServer`) in
  `Shared/`. A behavior one host needs belongs in that host project (ukiyo-platform-dev).
- Game code in a host folder, or a second `Program.cs`. Host folders hold a csproj (and Browser's `wwwroot`) only.
- Different `ProjectReference`s or defines per host to make a game behave differently: parity checks compare hosts.

Related: ukiyo-core (layout, loop), ukiyo-new-game (scaffold), ukiyo-live (the bridge), ukiyo-platform-dev (changing
hosts), ukiyo-scenes-assets.
