---
name: ukiyo-testing
description: Use when proving that a ukiyo change works — writing xunit tests for game logic, physics, UI and rendering, deterministic snapshot tests with input scripts, CPU-rendered frame captures, JS adapter tests, and the cross-renderer parity check.
---

# Testing a ukiyo game

Evidence, from cheapest to strongest. Say which ones you ran; do not upgrade one into another.

| Evidence | Proves | How |
|---|---|---|
| Unit/integration tests | logic, physics, UI, protocol | `ukiyo_test` / `dotnet test tests/Ukiyo.Tests` |
| Headless snapshots | behavior over time with scripted input | `ukiyo_run` / `--checkpoints … --input …` |
| CPU frame capture | what is drawn (geometry, colors, sprites, UI) | `ukiyo_capture` / `--capture <dir>` |
| Live GPU capture | the real Metal frame of a running build | `ukiyo_live {"action":"capture"}` |
| Parity | wgpu, Three.js and headless agree | `scripts/g0.sh` (needs a Mac with a GPU and a browser) |

## A game test

```csharp
[Fact]
public async Task Holding_right_moves_the_player_and_is_deterministic()
{
    const string script = """[{"tick":1,"key":"Enter","down":true},{"tick":30,"key":"Right","down":true},{"tick":120,"key":"Right","down":false}]""";
    async Task<(GameRuntime, MyGame)> Run()
    {
        var game = new MyGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>())) { Script = InputScript.Parse(script) };
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", new RenderExtent(640, 360, 1f)));
        runtime.StepTo(180);
        runtime.RenderFrame(new RenderExtent(640, 360, 1f));   // also validates the packet
        return (runtime, game);
    }

    var (a, game) = await Run();
    var (b, _) = await Run();
    Assert.True(game.Player.Position.X > 5);
    Assert.Equal(a.SnapshotJson(), b.SnapshotJson());
}
```

Link the game's Shared files (not `Program.cs`) into `tests/Ukiyo.Tests/Ukiyo.Tests.csproj`:
`<Compile Include="../../games/MyGame/Shared/MyGame.cs" Link="Shared/MyGame.cs" />` and reference any engine module
it uses (Physics, UI).

## Frames in tests

`SoftwareRenderer` (namespace `Ukiyo.Rendering.Software`) renders packets on the CPU and captures PNG:
`await runtime.StartAsync(renderer, …); runtime.RenderFrame(extent); var png = (await runtime.CaptureAsync())!.Png;`
Decode with `PngDecoder.Decode(png)` and assert pixels at known positions (a sprite's center, the clear color in a
corner). Prefer a few well-chosen pixels over whole-image hashes, which break on harmless changes.

## Rules

- Descriptive names that state the scenario. Test edges and errors: invalid input throws typed errors
  (`RenderException` with a `RenderErrorCode`, `ArgumentOutOfRangeException`, `FormatException`).
- No mock data presented as real. Procedurally generated sprites are fine in tests; say so.
- Tests are independent: build a new runtime/world per test.
- JS adapter: `cd web/three-adapter && bun test` (protocol, sprites). Protocol changes need tests on both sides.
- A failing test is reported as failing, with its output. Never weaken an assertion to make it pass without saying why.
