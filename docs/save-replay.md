# Save, replay and time travel

A game that implements `ISaveable` can be saved at any tick boundary, restored later (even in a new process), replayed
forward from a checkpoint, and, on a running desktop build, rewound. It is opt-in and costs nothing for games that do
not implement it. Tick boundary N means: after N updates, before `Update(N)`.

## The contract

```csharp
public sealed class MyGame : IGame, ISaveable
{
    public int SaveVersion => 1;                 // bump when Save's layout changes
    public void Save(SaveWriter writer) { … }    // everything the game keeps between ticks
    public void Load(SaveReader reader) { … }   // read exactly what Save wrote, in the same order
}
```

The engine saves the tick, the input state (held keys, buttons, pointer, queued events), the scene registry (active
scene, entities, applied/rejected/pending patches and every patch id) and then calls your `Save`. Your block holds only
your own state: counters, state machines, physics (`PhysicsWorld2D.Save/Load`), UI (`GameUi.Save/Load`), moved
entities. Do not save anything derived from the tick or from resources (handles, textures).

Rules for `Save`/`Load`:

- Same state, same bytes: write in a fixed order, sort anything unordered, never write hash codes or pointers.
- `Load` must read all of what `Save` wrote; the engine checks that nothing is left over or missing (`Truncated`/`Invalid`).
- Restoring happens on a started game (resources and registry exist). Scene entities are rebuilt as **new instances**
  through their prefabs (old ones go through the prefab `Dispose` hook), so re-look-up any reference you cache
  from `Scenes[...]` in `Load`. Physics is restored in place: the world must already have the same bodies in the same
  order (`Layout` error otherwise).
- Writers: `Bool Byte Int32 UInt32 Int64 UInt64 Single Double String Vector2/3/4 Quaternion Bytes Tag`; readers mirror
  them (`NullableString`, `Count()` for lengths). No reflection or JSON serializers (NativeAOT).

## Format

`"UKSV"`, u16 format version (1), game name, the game's `SaveVersion`, tick, whether a scene registry exists, an
FNV-1a 64 checksum, then the payload (little-endian). `GameRuntime.Inspect(bytes)` reads the header without restoring.
Errors are `SaveException` with `Code` and a `[SAVE:Code]:` message: `NotSaveable`, `BadMagic`, `FormatVersion`,
`Mismatch` (other game, or scene registry mismatch), `GameVersion`, `Corrupt` (checksum), `Truncated`, `Layout`,
`Invalid`, and for rewind `NoTimeline`, `FutureTick`, `OutOfWindow`. The header, checksum and engine sections are
validated before anything changes. If your own `Load` throws midway the game is half-loaded: restore again.

## Replay from a checkpoint

Input script + patch log + start state make a restorable replay. The snapshot after restore + replay equals the
snapshot of an uninterrupted run (`SaveReplayTests` proves it for LanternRun, SceneHop and the template cube).

```bash
bun tools/mcp/src/cli.ts run LanternRun --input in.json --checkpoints 100 --save /tmp/run.uksv
bun tools/mcp/src/cli.ts run LanternRun --input in.json --checkpoints 100,200 --load /tmp/run.uksv
```

`ukiyo_run` takes `saveTo` (writes after the last checkpoint) and `loadFrom` (restores before the first tick). With a
load, checkpoints before the saved tick are skipped, the input script continues from the saved tick, and patch-log
entries whose ids the save already holds are skipped (`patches … submitted=N skipped=M`). Use the same script and log
as the saved run. In C#: `runtime.Save()`, then on a started runtime `runtime.Restore(bytes)`. A bad file exits 3
(`HeadlessHost.SaveRefused`).

## Live time travel

Under the dev bridge a saveable game keeps a ring of checkpoints and a journal of what came from outside:
`ukiyo_live {"action":"rewind","tick":N}` (bridge `rewind`, CLI `live rewind --tick N`) pauses, restores the newest
checkpoint at or before N and steps forward deterministically to N. The answer has `tick`, `checkpoint`, `replayed`
and `window` (`oldest`, `newest`, `checkpoints`, `bytes`). Sounds do not replay.

- **Caps** (`TimeTravelOptions` defaults): a checkpoint every 60 ticks, at most 600 checkpoints and 32 MiB of
  checkpoints; the oldest goes first, the newest is always kept. The journal (input events, patches) only spans the
  window. A rewind to a tick older than the window is `[SAVE:OutOfWindow]`; a tick after the current one is
  `FutureTick`. Rewind never moves forward; use `step`.
- **The future is abandoned.** Checkpoints, journal entries and recorded input from N on are dropped; a script
  attached to the runtime continues from N, live input starts again from what arrives next. Patches submitted
  through the bridge after N must be sent again.
- If the replay fails the previous state is restored and the error returned.
- `UKIYO_TIME_TRAVEL=off` disables the ring. Games without `ISaveable` answer `[SAVE:NoTimeline]`.
- C#: `runtime.EnableTimeTravel(options)`, `runtime.Rewind(tick)`, `runtime.RewindWindow`. Input and patches must enter
  through `runtime.Input.Push` / `runtime.SubmitPatch` to be journaled.

## Proving a game opts in correctly

Run to K, `Save`, run on to N; build a second runtime, `Restore` the save, run to N; assert equal `SnapshotJson()` and
equal `Save()` bytes (copy `SaveReplayTests.Restoring_a_checkpoint_and_replaying_forward…`). Equal bytes catch state
you forgot to save even when the snapshot does not show it yet.
