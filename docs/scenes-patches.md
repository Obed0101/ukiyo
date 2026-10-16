# Scenes, prefabs and patches

Opt-in Core types (`src/Ukiyo.Core/Scenes.cs`, `SceneRegistry.cs`, `ScenePatch.cs`) that let a game describe its
world as **prefabs (code) plus scenes (data)**, switch scenes, and take **patches**: typed diffs applied between ticks.
`IGame` is unchanged; a game opts in by creating a `SceneRegistry<TEntity>` in `Initialize`. Example:
`samples/SceneHop` (two scenes, 60 ticks each; Headless and Desktop hosts). Tests: `tests/Ukiyo.Tests/SceneTests.cs`,
`ScenePatchTests.cs`, `HeadlessPatchTests.cs`, `DevBridgeTests.cs`.

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
  `TryGet` find one. `ActiveScene`, `LoadedAt`, `RequestedScene`, `Tick`, `AppliedPatches`, `PendingPatches`,
  `RejectedPatches`, `AppliedCount`, `RejectedCount`, `AppliedHash` and `RejectedHash` are readable at any time.
  `AppliedPatches` and `RejectedPatches` hold only the last `PatchHistory.Window` (16) entries; see
  [In snapshots](#in-snapshots).
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

One entry point: `runtime.SubmitPatch(patch)` (the registry's `Submit` is the same call, for game code). The headless
`--patches` flag, the dev bridge `patch` command and the tools below all go through it, so they apply exactly the rules
in the table; hot reload is meant to reuse it too.

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

Real output, scene section only, of `dotnet run --project samples/SceneHop/Headless -- --checkpoints 0,12,60,80,120
--patches log.json` with the four-patch log from `HeadlessPatchTests`, at tick 120:

```json
"scene": {
  "active": "meadow", "loadedAt": 120,
  "appliedCount": 3, "appliedHash": "0b380a05d110bf94",
  "patches":  [ { "id": "p1", "tick": 12 }, { "id": "p2", "tick": 40 }, { "id": "p3", "tick": 75 } ],
  "rejectedCount": 1, "rejectedHash": "021904ec402bed74",
  "rejected": [ { "id": "p4", "tick": 80, "code": "UnknownEntity", "message": "[SCENE:UnknownEntity]: patch p4 op #0 (moth): no live entity to dispose" } ]
}
```

The history is **bounded**, so a session with thousands of patches has snapshots the same size as one with twenty:

| Key | Meaning |
|---|---|
| `patches` | the last `PatchHistory.Window` (16) applied patches `{id, tick}`, oldest first; older ones are evicted first |
| `rejected` | the last 16 refusals at a patch's tick `{id, tick, code, message}`, oldest first, evicted the same way |
| `appliedCount`, `rejectedCount` | every applied patch / every refusal since the game started, including evicted ones |
| `appliedHash`, `rejectedHash` | hash chains over every entry since the game started, 16 lowercase hex digits |

Each chain is **FNV-1a 64** (offset basis `cbf29ce484222325`, prime `100000001b3`) continued entry by entry, so it
equals FNV-1a 64 of the concatenated entry encodings. An applied entry is the UTF-8 patch id, a `0` byte, then the
tick as 8 bytes little-endian; a refusal is the same followed by the UTF-8 code name (`UnknownEntity`) and a `0` byte
(the message is not hashed; it is shown in the window). Before any entry the chain is `cbf29ce484222325`.
`PatchHistory.ChainApplied`, `ChainRejected`, `AppliedHash(records)`, `RejectedHash(rejections)` and `Format`
compute it in C#; `tools/mcp/test/patches.test.ts` recomputes it in JavaScript.

Why this format: a window keeps what an agent reads most (what just happened) at a fixed size; counts say how much was
evicted; the chain makes two runs comparable exactly without the full list (equal count and hash ⇒ the same patches,
in the same order, at the same ticks, barring a 64-bit collision). FNV-1a is plain 64-bit integer arithmetic over a
fixed byte encoding, so CoreCLR, NativeAOT and WebAssembly give the same value; `string.GetHashCode` is randomized
per process and never used. It is not a cryptographic hash: it detects divergence, it does not authenticate a log.
The hash is a string because JSON numbers cannot carry 64 bits exactly into JavaScript. Ids stay reserved after
eviction (a reused id is still `DuplicatePatch`): the registry keeps every id it has seen, which is the only part of
the history that grows with the session, in memory and never in snapshots.

To check a full history, keep the patch log: replaying it gives the same counts and hashes, and the outcome of every
entry can be recomputed from it (`The_same_long_patch_log_gives_identical_snapshots_twice_and_live_and_the_hashes_replay_from_the_log`).
Pending patches are not in snapshots (they are not world state); read `PendingPatches` instead. Same code + same input
script + same patch log ⇒ the same snapshot sequence (tested by
`The_same_patch_log_gives_the_same_snapshot_sequence_whether_queued_up_front_or_submitted_live`; size bound by
`A_ten_thousand_patch_session_keeps_snapshots_under_a_fixed_byte_ceiling`, about 3 KB where the unbounded lists were
about 560 KB).

## Submitting from each entry point

| From | How | Result |
|---|---|---|
| C# (tests, game code, tools) | `runtime.SubmitPatch(ScenePatch.Parse(json))`; a log: `ScenePatch.ParseLog` then submit in order | throws `SceneException` on refusal |
| Headless host | `dotnet run --project samples/SceneHop/Headless -- --checkpoints 0,60,120 --patches log.json` (with `--input`, `--capture`, `--audio` as usual) | snapshot lines as always |
| MCP, headless | `ukiyo_run` (or `ukiyo_capture`) with `"patches": [ … ]` inline or `"patchLog": "path/in/repo.json"` | `patches: {submitted, appliedCount, appliedHash, applied, rejectedCount, rejectedHash, rejected, notReached}` |
| CLI, headless | `bun tools/mcp/src/cli.ts run SceneHop --checkpoints 0,60,120 --patches log.json` | the `ukiyo_run` result as JSON on stdout |
| Running desktop build | dev bridge `{"cmd":"patch","patch":{…}}`; `ukiyo_live {"action":"patch","patch":{…}}`; `bun tools/mcp/src/cli.ts live patch --pid N --patch patch.json` | `{"id","tick","status":"queued"\|"applied","currentTick","paused"}` |

**Headless `--patches <log.json>`.** The log (a JSON array, `ScenePatch.ParseLog`) is read before the game starts and
submitted in file order right after `Initialize`, at boundary 0, before any tick runs: the same as calling
`SubmitPatch` for each entry up front, so the same code + input script + log give the same snapshots as the C# API
(`HeadlessPatchTests`). The host prints `patches <absolute path> submitted=N` once the whole log is accepted. A log
that cannot be read or parsed, any submit-time refusal, or a game without a `SceneRegistry` stops the run before tick
0 with no snapshot lines, exit code 2 and one line on stderr:
`[HEADLESS]: --patches <path>: [SCENE:UnknownPrefab]: patch oops op #0: prefab "dragon" is not registered …`.
A tick-0 patch the world refuses is not a bad log: it is recorded in `rejected` and the run goes on, like any patch
refused at its tick.

**MCP `ukiyo_run`.** `patches` (inline array, validated against the advertised patch schema) or `patchLog` (a file
inside the repository), not both. The result adds, from the last snapshot's `scene` section, `patches.appliedCount`,
`appliedHash`, `rejectedCount`, `rejectedHash` and the windows `patches.applied` / `patches.rejected` (the last 16
each, refusals with the typed messages), and `patches.notReached` (submitted patches whose tick is after the last
checkpoint; every patch at or before it was applied or refused by then). Compare two runs by their counts and hashes,
not by the windows. A refused log makes the run an error result with `patches.refused` holding the host's stderr line.

**Dev bridge `patch`.** One patch per request, run on the host thread between frames, where the runtime is exactly at
a boundary. A patch for a later tick is queued (`"status":"queued"`); one for the current tick applies now
(`"applied"`). A running game moves 60 ticks a second, so read the tick first (`status`) and add a margin, or `pause`,
patch at the current tick and `step`. Refusals are `ok:false` with the typed message as `error`
(`[SCENE:PastTick]: patch p targets tick 1790, but tick 1791 is the current boundary; target 1791 or later`); a
world-dependent refusal at the current tick is also recorded in `rejected`. A game without scenes answers
`[RUNTIME]: <Game> has no SceneRegistry, so it cannot take patches`. `"applied"` is decided by `AppliedCount`
growing, not by looking the id up in the window. The bridge only exists in development builds
(`UkiyoDevBridge=true`), never in exports. Patches submitted live are not recorded into input recordings: keep the
patch files and replay them headless with `--patches`.

## Not in this slice

- Hot reload does not submit patches yet; `GameRuntime.SubmitPatch` is its hook.
- The Studio does not submit patches.
- No patch op loads a scene; scene changes come from game code (`LoadScene`).
- Game code cannot spawn or dispose registry entities mid-tick; runtime-only objects (bullets, particles) stay in the
  game's own collections.
