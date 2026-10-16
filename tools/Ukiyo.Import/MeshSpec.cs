using System.Text.Json;

namespace Ukiyo.Import;

/// <summary>
/// An <c>*.import.json</c> whose inputs are glTF 2.0 models: one generated class with one mesh per model.
/// Paths are relative to the spec's folder; a model's buffers are resolved relative to the model.
/// <code>
/// { "class": "PropMeshes", "namespace": "Ukiyo.Games.MyGame", "output": "../Shared/gen/PropMeshes.g.cs",
///   "inputs": ["lamp.gltf", "crate.glb"], "normals": "required", "doubleSided": "reject" }
/// </code>
/// </summary>
public sealed record MeshSpec(string SpecPath, string OutputPath, MeshImportRequest Request)
{
    private static readonly string[] Fields = ["class", "namespace", "output", "inputs", "normals", "doubleSided"];

    public static bool IsModel(string path) =>
        path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the spec's inputs are glTF models, false for PNG (atlas) specs and for files <see cref="ImportSpec.Load"/>
    /// should report on. A spec that mixes models and images is an error: one spec generates one kind of class.
    /// </summary>
    public static bool Describes(string specPath)
    {
        if (!File.Exists(specPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(specPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var paths = inputs.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList();
            var models = paths.Count(IsModel);
            if (models == 0)
            {
                return false;
            }

            return models == paths.Count ? true : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: inputs mix glTF models and other files; use one spec for meshes and another for images");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static MeshSpec Load(string specPath)
    {
        var fullSpec = Path.GetFullPath(specPath);
        if (!File.Exists(fullSpec))
        {
            throw new ImportException(ImportErrorCode.MissingFile, $"spec {specPath} does not exist");
        }

        var directory = Path.GetDirectoryName(fullSpec)!;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(fullSpec));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: must be a JSON object");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!Fields.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: unknown field \"{property.Name}\" for a mesh spec (expected {string.Join(", ", Fields)})");
                }
            }

            var output = Path.GetFullPath(Path.Combine(directory, ImportSpec.String(root, "output", specPath)));
            if (!output.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: output must end in .g.cs");
            }

            var inputs = root.TryGetProperty("inputs", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                ? list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: inputs must be strings")).ToList()
                : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: needs a non-empty \"inputs\" array of .gltf/.glb paths");

            // Options first, so a typo in the spec is reported before any model is read.
            var normals = Option(root, "normals", specPath, NormalsMode.Required, ("required", NormalsMode.Required), ("flat", NormalsMode.Flat), ("none", NormalsMode.None));
            var doubleSided = Option(root, "doubleSided", specPath, DoubleSidedMode.Reject, ("reject", DoubleSidedMode.Reject), ("cull", DoubleSidedMode.Cull));
            var request = new MeshImportRequest(ImportSpec.String(root, "class", specPath), ImportSpec.String(root, "namespace", specPath), [.. inputs.Select(input => ReadModel(directory, input, specPath))])
            {
                Normals = normals,
                DoubleSided = doubleSided,
                RegenerateCommand = $"dotnet run --project tools/Ukiyo.Import -- {ImportSpec.RepositoryRelative(fullSpec)}",
            };
            return new MeshSpec(fullSpec, output, request);
        }
        catch (JsonException error)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: {error.Message}");
        }
    }

    private static MeshSource ReadModel(string directory, string input, string specPath)
    {
        if (!IsModel(input))
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: input {input} is not a .gltf or .glb");
        }

        var full = Path.GetFullPath(Path.Combine(directory, input));
        if (!File.Exists(full))
        {
            throw new ImportException(ImportErrorCode.MissingFile, $"{specPath}: input {input} does not exist");
        }

        var modelDirectory = Path.GetDirectoryName(full)!;
        var label = Path.GetRelativePath(directory, full).Replace('\\', '/');
        return new MeshSource(label, File.ReadAllBytes(full), relative =>
        {
            var resource = Path.GetFullPath(Path.Combine(modelDirectory, relative));
            return File.Exists(resource) ? File.ReadAllBytes(resource) : null;
        });
    }

    private static T Option<T>(JsonElement root, string field, string specPath, T fallback, params (string Name, T Value)[] choices)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            return fallback;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        foreach (var (name, choice) in choices)
        {
            if (text == name)
            {
                return choice;
            }
        }

        throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: \"{field}\" must be one of {string.Join(", ", choices.Select(c => $"\"{c.Name}\""))}");
    }
}
