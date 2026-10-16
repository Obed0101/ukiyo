---
name: ukiyo-scenes-assets
description: Use when a ukiyo game needs levels, rooms or screens built from content — prefabs and scenes in a SceneRegistry, switching scenes, patches — or imported art compiled into Shared/gen with content hashes, embedded files in Shared/assets, and how both show up in snapshots and source hashes. Says which parts are still in progress.
---

# Scenes and assets

Content in ukiyo is code-first: **prefabs are C# functions, scenes are data, imported art is generated C#.** Nothing
is loaded from disk or the network at runtime; everything is compiled or embedded into every host.

## Available now, and still in progress

| Area | State on `dev` | Where |
|---|---|---|
| `SceneRegistry<T>`, prefabs, scene JSON, `LoadScene`, patches through `runtime.SubmitPatch` | available | `src/Ukiyo.Core/Scene*.cs`, docs/scenes-patches.md, `samples/SceneHop` |
| Texture/atlas importer `tools/Ukiyo.Import` → `Shared/gen/*.g.cs`, `--check` | available | docs/assets.md, `samples/TexturedQuad` |
| Mesh importer | **in progress** (branch `feat/g2-mesh-importer`) | not on `dev`: do not use |
| New template layout (`ukiyo_new_game`) | **in progress** (`feat/g05-game-template`) | today's 2d template has one prefab `crate`, one scene `start` built in code |
| Submitting patches from outside the game: headless `--patches FILE`, `ukiyo_run`/`ukiyo_capture` `patches`, `ukiyo_live` `patch`, dev bridge `patch` command, CLI `run --patches` | available | docs/scenes-patches.md, ukiyo-live |

Branch states are from 2026-10-16. Before relying on an "in progress" row, check whether it merged:
`git log dev --oneline | head`, `git grep -il mesh tools/Ukiyo.Import` (a mesh importer).

## Two scenes, the smallest working shape

Copy `samples/SceneHop/Shared/SceneHopGame.cs` (two JSON scenes, 60 ticks each) or write:

```csharp
private SceneRegistry<Mote> _scenes = null!;              // Mote: your own class (position, size…)

public void Initialize(GameContext context)
{
    _scenes = new SceneRegistry<Mote>(context);            // one per game; attaches to snapshots
    _scenes.Register(new Prefab<Mote>("lamp", args => new Mote(args.Position, args.Float("size")))
        { Parameters = [new PrefabParameter("size", 0.25, 3, 1)] });   // (Name, Min, Max, Default)
    _scenes.RegisterJson(context.Assets.ReadText("scenes/day.json"));   // Shared/assets/scenes/day.json
    _scenes.Register(new SceneData("night", [new EntityData("moon", "lamp", new Vector3(0, 2, 0))]));
    _scenes.LoadScene("day");                              // live at tick 0
}

public void Update(in TickInfo tick)
{
    if (tick.Tick + 1 - _scenes.LoadedAt >= 60)            // asked during tick N → live from tick N+1
        _scenes.LoadScene(_scenes.ActiveScene == "day" ? "night" : "day");
}
```

- Register every prefab before any scene: a scene is validated against the prefabs when registered, so a bad scene
  fails in `Initialize` with `SceneException` (`[SCENE:Code]:`), never mid-game.
- Scene JSON: `{"name","bounds"?,"entities":[{"id","prefab","position":[x,y(,z)],"values"?}]}`; unknown fields are
  errors. Rules: docs/scenes-patches.md § Scene JSON.
- Iterate `_scenes.Entities` (spawn order) in `Update`, `Extract` and `Snapshot`; `_scenes[id]` finds one. A load
  disposes the old entities newest first (`Prefab.Dispose` runs: remove physics bodies there).
- Files under `Shared/assets` are embedded at build time: rebuild after adding one. `context.Assets.Exists(path)`,
  `List()` show what got in.
- The `ukiyo_new_game` 2d template's `crate` prefab names its physics body after the entity id, and
  `PhysicsWorld2D.Poses()` keys snapshot poses by body name. An entity id equal to another body's name (`player`,
  `ground`) silently replaces that pose in the snapshot: keep ids distinct.

## Patches

Typed diffs (`spawn`, `dispose`, `retarget`) applied atomically at a tick boundary: `runtime.SubmitPatch(patch)`, or
`_scenes.Submit(patch)` from game code for `tick.Tick + 1` (the running tick is `PastTick`). Build one with
`ScenePatch.Parse(json)` or `new ScenePatch(id, tick, [new RetargetOp("crate1") { Values = … }])`. Format, timing, which
checks run at submit vs. at the tick, and error codes: **docs/scenes-patches.md** (do not guess them). Tests show
every case: `tests/Ukiyo.Tests/ScenePatchTests.cs`.

## How scenes show in snapshots

A game with a registry gets a `"scene"` key in every snapshot; its own poses and values are unchanged. Real line,
`dotnet run --project samples/SceneHop/Headless -- --checkpoints 0,59,60,120`, tick 60 (trimmed):

```
snapshot {"tick":60,…,"entities":{"bat-1":…,"torch":…},"values":{"entities":4,…},"scene":{"active":"cavern","loadedAt":60,"patches":[],"rejected":[]}}
```

`patches` lists applied patches `{id,tick}` in order; `rejected` holds `{id,tick,code,message}` for patches refused at
their tick; pending patches are not listed. Put scene facts you will assert on (entity count, ticks in scene) in
`Snapshot().Values` too.

## Imported art: `Shared/gen/`, content-hashed

```
games/<Name>/art/hero.png, hero.json        source art + optional atlas (ukiyo_sprite_* format); never shipped
games/<Name>/art/hero.import.json           {"class","namespace","output":"../Shared/gen/HeroArt.g.cs","inputs":[…]}
games/<Name>/Shared/gen/HeroArt.g.cs        generated, committed, compiled into every host
```

```sh
dotnet run --project tools/Ukiyo.Import -- games/<Name>/art            # every *.import.json under the folder
dotnet run --project tools/Ukiyo.Import -- --check games/<Name>/art    # exit 1 + [IMPORT:Stale] if out of date; 2 = usage
```

`--check` prints `[import] up to date samples/TexturedQuad/Shared/gen/QuadArt.g.cs (60x40, 5 frames, 1 animations,
content b2320091ad58)` when nothing changed. Use it before committing art and in CI. In the game:
`_sheet = HeroArt.Load(context);` in `Initialize`, then `_sheet.Animation("hero_run")` for an atlas animation or
`_sheet.Frame("stone")` for a PNG imported without JSON (one frame named after the file); draw with
`frame.DrawSprite(...)` (ukiyo-sprites). Full API and atlas rules: docs/assets.md.

- **Two hashes, two meanings.** `HeroArt.ContentHash` is the SHA-256 of what the game receives (size, frames,
  animations, pixels); re-exporting identical art keeps it, and the importer leaves the file untouched. The file's own
  SHA-256 appears in the host's source identity: `[ukiyo] target=headless game=TexturedQuad … QuadArt.g.cs=348884E7F07A`
  and in each snapshot's `"source"` object. The `.g.cs` header lists the SHA-256 of every source file.
- **`Shared/gen`, not `Shared/Assets/gen`.** macOS file systems are case-insensitive by default, so `Shared/Assets` is
  the same folder as `Shared/assets`, which `Directory.Build.targets` embeds byte for byte into every host. Generated
  code goes in `Shared/gen/` and art sources in `art/`, outside `Shared/`.
- The host csproj must glob `../Shared/**/*.cs` (ukiyo-hosts) or the generated class is not compiled.
- Never edit a `.g.cs` by hand; change the art or the spec and regenerate.

## Proving it

1. `ukiyo_run {"game":"<Name>","checkpoints":[0,59,60,120]}`: `scene.active` flips at 60, `loadedAt` matches,
   entity ids match the scene file.
2. A test: run N ticks twice with the same input (and the same patch log) and compare snapshot JSON (ukiyo-testing).
3. `dotnet run --project tools/Ukiyo.Import -- --check games/<Name>/art` exits 0.
4. `ukiyo_capture` to see the scene (CPU renderer).

Related: ukiyo-core, ukiyo-hosts, ukiyo-sprites (drawing the imported frames), ukiyo-sprite-generation (making art),
ukiyo-testing.
