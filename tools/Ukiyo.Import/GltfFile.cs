using System.Buffers.Binary;
using System.Text.Json;

namespace Ukiyo.Import;

/// <summary>
/// The container half of glTF 2.0: a <c>.gltf</c> JSON file with external or <c>data:</c> buffers, or a <c>.glb</c>
/// (12-byte header, JSON chunk, optional BIN chunk). Parses the JSON with <see cref="JsonDocument"/> (no reflection),
/// loads every buffer once and gives bounds-checked access to accessors. Geometry rules live in <see cref="MeshImporter"/>.
/// </summary>
internal sealed class GltfFile : IDisposable
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A; // "JSON"
    private const uint BinChunk = 0x004E4942; // "BIN\0"

    public const int Byte = 5120;
    public const int UnsignedByte = 5121;
    public const int Short = 5122;
    public const int UnsignedShort = 5123;
    public const int UnsignedInt = 5125;
    public const int Float = 5126;

    private readonly JsonDocument _document;
    private readonly byte[][] _buffers;

    private GltfFile(string path, JsonDocument document, byte[][] buffers, List<(string Path, byte[] Bytes)> resources)
    {
        Path = path;
        _document = document;
        _buffers = buffers;
        Resources = resources;
    }

    /// <summary>The model's path as written in the header.</summary>
    public string Path { get; }

    public JsonElement Root => _document.RootElement;

    /// <summary>External files the model referenced (its <c>.bin</c> buffers), in buffer order, with their bytes.</summary>
    public IReadOnlyList<(string Path, byte[] Bytes)> Resources { get; }

    public void Dispose() => _document.Dispose();

    public static GltfFile Open(MeshSource source)
    {
        var isGlb = source.Path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
        var (json, bin) = isGlb ? SplitGlb(source) : (source.Bytes, null);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw Invalid(source.Path, $"JSON: {error.Message}");
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(source.Path, "the top level must be a JSON object");
            }

            RequireVersion(source.Path, root);
            if (root.TryGetProperty("extensionsRequired", out var required) && required.ValueKind == JsonValueKind.Array && required.GetArrayLength() > 0)
            {
                var names = string.Join(", ", required.EnumerateArray().Select(e => e.ToString()));
                throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{source.Path}: requires glTF extensions the importer does not implement: {names}");
            }

            var resources = new List<(string, byte[])>();
            var buffers = LoadBuffers(source, root, bin, resources);
            return new GltfFile(source.Path, document, buffers, resources);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static ImportException Invalid(string path, string message) => new(ImportErrorCode.InvalidMesh, $"{path}: {message}");

    public ImportException Invalid(string message) => Invalid(Path, message);

    /// <summary>The element at <paramref name="index"/> of a top-level array such as "nodes" or "accessors".</summary>
    public JsonElement Item(string array, int index, string where)
    {
        if (!Root.TryGetProperty(array, out var list) || list.ValueKind != JsonValueKind.Array || index < 0 || index >= list.GetArrayLength())
        {
            throw Invalid($"{where} refers to {array}[{index}], which does not exist");
        }

        var item = list[index];
        return item.ValueKind == JsonValueKind.Object ? item : throw Invalid($"{array}[{index}] must be an object");
    }

    /// <summary>Reads an accessor's layout and checks that every element it describes lies inside its buffer view.</summary>
    public Accessor Accessor(int index, string where)
    {
        var accessor = Item("accessors", index, where);
        var name = $"accessors[{index}] ({where})";
        if (accessor.TryGetProperty("sparse", out _))
        {
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{Path}: {name} is a sparse accessor; export without sparse accessors");
        }

        RejectExtensions(accessor, $"{Path}: {name}");
        var componentType = RequiredInt(accessor, "componentType", name);
        var componentSize = componentType switch
        {
            Byte or UnsignedByte => 1,
            Short or UnsignedShort => 2,
            UnsignedInt or Float => 4,
            _ => throw Invalid($"{name} has unknown componentType {componentType}"),
        };
        var components = String(accessor, "type", name) switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            var other => throw Invalid($"{name} has unknown type \"{other}\""),
        };
        var count = RequiredInt(accessor, "count", name);
        if (count < 1)
        {
            throw Invalid($"{name} count must be at least 1");
        }

        var normalized = accessor.TryGetProperty("normalized", out var flag) && flag.ValueKind == JsonValueKind.True;
        var elementSize = componentSize * components;
        if (!accessor.TryGetProperty("bufferView", out var viewElement))
        {
            // glTF: an accessor without a buffer view is all zeros.
            return new Accessor(name, count, componentType, components, normalized, null, 0, elementSize);
        }

        var viewIndex = viewElement.TryGetInt32(out var v) ? v : throw Invalid($"{name} bufferView must be an integer");
        var view = Item("bufferViews", viewIndex, name);
        var viewName = $"bufferViews[{viewIndex}]";
        RejectExtensions(view, $"{Path}: {viewName}");
        var bufferIndex = RequiredInt(view, "buffer", viewName);
        if (bufferIndex < 0 || bufferIndex >= _buffers.Length)
        {
            throw Invalid($"{viewName} refers to buffers[{bufferIndex}], which does not exist");
        }

        var viewOffset = OptionalInt(view, "byteOffset", viewName, 0);
        var viewLength = RequiredInt(view, "byteLength", viewName);
        var buffer = _buffers[bufferIndex];
        if (viewOffset < 0 || viewLength < 1 || (long)viewOffset + viewLength > buffer.Length)
        {
            throw Invalid($"{viewName} ({viewOffset}+{viewLength} bytes) lies outside buffers[{bufferIndex}] ({buffer.Length} bytes)");
        }

        var stride = OptionalInt(view, "byteStride", viewName, elementSize);
        if (stride < elementSize || (view.TryGetProperty("byteStride", out _) && (stride > 252 || stride % 4 != 0)))
        {
            throw Invalid($"{viewName} byteStride {stride} must be a multiple of 4 between the element size {elementSize} and 252");
        }

        var offset = OptionalInt(accessor, "byteOffset", name, 0);
        if (offset < 0 || offset + (long)stride * (count - 1) + elementSize > viewLength)
        {
            throw Invalid($"{name} ({count} elements of {elementSize} bytes, stride {stride}, offset {offset}) reaches past the end of {viewName} ({viewLength} bytes)");
        }

        return new Accessor(name, count, componentType, components, normalized, buffer, viewOffset + offset, stride);
    }

    public static void RejectExtensions(JsonElement element, string where)
    {
        if (element.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object && extensions.EnumerateObject().Any())
        {
            var names = string.Join(", ", extensions.EnumerateObject().Select(p => p.Name));
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{where} uses extensions the importer does not implement: {names}");
        }
    }

    public int RequiredInt(JsonElement element, string field, string where) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw Invalid($"{where} needs an integer \"{field}\"");

    public int OptionalInt(JsonElement element, string field, string where, int fallback)
    {
        if (!element.TryGetProperty(field, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : throw Invalid($"{where} \"{field}\" must be an integer");
    }

    /// <summary>A fixed-length array of finite numbers, or <paramref name="fallback"/> when the field is absent.</summary>
    public double[] Numbers(JsonElement element, string field, int length, string where, double[] fallback)
    {
        if (!element.TryGetProperty(field, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
        {
            throw Invalid($"{where} \"{field}\" must be an array of {length} numbers");
        }

        var result = new double[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = value[i].ValueKind == JsonValueKind.Number && value[i].TryGetDouble(out var number) && double.IsFinite(number)
                ? number
                : throw Invalid($"{where} \"{field}\"[{i}] must be a finite number");
        }

        return result;
    }

    private string String(JsonElement element, string field, string where) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid($"{where} needs a string \"{field}\"");

    private static void RequireVersion(string path, JsonElement root)
    {
        var version = root.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw Invalid(path, "needs \"asset\": { \"version\": \"2.0\" }");
        if (!version.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{path}: glTF version {version}; only glTF 2.x is supported");
        }

        if (asset.TryGetProperty("minVersion", out var min) && min.ValueKind == JsonValueKind.String && min.GetString() != "2.0")
        {
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{path}: needs glTF {min.GetString()}; the importer reads glTF 2.0");
        }
    }

    private static (byte[] Json, byte[]? Bin) SplitGlb(MeshSource source)
    {
        var bytes = source.Bytes;
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic)
        {
            throw Invalid(source.Path, "not a GLB file (missing the \"glTF\" header)");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (version != 2)
        {
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{source.Path}: GLB container version {version}; only version 2 (glTF 2.0) is supported");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        if (length != bytes.Length)
        {
            throw Invalid(source.Path, $"GLB header says {length} bytes, the file has {bytes.Length}");
        }

        byte[]? json = null;
        byte[]? bin = null;
        var offset = 12;
        for (var chunk = 0; offset < bytes.Length; chunk++)
        {
            if (bytes.Length - offset < 8)
            {
                throw Invalid(source.Path, $"GLB chunk {chunk} header is truncated");
            }

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (chunkLength > bytes.Length - offset - 8)
            {
                throw Invalid(source.Path, $"GLB chunk {chunk} ({chunkLength} bytes) runs past the end of the file");
            }

            var data = bytes.AsSpan(offset + 8, (int)chunkLength).ToArray();
            if (chunk == 0)
            {
                json = chunkType == JsonChunk ? data : throw Invalid(source.Path, "the first GLB chunk must be JSON");
            }
            else if (chunk == 1 && chunkType == BinChunk)
            {
                bin = data;
            }

            // Other chunk types are reserved; glTF readers skip them.
            offset += 8 + (int)chunkLength;
        }

        return (json ?? throw Invalid(source.Path, "GLB has no JSON chunk"), bin);
    }

    private static byte[][] LoadBuffers(MeshSource source, JsonElement root, byte[]? bin, List<(string, byte[])> resources)
    {
        if (!root.TryGetProperty("buffers", out var list))
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(source.Path, "\"buffers\" must be an array");
        }

        var buffers = new byte[list.GetArrayLength()][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var buffer = list[i];
            var name = $"buffers[{i}]";
            var byteLength = buffer.ValueKind == JsonValueKind.Object && buffer.TryGetProperty("byteLength", out var lengthElement) && lengthElement.TryGetInt32(out var l) && l >= 1
                ? l
                : throw Invalid(source.Path, $"{name} needs a positive integer \"byteLength\"");
            byte[] data;
            if (!buffer.TryGetProperty("uri", out var uriElement))
            {
                data = i == 0 && bin is not null ? bin : throw Invalid(source.Path, $"{name} has no \"uri\" and is not the GLB binary chunk");
            }
            else
            {
                var uri = uriElement.ValueKind == JsonValueKind.String ? uriElement.GetString()! : throw Invalid(source.Path, $"{name} \"uri\" must be a string");
                data = uri.StartsWith("data:", StringComparison.Ordinal) ? DecodeDataUri(source.Path, name, uri) : ReadExternal(source, name, uri, resources);
            }

            // byteLength is the logical size; the GLB binary chunk may carry up to 3 padding bytes after it.
            buffers[i] = data.Length >= byteLength ? data[..byteLength] : throw Invalid(source.Path, $"{name} declares {byteLength} bytes but holds {data.Length}");
        }

        return buffers;
    }

    private static byte[] DecodeDataUri(string path, string name, string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
        {
            throw Invalid(path, $"{name} data URI must be base64 (data:application/octet-stream;base64,…)");
        }

        try
        {
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        catch (FormatException)
        {
            throw Invalid(path, $"{name} data URI is not valid base64");
        }
    }

    private static byte[] ReadExternal(MeshSource source, string name, string uri, List<(string, byte[])> resources)
    {
        var relative = Uri.UnescapeDataString(uri);
        if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal) || relative[0] is '/' or '\\')
        {
            throw new ImportException(ImportErrorCode.UnsupportedFeature, $"{source.Path}: {name} uri \"{uri}\" must be a relative file path or a data: URI");
        }

        var label = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.Path) ?? "", relative).Replace('\\', '/');
        var bytes = source.ReadResource?.Invoke(relative) ?? throw new ImportException(ImportErrorCode.MissingFile, $"{source.Path}: {name} file {relative} does not exist");
        resources.Add((label, bytes));
        return bytes;
    }
}

/// <summary>A bounds-checked view of one accessor. Elements without a buffer view read as zeros, as glTF specifies.</summary>
internal sealed record Accessor(string Name, int Count, int ComponentType, int Components, bool Normalized, byte[]? Data, int Offset, int Stride)
{
    /// <summary>A component as float: floats as stored, normalized unsigned integers as c / max (glTF's decoding).</summary>
    public float Float(int element, int component)
    {
        if (Data is null)
        {
            return 0f;
        }

        var span = Data.AsSpan(Offset + element * Stride);
        return ComponentType switch
        {
            GltfFile.Float => BinaryPrimitives.ReadSingleLittleEndian(span[(component * 4)..]),
            GltfFile.UnsignedByte => span[component] / 255f,
            GltfFile.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(span[(component * 2)..]) / 65535f,
            _ => throw new InvalidOperationException($"[IMPORT]: {Name} component type {ComponentType} has no float decoding"),
        };
    }

    /// <summary>An index value (unsigned byte, short or int scalar).</summary>
    public uint Index(int element)
    {
        if (Data is null)
        {
            return 0;
        }

        var span = Data.AsSpan(Offset + element * Stride);
        return ComponentType switch
        {
            GltfFile.UnsignedByte => span[0],
            GltfFile.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(span),
            GltfFile.UnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(span),
            _ => throw new InvalidOperationException($"[IMPORT]: {Name} component type {ComponentType} is not an index type"),
        };
    }
}
