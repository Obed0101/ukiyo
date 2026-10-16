# Scenes, prefabs and patches

Opt-in Core types (`src/Ukiyo.Core/Scenes.cs`, `SceneRegistry.cs`, `ScenePatch.cs`) that let a game describe its
world as **prefabs (code) plus scenes (data)**, switch scenes, and take **patches**: typed diffs applied between ticks.
`IGame` is unchanged; a game opts in by creating a `SceneRegistry<TEntity>` in `Initialize`. Example:
`samples/SceneHop` (two scenes, 60 ticks each). Tests: `tests/Ukiyo.Tests/SceneTests.cs`, `ScenePatchTests.cs`.

## Using it in a game

```csharp
private SceneRegistry<Mote> _scenes = null!;

public void Initialize(GameContext context)
{
    _scenes = new SceneRegistry<Mote>(context);            // attaches to the runtime and to snapshots
    _scenes.Register(new Prefab<Mote>("lantern", args => new Mote(args.Position, args.Float("glow")))
    {
        Parameters = [new PrefabParameter("glow", Min: 0, Max: 1, Default: 1)],
        Dispose = mote => { /* undo side effects: remove a physics body */ },
        Retarget = (mote, args) => { /* optional: update in place; otherwise retarget = dispose + create */ },
    });
    _scenes.RegisterJson(context.Assets.ReadText("scenes/meadow.json"));   // or Register(new SceneData(...))
    _scenes.LoadScene("meadow");                           // live at tick 0
}

public void Update(in TickInfo tick)
{
    foreach (var entity in _scenes.Entities) { /* entity.Id, entity.Args, entity.Instance */ }
    if (done) _scenes.LoadScene("cavern");                 // live from the next tick
}
```

- **Prefabs are functions** `PrefabArgs → TEntity`. `TEntity` is whatever the game uses (a class, a `Body2D`…).
  `PrefabArgs` holds `Id`, `Prefab`, `Position` (Vector3) and every declared parameter with defaults filled in;
  `args["name"]` / `args.Float("name")` throw `UnknownParameter` for a name the prefab did not declare.
- **Register prefabs before scenes.** `Register(SceneData)` validates every entity against them; one registry per game.
- `Entities` is in spawn order (scene order, then patch order); retarget keeps an entity's place. `this[id]` /
  `TryGet` find one. `ActiveScene`, `LoadedAt`, `RequestedScene`, `Tick`, `AppliedPatches`, `PendingPatches` and
  `RejectedPatches` are readable at any time.
- The game still writes its own `Snapshot` (poses, values); the runtime adds the `scene` section.

## Timing: the tick boundary

The world is at tick N after N updates. The **boundary N** is the moment between `Update(N-1)` and `Update(N)`.
`GameRuntime` processes it right after the tick counter reaches N (and once after `Initialize`, for boundary 0):

1. the requested scene load, if any: current entities are disposed newest first, then the scene's entities spawn in order;
2. queued patches whose tick is N, in submission order.

So a load requested during `Update(N)` is live from tick N+1, a snapshot at tick N already shows what boundary N
applied, and `Update(N)` runs on it. Every host advances through `GameRuntime.Step`, so this is identical on headless,
desktop, browser and in replays. Nothing here reads a clock or a random source.

## Scene JSON

```json
{
  "name": "meadow",
  "bounds": { "min": [-8, -4.5, 0], "max": [8, 4.5, 0] },
  "entities": [
    { "id": "lamp-west", "prefab": "lantern", "position": [-4, 0], "values": { "glow": 0.9 } },
    { "id": "moth", "prefab": "drifter", "position": [-6, 2, 0] }
  ]
}
```

| Field | Rule |
|---|---|
| `name`, `id` | 1–64 chars of `A-Z a-z 0-9 _ . -`; entity ids unique within the scene |
| `bounds` | optional; inclusive box, finite, `min <= max` per axis; every spawned or retargeted position must be inside |
| `prefab` | a registered prefab |
| `position` | `[x, y]` or `[x, y, z]` (z defaults to 0), finite |
| `values` | optional; only parameters the prefab declares, each finite and within `[Min, Max]`; the rest take `Default` |

Unknown fields are errors, not ignored. The same structure can be built in code:
`new SceneData("meadow", [new EntityData("lamp-west", "lantern", new Vector3(-4, 0, 0)) { Values = … }]) { Bounds = … }`.

## Patch format

```json
{
  "id": "dim-west",
  "tick": 90,
  "scene": "meadow",
  "ops": [
    { "op": "spawn",    "id": "bat-3", "prefab": "drifter", "position": [-5, -1], "values": { "speed": 3 } },
    { "op": "retarget", "id": "lamp-west", "position": [2, 1], "values": { "glow": 0.25 } },
    { "op": "dispose",  "id": "moth" }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | unique for the session (same id rules as entities); recorded in snapshots |
| `tick` | the boundary it applies at (integer ≥ current tick) |
| `scene` | optional guard: refused unless this scene is active at `tick` |
| `ops` | at least one; applied in order, so one patch can dispose `x`, spawn a new `x` and retarget it |
| `spawn` | `id`, `prefab`, `position` required; `values` optional |
| `dispose` | `id` of a live entity; its prefab's `Dispose` hook runs |
| `retarget` | `id` of a live entity plus `position` and/or `values` (at least one). Unset values keep their current value. Uses the prefab's `Retarget` hook, or disposes and re-creates the entity from the merged arguments when there is none |

A patch log is a JSON array of patches (`ScenePatch.ParseLog`, `ScenePatch.WriteLog`). `ToJson` is stable (values
sorted by name). In C#: `new ScenePatch("p1", 90, [new SpawnOp("bat-3", "drifter", pos) { Values = … }, new DisposeOp("moth"), new RetargetOp("lamp-west") { Position = … }]) { Scene = "meadow" }`.

## Submitting and what happens to a patch

One entry point: `runtime.SubmitPatch(patch)` (the registry's `Submit` is the same call, for game code). It is what
the dev bridge, the agent CLI and hot reload are meant to reuse.

| When | Check | Outcome when it fails |
|---|---|---|
| at submit | JSON shape, ids, `ops` non-empty, retarget changes something, prefab registered, parameter names and ranges, finite positions, `scene` registered, id not used before, `tick` not in the past | `SceneException` thrown; nothing recorded; the id stays free |
| at the patch's tick | `scene` guard, entity exists (dispose/retarget) or not (spawn), retarget parameter names and ranges, scene bounds | whole patch refused, world unchanged, entry added to `rejected` |

- A patch for a **future** tick is queued (`PendingPatches`) and checked against the world when its tick arrives.
- A patch for the **current** tick while the runtime is paused between steps is checked and applied immediately; a
  world-dependent refusal is thrown *and* recorded, exactly as if it had been queued, so a live session and a replay of
  the same submissions give the same snapshots.
- During `Update(N)` (game code), tick N is already running: a patch for N is `PastTick`; target N+1.
- A patch is atomic: every op is validated on a scratch view first; nothing applies unless all pass.

Error codes (`SceneErrorCode`, message prefix `[SCENE:Code]:`): `Malformed`, `InvalidId`, `DuplicateScene`,
`UnknownScene`, `DuplicatePrefab`, `UnknownPrefab`, `DuplicateEntity`, `UnknownEntity`, `UnknownParameter`,
`OutOfRange`, `PastTick`, `DuplicatePatch`, `WrongScene`.

## In snapshots

Games with a registry get one extra key; every other game's snapshot JSON is byte-identical to before.

```json
"scene": {
  "active": "meadow", "loadedAt": 0,
  "patches":  [ { "id": "p1", "tick": 12 }, { "id": "p2", "tick": 40 } ],
  "rejected": [ { "id": "p4", "tick": 80, "code": "UnknownEntity", "message": "[SCENE:UnknownEntity]: patch p4 op #0 (moth): no live entity to dispose" } ]
}
```

`patches` lists applied patches in application order. Pending patches are not in snapshots (they are not world state);
read `PendingPatches` instead. Same code + same input script + same patch log ⇒ the same snapshot sequence (tested by
`The_same_patch_log_gives_the_same_snapshot_sequence_whether_queued_up_front_or_submitted_live`).

## Not in this slice

- No host flag or MCP tool submits patches yet; `GameRuntime.SubmitPatch` is the hook for the dev bridge, a headless
  `--patches <log.json>` option and G3 reload.
- No patch op loads a scene; scene changes come from game code (`LoadScene`).
- Game code cannot spawn or dispose registry entities mid-tick; runtime-only objects (bullets, particles) stay in the
  game's own collections.
