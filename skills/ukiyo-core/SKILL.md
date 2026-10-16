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
  Shared/<Name>Game.cs         your IGame; other .cs under Shared/ compile into every host (host csproj glob ../Shared/**/*.cs)
  Shared/gen/*.g.cs            imported art (tools/Ukiyo.Import): pixels and meshes compiled in, nothing decoded at runtime
  Shared/assets/**             embedded into every host as assets/<path>
  art/                         source PNGs, glTF models + *.import.json; never shipped (see docs/assets.md)
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

## Scenes, prefabs and patches (opt-in)

For worlds built from content: prefabs are functions, scenes are data, and changes arrive as patches applied between
ticks. Full format and rules: `docs/scenes-patches.md`; example: `samples/SceneHop`; the 2d `ukiyo_new_game` template uses it.

```csharp
_scenes = new SceneRegistry<Mote>(context);                       // in Initialize; one per game
_scenes.Register(new Prefab<Mote>("lantern", args => new Mote(args.Position, args.Float("glow")))
    { Parameters = [new PrefabParameter("glow", 0, 1, 1)], Dispose = m => …, Retarget = (m, args) => … });
_scenes.RegisterJson(context.Assets.ReadText("scenes/meadow.json")); // validated now: bad content fails at start
_scenes.LoadScene("meadow");                                       // live at tick 0
// Update: foreach (var e in _scenes.Entities) … e.Id, e.Args, e.Instance; LoadScene("cavern") → live next tick
```

- Register prefabs before scenes. Scene JSON: `{"name","bounds"?,"entities":[{"id","prefab","position":[x,y(,z)],"values"?}]}`;
  unknown fields, unknown prefabs/parameters, out-of-range values or positions outside `bounds` throw
  `SceneException` (`[SCENE:Code]`, `SceneErrorCode`).
- Patches: `runtime.SubmitPatch(ScenePatch.Parse(json))` with
  `{"id","tick","scene"?,"ops":[{"op":"spawn","id","prefab","position","values"?},{"op":"dispose","id"},{"op":"retarget","id","position"?,"values"?}]}`.
  Applied at the boundary before `tick` (now, if paused exactly there), whole or not at all. Past ticks, unknown
  prefabs/parameters and out-of-range values throw at submit; missing entities, bounds and the `scene` guard are
  checked at the tick and land in `RejectedPatches`. Game code may submit for `tick.Tick + 1`, never the running tick.
  At a boundary a pending `LoadScene` happens first, then that tick's patches. A patch changes the live entities only:
  reloading the scene rebuilds them from the scene file, so the patch's effect is gone while it stays counted in
  `scene.appliedCount`/`appliedHash`.
- Snapshots of games with a registry carry
  `"scene":{"active","loadedAt","appliedCount","appliedHash","patches":[{"id","tick"}],"rejectedCount","rejectedHash","rejected":[…]}`:
  `patches`/`rejected` are the last 16 only, counts and FNV-1a 64 hashes cover the whole session.
  Same input script + same patch log ⇒ same snapshots.
- Submitting without code: headless `--patches log.json` (a JSON array, submitted before tick 0; a bad log exits 2
  with the `[SCENE:Code]` line on stderr), `ukiyo_run {"patches":[…]}` or `{"patchLog":"path"}` (reports `appliedCount`,
  `appliedHash`, `applied`, `rejectedCount`, `rejectedHash`, `rejected`, `notReached`), and on a running desktop build `ukiyo_live {"action":"patch","patch":{…}}` /
  `bun tools/mcp/src/cli.ts live patch --pid N --patch p.json` (`queued` or `applied`). Details per entry point:
  docs/scenes-patches.md.

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

Without a configured MCP client: `UKIYO_ROOT=<repo> bun tools/mcp/src/server.ts` speaks MCP over stdio (always set
`UKIYO_ROOT` to the checkout you mean to change; a real path, not `/tmp/…`, which is a symlink on macOS). The CLI
`bun tools/mcp/src/cli.ts` covers `run <Game>` (`--checkpoints`, `--patches`, `--input`, `--size`) and `live
list|step|capture|patch`; there is no CLI for new-game or capture. Determinism without a test: run the same headless
command twice and `diff` stdout. MCP results contain per-run temp paths (patch logs, frame dirs under the OS temp
dir), so compare only their `snapshots`.

Sounds: `tick.Audio.Play(handle)` in `Update` (see ukiyo-audio); they are events, never state.

Related skills: ukiyo-new-game, ukiyo-scenes-assets (scenes, patches, imported art), ukiyo-hosts (hosts, flags,
exports), ukiyo-rendering, ukiyo-sprites, ukiyo-physics, ukiyo-input, ukiyo-ui, ukiyo-audio, ukiyo-testing, ukiyo-live.
