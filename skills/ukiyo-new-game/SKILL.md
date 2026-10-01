---
name: ukiyo-new-game
description: Use when starting a new ukiyo game or prototype from an idea — scaffolding the project, choosing modules (2D/3D, physics, UI), building the first playable loop, adding art and proving it runs on every target.
---

# Starting a new game

1. **Scaffold.** `ukiyo_new_game {"name":"SkyLantern","template":"2d"}` creates `games/SkyLantern/` with
   `Shared/Program.cs`, `Shared/SkyLanternGame.cs`, `Shared/assets/`, and Headless, Desktop (dev bridge on) and Browser
   hosts, and adds them to `Ukiyo.slnx`. The 2d template has a sprite, a physics body, arrow/WASD movement, jump and a
   HUD label; 3d has a mesh turned by the arrow keys.
2. **Prove the skeleton.** `ukiyo_run {"game":"SkyLantern"}` (it builds and ticks), then `ukiyo_capture`.
3. **Make the core loop playable before anything else.** One verb (jump, shoot, place), one goal, one failure.
   Keep everything in `Shared/` and split files by responsibility (`Player.cs`, `Level.cs`, `Hud.cs`) as it grows.
4. **States.** Title → Playing → Paused → Won/GameOver as an enum, with menus from ukiyo-ui and focus on the default
   button. Put the state and the numbers that matter in `Snapshot().Values`.
5. **Art.** Start with `PixelArt.FromRows` placeholders or `ukiyo_sprite_generate` (provider `procedural` works
   offline), then real generated or drawn art (ukiyo-sprite-generation). Keep one palette.
6. **Lock it in.** Write a scripted-input test that plays the first seconds and asserts state and determinism
   (ukiyo-testing).
7. **Play it.** `dotnet run --project games/SkyLantern/Desktop` and use `ukiyo_live` to inspect while a human plays.
   Browser: `dotnet run --project games/SkyLantern/Browser` after `(cd web/three-adapter && bun install)`.

Reference implementation: `samples/LanternRun` (platformer: physics, sensors, sprite sheet, title/pause/end menus,
HUD, deterministic snapshot values).

## Desktop prerequisites

`scripts/fetch-native.sh` once (wgpu-native + SDL3, checksummed). The desktop host is macOS arm64 today.
