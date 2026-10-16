using System.Diagnostics.CodeAnalysis;

namespace Ukiyo;

/// <summary>
/// Opt-in scenes for a game: register prefabs (functions that build your entity type) and scenes (data), then
/// <see cref="LoadScene"/>. Loads and patches never happen in the middle of a tick: the runtime applies them at the
/// boundary before the next tick, the same way on every host and in replays. Create it in
/// <see cref="IGame.Initialize"/> with the game's context; that is what attaches it to the runtime and to snapshots.
/// </summary>
public sealed class SceneRegistry<TEntity> : ISceneDirector
    where TEntity : notnull
{
    private readonly Dictionary<string, Prefab<TEntity>> _prefabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneData> _scenes = new(StringComparer.Ordinal);
    private readonly List<SceneEntity<TEntity>> _entities = [];
    private readonly Dictionary<string, SceneEntity<TEntity>> _byId = new(StringComparer.Ordinal);
    private readonly List<ScenePatch> _pending = [];
    private readonly List<ScenePatch> _applied = [];
    private readonly List<PatchRejection> _rejected = [];
    private readonly HashSet<string> _patchIds = new(StringComparer.Ordinal);
    private string? _requested;
    private bool _simulating;

    public SceneRegistry(GameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.UseScenes(this);
    }

    /// <summary>The scene whose entities are live, or null before the first load.</summary>
    public string? ActiveScene { get; private set; }

    public SceneData? Active => ActiveScene is null ? null : _scenes[ActiveScene];

    /// <summary>The boundary tick at which <see cref="ActiveScene"/> was loaded (-1 before the first load).</summary>
    public long LoadedAt { get; private set; } = -1;

    /// <summary>The load waiting for the next boundary, if any.</summary>
    public string? RequestedScene => _requested;

    /// <summary>The last boundary the runtime reached (-1 before the game started).</summary>
    public long Tick { get; private set; } = -1;

    public IEnumerable<string> SceneNames => _scenes.Keys.Order(StringComparer.Ordinal);

    /// <summary>Live entities in spawn order (scene order, then patch order). Retarget keeps an entity's place.</summary>
    public IReadOnlyList<SceneEntity<TEntity>> Entities => _entities;

    public IReadOnlyList<ScenePatch> AppliedPatches => _applied;

    public IReadOnlyList<ScenePatch> PendingPatches => _pending;

    public IReadOnlyList<PatchRejection> RejectedPatches => _rejected;

    public TEntity this[string id] =>
        _byId.TryGetValue(id, out var entity) ? entity.Instance : throw new SceneException(SceneErrorCode.UnknownEntity, $"no live entity \"{id}\" in scene {ActiveScene ?? "(none)"}");

    public bool TryGet(string id, [MaybeNullWhen(false)] out TEntity instance)
    {
        var found = _byId.TryGetValue(id, out var entity);
        instance = found ? entity!.Instance : default;
        return found;
    }

    public void Register(Prefab<TEntity> prefab)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        ArgumentNullException.ThrowIfNull(prefab.Create);
        SceneRules.Prefab(prefab.Name, prefab.Parameters);
        if (!_prefabs.TryAdd(prefab.Name, prefab))
        {
            throw new SceneException(SceneErrorCode.DuplicatePrefab, $"prefab {prefab.Name} is already registered");
        }
    }

    /// <summary>Validates every entity against the registered prefabs (register prefabs first) and stores the scene.</summary>
    public SceneData Register(SceneData scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneRules.Id(scene.Name, "scene");
        if (_scenes.ContainsKey(scene.Name))
        {
            throw new SceneException(SceneErrorCode.DuplicateScene, $"scene {scene.Name} is already registered");
        }

        if (scene.Bounds is { } bounds)
        {
            var min = bounds.Min;
            var max = bounds.Max;
            if (!float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            {
                throw new SceneException(SceneErrorCode.OutOfRange, $"scene {scene.Name}: bounds {min}..{max} must be finite with min <= max");
            }
        }

        if (scene.Entities is null)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"scene {scene.Name}: entities is null");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in scene.Entities)
        {
            var where = $"scene {scene.Name} entity {entity?.Id}";
            if (entity is null)
            {
                throw new SceneException(SceneErrorCode.Malformed, $"{where}: null entry");
            }

            SceneRules.Id(entity.Id, $"{where}: entity id");
            if (!ids.Add(entity.Id))
            {
                throw new SceneException(SceneErrorCode.DuplicateEntity, $"{where}: id used twice");
            }

            Resolve(entity.Id, entity.Prefab, entity.Position, entity.Values, null, scene.Bounds, where);
        }

        _scenes.Add(scene.Name, scene);
        return scene;
    }

    public SceneData RegisterJson(string json) => Register(SceneData.Parse(json));

    /// <summary>
    /// Requests a load at the next boundary: the current entities are disposed (newest first) and the scene's entities
    /// spawned in order. Called during tick N, the new scene is live from tick N+1. A later request replaces an earlier
    /// one that has not happened yet; loading the active scene again resets it.
    /// </summary>
    public void LoadScene(string name)
    {
        if (name is null || !_scenes.ContainsKey(name))
        {
            throw new SceneException(SceneErrorCode.UnknownScene, $"scene \"{name}\" is not registered (registered: {string.Join(", ", SceneNames)})");
        }

        _requested = name;
    }

    /// <summary>
    /// Validates <paramref name="patch"/> and queues it for its tick, or applies it now when the runtime is paused at that
    /// exact boundary. Anything checkable without the world (format, ids, prefabs, parameter ranges, past ticks, reused
    /// ids) throws <see cref="SceneException"/> and is forgotten. Checks that need the world at the patch's tick (live
    /// entities, scene bounds, expected scene) run at that tick; a refusal there is recorded in
    /// <see cref="RejectedPatches"/> (and also thrown when that tick is now). A refused patch never changes the world.
    /// </summary>
    public void Submit(ScenePatch patch)
    {
        ValidateShape(patch);
        if (_patchIds.Contains(patch.Id))
        {
            throw new SceneException(SceneErrorCode.DuplicatePatch, $"patch {patch.Id} was already submitted");
        }

        if (Tick >= 0 && (patch.Tick < Tick || (patch.Tick == Tick && _simulating)))
        {
            throw new SceneException(SceneErrorCode.PastTick, $"patch {patch.Id} targets tick {patch.Tick}, but tick {Tick} {(_simulating ? "is already running" : "is the current boundary")}; target {(_simulating ? Tick + 1 : Tick)} or later");
        }

        if (Tick >= 0 && patch.Tick == Tick)
        {
            List<(PatchOp Op, PrefabArgs? Args)> plan;
            try
            {
                plan = Plan(patch);
            }
            catch (SceneException refused)
            {
                // Recorded exactly as a queued patch refused at this tick would be, so a live session and a replay of
                // the same submissions produce the same snapshots.
                _rejected.Add(new PatchRejection(patch.Id, patch.Tick, refused.Code, refused.Message));
                _patchIds.Add(patch.Id);
                throw;
            }

            Apply(patch, plan);
        }
        else
        {
            var index = _pending.FindIndex(queued => queued.Tick > patch.Tick);
            _pending.Insert(index < 0 ? _pending.Count : index, patch);
        }

        _patchIds.Add(patch.Id);
    }

    public SceneState State() =>
        new(ActiveScene, LoadedAt, [.. _applied.Select(patch => new PatchRecord(patch.Id, patch.Tick))], [.. _rejected]);

    void ISceneDirector.AtBoundary(long tick)
    {
        if (tick < Tick)
        {
            throw new InvalidOperationException($"[SCENE]: boundary {tick} comes before the current boundary {Tick}");
        }

        Tick = tick;
        _simulating = false;
        if (_requested is { } name)
        {
            _requested = null;
            Load(name);
        }

        while (_pending.Count > 0 && _pending[0].Tick <= tick)
        {
            var patch = _pending[0];
            _pending.RemoveAt(0);
            if (patch.Tick < tick)
            {
                _rejected.Add(new PatchRejection(patch.Id, patch.Tick, SceneErrorCode.PastTick, $"[SCENE:PastTick]: boundary {patch.Tick} was never reached (resumed at {tick})"));
                continue;
            }

            List<(PatchOp Op, PrefabArgs? Args)> plan;
            try
            {
                plan = Plan(patch);
            }
            catch (SceneException refused)
            {
                _rejected.Add(new PatchRejection(patch.Id, patch.Tick, refused.Code, refused.Message));
                continue;
            }

            Apply(patch, plan);
        }
    }

    void ISceneDirector.BeginUpdate(long tick)
    {
        if (tick != Tick)
        {
            ((ISceneDirector)this).AtBoundary(tick);
        }

        _simulating = true;
    }

    private void Load(string name)
    {
        for (var i = _entities.Count - 1; i >= 0; i--)
        {
            var entity = _entities[i];
            _prefabs[entity.Prefab].Dispose?.Invoke(entity.Instance);
        }

        _entities.Clear();
        _byId.Clear();
        var scene = _scenes[name];
        foreach (var data in scene.Entities)
        {
            Spawn(Resolve(data.Id, data.Prefab, data.Position, data.Values, null, scene.Bounds, $"scene {name} entity {data.Id}"));
        }

        ActiveScene = name;
        LoadedAt = Tick;
    }

    private void Spawn(PrefabArgs args)
    {
        var entity = new SceneEntity<TEntity>(args, _prefabs[args.Prefab].Create(args));
        _entities.Add(entity);
        _byId.Add(args.Id, entity);
    }

    private PrefabArgs Resolve(string id, string prefabName, System.Numerics.Vector3 position, IReadOnlyDictionary<string, double> values, PrefabArgs? current, SceneBounds? bounds, string where)
    {
        if (prefabName is null || !_prefabs.TryGetValue(prefabName, out var prefab))
        {
            throw new SceneException(SceneErrorCode.UnknownPrefab, $"{where}: prefab \"{prefabName}\" is not registered (registered: {string.Join(", ", _prefabs.Keys.Order(StringComparer.Ordinal))}); register prefabs before scenes");
        }

        SceneRules.Position(position, bounds, where);
        return new PrefabArgs(id, prefab.Name, position, SceneRules.Values(prefab.Name, prefab.Parameters, current?.Values, values ?? SceneValues.None, where));
    }

    /// <summary>Checks that do not depend on the world: they hold at submit time and at the patch's tick alike.</summary>
    private void ValidateShape(ScenePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        SceneRules.Id(patch.Id, "patch id");
        if (patch.Tick < 0)
        {
            throw new SceneException(SceneErrorCode.PastTick, $"patch {patch.Id}: tick {patch.Tick} is negative");
        }

        if (patch.Ops is null || patch.Ops.Count == 0)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"patch {patch.Id} has no ops");
        }

        if (patch.Scene is not null && !_scenes.ContainsKey(patch.Scene))
        {
            throw new SceneException(SceneErrorCode.UnknownScene, $"patch {patch.Id} expects scene \"{patch.Scene}\", which is not registered");
        }

        for (var i = 0; i < patch.Ops.Count; i++)
        {
            var op = patch.Ops[i];
            var where = $"patch {patch.Id} op #{i}";
            if (op is null)
            {
                throw new SceneException(SceneErrorCode.Malformed, $"{where} is null");
            }

            SceneRules.Id(op.Entity, $"{where} entity id");
            switch (op)
            {
                case SpawnOp spawn:
                    Resolve(spawn.Entity, spawn.Prefab, spawn.Position, spawn.Values, null, null, where);
                    break;
                case RetargetOp retarget:
                    if (retarget.Position is null && (retarget.Values is null || retarget.Values.Count == 0))
                    {
                        throw new SceneException(SceneErrorCode.Malformed, $"{where}: retarget {op.Entity} changes nothing; give a position or values");
                    }

                    if (retarget.Position is { } position)
                    {
                        SceneRules.Position(position, null, where);
                    }

                    break;
                case DisposeOp:
                    break;
                default:
                    throw new SceneException(SceneErrorCode.Malformed, $"{where}: unknown op type {op.GetType().Name}");
            }
        }
    }

    /// <summary>Validates the whole patch against the live world on a scratch overlay; nothing changes until it all passes.</summary>
    private List<(PatchOp Op, PrefabArgs? Args)> Plan(ScenePatch patch)
    {
        if (patch.Scene is not null && patch.Scene != ActiveScene)
        {
            throw new SceneException(SceneErrorCode.WrongScene, $"patch {patch.Id} expects scene {patch.Scene}, but {ActiveScene ?? "no scene"} is active at tick {Tick}");
        }

        var bounds = Active?.Bounds;
        var overlay = new Dictionary<string, PrefabArgs?>(StringComparer.Ordinal);
        var plan = new List<(PatchOp, PrefabArgs?)>(patch.Ops.Count);
        for (var i = 0; i < patch.Ops.Count; i++)
        {
            var op = patch.Ops[i];
            var where = $"patch {patch.Id} op #{i} ({op.Entity})";
            var current = overlay.TryGetValue(op.Entity, out var overlaid) ? overlaid : _byId.GetValueOrDefault(op.Entity)?.Args;
            switch (op)
            {
                case SpawnOp spawn:
                    if (current is not null)
                    {
                        throw new SceneException(SceneErrorCode.DuplicateEntity, $"{where}: an entity with this id is already live");
                    }

                    overlay[op.Entity] = Resolve(spawn.Entity, spawn.Prefab, spawn.Position, spawn.Values, null, bounds, where);
                    plan.Add((op, overlay[op.Entity]));
                    break;
                case DisposeOp:
                    if (current is null)
                    {
                        throw new SceneException(SceneErrorCode.UnknownEntity, $"{where}: no live entity to dispose");
                    }

                    overlay[op.Entity] = null;
                    plan.Add((op, null));
                    break;
                case RetargetOp retarget:
                    if (current is null)
                    {
                        throw new SceneException(SceneErrorCode.UnknownEntity, $"{where}: no live entity to retarget");
                    }

                    overlay[op.Entity] = Resolve(current.Id, current.Prefab, retarget.Position ?? current.Position, retarget.Values, current, bounds, where);
                    plan.Add((op, overlay[op.Entity]));
                    break;
            }
        }

        return plan;
    }

    private void Apply(ScenePatch patch, List<(PatchOp Op, PrefabArgs? Args)> plan)
    {
        foreach (var (op, args) in plan)
        {
            switch (op)
            {
                case SpawnOp:
                    Spawn(args!);
                    break;
                case DisposeOp:
                    var disposed = _byId[op.Entity];
                    _prefabs[disposed.Prefab].Dispose?.Invoke(disposed.Instance);
                    _entities.Remove(disposed);
                    _byId.Remove(op.Entity);
                    break;
                case RetargetOp:
                    var entity = _byId[op.Entity];
                    var prefab = _prefabs[entity.Prefab];
                    if (prefab.Retarget is { } retarget)
                    {
                        retarget(entity.Instance, args!);
                    }
                    else
                    {
                        prefab.Dispose?.Invoke(entity.Instance);
                        entity.Instance = prefab.Create(args!);
                    }

                    entity.Args = args!;
                    break;
            }
        }

        _applied.Add(patch);
    }
}
