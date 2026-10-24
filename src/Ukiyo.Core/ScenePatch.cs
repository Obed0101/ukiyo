using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Ukiyo;

/// <summary>One change in a patch. Ops run in order, so a patch can spawn an entity and retarget it in the same step.</summary>
public abstract record PatchOp(string Entity);

/// <summary>Builds a new entity from a registered prefab.</summary>
public sealed record SpawnOp(string Entity, string Prefab, Vector3 Position) : PatchOp(Entity)
{
    public IReadOnlyDictionary<string, double> Values { get; init; } = SceneValues.None;
}

/// <summary>Removes a live entity (its prefab's Dispose hook runs).</summary>
public sealed record DisposeOp(string Entity) : PatchOp(Entity);

/// <summary>Changes a live entity's content: a new position and/or parameter values. Unset parameters keep their value.</summary>
public sealed record RetargetOp(string Entity) : PatchOp(Entity)
{
    public Vector3? Position { get; init; }

    public IReadOnlyDictionary<string, double> Values { get; init; } = SceneValues.None;
}

/// <summary>
/// A typed diff applied at the boundary before tick <see cref="Tick"/> runs, identically on every host and in replays.
/// A patch applies whole or not at all. The same type is what the dev bridge, the agent CLI and hot reload submit.
/// </summary>
public sealed record ScenePatch(string Id, long Tick, IReadOnlyList<PatchOp> Ops)
{
    /// <summary>When set, the patch is refused unless this scene is active at its tick.</summary>
    public string? Scene { get; init; }

    /// <summary>Parses one patch object (docs/scenes-patches.md).</summary>
    public static ScenePatch Parse(string json)
    {
        using var document = SceneJson.Open(json, "patch");
        return Read(document.RootElement);
    }

    /// <summary>Parses a patch log: a JSON array of patches, as written by <see cref="WriteLog"/>.</summary>
    public static IReadOnlyList<ScenePatch> ParseLog(string json)
    {
        using var document = SceneJson.Open(json, "patch log");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new SceneException(SceneErrorCode.Malformed, "a patch log is a JSON array of patches");
        }

        return [.. document.RootElement.EnumerateArray().Select(Read)];
    }

    public static string WriteLog(IEnumerable<ScenePatch> patches)
    {
        var json = new StringBuilder("[");
        var first = true;
        foreach (var patch in patches)
        {
            json.Append(first ? "\n  " : ",\n  ").Append(patch.ToJson());
            first = false;
        }

        return json.Append(first ? "]" : "\n]").ToString();
    }

    /// <summary>Compact, stable JSON (values sorted by name) : <c>Parse(p.ToJson()).ToJson() == p.ToJson()</c>.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", Id);
            json.WriteNumber("tick", Tick);
            if (Scene is not null)
            {
                json.WriteString("scene", Scene);
            }

            json.WriteStartArray("ops");
            foreach (var op in Ops)
            {
                json.WriteStartObject();
                switch (op)
                {
                    case SpawnOp spawn:
                        json.WriteString("op", "spawn");
                        json.WriteString("id", spawn.Entity);
                        json.WriteString("prefab", spawn.Prefab);
                        WriteVector(json, spawn.Position);
                        WriteValues(json, spawn.Values);
                        break;
                    case DisposeOp dispose:
                        json.WriteString("op", "dispose");
                        json.WriteString("id", dispose.Entity);
                        break;
                    case RetargetOp retarget:
                        json.WriteString("op", "retarget");
                        json.WriteString("id", retarget.Entity);
                        if (retarget.Position is { } position)
                        {
                            WriteVector(json, position);
                        }

                        WriteValues(json, retarget.Values);
                        break;
                    default:
                        throw new SceneException(SceneErrorCode.Malformed, $"patch {Id}: unknown op type {op.GetType().Name}");
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ScenePatch Read(JsonElement element)
    {
        SceneJson.Fields(element, "patch", "id", "tick", "scene", "ops");
        var id = SceneJson.RequiredString(element, "id", "patch");
        var where = $"patch {id}";
        var tickElement = SceneJson.Required(element, "tick", where);
        if (tickElement.ValueKind != JsonValueKind.Number || !tickElement.TryGetInt64(out var tick))
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where}: \"tick\" must be an integer");
        }

        string? scene = element.TryGetProperty("scene", out var sceneElement)
            ? sceneElement.ValueKind == JsonValueKind.String ? sceneElement.GetString() : throw new SceneException(SceneErrorCode.Malformed, $"{where}: \"scene\" must be a string")
            : null;
        var opsElement = SceneJson.Required(element, "ops", where);
        if (opsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where}: \"ops\" must be an array");
        }

        var ops = new List<PatchOp>();
        foreach (var item in opsElement.EnumerateArray())
        {
            ops.Add(ReadOp(item, $"{where} op #{ops.Count}"));
        }

        return new ScenePatch(id, tick, ops) { Scene = scene };
    }

    private static PatchOp ReadOp(JsonElement item, string where)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where} must be an object");
        }

        var kind = SceneJson.RequiredString(item, "op", where);
        switch (kind)
        {
            case "spawn":
                SceneJson.Fields(item, where, "op", "id", "prefab", "position", "values");
                return new SpawnOp(SceneJson.RequiredString(item, "id", where), SceneJson.RequiredString(item, "prefab", where), SceneJson.Vector(SceneJson.Required(item, "position", where), $"{where} position"))
                {
                    Values = SceneJson.Values(item, $"{where} values"),
                };
            case "dispose":
                SceneJson.Fields(item, where, "op", "id");
                return new DisposeOp(SceneJson.RequiredString(item, "id", where));
            case "retarget":
                SceneJson.Fields(item, where, "op", "id", "position", "values");
                return new RetargetOp(SceneJson.RequiredString(item, "id", where))
                {
                    Position = item.TryGetProperty("position", out var position) ? SceneJson.Vector(position, $"{where} position") : null,
                    Values = SceneJson.Values(item, $"{where} values"),
                };
            default:
                throw new SceneException(SceneErrorCode.Malformed, $"{where}: unknown op \"{kind}\" (spawn, dispose, retarget)");
        }
    }

    private static void WriteVector(Utf8JsonWriter json, Vector3 position)
    {
        json.WriteStartArray("position");
        json.WriteNumberValue(position.X);
        json.WriteNumberValue(position.Y);
        json.WriteNumberValue(position.Z);
        json.WriteEndArray();
    }

    private static void WriteValues(Utf8JsonWriter json, IReadOnlyDictionary<string, double> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        json.WriteStartObject("values");
        foreach (var (name, value) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            json.WriteNumber(name, value);
        }

        json.WriteEndObject();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"patch {Id} @ tick {Tick} ({Ops.Count} ops)");
}

/// <summary>A patch that reached its tick and was applied.</summary>
public readonly record struct PatchRecord(string Id, long Tick);

/// <summary>A queued patch that was refused at its tick because the world had changed; nothing it described happened.</summary>
public readonly record struct PatchRejection(string Id, long Tick, SceneErrorCode Code, string Message);

/// <summary>
/// What snapshots record about scenes: the active scene, when it loaded, and a bounded patch history.
/// <see cref="Applied"/> and <see cref="Rejected"/> hold only the last <see cref="PatchHistory.Window"/> entries (oldest
/// first); the counts and hash chains cover every entry since the game started, so two runs can be compared exactly
/// without the full lists and a long session's snapshots stay the same size.
/// </summary>
public sealed record SceneState(string? Active, long LoadedAt, IReadOnlyList<PatchRecord> Applied, IReadOnlyList<PatchRejection> Rejected)
{
    /// <summary>Patches applied since the game started, including those no longer in <see cref="Applied"/>.</summary>
    public long AppliedCount { get; init; }

    /// <summary><see cref="PatchHistory.ChainApplied"/> folded over every applied patch, in application order.</summary>
    public ulong AppliedHash { get; init; } = PatchHistory.EmptyHash;

    /// <summary>Patches refused at their tick since the game started, including those no longer in <see cref="Rejected"/>.</summary>
    public long RejectedCount { get; init; }

    /// <summary><see cref="PatchHistory.ChainRejected"/> folded over every refusal, in the order they were recorded.</summary>
    public ulong RejectedHash { get; init; } = PatchHistory.EmptyHash;
}

/// <summary>
/// The bounded patch history in snapshots (docs/scenes-patches.md): a rolling window of the last <see cref="Window"/>
/// entries plus FNV-1a 64 hash chains over every entry. FNV-1a is plain integer arithmetic over a fixed byte encoding,
/// so it gives the same value on CoreCLR, NativeAOT and WebAssembly; <see cref="string.GetHashCode()"/> is randomized
/// per process and must never be used for this. Chaining continues the FNV state, so a chain equals FNV-1a 64 of the
/// concatenated entry encodings, which any language can recompute from a patch log's outcomes.
/// </summary>
public static class PatchHistory
{
    /// <summary>How many applied and how many rejected entries a snapshot lists.</summary>
    public const int Window = 16;

    /// <summary>The chain before any entry: the FNV-1a 64 offset basis.</summary>
    public const ulong EmptyHash = 0xcbf29ce484222325;

    private const ulong Prime = 0x100000001b3;

    /// <summary>Adds an applied patch: UTF-8 <paramref name="id"/>, a 0 byte, then <paramref name="tick"/> as 8 bytes little-endian.</summary>
    public static ulong ChainApplied(ulong hash, string id, long tick) => Tick(Text(hash, id), tick);

    /// <summary>Adds a refusal: the applied encoding followed by the UTF-8 code name (e.g. <c>UnknownEntity</c>) and a 0 byte.</summary>
    public static ulong ChainRejected(ulong hash, string id, long tick, SceneErrorCode code) => Text(Tick(Text(hash, id), tick), code.ToString());

    public static ulong AppliedHash(IEnumerable<PatchRecord> applied) =>
        applied.Aggregate(EmptyHash, (hash, patch) => ChainApplied(hash, patch.Id, patch.Tick));

    public static ulong RejectedHash(IEnumerable<PatchRejection> rejected) =>
        rejected.Aggregate(EmptyHash, (hash, rejection) => ChainRejected(hash, rejection.Id, rejection.Tick, rejection.Code));

    /// <summary>16 lowercase hex digits: JSON numbers cannot carry 64 bits exactly into JavaScript.</summary>
    public static string Format(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    private static ulong Text(ulong hash, string value)
    {
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash = Byte(hash, b);
        }

        return Byte(hash, 0);
    }

    private static ulong Tick(ulong hash, long tick)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash = Byte(hash, (byte)((ulong)tick >> shift));
        }

        return hash;
    }

    private static ulong Byte(ulong hash, byte value) => (hash ^ value) * Prime;
}

/// <summary>
/// The runtime's view of a game's scene registry (<see cref="SceneRegistry{TEntity}"/>). GameRuntime calls
/// <see cref="BeginUpdate"/> right before each Update and <see cref="AtBoundary"/> right after each tick (and once
/// after Initialize). Internal so only the runtime drives it; tools submit through <see cref="GameRuntime.SubmitPatch"/>.
/// </summary>
internal interface ISceneDirector
{
    void Submit(ScenePatch patch);

    SceneState State();

    /// <summary>The world is at <paramref name="tick"/>: apply the requested scene load, then patches due at this tick.</summary>
    void AtBoundary(long tick);

    /// <summary>Tick <paramref name="tick"/> is being simulated; patches for it are now in the past.</summary>
    void BeginUpdate(long tick);

    /// <summary>Writes the active scene, entities (as arguments), patch history, pending patches and reserved patch ids.</summary>
    void SaveState(SaveWriter writer);

    /// <summary>
    /// Replaces all of the above from a save, at boundary <paramref name="tick"/>. Everything is parsed and validated
    /// first; live entities are then disposed (newest first) and rebuilt through their prefabs' Create.
    /// </summary>
    void RestoreState(SaveReader reader, long tick);
}
