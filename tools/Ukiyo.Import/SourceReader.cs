using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo.Import;

/// <summary>A frame cut from a decoded source image, before packing.</summary>
internal sealed record SourceFrame(string Name, DecodedImage Image, int X, int Y, int Width, int Height);

/// <summary>Decodes source PNGs with the engine's decoder and reads their frames and animations.</summary>
internal static class SourceReader
{
    /// <summary>Default frame rate of an animation without "fps", the same default as <c>SpriteSheet.Parse</c>.</summary>
    private const float DefaultFramesPerSecond = 8f;

    public static (List<SourceFrame> Frames, List<PackedAnimation> Animations) Read(IReadOnlyList<ImportSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, "no inputs");
        }

        var frames = new List<SourceFrame>();
        var animations = new List<PackedAnimation>();
        var frameNames = new HashSet<string>(StringComparer.Ordinal);
        var animationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var image = Decode(source);
            var (sourceFrames, sourceAnimations) = source.AtlasJson is { } atlasJson
                ? ReadAtlas(source.Path, atlasJson, image)
                : (WholeImage(source.Path, image), []);
            foreach (var frame in sourceFrames)
            {
                if (!frameNames.Add(frame.Name))
                {
                    throw new ImportException(ImportErrorCode.DuplicateName, $"frame \"{frame.Name}\" in {source.Path} already exists in an earlier input; rename one of them");
                }

                frames.Add(frame);
            }

            foreach (var animation in sourceAnimations)
            {
                if (!animationNames.Add(animation.Name))
                {
                    throw new ImportException(ImportErrorCode.DuplicateName, $"animation \"{animation.Name}\" in {source.Path} already exists in an earlier input; rename one of them");
                }

                animations.Add(animation);
            }
        }

        return (frames, animations);
    }

    private static DecodedImage Decode(ImportSource source)
    {
        try
        {
            return PngDecoder.Decode(source.Png);
        }
        catch (InvalidDataException error)
        {
            throw new ImportException(ImportErrorCode.InvalidPng, $"{source.Path}: {error.Message}");
        }
    }

    /// <summary>A PNG without atlas JSON is one frame named after its file.</summary>
    private static List<SourceFrame> WholeImage(string path, DecodedImage image) =>
        [new SourceFrame(Names.Require(Path.GetFileNameWithoutExtension(path), $"{path} file name"), image, 0, 0, image.Width, image.Height)];

    private static (List<SourceFrame>, List<PackedAnimation>) ReadAtlas(string sourcePath, byte[] atlasJson, DecodedImage image)
    {
        var atlasPath = Path.ChangeExtension(sourcePath, ".json");
        try
        {
            using var document = JsonDocument.Parse(atlasJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("frames", out var framesElement) || framesElement.ValueKind != JsonValueKind.Object)
            {
                throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: needs a \"frames\" object");
            }

            var frames = new List<SourceFrame>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in framesElement.EnumerateObject())
            {
                var name = Names.Require(property.Name, $"{atlasPath} frame");
                if (!names.Add(name))
                {
                    throw new ImportException(ImportErrorCode.DuplicateName, $"{atlasPath}: frame \"{name}\" appears twice");
                }

                var (x, y, w, h) = (Int(property.Value, "x", atlasPath, name), Int(property.Value, "y", atlasPath, name), Int(property.Value, "w", atlasPath, name), Int(property.Value, "h", atlasPath, name));
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > image.Width || y + h > image.Height)
                {
                    throw new ImportException(ImportErrorCode.FrameOutOfBounds, $"{atlasPath}: frame \"{name}\" ({x},{y},{w},{h}) is outside the {image.Width}x{image.Height} image");
                }

                frames.Add(new SourceFrame(name, image, x, y, w, h));
            }

            if (frames.Count == 0)
            {
                throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: \"frames\" is empty");
            }

            return (frames, ReadAnimations(root, atlasPath, names));
        }
        catch (JsonException error)
        {
            throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: {error.Message}");
        }
    }

    private static List<PackedAnimation> ReadAnimations(JsonElement root, string atlasPath, HashSet<string> frameNames)
    {
        var animations = new List<PackedAnimation>();
        if (!root.TryGetProperty("animations", out var element))
        {
            return animations;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: \"animations\" must be an object");
        }

        foreach (var property in element.EnumerateObject())
        {
            var name = Names.Require(property.Name, $"{atlasPath} animation");
            if (!property.Value.TryGetProperty("frames", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: animation \"{name}\" needs a non-empty \"frames\" array");
            }

            var sequence = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                var frame = item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: animation \"{name}\" has a frame that is not a string");
                sequence.Add(frameNames.Contains(frame) ? frame : throw new ImportException(ImportErrorCode.UnknownFrame, $"{atlasPath}: animation \"{name}\" uses unknown frame \"{frame}\""));
            }

            var fps = DefaultFramesPerSecond;
            if (property.Value.TryGetProperty("fps", out var fpsElement) && !(fpsElement.ValueKind == JsonValueKind.Number && fpsElement.TryGetSingle(out fps) && float.IsFinite(fps) && fps > 0))
            {
                throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: animation \"{name}\" needs a finite fps > 0");
            }

            var loop = !property.Value.TryGetProperty("loop", out var loopElement) || loopElement.ValueKind != JsonValueKind.False;
            animations.Add(new PackedAnimation(name, sequence, fps, loop));
        }

        return animations;
    }

    private static int Int(JsonElement rect, string field, string atlasPath, string frame) =>
        rect.ValueKind == JsonValueKind.Object && rect.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new ImportException(ImportErrorCode.InvalidAtlas, $"{atlasPath}: frame \"{frame}\" needs an integer \"{field}\"");
}

/// <summary>Names that end up in generated C#: frame and animation strings, the class and its namespace.</summary>
internal static class Names
{
    private const int MaxLength = 64;

    /// <summary>Frame and animation names: letters, digits, '_', '.', '-', starting with a letter or '_'.</summary>
    public static string Require(string name, string what)
    {
        var valid = name.Length is > 0 and <= MaxLength && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        return valid ? name : throw new ImportException(ImportErrorCode.InvalidName, $"{what} \"{name}\" must be 1-{MaxLength} characters of letters, digits, '_', '.', '-', starting with a letter or '_'");
    }

    /// <summary>A C# identifier made of ASCII letters, digits and '_' (no keywords: they are all lowercase, so require an uppercase start).</summary>
    public static string RequireIdentifier(string name, string what)
    {
        var valid = name.Length is > 0 and <= MaxLength && char.IsAsciiLetterUpper(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        return valid ? name : throw new ImportException(ImportErrorCode.InvalidName, $"{what} \"{name}\" must be a PascalCase C# identifier (A-Z first, then letters, digits, '_')");
    }

    public static string RequireNamespace(string name) =>
        name.Split('.').All(part => part.Length > 0 && part.Length <= MaxLength && (char.IsAsciiLetter(part[0]) || part[0] == '_') && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            ? name
            : throw new ImportException(ImportErrorCode.InvalidName, $"namespace \"{name}\" must be dot-separated C# identifiers");
}
