---
name: ukiyo-core
description: Use for any work on a ukiyo game. The game loop (IGame, 60 Hz ticks, Extract, Snapshot), the one-Program.cs-many-hosts layout, assets, determinism rules, and how to prove a change. Load this first.
---

# ukiyo core

ukiyo is a C# (.NET 10) engine where the game is code. One `Program.cs` builds for desktop (wgpu → Metal), browser
(WebAssembly + Three.js) and headless (no window, no GPU). Agents change code, then prove the result with snapshots,
captured frames and tests.

## Layout of a game

```
games/<Name>/                  (samples/ holds the engine's own examples)
  Shared/Program.cs            the only entry point, identical for every host
  Shared/<Name>Game.cs         your IGame; any other Shared/*.cs is compiled into every host
  Shared/assets/**             embedded into every host as assets/<path>
  Headless/ Desktop/ Browser/  one csproj per host, nothing else
```

Never add `#if`, platform checks or host-specific code to `Shared/`. Create a new game with the MCP tool
`ukiyo_new_game` (or copy `samples/LanternRun`).

## The loop

```csharp
public sealed class MyGame : IGame
{
    public string Name => "MyGame";
    public void Initialize(GameContext context) { /* create meshes, materials, textures; load assets */ }
    public void Update(in TickInfo tick) { /* tick.Tick, tick.DeltaSeconds (1/60), tick.Input */ }
    public void Extract(FrameBuilder frame) { /* describe what to draw; no simulation here */ }
    public GameSnapshot Snapshot(long tick) => new(tick, poses) { Values = new Dictionary<string, double> { ["score"] = _score } };
}
```

- `Update` runs at a fixed 60 Hz (`Simulation.TickRate`, `Simulation.TickSecondsF`). After N updates the world is at tick N.
- `Extract` may run 0..n times per tick. It must not change state.
- `Snapshot` is the machine-readable truth agents and tests compare: entity poses (`EntityPose`, `EntityPose.Planar(pos2, rot)`)
  plus named numbers in `Values` (score, state, lives…). Put every value you will want to assert on in it.

## Determinism rules

- No `DateTime.Now`, `Random.Shared`, `Stopwatch` or wall-clock time in game code. Use `tick.Tick`; seed your own `Random(seed)`.
- Iterate collections in a stable order. Physics uses `Ukiyo.Physics.DetMath` for sin/cos; do the same for anything that must
  match across machines.
- Same code + same input script ⇒ same snapshot JSON. Tests assert exactly that.

## Resources

`GameContext` returns handles: `CreateMesh(MeshData)`, `CreateMaterial(MaterialData)`, `CreateTexture(TextureData)`,
`LoadTexture(pngBytes, filter)`, `Destroy(handle)`. Assets: `context.Assets.Read("sprites/hero.png")`,
`ReadText`, `Exists`, `List()` (paths are relative to `Shared/assets`; rebuild after adding files).

## Proving a change (in this order)

1. `ukiyo_run {"game":"<Name>","checkpoints":[0,60,300],"input":[…]}` → snapshots at those ticks.
2. `ukiyo_capture` → PNG frames from the CPU renderer (same geometry and colors as the GPU paths).
3. `ukiyo_test` with a filter for the area you touched; add a test for new behavior.
4. With a desktop build running, `ukiyo_live` (pause, step, capture the real GPU frame, inject input).

Report what each check proved. A green build is not a visual check; a CPU capture is not a GPU capture.

Sounds: `tick.Audio.Play(handle)` in `Update` (see ukiyo-audio); they are events, never state.

Related skills: ukiyo-rendering, ukiyo-sprites, ukiyo-physics, ukiyo-input, ukiyo-ui, ukiyo-audio, ukiyo-testing, ukiyo-live.
