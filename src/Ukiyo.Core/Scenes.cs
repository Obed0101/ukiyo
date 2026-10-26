using System.Numerics;
using System.Text.Json;

namespace Ukiyo;

/// <summary>Why scene content or a patch was refused. Stable names: agents and tests match on them.</summary>
public enum SceneErrorCode
{
    Malformed,
    InvalidId,
    DuplicateScene,
    UnknownScene,
    DuplicatePrefab,
    UnknownPrefab,
    DuplicateEntity,
    UnknownEntity,
    UnknownParameter,
    OutOfRange,
    PastTick,
    DuplicatePatch,
    WrongScene,
}

/// <summary>Invalid scene content or a refused patch. Thrown before anything changes, so state stays as it was.</summary>
public sealed class SceneException(SceneErrorCode code, string message, Exception? inner = null)
    : Exception($"[SCENE:{code}]: {message}", inner)
{
    public SceneErrorCode Code { get; } = code;
}

/// <summary>A number a prefab accepts, with the range content and patches must respect.</summary>
public readonly record struct PrefabParameter(string Name, double Min, double Max, double Default);

/// <summary>Axis-aligned box (inclusive) that every entity position in a scene must lie inside.</summary>
public readonly record struct SceneBounds(Vector3 Min, Vector3 Max)
{
    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.Y >= Min.Y && point.Z >= Min.Z && point.X <= Max.X && point.Y <= Max.Y && point.Z <= Max.Z;
}

/// <summary>One entity as content: which prefab builds it, where, and the parameter values it overrides.</summary>
public sealed record EntityData(string Id, string Prefab, Vector3 Position)
{
    public IReadOnlyDictionary<string, double> Values { get; init; } = SceneValues.None;
}

/// <summary>
/// A scene as data: the entities a load spawns, in order. Written in code or as JSON under <c>Shared/assets</c>; either
/// way it is validated against the registered prefabs when it is registered, so bad content fails at game start.
/// </summary>
public sealed record SceneData(string Name, IReadOnlyList<EntityData> Entities)
{
    /// <summary>When set, every spawned or retargeted position must be inside it.</summary>
    public SceneBounds? Bounds { get; init; }

    /// <summary>Parses the scene JSON format (see docs/scenes-patches.md). Unknown fields are errors, not ignored.</summary>
    public static SceneData Parse(string json)
    {
        using var document = SceneJson.Open(json, "scene");
        var root = document.RootElement;
        SceneJson.Fields(root, "scene", "name", "bounds", "entities");
        var name = SceneJson.RequiredString(root, "name", "scene");
        SceneBounds? bounds = null;
        if (root.TryGetProperty("bounds", out var boundsElement))
        {
            SceneJson.Fields(boundsElement, $"scene {name} bounds", "min", "max");
            bounds = new SceneBounds(
                SceneJson.Vector(SceneJson.Required(boundsElement, "min", $"scene {name} bounds"), $"scene {name} bounds.min"),
                SceneJson.Vector(SceneJson.Required(boundsElement, "max", $"scene {name} bounds"), $"scene {name} bounds.max"));
        }

        var entitiesElement = SceneJson.Required(root, "entities", $"scene {name}");
        if (entitiesElement.ValueKind != JsonValueKind.Array)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"scene {name}: \"entities\" must be an array");
        }

        var entities = new List<EntityData>();
        foreach (var item in entitiesElement.EnumerateArray())
        {
            var where = $"scene {name} entity #{entities.Count}";
            SceneJson.Fields(item, where, "id", "prefab", "position", "values");
            var id = SceneJson.RequiredString(item, "id", where);
            entities.Add(new EntityData(id, SceneJson.RequiredString(item, "prefab", where), SceneJson.Vector(SceneJson.Required(item, "position", where), $"{where} position"))
            {
                Values = SceneJson.Values(item, $"{where} values"),
            });
        }

        return new SceneData(name, entities) { Bounds = bounds };
    }
}

/// <summary>What a prefab function receives: the entity id, its position and every parameter (defaults filled in).</summary>
public sealed class PrefabArgs(string id, string prefab, Vector3 position, IReadOnlyDictionary<string, double> values)
{
    public string Id { get; } = id;

    public string Prefab { get; } = prefab;

    public Vector3 Position { get; } = position;

    public IReadOnlyDictionary<string, double> Values { get; } = values;

    /// <summary>A declared parameter. A name the prefab did not declare is a bug in the prefab and throws.</summary>
    public double this[string name] =>
        Values.TryGetValue(name, out var value)
            ? value
            : throw new SceneException(SceneErrorCode.UnknownParameter, $"prefab {Prefab} reads \"{name}\" but declares only [{string.Join(", ", Values.Keys)}]");

    public float Float(string name) => (float)this[name];
}

/// <summary>
/// A prefab is a function: given validated arguments it builds the game's entity. Optional hooks undo it (remove a
/// physics body) and retarget it in place; without <see cref="Retarget"/> a retarget disposes and rebuilds the entity.
/// </summary>
public sealed record Prefab<TEntity>(string Name, Func<PrefabArgs, TEntity> Create)
    where TEntity : notnull
{
    public IReadOnlyList<PrefabParameter> Parameters { get; init; } = [];

    public Action<TEntity>? Dispose { get; init; }

    public Action<TEntity, PrefabArgs>? Retarget { get; init; }
}

/// <summary>A live entity: the arguments it was built from (content, not simulation state) and the game's instance.</summary>
public sealed class SceneEntity<TEntity>(PrefabArgs args, TEntity instance)
    where TEntity : notnull
{
    public string Id => Args.Id;

    public string Prefab => Args.Prefab;

    public PrefabArgs Args { get; internal set; } = args;

    public TEntity Instance { get; internal set; } = instance;
}

public static class SceneValues
{
    public static readonly IReadOnlyDictionary<string, double> None = new Dictionary<string, double>();
}

/// <summary>Strict JSON reading shared by scenes and patches: JsonDocument only (NativeAOT safe), unknown fields refused.</summary>
internal static class SceneJson
{
    public static JsonDocument Open(string json, string what)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{what} is not valid JSON: {error.Message}", error);
        }
    }

    public static void Fields(JsonElement element, string where, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where} must be a JSON object, got {element.ValueKind}");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                throw new SceneException(SceneErrorCode.Malformed, $"{where}: unknown field \"{property.Name}\" (allowed: {string.Join(", ", allowed)})");
            }
        }
    }

    public static JsonElement Required(JsonElement element, string name, string where) =>
        element.TryGetProperty(name, out var value) ? value : throw new SceneException(SceneErrorCode.Malformed, $"{where}: missing \"{name}\"");

    public static string RequiredString(JsonElement element, string name, string where)
    {
        var value = Required(element, name, where);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new SceneException(SceneErrorCode.Malformed, $"{where}: \"{name}\" must be a string, got {value.ValueKind}");
    }

    /// <summary>[x, y] or [x, y, z]; z defaults to 0 so 2D content stays short.</summary>
    public static Vector3 Vector(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 2 or > 3)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where} must be [x, y] or [x, y, z]");
        }

        Span<float> xyz = stackalloc float[3];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            xyz[i] = (float)Number(item, $"{where}[{i}]");
            i++;
        }

        return new Vector3(xyz[0], xyz[1], xyz[2]);
    }

    public static IReadOnlyDictionary<string, double> Values(JsonElement parent, string where)
    {
        if (!parent.TryGetProperty("values", out var element))
        {
            return SceneValues.None;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SceneException(SceneErrorCode.Malformed, $"{where} must be an object of numbers");
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = Number(property.Value, $"{where}.{property.Name}");
        }

        return values;
    }

    private static double Number(JsonElement element, string where) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : throw new SceneException(SceneErrorCode.Malformed, $"{where} must be a number, got {element.ValueKind}");
}

/// <summary>Validation rules shared by scene registration and patches.</summary>
internal static class SceneRules
{
    public const int MaxIdLength = 64;

    /// <summary>Ids are 1–64 chars of [A-Za-z0-9_.-]: safe as snapshot keys, file names and CLI arguments.</summary>
    public static void Id(string? id, string what)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            throw new SceneException(SceneErrorCode.InvalidId, $"{what} \"{id}\" must be 1-{MaxIdLength} characters of A-Z a-z 0-9 _ . -");
        }
    }

    public static void Position(Vector3 position, SceneBounds? bounds, string where)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            throw new SceneException(SceneErrorCode.OutOfRange, $"{where}: position {position} is not finite");
        }

        if (bounds is { } box && !box.Contains(position))
        {
            throw new SceneException(SceneErrorCode.OutOfRange, $"{where}: position {position} is outside the scene bounds {box.Min}..{box.Max}");
        }
    }

    /// <summary>Merges <paramref name="overrides"/> over <paramref name="current"/> (or the prefab defaults) after checking every value.</summary>
    public static IReadOnlyDictionary<string, double> Values(string prefab, IReadOnlyList<PrefabParameter> parameters, IReadOnlyDictionary<string, double>? current, IReadOnlyDictionary<string, double> overrides, string where)
    {
        foreach (var (name, value) in overrides)
        {
            var declared = false;
            foreach (var parameter in parameters)
            {
                if (parameter.Name != name)
                {
                    continue;
                }

                declared = true;
                if (!double.IsFinite(value) || value < parameter.Min || value > parameter.Max)
                {
                    throw new SceneException(SceneErrorCode.OutOfRange, $"{where}: {name}={value} is outside {prefab}'s range [{parameter.Min}, {parameter.Max}]");
                }
            }

            if (!declared)
            {
                throw new SceneException(SceneErrorCode.UnknownParameter, $"{where}: prefab {prefab} has no parameter \"{name}\" (declared: [{string.Join(", ", parameters.Select(p => p.Name))}])");
            }
        }

        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            merged[parameter.Name] = overrides.TryGetValue(parameter.Name, out var value)
                ? value
                : current is not null && current.TryGetValue(parameter.Name, out var previous) ? previous : parameter.Default;
        }

        return merged;
    }

    public static void Prefab(string name, IReadOnlyList<PrefabParameter> parameters)
    {
        Id(name, "prefab");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            Id(parameter.Name, $"prefab {name} parameter");
            if (!seen.Add(parameter.Name))
            {
                throw new SceneException(SceneErrorCode.DuplicatePrefab, $"prefab {name} declares parameter \"{parameter.Name}\" twice");
            }

            if (!double.IsFinite(parameter.Min) || !double.IsFinite(parameter.Max) || parameter.Min > parameter.Max || parameter.Default < parameter.Min || parameter.Default > parameter.Max)
            {
                throw new SceneException(SceneErrorCode.OutOfRange, $"prefab {name} parameter {parameter.Name}: default {parameter.Default} must lie in a finite range [{parameter.Min}, {parameter.Max}]");
            }
        }
    }
}
