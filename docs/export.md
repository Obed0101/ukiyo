# NativeAOT desktop export

The `Export.Desktop` host publishes a game as one macOS arm64 executable: NativeAOT, wgpu-native and SDL3 linked
statically (`DirectPInvoke` + `NativeLibrary` `.a`), no .NET runtime, no dylibs, no dev bridge. How to add one to a
game: `skills/ukiyo-hosts/SKILL.md` § NativeAOT export.

## What an export must satisfy

- `otool -L <binary>` lists only `/System/Library/Frameworks/*` and `/usr/lib/*`. The csproj links the system
  frameworks SDL3 and wgpu-native need (`LinkerArg`); those are allowed OS dependencies, nothing else is.
- `strings <binary> | grep -c DevBridge` prints 0: export hosts never set `UkiyoDevBridge`.
- The binary runs alone: copied to an empty folder and started with `env -i PATH=/usr/bin:/bin HOME=<empty dir>` (no
  `dotnet`, no `DOTNET_ROOT`, no repository path), it opens a window on Metal and writes GPU frames with
  `--capture <dir>`. `scripts/clean-run.sh` proves this (below).
- It runs the same game: its `snapshot` lines equal a headless JIT run at the same checkpoints once the `renderer`
  and `backend` fields are removed, and its `[ukiyo] target=… game=… <file>=<hash>` line equals headless after
  `target=`. Scene JSON and other files in `Shared/assets` are embedded resources and are not in the hash; the
  snapshot equality covers them (SceneHop's snapshots carry the active scene and its entities).

## Exports

| Game | Host project | Binary |
|---|---|---|
| RotatingCube | `samples/RotatingCube/Hosts/Export.Desktop` | `rotating-cube` |
| TexturedQuad | `samples/TexturedQuad/Export.Desktop` | `textured-quad` |
| LitCube | `samples/LitCube/Export.Desktop` | `lit-cube` |
| SceneHop | `samples/SceneHop/Export.Desktop` | `scene-hop` |

TexturedQuad, LitCube and SceneHop keep the older layout (`<game>/<Host>/`, docs/game-layout.md); their export
csproj is the template's with relative references, no `UkiyoGameDir` (the game folder is the host folder's parent,
which is where `Directory.Build.targets` finds `Shared/assets`) and the same `UkiyoSharedSource` glob and optional
`ProjectReference`s as the game's Desktop host: TexturedQuad globs `Shared/**/*.cs` so `gen/QuadArt.g.cs` is compiled
and hashed, LitCube adds `Ukiyo.Physics`, SceneHop embeds `Shared/assets/scenes/*.json`. RotatingCube's checks are in
G0.md and G1.md and run in `scripts/g0.sh`.

## Checks

- **CI (macOS job, no window):** publish all four, `otool -L` system-only, no `DevBridge` string
  (`.github/workflows/ci.yml`, step "NativeAOT exports"). The ubuntu job builds `Ukiyo.slnx` without every project
  whose path matches `/(Export\.)?Desktop/`, which covers the four export hosts.
- **Local, opens a window:** `scripts/aot-exports.sh [TexturedQuad LitCube SceneHop]` (checkpoints `CHECKPOINTS`,
  default `0,60`). Per sample it publishes to `artifacts/<binary>`, checks `otool` and `DevBridge`, runs the headless
  JIT host, runs the binary alone under `env -i` with `--capture`, and compares snapshots and the hash line. The
  window run takes `/tmp/ukiyo-gate.lock` and releases it on exit or failure. Evidence and a results table:
  `artifacts/evidence-aot-exports/`. Exit 1 if any check fails.

- **Clean machine, opens a window:** `scripts/clean-run.sh [Sample] [--binary PATH]` (default RotatingCube; publishes
  its `Export.Desktop` unless `--binary` names an already published executable). It copies only the executable to a
  fresh temp folder and requires, under `env -i PATH=/usr/bin:/bin HOME=<temp>/home`: `command -v dotnet` finds
  nothing and `DOTNET_ROOT` is unset; `otool -L` lists only `/System/Library/Frameworks`, `/usr/lib` or bundled
  dylibs that exist next to the binary (the export bundles none); the run exits 0 and `--capture` writes real PNG
  frames; and the dylibs dyld loaded (`DYLD_PRINT_LIBRARIES`, reported when dyld honors it) all come from the OS or the
  temp folder. Evidence: `artifacts/evidence-clean-run/<binary>/` and `results.md`. Exit 1 on any failure. What it
  does not prove: a Mac that lacks the SDK on disk (the dotnet binary is hidden from the process, not absent), or
  another macOS version.

## Results (UKIYO-42, 2026-10-16)

Apple M4 Pro, macOS 27.0, .NET SDK 10.0.401, wgpu-native 29.0.1.1, `scripts/aot-exports.sh`, checkpoints 0,60.

| Game | Bytes | `otool -L` | `DevBridge` | Snapshots vs headless (renderer/backend removed) | Hash line vs headless | Frames |
|---|---|---|---|---|---|---|
| TexturedQuad | 10,752,016 | 28 entries, all `/System/Library/Frameworks` or `/usr/lib` | 0 | equal, ticks 0 and 60 | equal: `Program.cs=067A82A56A30 TexturedQuadGame.cs=E033A2D19CCA gen/QuadArt.g.cs=348884E7F07A` | 2 × 1920x1280, Metal |
| LitCube | 10,735,448 | same 28 entries | 0 | equal, ticks 0 and 60 | equal: `LitCubeGame.cs=DBA95819877B Program.cs=81948D92886F` | 2 × 1920x1280, Metal |
| SceneHop | 10,851,608 | same 28 entries | 0 | equal, ticks 0 and 60 | equal: `Program.cs=2DC7CF81261C SceneHopGame.cs=DC07780FDB1C` | 2 × 1920x1280, Metal |

Frames looked at (tick 60, `artifacts/evidence-aot-exports/<binary>/capture/desktop-tick-000060.png`):

- TexturedQuad: the three wisps from the imported atlas (the right one tinted, the left one flipped), the row of nine
  glass tiles, the glass over the middle wisp and the screen-space glass in the top-left corner. The snapshot reports
  the 60x40 atlas from `gen/QuadArt.g.cs` and wisp frame 2.
- LitCube: the rotated cube on its floor, the warm lamp marker to the right and its falloff on the floor (G1Lit).
- SceneHop: the cavern scene (purple clear color) with the torch and three motes at the positions and scales of the
  tick-60 snapshot, which was loaded from the embedded `scenes/cavern.json` after the meadow scene's ticks ran out.

The 1920x1280 frames are the window's Retina backing size; headless runs used no renderer (`NullRenderer`), so the
comparison is of snapshots, not pixels.

## Clean run (UKIYO-14, 2026-10-24)

Apple M4 Pro, macOS 27.0, `scripts/clean-run.sh RotatingCube`, checkpoints 0,60: `rotating-cube` (10,718,880 bytes), alone in a
fresh temp folder, `env -i PATH=/usr/bin:/bin HOME=<temp>/home`. No `dotnet` on that PATH, `DOTNET_ROOT` unset;
`otool -L` 28 entries, all `/System/Library/Frameworks` or `/usr/lib` (no bundled dylibs); exit 0; two 1920x1280 Metal
frames written; every dylib dyld reported came from the OS. PASS.

Not covered: the binaries have not run on a physical Mac that has no .NET SDK installed at all (the clean run hides it
from the process but it is on this disk), and no export was checked with `--input` scripts or beyond tick 60.
