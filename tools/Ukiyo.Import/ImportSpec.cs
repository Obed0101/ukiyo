using System.Text.Json;

namespace Ukiyo.Import;

/// <summary>
/// An <c>*.import.json</c> file: what one generated atlas is made of. Paths are relative to the spec's folder.
/// <code>
/// { "class": "QuadArt", "namespace": "Ukiyo.Samples.TexturedQuad", "output": "../Shared/gen/QuadArt.g.cs",
///   "inputs": ["wisp_float.png", "glass.png"], "extrude": 1, "maxWidth": 1024 }
/// </code>
/// A PNG with a sibling <c>.json</c> of the same name uses it as its atlas (frames and animations); otherwise the
/// whole PNG is one frame named after the file.
/// </summary>
public sealed record ImportSpec(string SpecPath, string OutputPath, ImportRequest Request)
{
    public const string Suffix = ".import.json";

    private static readonly string[] Fields = ["class", "namespace", "output", "inputs", "extrude", "maxWidth"];

    public static ImportSpec Load(string specPath)
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
                    throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: unknown field \"{property.Name}\" (expected {string.Join(", ", Fields)})");
                }
            }

            var output = Path.GetFullPath(Path.Combine(directory, String(root, "output", specPath)));
            if (!output.EndsWith(".g.cs", StringComparison.Ordinal))
            {
                throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: output must end in .g.cs");
            }

            var inputs = root.TryGetProperty("inputs", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                ? list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: inputs must be strings")).ToList()
                : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: needs a non-empty \"inputs\" array of PNG paths");

            var request = new ImportRequest(String(root, "class", specPath), String(root, "namespace", specPath), [.. inputs.Select(input => ReadSource(directory, input, specPath))])
            {
                Extrude = OptionalInt(root, "extrude", specPath) ?? 1,
                MaxWidth = OptionalInt(root, "maxWidth", specPath) ?? 1024,
                RegenerateCommand = $"dotnet run --project tools/Ukiyo.Import -- {RepositoryRelative(fullSpec)}",
            };
            return new ImportSpec(fullSpec, output, request);
        }
        catch (JsonException error)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: {error.Message}");
        }
    }

    /// <summary>Every <c>*.import.json</c> under a folder, in ordinal path order.</summary>
    public static IEnumerable<string> Find(string directory) =>
        Directory.EnumerateFiles(directory, "*" + Suffix, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules"))
            .Order(StringComparer.Ordinal);

    private static ImportSource ReadSource(string directory, string input, string specPath)
    {
        if (!input.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: input {input} is not a .png");
        }

        var full = Path.GetFullPath(Path.Combine(directory, input));
        if (!File.Exists(full))
        {
            throw new ImportException(ImportErrorCode.MissingFile, $"{specPath}: input {input} does not exist");
        }

        var atlas = Path.ChangeExtension(full, ".json");
        var label = Path.GetRelativePath(directory, full).Replace('\\', '/');
        return new ImportSource(label, File.ReadAllBytes(full), File.Exists(atlas) ? File.ReadAllBytes(atlas) : null);
    }

    /// <summary>The spec path from the repository root (the folder holding Ukiyo.slnx), so headers match on every machine.</summary>
    private static string RepositoryRelative(string fullSpec)
    {
        for (var dir = Path.GetDirectoryName(fullSpec); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "Ukiyo.slnx")))
            {
                return Path.GetRelativePath(dir, fullSpec).Replace('\\', '/');
            }
        }

        return Path.GetFileName(fullSpec);
    }

    private static string String(JsonElement root, string field, string specPath) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: needs a string \"{field}\"");

    private static int? OptionalInt(JsonElement root, string field, string specPath)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new ImportException(ImportErrorCode.InvalidSpec, $"{specPath}: \"{field}\" must be an integer");
    }
}
