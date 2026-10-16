# Game layout

Every new game starts as a copy of `templates/game/` (by hand or with `ukiyo_new_game`). `samples/RotatingCube` is
that template with its names filled in, and `tests/Ukiyo.Tests/GameLayoutTests.cs` keeps the two identical. The
layout is a convention backed by those checks, not a build rule: older samples (LanternRun, TexturedQuad, SceneHop,
LitCube) keep `<game>/<Host>/` and still build. Nothing in `IGame` or the engine changed for it.

```
MyGame/
  Shared/                    compiled into EVERY host and hashed into the source identity; no #if, no host APIs
    Program.cs               the only entry point; never edited after the copy
    Game.cs                  the IGame (the template ships the G0 CubeGame verbatim)
    Scenes/                  prefabs, scene setup, SceneRegistry wiring (scene JSON itself goes in assets/scenes/)
    Entities/                entity types and their state
    Systems/                 per-tick logic called from Game.Update (movement, rules, spawning)
    Lib/                     game-local helpers with no engine state (math, tables, palettes)
    gen/                     generated C# from tools/Ukiyo.Import (*.g.cs); never edited by hand
    assets/                  embedded into every host as assets/<path> (PNG, JSON, sounds); read via context.Assets
  Platform/
    Desktop/                 C# only the Desktop and Export.Desktop hosts compile
    Browser/                 C# only the Browser host compiles (JSImport and friends)
  Hosts/
    Headless/MyGame.Headless.csproj               no window, no GPU: snapshots and CPU captures
    Desktop/MyGame.Desktop.csproj                 SDL3 + wgpu/Metal, JIT, dev bridge on
    Browser/MyGame.Browser.csproj                 .NET WebAssembly + three.js, serves Www/
    Export.Desktop/MyGame.Export.Desktop.csproj   NativeAOT single binary, no dev bridge
  Www/
    index.html  main.js      the browser page; lib/ is synced from web/three-adapter at build (gitignored)
  art/                       optional: source PNGs + *.import.json, never shipped (docs/assets.md)
```

Empty folders carry a `.gitkeep` so they survive a clone; dot files are never embedded as assets.

## Mapping to the task's folder list

UKIYO-16 listed `MyGame/` with `Program.cs`, `Game.cs`, `Scenes/ Entities/ Systems/ Assets(+gen)/ Lib/ Shared/
Platform/{Desktop,Browser}/ Hosts/ Www/`. The layout keeps every item; three are placed or named differently:

| Task item | Here | Why |
|---|---|---|
| `Program.cs`, `Game.cs`, `Scenes/`, `Entities/`, `Systems/`, `Lib/` | `Shared/…` | `Shared/` is "compiled into every host": one glob (`Shared/**/*.cs`) is the whole shared surface, hashed as one identity, and the place a test can prove has no `#if`. The tools (`ukiyo_run`, sprites, sounds, importer) already look for `Shared/`. |
| `Assets/` | `Shared/assets/` | The embedded-asset folder every host, tool and skill already uses. A sibling `Shared/Assets/` would be the same directory on macOS (case-insensitive file system). |
| `Assets/gen` | `Shared/gen/` | Where `tools/Ukiyo.Import` writes by default. Generated code must compile, assets must not: inside `assets/` it would be embedded as data, and a separate `Assets/` collides with `assets/` (above). |
| `Shared/` | `Shared/` (the parent of the above) | |
| `Platform/{Desktop,Browser}/`, `Hosts/` (4 csprojs), `Www/` | as listed | |

## What compiles where

| Folder | Headless | Desktop | Export.Desktop | Browser | In source hash |
|---|---|---|---|---|---|
| `Shared/**/*.cs` | yes | yes | yes | yes | yes |
| `Platform/Desktop/**/*.cs` | no | yes | yes | no | no |
| `Platform/Browser/**/*.cs` | no | no | no | yes | no |
| `Shared/assets/**` | embedded | embedded | embedded | embedded | no |
| `Www/**` | no | no | no | web root | no |

Each host csproj sets `UkiyoGameDir` (its game folder, two levels up) and links the shared code with the
`UkiyoSharedSource` pattern:

```xml
<UkiyoGameDir>$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '..', '..'))</UkiyoGameDir>
...
<UkiyoSharedSource Include="$(UkiyoGameDir)Shared/**/*.cs" />
<Compile Include="@(UkiyoSharedSource)" Link="Shared/%(RecursiveDir)%(Filename)%(Extension)" />
<ProjectReference Include="$(UkiyoRoot)src/Ukiyo.Platform.Headless/Ukiyo.Platform.Headless.csproj" />
```

`Directory.Build.targets` does the rest: it hashes every `UkiyoSharedSource` file into `ukiyo.source:<path>` (files in
subfolders keep their relative path, `Scenes/Level.cs`, so equal names never collide), embeds
`$(UkiyoGameDir)Shared/assets/`, and for the Browser host (`UkiyoWebRoot=$(UkiyoGameDir)Www/`) syncs the adapter, the
shell and three into `Www/lib/` and serves `Www/` as `wwwroot` for build, `dotnet run` and publish. References use
`$(UkiyoRoot)`, so a game folder works at any depth under the repository. Optional systems (Physics, UI, Audio) are a
`ProjectReference` per host, after the platform reference.

## Host-specific code without `#if`

Shared code declares what it needs; a `Platform/<Host>/` file supplies it. With a partial method the hosts that have
no implementation compile the call away:

```csharp
// Shared/Lib/HostInfo.cs
public static partial class HostInfo
{
    public static string Name { get { var name = "headless"; Describe(ref name); return name; } }
    static partial void Describe(ref string name);
}
// Platform/Desktop/HostInfo.Desktop.cs
public static partial class HostInfo { static partial void Describe(ref string name) => name = "desktop"; }
```

Anything a host-specific file changes must stay out of the simulation (`Update`, `Snapshot`), or headless, desktop
and web stop agreeing. Use it for presentation and integration (window title, a browser share button), never rules.

## Make a new game by hand

From the repository root, with `<Name>` in PascalCase (`SkyLantern`) and `<kebab>` its kebab case (`sky-lantern`):

```sh
mkdir -p games/<Name> && rsync -a --exclude bin --exclude obj --exclude Www/lib templates/game/ games/<Name>/
cd games/<Name>
for h in Headless Desktop Browser Export.Desktop; do mv "Hosts/$h/MyGame.$h.csproj" "Hosts/$h/<Name>.$h.csproj"; done
grep -rl --exclude-dir=bin --exclude-dir=obj -e MyGame -e my-game . | xargs sed -i '' -e 's/MyGame/<Name>/g' -e 's/my-game/<kebab>/g'
cd ../..
```

That is the whole rename: `MyGame` (namespace `Ukiyo.Games.MyGame`, `Name`, page title, project names) and `my-game`
(assembly names) are the only template tokens; `grep -r -e MyGame -e my-game games/<Name>` must print nothing.
`Program.cs` keeps creating `CubeGame`: rename the class in `Game.cs` and that one line together when you replace the
cube. Then add the four hosts to `Ukiyo.slnx` (a `<Folder Name="/games/">` with
`<Project Path="games/<Name>/Hosts/<Host>/<Name>.<Host>.csproj" />` each) and prove it:

```sh
dotnet build games/<Name>/Hosts/Headless && dotnet run --project games/<Name>/Hosts/Headless -- --checkpoints 0,60
dotnet build games/<Name>/Hosts/Desktop                # needs scripts/fetch-native.sh once
dotnet build games/<Name>/Hosts/Browser                # needs (cd web/three-adapter && bun install) once
dotnet publish games/<Name>/Hosts/Export.Desktop -c Release -o artifacts/<kebab>
dotnet test tests/Ukiyo.Tests --filter GameLayoutTests # the layout checks also cover games/*
```

The first line prints `[ukiyo] target=headless game=<Name> Game.cs=… Program.cs=…`: the same hashes appear on every
host that compiled the same files.

## Checks (`GameLayoutTests`)

- `templates/game` has every folder and file listed above.
- `samples/RotatingCube` equals the template file for file after `Ukiyo.Games.MyGame → Ukiyo.Samples.RotatingCube`,
  `MyGame → RotatingCube`, `my-game → rotating-cube` (build output and `Www/lib` excluded). Change the template and
  the sample together.
- For the template and every `samples/*` or `games/*` folder with `Hosts/`: each host folder holds exactly
  `<Game>.<Host>.csproj`, which sets `UkiyoGameDir`, links `Shared/**/*.cs` and links `Platform/Desktop` only from
  Desktop/Export.Desktop and `Platform/Browser` only from Browser; no `Shared/**/*.cs` has `#if`, `JSImport`,
  `JSExport`, `LibraryImport`, `DllImport` or `OperatingSystem.Is…`.

## RotatingCube migration (UKIYO-16)

`Shared/CubeGame.cs` became `Shared/Game.cs` with the same bytes; `Program.cs` is unchanged. The SHA-256 of both files
is unchanged (`Game.cs=9846313B6743…`, `Program.cs=138C7579F527…`); only the identity key of the game file changed
from `CubeGame.cs` to `Game.cs`. Hosts moved from `samples/RotatingCube/<Host>/` to `samples/RotatingCube/Hosts/<Host>/`
and the page from `Browser/wwwroot/` to `Www/` (now with a focusable canvas, as every scaffolded game has). Scripts
(`g0.sh`, `g0-change-test.sh`, `g0-parity.ts`, `g1lit.sh`) use the new paths.
