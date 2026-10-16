---
name: ukiyo-new-game
description: Use when starting a new ukiyo game or prototype from an idea — scaffolding the project, choosing modules (2D/3D, physics, UI), building the first playable loop, adding art and proving it runs on every target.
---

# Starting a new game

Every game is a copy of `templates/game/` (layout, rules and checks: `docs/game-layout.md`):

```
games/<Name>/
  Shared/Program.cs  Shared/Game.cs        every host compiles all of Shared/**/*.cs; no #if, no host APIs
  Shared/Scenes/ Entities/ Systems/ Lib/   game code by role (grow into them; empty at first)
  Shared/gen/                              imported art (*.g.cs from tools/Ukiyo.Import)
  Shared/assets/                           embedded as assets/<path>
  Platform/Desktop/  Platform/Browser/     C# only that host compiles
  Hosts/{Headless,Desktop,Browser,Export.Desktop}/<Name>.<Host>.csproj   one csproj each, nothing else
  Www/index.html  Www/main.js              browser page (Www/lib is synced at build)
```

1. **Scaffold.** Either way gives the same layout.
   - Tool: `ukiyo_new_game {"name":"SkyLantern","template":"2d"}` creates `games/SkyLantern/` with all four hosts
     (pass `hosts` for fewer) and adds them to `Ukiyo.slnx`. The 2d template has a sprite, a physics body,
     arrow/WASD movement, jump, a HUD label and a `SceneRegistry` with one prefab (`crate`) and one scene (`start`),
     the place to grow levels and accept patches (ukiyo-core, `docs/scenes-patches.md`); 3d has a mesh turned by the
     arrow keys. The class is `SkyLanternGame` in `Shared/Game.cs`.
   - By hand: copy `templates/game` (it starts as the G0 rotating cube) and replace its only two tokens, `MyGame` and
     `my-game`, in file contents and the four csproj names. Exact commands: `docs/game-layout.md` § "Make a new game
     by hand". `grep -r -e MyGame -e my-game games/<Name>` must print nothing; then add the hosts to `Ukiyo.slnx`.
2. **Prove the skeleton.** `ukiyo_run {"game":"SkyLantern"}` (it builds and ticks), then `ukiyo_capture`. Without
   the MCP: `dotnet run --project games/SkyLantern/Hosts/Headless -- --checkpoints 0,60`; build the other hosts with
   `dotnet build games/SkyLantern/Hosts/<Host>`; `dotnet test tests/Ukiyo.Tests --filter GameLayoutTests` checks the
   layout.
3. **Make the core loop playable before anything else.** One verb (jump, shoot, place), one goal, one failure.
   Split by responsibility as it grows: `Shared/Entities/Player.cs`, `Shared/Systems/Movement.cs`,
   `Shared/Scenes/Levels.cs`; scene JSON in `Shared/assets/scenes/`. New files are compiled and hashed by every host
   with no csproj change.
4. **States.** Title → Playing → Paused → Won/GameOver as an enum, with menus from ukiyo-ui and focus on the default
   button. Put the state and the numbers that matter in `Snapshot().Values`.
5. **Art.** Start with `PixelArt.FromRows` placeholders or `ukiyo_sprite_generate` (provider `procedural` works
   offline), then real generated or drawn art (ukiyo-sprite-generation). Keep one palette. Imported atlases: source
   in `art/`, spec `"output": "../Shared/gen/<Class>.g.cs"` (docs/assets.md).
6. **Lock it in.** Write a scripted-input test that plays the first seconds and asserts state and determinism
   (ukiyo-testing).
7. **Play it.** `dotnet run --project games/SkyLantern/Hosts/Desktop` and use `ukiyo_live` to inspect while a human
   plays. Browser: `dotnet run --project games/SkyLantern/Hosts/Browser` after `(cd web/three-adapter && bun install)`.
   Ship: `dotnet publish games/SkyLantern/Hosts/Export.Desktop -c Release -o artifacts/sky-lantern`.

Host-specific needs (a window title, a browser-only API) go in `Platform/<Host>/` behind a partial method declared in
`Shared/`, never as `#if` (docs/game-layout.md). Keep them out of `Update` and `Snapshot`.

Reference implementations: `samples/RotatingCube` (the template itself, G0 parity evidence) and `samples/LanternRun`
(platformer in the older `<game>/<Host>/` layout: physics, sensors, sprite sheet, title/pause/end menus, HUD,
deterministic snapshot values).

## Desktop prerequisites

`scripts/fetch-native.sh` once (wgpu-native + SDL3, checksummed). The desktop host is macOS arm64 today.
