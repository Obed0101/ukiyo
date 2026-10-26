using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo.Import;

/// <summary>
/// glTF 2.0 → engine meshes → C# source. glTF already uses the engine's conventions (meters, right-handed, +Y up,
/// counter-clockwise front faces, linear vertex colors), so positions, normals and colors are copied as stored; the
/// only conversions are baking node transforms and the material's base color into the vertices. Anything the engine
/// cannot draw as the file describes it fails with a typed <see cref="ImportException"/> instead of being dropped.
/// Pure: the same request always produces the same bytes (see docs/assets.md, "Meshes").
/// </summary>
public static class MeshImporter
{
    /// <summary>
    /// The pinned importer version. It is written into every generated header and hashed into every content hash, so
    /// after a bump every file it generated reads as stale under <c>--check</c> until it is regenerated. Bump it whenever
    /// the same model would produce different code or data.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Names the generated class already uses or refers to; a nested mesh class with one of them would collide or
    /// shadow a type the generated code needs.
    /// </summary>
    private static readonly string[] Reserved =
    [
        "Count", "VertexCount", "TriangleCount", "ContentHash", "Names", "Create", "Build",
        "MeshData", "Vector3", "VertexPositionColor", "ReadOnlySpan", "IReadOnlyList", "ArgumentOutOfRangeException",
    ];

    public static MeshImportResult Import(MeshImportRequest request)
    {
        var className = Names.RequireIdentifier(request.ClassName, "class");
        var ns = Names.RequireNamespace(request.Namespace);
        if (request.Sources.Count == 0)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, "no inputs");
        }

        if (!Enum.IsDefined(request.Normals) || !Enum.IsDefined(request.DoubleSided))
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"unknown option normals={request.Normals} doubleSided={request.DoubleSided}");
        }

        var meshes = new List<ImportedMesh>();
        var hashes = new List<(string, string)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            var name = Names.Require(Path.GetFileNameWithoutExtension(source.Path), $"{source.Path} file name");
            var member = MemberName(name, className, source.Path);
            if (!names.Add(name) || !members.Add(member))
            {
                throw new ImportException(ImportErrorCode.DuplicateName, $"{source.Path}: a mesh named \"{name}\" (member {member}) already exists in an earlier input; rename one of the files");
            }

            using var file = GltfFile.Open(source);
            hashes.Add((source.Path, Hex(SHA256.HashData(source.Bytes))));
            hashes.AddRange(file.Resources.Select(r => (r.Path, Hex(SHA256.HashData(r.Bytes)))));
            var data = new GeometryReader(file, request).Read();
            meshes.Add(Finish(name, member, data, source.Path));
        }

        meshes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var hash = ContentHash(meshes);
        var code = MeshCodeEmitter.Emit(new MeshCodeEmitter.Input(className, ns, meshes, hash, hashes, request));
        return new MeshImportResult(meshes, hash, hashes, code);
    }

    /// <summary>File name → PascalCase member: <c>street_lamp-v2</c> → <c>StreetLampV2</c>.</summary>
    internal static string MemberName(string name, string className, string path)
    {
        var member = string.Concat(name.Split('_', '.', '-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        Names.RequireIdentifier(member, $"{path}: member name of mesh \"{name}\"");
        if (member == className || Reserved.Contains(member, StringComparer.Ordinal))
        {
            throw new ImportException(ImportErrorCode.InvalidName, $"{path}: mesh \"{name}\" would become member {member}, which the generated class {className} already uses; rename the file");
        }

        return member;
    }

    /// <summary>SHA-256 of the format version and every mesh's name and content hash, in name order.</summary>
    public static string ContentHash(IReadOnlyList<ImportedMesh> meshes)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"ukiyo-meshes/{Version}\n");
        foreach (var mesh in meshes)
        {
            text.Append(CultureInfo.InvariantCulture, $"{mesh.Name} {mesh.ContentHash}\n");
        }

        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// SHA-256 of what the game receives for one mesh: version, name, counts, then positions, colors, normals (float32
    /// little-endian) and indices (uint32 little-endian). Independent of file names, buffer layout and JSON formatting.
    /// </summary>
    public static string ContentHash(string name, MeshData mesh)
    {
        var header = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"ukiyo-mesh/{Version}\n{name} vertices {mesh.Vertices.Length} indices {mesh.Indices.Length} normals {(mesh.Normals is null ? "no" : "yes")}\n"));
        var normals = mesh.Normals ?? [];
        var data = new byte[header.Length + 4 * (6 * mesh.Vertices.Length + 3 * normals.Length + mesh.Indices.Length)];
        header.CopyTo(data, 0);
        var offset = header.Length;
        void Word(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
            offset += 4;
        }

        void Vector(Vector3 v)
        {
            Word(v.X);
            Word(v.Y);
            Word(v.Z);
        }

        foreach (var v in mesh.Vertices)
        {
            Vector(v.Position);
        }

        foreach (var v in mesh.Vertices)
        {
            Vector(v.Color);
        }

        foreach (var n in normals)
        {
            Vector(n);
        }

        foreach (var index in mesh.Indices)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), index);
            offset += 4;
        }

        return Hex(SHA256.HashData(data));
    }

    private static ImportedMesh Finish(string name, string member, MeshData data, string path)
    {
        try
        {
            // The same checks every renderer runs on CreateMesh, so an imported mesh can never fail at runtime.
            RenderValidation.Validate(data);
        }
        catch (RenderException error)
        {
            throw new ImportException(ImportErrorCode.InvalidMesh, $"{path}: {error.Message}");
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var v in data.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        return new ImportedMesh(name, member, data, min, max, ContentHash(name, data));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Walks the default scene and appends every primitive of every mesh node, transformed to scene space.</summary>
    private sealed class GeometryReader(GltfFile file, MeshImportRequest request)
    {
        private readonly List<Vector3> _positions = [];
        private readonly List<Vector3> _colors = [];
        private readonly List<Vector3> _normals = [];
        private readonly List<uint> _indices = [];
        private readonly HashSet<int> _visited = [];

        public MeshData Read()
        {
            var root = file.Root;
            int sceneIndex;
            if (root.TryGetProperty("scene", out _))
            {
                sceneIndex = file.RequiredInt(root, "scene", "the file");
            }
            else if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array && scenes.GetArrayLength() > 0)
            {
                sceneIndex = 0;
            }
            else
            {
                throw file.Invalid("has no scene; the importer imports what the default scene shows");
            }

            var scene = file.Item("scenes", sceneIndex, "\"scene\"");
            if (scene.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in Indices(nodes, $"scenes[{sceneIndex}].nodes"))
                {
                    Visit(node, Affine.Identity);
                }
            }

            if (_indices.Count == 0)
            {
                throw file.Invalid($"scenes[{sceneIndex}] contains no triangles");
            }

            if (request.Normals == NormalsMode.Flat)
            {
                return Flat();
            }

            return new MeshData([.. _positions.Select((p, i) => new VertexPositionColor(p, _colors[i]))], [.. _indices])
            {
                Normals = request.Normals == NormalsMode.Required ? [.. _normals] : null,
            };
        }

        private void Visit(int index, Affine parent)
        {
            var where = $"nodes[{index}]";
            var node = file.Item("nodes", index, "the scene graph");
            if (!_visited.Add(index))
            {
                throw file.Invalid($"{where} appears twice in the scene graph; glTF nodes must form a tree");
            }

            GltfFile.RejectExtensions(node, $"{file.Path}: {where}");
            if (node.TryGetProperty("skin", out _) || node.TryGetProperty("weights", out _))
            {
                throw Unsupported($"{where} is skinned or has morph weights; the engine draws static meshes only (apply the pose before exporting)");
            }

            var world = Local(node, where).Then(parent);
            if (node.TryGetProperty("mesh", out _))
            {
                AddMesh(file.RequiredInt(node, "mesh", where), world);
            }

            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in Indices(children, $"{where}.children"))
                {
                    Visit(child, world);
                }
            }
        }

        /// <summary>Node transform in row-vector form (v' = v · M): a column-major glTF "matrix", or scale · rotation · translation.</summary>
        private Affine Local(JsonElement node, string where)
        {
            var hasTrs = node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _);
            if (node.TryGetProperty("matrix", out _))
            {
                if (hasTrs)
                {
                    throw file.Invalid($"{where} has both \"matrix\" and translation/rotation/scale");
                }

                // Column-major column-vector matrix = row-major row-vector matrix: row r is m[4r..4r+3].
                var m = file.Numbers(node, "matrix", 16, where, []);
                if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
                {
                    throw file.Invalid($"{where} matrix is not an affine transform (its last row must be 0, 0, 0, 1)");
                }

                return new Affine(m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14]);
            }

            var t = file.Numbers(node, "translation", 3, where, [0, 0, 0]);
            var q = file.Numbers(node, "rotation", 4, where, [0, 0, 0, 1]);
            var s = file.Numbers(node, "scale", 3, where, [1, 1, 1]);
            var length = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (!(length > 0))
            {
                throw file.Invalid($"{where} rotation is a zero quaternion");
            }

            var (x, y, z, w) = (q[0] / length, q[1] / length, q[2] / length, q[3] / length);

            // System.Numerics' Matrix4x4.CreateFromQuaternion, in double; each row scaled by its axis' scale.
            return new Affine(
                s[0] * (1 - 2 * (y * y + z * z)), s[0] * 2 * (x * y + z * w), s[0] * 2 * (x * z - y * w),
                s[1] * 2 * (x * y - z * w), s[1] * (1 - 2 * (x * x + z * z)), s[1] * 2 * (y * z + x * w),
                s[2] * 2 * (x * z + y * w), s[2] * 2 * (y * z - x * w), s[2] * (1 - 2 * (x * x + y * y)),
                t[0], t[1], t[2]);
        }

        private void AddMesh(int meshIndex, Affine world)
        {
            var where = $"meshes[{meshIndex}]";
            var mesh = file.Item("meshes", meshIndex, "a node");
            GltfFile.RejectExtensions(mesh, $"{file.Path}: {where}");
            if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.ValueKind != JsonValueKind.Array || primitives.GetArrayLength() == 0)
            {
                throw file.Invalid($"{where} needs a non-empty \"primitives\" array");
            }

            var determinant = world.Determinant;
            if (determinant == 0 || !double.IsFinite(determinant))
            {
                throw file.Invalid($"{where} is placed by a singular transform (a zero scale); it has no visible surface");
            }

            for (var p = 0; p < primitives.GetArrayLength(); p++)
            {
                AddPrimitive(primitives[p], $"{where}.primitives[{p}]", world, determinant);
            }
        }

        private void AddPrimitive(JsonElement primitive, string where, Affine world, double determinant)
        {
            GltfFile.RejectExtensions(primitive, $"{file.Path}: {where}");
            var mode = file.OptionalInt(primitive, "mode", where, 4);
            if (mode is >= 0 and <= 3)
            {
                throw Unsupported($"{where} draws {(mode == 0 ? "points" : "lines")} (mode {mode}); the engine draws triangles only");
            }

            if (mode is not (4 or 5 or 6))
            {
                throw file.Invalid($"{where} has unknown mode {mode}");
            }

            if (primitive.TryGetProperty("targets", out _))
            {
                throw Unsupported($"{where} has morph targets; the engine draws static meshes only");
            }

            if (!primitive.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            {
                throw file.Invalid($"{where} needs an \"attributes\" object");
            }

            foreach (var attribute in attributes.EnumerateObject())
            {
                if (attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                {
                    throw Unsupported($"{where} has skinning attribute {attribute.Name}; the engine draws static meshes only");
                }
            }

            var positions = Attribute(attributes, "POSITION", where) ?? throw file.Invalid($"{where} has no POSITION attribute");
            Require(positions.Components == 3 && positions.ComponentType == GltfFile.Float, positions, "must be VEC3 float");
            var count = positions.Count;
            Accessor? normals = null;
            if (request.Normals == NormalsMode.Required)
            {
                normals = Attribute(attributes, "NORMAL", where) ?? throw new ImportException(ImportErrorCode.MissingNormals,
                    $"{file.Path}: {where} has no NORMAL attribute. Export with normals, or set \"normals\": \"flat\" (generate face normals) or \"none\" (unlit mesh) in the import spec");
                Require(normals.Components == 3 && normals.ComponentType == GltfFile.Float && normals.Count == count, normals, $"must be VEC3 float with {count} elements, like POSITION");
            }

            var colors = Attribute(attributes, "COLOR_0", where);
            if (colors is not null)
            {
                var encoded = colors.ComponentType == GltfFile.Float || (colors.ComponentType is GltfFile.UnsignedByte or GltfFile.UnsignedShort && colors.Normalized);
                Require(colors.Components is 3 or 4 && encoded && colors.Count == count, colors, $"must be VEC3/VEC4 of float or normalized unsigned byte/short with {count} elements");
            }

            var baseColor = Material(primitive, where);
            var flip = determinant < 0;
            var identity = world.IsIdentity;
            var cofactor = world.Cofactor(Math.Sign(determinant));
            var start = _positions.Count;
            if ((long)start + count > RenderValidation.MaxVertices)
            {
                throw new ImportException(ImportErrorCode.MeshTooLarge, $"{file.Path}: more than {RenderValidation.MaxVertices} vertices (protocol limit); split the model");
            }

            for (var i = 0; i < count; i++)
            {
                var position = new Vector3(positions.Float(i, 0), positions.Float(i, 1), positions.Float(i, 2));
                RequireFinite(position, positions, i);
                _positions.Add(identity ? position : world.Point(position));
                _colors.Add(VertexColor(colors, i) * baseColor);
                if (normals is not null)
                {
                    var normal = new Vector3(normals.Float(i, 0), normals.Float(i, 1), normals.Float(i, 2));
                    RequireFinite(normal, normals, i);
                    if (normal == Vector3.Zero)
                    {
                        throw file.Invalid($"{normals.Name} vertex {i} has a zero normal");
                    }

                    _normals.Add(identity ? normal : cofactor.Normal(normal));
                }
            }

            var triangles = Triangles(primitive, where, mode, count);
            if ((long)_indices.Count + triangles.Count > RenderValidation.MaxIndices)
            {
                throw new ImportException(ImportErrorCode.MeshTooLarge, $"{file.Path}: more than {RenderValidation.MaxIndices} indices (protocol limit); split the model");
            }

            for (var i = 0; i < triangles.Count; i += 3)
            {
                // A mirroring transform (negative determinant) turns counter-clockwise faces clockwise; glTF says to flip them back.
                _indices.Add((uint)start + triangles[i]);
                _indices.Add((uint)start + triangles[flip ? i + 2 : i + 1]);
                _indices.Add((uint)start + triangles[flip ? i + 1 : i + 2]);
            }
        }

        /// <summary>Triangle list in the primitive's own vertex numbering; strips and fans are unrolled with glTF's winding rules.</summary>
        private List<uint> Triangles(JsonElement primitive, string where, int mode, int vertexCount)
        {
            uint[] source;
            if (primitive.TryGetProperty("indices", out _))
            {
                var accessor = file.Accessor(file.RequiredInt(primitive, "indices", where), $"{where} indices");
                Require(accessor.Components == 1 && accessor.ComponentType is GltfFile.UnsignedByte or GltfFile.UnsignedShort or GltfFile.UnsignedInt && !accessor.Normalized, accessor, "must be SCALAR unsigned byte, short or int");
                source = new uint[accessor.Count];
                for (var i = 0; i < source.Length; i++)
                {
                    source[i] = accessor.Index(i) < vertexCount ? accessor.Index(i) : throw file.Invalid($"{accessor.Name} index {i} is {accessor.Index(i)}, but the primitive has {vertexCount} vertices");
                }
            }
            else
            {
                source = [.. Enumerable.Range(0, vertexCount).Select(i => (uint)i)];
            }

            var result = new List<uint>();
            switch (mode)
            {
                case 4:
                    if (source.Length % 3 != 0)
                    {
                        throw file.Invalid($"{where} has {source.Length} indices, not a multiple of 3");
                    }

                    result.AddRange(source);
                    break;
                case 5:
                    // Strip: triangle i is (i, i+1+i%2, i+2-i%2), so every triangle keeps the first one's orientation.
                    for (var i = 0; i + 2 < source.Length; i++)
                    {
                        result.AddRange([source[i], source[i + 1 + i % 2], source[i + 2 - i % 2]]);
                    }

                    break;
                default:
                    // Fan: triangle i is (i+1, i+2, 0).
                    for (var i = 0; i + 2 < source.Length; i++)
                    {
                        result.AddRange([source[i + 1], source[i + 2], source[0]]);
                    }

                    break;
            }

            return result.Count > 0 ? result : throw file.Invalid($"{where} has fewer than 3 vertices for a triangle");
        }

        /// <summary>The material's linear base color, baked into the vertex colors; everything it cannot represent fails.</summary>
        private Vector3 Material(JsonElement primitive, string where)
        {
            if (!primitive.TryGetProperty("material", out _))
            {
                return Vector3.One;
            }

            var index = file.RequiredInt(primitive, "material", where);
            var material = file.Item("materials", index, where);
            var name = $"materials[{index}]{(material.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? $" \"{n.GetString()}\"" : "")}";
            foreach (var texture in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            {
                if (material.TryGetProperty(texture, out _))
                {
                    throw Unsupported($"{name} uses {texture}; engine meshes have no textures (remove the texture or bake it into vertex colors)");
                }
            }

            var factor = new double[] { 1, 1, 1, 1 };
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                foreach (var texture in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                {
                    if (pbr.TryGetProperty(texture, out _))
                    {
                        throw Unsupported($"{name} uses {texture}; engine meshes have no textures (remove the texture or bake it into vertex colors)");
                    }
                }

                factor = file.Numbers(pbr, "baseColorFactor", 4, name, factor);
            }

            var emissive = file.Numbers(material, "emissiveFactor", 3, name, [0, 0, 0]);
            if (emissive.Any(e => e != 0))
            {
                throw Unsupported($"{name} is emissive; engine materials have no emission (use an unlit material in game code instead)");
            }

            var alphaMode = material.TryGetProperty("alphaMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : "OPAQUE";
            if (alphaMode != "OPAQUE" || factor[3] != 1)
            {
                throw Unsupported($"{name} is not opaque (alphaMode {alphaMode}, base alpha {factor[3].ToString("R", CultureInfo.InvariantCulture)}); engine meshes are opaque");
            }

            if (material.TryGetProperty("doubleSided", out var doubleSided) && doubleSided.ValueKind == JsonValueKind.True && request.DoubleSided == DoubleSidedMode.Reject)
            {
                throw Unsupported($"{name} is double-sided, but the engine culls back faces. Turn on backface culling in the exporter, or set \"doubleSided\": \"cull\" in the import spec to import its faces once");
            }

            return new Vector3((float)factor[0], (float)factor[1], (float)factor[2]);
        }

        /// <summary>Unwelds every triangle into three vertices that share its face normal (normals mode "flat").</summary>
        private MeshData Flat()
        {
            if (_indices.Count > RenderValidation.MaxVertices)
            {
                throw new ImportException(ImportErrorCode.MeshTooLarge, $"{file.Path}: flat normals need one vertex per triangle corner, {_indices.Count} > {RenderValidation.MaxVertices}; split the model");
            }

            var vertices = new VertexPositionColor[_indices.Count];
            var normals = new Vector3[_indices.Count];
            for (var i = 0; i < _indices.Count; i += 3)
            {
                var (a, b, c) = (_positions[(int)_indices[i]], _positions[(int)_indices[i + 1]], _positions[(int)_indices[i + 2]]);
                var normal = Affine.FaceNormal(a, b, c) ?? throw file.Invalid($"triangle {i / 3} has zero area, so it has no face normal");
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = (int)_indices[i + corner];
                    vertices[i + corner] = new VertexPositionColor(_positions[index], _colors[index]);
                    normals[i + corner] = normal;
                }
            }

            return new MeshData(vertices, [.. Enumerable.Range(0, vertices.Length).Select(i => (uint)i)]) { Normals = normals };
        }

        /// <summary>COLOR_0 as linear RGB (white without it). Alpha below 1 cannot be represented and fails.</summary>
        private Vector3 VertexColor(Accessor? colors, int vertex)
        {
            if (colors is null)
            {
                return Vector3.One;
            }

            var color = new Vector3(colors.Float(vertex, 0), colors.Float(vertex, 1), colors.Float(vertex, 2));
            RequireFinite(color, colors, vertex);
            if (colors.Components == 4 && colors.Float(vertex, 3) != 1f)
            {
                throw Unsupported($"{colors.Name} vertex {vertex} has alpha {colors.Float(vertex, 3).ToString("R", CultureInfo.InvariantCulture)}; engine meshes are opaque (vertex colors are RGB)");
            }

            return color;
        }

        private Accessor? Attribute(JsonElement attributes, string semantic, string where) =>
            attributes.TryGetProperty(semantic, out _) ? file.Accessor(file.RequiredInt(attributes, semantic, where), $"{where} {semantic}") : null;

        private IEnumerable<int> Indices(JsonElement list, string where)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                throw file.Invalid($"{where} must be an array of node indices");
            }

            return list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : throw file.Invalid($"{where} must contain integers")).ToList();
        }

        private void Require(bool condition, Accessor accessor, string rule)
        {
            if (!condition)
            {
                throw file.Invalid($"{accessor.Name} {rule}");
            }
        }

        private void RequireFinite(Vector3 value, Accessor accessor, int vertex)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw file.Invalid($"{accessor.Name} vertex {vertex} is not finite: {value}");
            }
        }

        private ImportException Unsupported(string message) => new(ImportErrorCode.UnsupportedFeature, $"{file.Path}: {message}");
    }
}

/// <summary>
/// Affine transform in row-vector form (p' = p · L + t), in double so baked vertices round to float only once and the
/// result does not depend on SIMD width or operation fusing of the machine that runs the importer.
/// </summary>
internal readonly record struct Affine(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33,
    double Tx, double Ty, double Tz)
{
    public static Affine Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);

    public bool IsIdentity => this == Identity;

    public double Determinant => M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);

    /// <summary>This transform followed by <paramref name="parent"/> (row vectors: child · parent).</summary>
    public Affine Then(Affine p) => new(
        M11 * p.M11 + M12 * p.M21 + M13 * p.M31, M11 * p.M12 + M12 * p.M22 + M13 * p.M32, M11 * p.M13 + M12 * p.M23 + M13 * p.M33,
        M21 * p.M11 + M22 * p.M21 + M23 * p.M31, M21 * p.M12 + M22 * p.M22 + M23 * p.M32, M21 * p.M13 + M22 * p.M23 + M23 * p.M33,
        M31 * p.M11 + M32 * p.M21 + M33 * p.M31, M31 * p.M12 + M32 * p.M22 + M33 * p.M32, M31 * p.M13 + M32 * p.M23 + M33 * p.M33,
        Tx * p.M11 + Ty * p.M21 + Tz * p.M31 + p.Tx, Tx * p.M12 + Ty * p.M22 + Tz * p.M32 + p.Ty, Tx * p.M13 + Ty * p.M23 + Tz * p.M33 + p.Tz);

    public Vector3 Point(Vector3 v) => new(
        (float)(v.X * M11 + v.Y * M21 + v.Z * M31 + Tx),
        (float)(v.X * M12 + v.Y * M22 + v.Z * M32 + Ty),
        (float)(v.X * M13 + v.Y * M23 + v.Z * M33 + Tz));

    /// <summary>
    /// The normal matrix, transpose(inverse(L)), up to a positive factor: the cofactor matrix times the sign of the
    /// determinant. <see cref="Normal"/> normalizes, so the factor does not matter.
    /// </summary>
    public Affine Cofactor(int sign) => new(
        sign * (M22 * M33 - M23 * M32), sign * (M23 * M31 - M21 * M33), sign * (M21 * M32 - M22 * M31),
        sign * (M13 * M32 - M12 * M33), sign * (M11 * M33 - M13 * M31), sign * (M12 * M31 - M11 * M32),
        sign * (M12 * M23 - M13 * M22), sign * (M13 * M21 - M11 * M23), sign * (M11 * M22 - M12 * M21),
        0, 0, 0);

    /// <summary>A normal through this (normal) matrix, as a row vector, renormalized to unit length.</summary>
    public Vector3 Normal(Vector3 n)
    {
        var (x, y, z) = (n.X * M11 + n.Y * M21 + n.Z * M31, n.X * M12 + n.Y * M22 + n.Z * M32, n.X * M13 + n.Y * M23 + n.Z * M33);
        var length = Math.Sqrt(x * x + y * y + z * z);
        return new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
    }

    /// <summary>Unit normal of a counter-clockwise triangle, (b − a) × (c − a), or null when it has zero area.</summary>
    public static Vector3? FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
        double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
        var (x, y, z) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length > 0 ? new Vector3((float)(x / length), (float)(y / length), (float)(z / length)) : null;
    }
}
