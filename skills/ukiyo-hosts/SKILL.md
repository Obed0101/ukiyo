---
name: ukiyo-hosts
description: Use when running or shipping a ukiyo game on a host — headless, the desktop dev host (JIT + dylibs, dev bridge), the browser host (WebAssembly + three.js), the NativeAOT single-binary export or the static web export — or when a host csproj, a host's command-line flags, the web adapter sync into Www/lib or wwwroot/lib or whether the dev bridge is linked is in question. Not for changing host code (ukiyo-platform-dev).
---

# Hosts and export profiles

A game is one `Shared/` folder plus one folder per host, each holding only a csproj. Two layouts exist:

| Layout | Host projects | Browser page | Who uses it |
|---|---|---|---|
| Template (docs/game-layout.md) | `<game>/Hosts/<Host>/<Game>.<Host>.csproj` | `<game>/Www/` (index.html, main.js; `Www/lib/` synced) | `templates/game`, `samples/RotatingCube`, every `ukiyo_new_game` game |
| Older | `<game>/<Host>/<Game>.<Host>.csproj` | `<game>/Browser/wwwroot/` | LanternRun, TexturedQuad, SceneHop, LitCube, ImportedMesh, Minimap |

Below, `<host dir>` is `<game>/Hosts/<Host>` or `<game>/<Host>`. Every host compiles the same `Shared/Program.cs`;
`PlatformBootstrap.Create(args)` resolves to the bootstrap of the platform project that host references
(`src/Ukiyo.Platform.{Headless,Desktop,Browser}`). Game code never knows which one runs.

| Host | Build | Renderer | Dev bridge | Run |
|---|---|---|---|---|
| `Headless` | `Microsoft.NET.Sdk`, JIT | `NullRenderer`; CPU `SoftwareRenderer` with `--capture` | no | `dotnet run --project <host dir> -- <flags>` or `ukiyo_run` |
| `Desktop` (dev) | JIT, `osx-arm64`, `SelfContained=false`; copies `libwgpu_native.dylib` + `libSDL3.dylib` from `native/osx-arm64/lib` next to the exe | wgpu → Metal | **yes** (`UkiyoDevBridge=true`) | `dotnet run --project <host dir>` |
| `Browser` | `Microsoft.NET.Sdk.WebAssembly` (IL; `-p:RunAOTCompilation=true` for WASM AOT) | three.js, WebGL2 | no | `dotnet run` (SDK dev server `WasmAppHost`, random port) or publish (below) |
| `Export.Desktop` | `PublishAot=true`; wgpu-native and SDL3 linked statically (`DirectPInvoke` + `.a`); only system frameworks | wgpu → Metal | **never** | `dotnet publish <host dir> -c Release -o artifacts/desktop` |

Check any host instead of trusting this table:
`dotnet msbuild <host dir> -getProperty:UkiyoDevBridge -getProperty:PublishAot -getProperty:UkiyoGameDir -getItem:ProjectReference`
(Desktop shows `"UkiyoDevBridge": "true"` and a `Ukiyo.DevBridge.csproj` reference; Export.Desktop shows
`"PublishAot": "true"` and no bridge). Desktop needs `scripts/fetch-native.sh` once (the csproj fails with "Native
dependencies missing" otherwise); it is macOS arm64 only.

## How a host gets the game

Template layout (copy from `templates/game/Hosts/<Host>/`):

```xml
<UkiyoGameDir>$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '..', '..'))</UkiyoGameDir>   <!-- PropertyGroup -->
<UkiyoSharedSource Include="$(UkiyoGameDir)Shared/**/*.cs" />
<Compile Include="@(UkiyoSharedSource)" Link="Shared/%(RecursiveDir)%(Filename)%(Extension)" />
<Compile Include="$(UkiyoGameDir)Platform/Desktop/**/*.cs" Link="Platform/Desktop/%(RecursiveDir)%(Filename)%(Extension)" />  <!-- Desktop, Export.Desktop; Browser links Platform/Browser -->
<ProjectReference Include="$(UkiyoRoot)src/Ukiyo.Platform.Headless/Ukiyo.Platform.Headless.csproj" />
<ProjectReference Include="$(UkiyoRoot)src/Ukiyo.Physics/Ukiyo.Physics.csproj" />   <!-- + UI, Audio if the game uses them -->
```

Older layout: `<UkiyoSharedSource Include="../Shared/**/*.cs" />` (or `../Shared/*.cs`) and relative
`../../../src/…` references; without `UkiyoGameDir`, `Directory.Build.targets` takes the host folder's parent as the game.

- The template layout, `ukiyo_new_game` games, TexturedQuad and ImportedMesh glob `Shared/**/*.cs`;
  LanternRun, LitCube, SceneHop and Minimap glob `Shared/*.cs`, so a subfolder (`Shared/gen/`, `Shared/Levels/`) is **not
  compiled** there.
- `Platform/<Host>/` (template layout) is host-only C#: not in the other hosts, not in the source hash. Keep it out of
  `Update`/`Snapshot` or the hosts stop agreeing (docs/game-layout.md, partial-method pattern).
- Every host lists the same optional systems (`Ukiyo.Physics`, `Ukiyo.UI`, `Ukiyo.Audio`). One missing reference
  breaks only that host's build. A game builds only inside the repository (`$(UkiyoRoot)` and `Directory.Build.*`).
- `Directory.Build.targets` embeds `$(UkiyoGameDir)Shared/assets/**` as `assets/<path>` (dot files such as `.gitkeep`
  excluded) and hashes each `UkiyoSharedSource` file (SHA-256) into the assembly, keyed by its path below the glob
  (`Game.cs`, `gen/QuadArt.g.cs`). Every run prints it and every snapshot carries it as `"source"`:
  `[ukiyo] target=headless game=RotatingCube Game.cs=9846313B6743 Program.cs=138C7579F527`. Same hashes on two
  targets = same game code. Files in `Shared/assets` (scene JSON, embedded PNGs) are **not** in `source`: equal
  hashes do not prove equal scenes.
- `dotnet test tests/Ukiyo.Tests --filter GameLayoutTests` checks every template-layout game: one csproj per host
  folder, the `UkiyoGameDir`/`Shared/**/*.cs` lines, the right `Platform/` folder per host, no `#if` or interop in
  `Shared/`.

## Command-line flags (from each host's `Parse`)

Unknown flags are ignored without an error, and a flag missing its value is skipped: check the defaults in the output.
`--checkpoints` takes comma-separated ticks, sorted; a non-number throws `FormatException`.

| Host | Flags |
|---|---|
| Headless | `--checkpoints 0,60,180,600` (default) · `--input script.json` · `--capture dir` (CPU frames `headless-tick-NNNNNN.png` + `.json`) · `--size 1280x720` (default; bad value → `[HEADLESS]:` error) · `--audio run.wav` · `--patches log.json` (scene patch log, submitted before tick 0; prints `patches <path> submitted=N`) · `--edge-masks dir` (debug, needs `--capture`: geometric edge mask `edges-tick-NNNNNN.png`, and `…-pass-K.png` per pass, for pixel gates; frames unchanged) |
| Desktop, Export.Desktop (same `DesktopHost`) | `--capture dir` (GPU readback at each checkpoint, `desktop-tick-NNNNNN.png` + `.json`, then exit; frame size = the window's backing size, e.g. 1920x1280 on Retina, so framing differs from the 1280x720 headless default) · `--checkpoints` · `--frames N` (exit after N presented frames, prints a snapshot) · `--resize-test` · `--input script.json`. **No `--patches`: it is silently ignored** (no `patches … submitted=` line, `"patches":[]`); patch a running dev build with `ukiyo_live patch`, and exports cannot take patches. Keys: F5 pause, F6 step one tick (prints a snapshot) |
| Browser (URL query → args in `web/three-adapter/ukiyo-host.js`) | `?checkpoints=0,60` → `--checkpoints` · `?paused` → `--paused` · `?fixed` → `--fixed-size` · `?input=input.json` (same origin; fetched and passed as `--input-json`). Checkpoint snapshots are not printed to the console: they land in a `<script type="application/json" id="ukiyo-evidence">` element and the title becomes `ukiyo:evidence-ready` (read it with `scripts/web-evidence.sh <wwwroot> <out.html> [port] [query]`, or `globalThis.ukiyo.snapshot()` from DevTools/Playwright). Keys: `` ` `` pause, Shift+`` ` `` step |

Headless stdout is a contract the MCP tools parse: `[ukiyo] target=…`, `[ukiyo] renderer=…`, one `snapshot {json}` per
checkpoint, `capture <path>`, `edges <path>` (only with `--edge-masks`), `audio <path> seconds=… sounds=… peak=… rms=…`.

## The dev bridge exists only in dev hosts

A host csproj with `<UkiyoDevBridge>true</UkiyoDevBridge>` gets `src/Ukiyo.DevBridge` plus a module initializer that
sets `DevHooks.Factory`; the desktop loop calls `DevHooks.Start`, which is `null` everywhere else. Only Desktop dev hosts
set it. Headless and Browser never start a bridge (`ukiyo_live` works only with a running Desktop dev build). Never set
it in an export, and never call bridge types from `Shared/`. Proof for a published binary: `strings <binary> | grep -c
DevBridge` prints 0 (docs/G1.md recorded this for RotatingCube).

## Static web export: no daemon

```sh
(cd web/three-adapter && bun install)                         # once: the build copies three from node_modules
dotnet publish <host dir of Browser> -c Release -o artifacts/web   # add -p:RunAOTCompilation=true for WASM AOT
python3 -m http.server --directory artifacts/web/wwwroot 8080   # any static server; open http://localhost:8080
```

`artifacts/web/wwwroot` is the whole game in both layouts (the template's `Www/` is published as `wwwroot`). It must
run from a plain static server: no ukiyo process, Studio, MCP server or bridge behind it, no requests to localhost
services, no server-side code. Anything the game needs is compiled in or embedded from `Shared/assets`.

After an AOT publish, the Browser host's `obj/` holds AOT-trimmed assemblies; a following plain publish can fail at
runtime start ("NIY encountered", page status `error: Program terminated with exit(1)`). Move that `obj/` away and
publish again (docs/G1.md).

## How `web/three-adapter` reaches the page

`<UkiyoBrowserHost>true</UkiyoBrowserHost>` runs `UkiyoSyncWebAdapter` before every build: it copies `protocol.js`,
`ukiyo-three.js`, `sprite-geometry.js`, `lit-material.js`, `ukiyo-host.js` into `<web root>/lib/` and three's
`three.module.js` + `three.core.js` into `<web root>/lib/three/` (error "three is not installed" without `bun install`).
The web root is the host's `wwwroot/` (older layout) or `UkiyoWebRoot` = `$(UkiyoGameDir)Www/` (template layout),
whose files are linked into the project as `wwwroot/…` so build, `dotnet run` and publish serve them unchanged.
`index.html` maps `"three"` to `./lib/three/three.module.js`; `main.js` calls `boot` from `./lib/ukiyo-host.js`.
Check: build the Browser host, then `ls <game>/Www/lib` (or `<game>/Browser/wwwroot/lib`).

- Never edit `lib/`: the next build overwrites it. Change `web/three-adapter/` (an engine change).
- `.gitignore` covers `**/Www/lib/`, `samples/*/Browser/wwwroot/lib/` and `games/*/Browser/wwwroot/lib/`; do not
  commit the copies.

## NativeAOT export

Template-layout games have `Hosts/Export.Desktop/` already (`ukiyo_new_game` creates it unless `hosts` leaves it
out). For an older-layout game, copy `templates/game/Hosts/Export.Desktop/MyGame.Export.Desktop.csproj` to
`<game>/Export.Desktop/<Name>.Export.Desktop.csproj`, replace `MyGame`/`my-game`, drop the `UkiyoGameDir` line (or
point it at the game folder) and add the same optional `ProjectReference`s as the game's Desktop host. Keep
`PublishAot`, `DirectPInvoke`, `NativeLibrary` and the framework `LinkerArg`; do not add `UkiyoDevBridge`.
Prove it: `dotnet publish <host dir> -c Release -o artifacts/<name>`, then `otool -L artifacts/<name>/<assembly>`
lists only `/System/Library/Frameworks/*` and `/usr/lib/*`, and the binary runs alone: `./<assembly> --capture
/tmp/cap` opens a window and writes GPU frames (needs a visible window on the Mac). TexturedQuad, LitCube and SceneHop
have such an older-layout export; `scripts/aot-exports.sh` runs all of these checks for them plus snapshot and
source-hash equality with headless (docs/export.md has the results).

## Never

- `#if`, `OperatingSystem.Is…`, `RuntimeInformation` or host types (`Sdl3`, `JSImport`, `DevBridgeServer`) in
  `Shared/`. A behavior one host needs belongs in `Platform/<Host>/` (template layout) or that host project
  (ukiyo-platform-dev).
- Game code in a host folder, or a second `Program.cs`. Host folders hold a csproj only (older Browser hosts also
  `wwwroot/`).
- Different `ProjectReference`s or defines per host to make a game behave differently: parity checks compare hosts.

Related: ukiyo-core (layout, loop), ukiyo-new-game (scaffold), ukiyo-live (the bridge), ukiyo-platform-dev (changing
hosts), ukiyo-scenes-assets.
