using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Ukiyo.Import;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.ImportedMesh;
using Xunit;

namespace Ukiyo.Tests;

public sealed class MeshImportTests
{
    private static readonly string ArtDir = Path.Combine(CoreTests.RepoRoot(), "samples", "ImportedMesh", "art");

    /// <summary>
    /// A glTF 2.0 model written by the test: accessors are packed into one buffer, embedded as a data URI (.gltf) or
    /// as the binary chunk (.glb). The geometry is small and hand-chosen so every expected value can be stated.
    /// </summary>
    private sealed record Model(float[] Positions)
    {
        public float[]? Normals { get; init; }
        public float[]? Colors { get; init; }
        public byte[]? ColorBytes { get; init; }
        public uint[]? Indices { get; init; }
        public string Node { get; init; } = "";
        public string Primitive { get; init; } = "";
        public string? Material { get; init; }
        public string Root { get; init; } = "";

        public (string Json, byte[] Bin) Build(string? bufferUri)
        {
            var bin = new List<byte>();
            var views = new List<string>();
            var accessors = new List<string>();
            var attributes = new List<string>();
            int Add(byte[] data, string accessor)
            {
                views.Add($$"""{"buffer":0,"byteOffset":{{bin.Count}},"byteLength":{{data.Length}}}""");
                bin.AddRange(data);
                while (bin.Count % 4 != 0)
                {
                    bin.Add(0);
                }

                accessors.Add(accessor.Replace("VIEW", (views.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
                return accessors.Count - 1;
            }

            attributes.Add($"\"POSITION\":{Add(FloatBytes(Positions), $$"""{"bufferView":VIEW,"componentType":5126,"count":{{Positions.Length / 3}},"type":"VEC3"}""")}");
            if (Normals is not null)
            {
                attributes.Add($"\"NORMAL\":{Add(FloatBytes(Normals), $$"""{"bufferView":VIEW,"componentType":5126,"count":{{Normals.Length / 3}},"type":"VEC3"}""")}");
            }

            if (Colors is not null)
            {
                attributes.Add($"\"COLOR_0\":{Add(FloatBytes(Colors), $$"""{"bufferView":VIEW,"componentType":5126,"count":{{Colors.Length / 3}},"type":"VEC3"}""")}");
            }

            if (ColorBytes is not null)
            {
                attributes.Add($"\"COLOR_0\":{Add(ColorBytes, $$"""{"bufferView":VIEW,"componentType":5121,"normalized":true,"count":{{ColorBytes.Length / 4}},"type":"VEC4"}""")}");
            }

            var indices = Indices is null ? "" : $"\"indices\":{Add([.. Indices.SelectMany(i => BitConverter.GetBytes(i))], $$"""{"bufferView":VIEW,"componentType":5125,"count":{{Indices.Length}},"type":"SCALAR"}""")},";
            var material = Material is null ? "" : "\"material\":0,";
            var materials = Material is null ? "" : $",\"materials\":[{Material}]";
            var uri = bufferUri is null ? "" : $",\"uri\":\"{bufferUri}\"";
            var json = "{\"asset\":{\"version\":\"2.0\"}," + Root + "\"scene\":0,\"scenes\":[{\"nodes\":[0]}],"
                + "\"nodes\":[{" + Node + "\"mesh\":0}],"
                + "\"meshes\":[{\"primitives\":[{" + indices + material + Primitive + "\"attributes\":{" + string.Join(",", attributes) + "}}]}],"
                + "\"accessors\":[" + string.Join(",", accessors) + "],\"bufferViews\":[" + string.Join(",", views) + "],"
                + "\"buffers\":[{\"byteLength\":" + bin.Count + uri + "}]" + materials + "}";
            return (json, [.. bin]);
        }

        public MeshSource Gltf(string path = "model.gltf")
        {
            var bin = Build(null).Bin;
            var json = Build("data:application/octet-stream;base64," + Convert.ToBase64String(bin)).Json;
            return new MeshSource(path, Encoding.UTF8.GetBytes(json));
        }

        public MeshSource Glb(string path = "model.glb")
        {
            var (json, bin) = Build(null);
            return new MeshSource(path, GlbBytes(json, bin));
        }
    }

    private static byte[] FloatBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static byte[] GlbBytes(string json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        while (jsonBytes.Count % 4 != 0)
        {
            jsonBytes.Add((byte)' ');
        }

        var total = 12 + 8 + jsonBytes.Count + 8 + bin.Length;
        var glb = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonBytes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        jsonBytes.CopyTo(glb, 20);
        var binAt = 20 + jsonBytes.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt + 4), 0x004E4942);
        bin.CopyTo(glb, binAt + 8);
        return glb;
    }

    /// <summary>One counter-clockwise triangle seen from +Z, entirely right of the origin (x 0.2..0.9), facing +Z.</summary>
    private static Model RightTriangle => new([0.2f, -0.5f, 0f, 0.9f, -0.5f, 0f, 0.2f, 0.5f, 0f])
    {
        Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
        Colors = [1, 0, 0, 0, 1, 0, 0, 0, 1],
    };

    private static ImportedMesh ImportOne(MeshSource source, NormalsMode normals = NormalsMode.Required, DoubleSidedMode doubleSided = DoubleSidedMode.Reject) =>
        Assert.Single(MeshImporter.Import(new MeshImportRequest("TestMeshes", "Ukiyo.Tests.Gen", [source]) { Normals = normals, DoubleSided = doubleSided }).Meshes);

    private static ImportErrorCode Code(Action action) => Assert.Throws<ImportException>(action).Code;

    private static void AssertSameMesh(MeshData expected, MeshData actual)
    {
        Assert.Equal(expected.Vertices, actual.Vertices);
        Assert.Equal(expected.Indices, actual.Indices);
        Assert.Equal(expected.Normals, actual.Normals);
    }

    [Fact]
    public void The_compiled_sample_meshes_round_trip_bit_for_bit_to_a_fresh_import_of_the_blender_exports()
    {
        // SceneMeshes.g.cs is compiled into this assembly: its float literals must come back as exactly the bits the
        // importer produced from the .gltf/.bin and .glb files on disk.
        var spec = MeshSpec.Load(Path.Combine(ArtDir, "meshes.import.json"));
        var result = MeshImporter.Import(spec.Request);
        Assert.Equal(["pedestal", "suzanne"], result.Meshes.Select(m => m.Name));
        foreach (var mesh in result.Meshes)
        {
            var compiled = SceneMeshes.Create(mesh.Name);
            AssertSameMesh(mesh.Data, compiled);
            Assert.Equal(mesh.ContentHash, MeshImporter.ContentHash(mesh.Name, compiled));
        }

        Assert.Equal(SceneMeshes.ContentHash, result.ContentHash);
        Assert.Equal((2, 555, 996), (SceneMeshes.Count, SceneMeshes.VertexCount, SceneMeshes.TriangleCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneMeshes.Create("missing"));
    }

    [Fact]
    public void Suzanne_keeps_the_exact_positions_normals_colors_and_indices_stored_in_her_bin_file()
    {
        // An independent reading of suzanne.bin through the accessor table: no node transform, so values are copied.
        using var gltf = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ArtDir, "suzanne.gltf")));
        var bin = File.ReadAllBytes(Path.Combine(ArtDir, "suzanne.bin"));
        var root = gltf.RootElement;
        var attributes = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
        int ViewOffset(int accessor) => root.GetProperty("bufferViews")[root.GetProperty("accessors")[accessor].GetProperty("bufferView").GetInt32()].TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        float F(int accessor, int i) => BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(ViewOffset(accessor) + i * 4));
        var (position, normal, color) = (attributes.GetProperty("POSITION").GetInt32(), attributes.GetProperty("NORMAL").GetInt32(), attributes.GetProperty("COLOR_0").GetInt32());
        var indices = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32();
        Assert.Equal(5123, root.GetProperty("accessors")[color].GetProperty("componentType").GetInt32());

        var mesh = SceneMeshes.Suzanne.Create();
        Assert.Equal(507, mesh.Vertices.Length);
        for (var v = 0; v < mesh.Vertices.Length; v++)
        {
            Assert.Equal(new Vector3(F(position, 3 * v), F(position, 3 * v + 1), F(position, 3 * v + 2)), mesh.Vertices[v].Position);
            Assert.Equal(new Vector3(F(normal, 3 * v), F(normal, 3 * v + 1), F(normal, 3 * v + 2)), mesh.Normals![v]);
            var c = bin.AsSpan(ViewOffset(color) + v * 8);
            Assert.Equal(new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(c) / 65535f, BinaryPrimitives.ReadUInt16LittleEndian(c[2..]) / 65535f, BinaryPrimitives.ReadUInt16LittleEndian(c[4..]) / 65535f), mesh.Vertices[v].Color);
        }

        Assert.Equal(Enumerable.Range(0, 968 * 3).Select(i => (uint)BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan(ViewOffset(indices) + 2 * i))), mesh.Indices);
    }

    [Fact]
    public void The_pedestal_node_transform_and_material_color_are_baked_into_its_vertices()
    {
        // pedestal.glb: translation y -0.12, rotation 22.5° about +Y, scale y 0.8, base color (0.55, 0.52, 0.48).
        var mesh = SceneMeshes.Pedestal.Create();
        Assert.All(mesh.Vertices, v => Assert.Equal(new Vector3(0.55f, 0.52f, 0.48f), v.Color));
        Assert.Equal(-0.24f, SceneMeshes.Pedestal.BoundsMin.Y, 5);
        Assert.Equal(0f, SceneMeshes.Pedestal.BoundsMax.Y, 5);
        Assert.All(mesh.Normals!, n => Assert.Equal(1f, n.Length(), 5));
        // Top cap normals point straight up; side normals stay horizontal (the y scale does not tilt them).
        Assert.Contains(mesh.Normals!, n => Vector3.Distance(n, Vector3.UnitY) < 1e-6f);
        Assert.All(mesh.Normals!.Where(n => MathF.Abs(n.Y) < 0.5f), n => Assert.Equal(0f, n.Y, 6));
    }

    [Fact]
    public void Importing_twice_is_byte_identical_matches_the_checked_in_file_and_pins_the_importer_version()
    {
        var spec = MeshSpec.Load(Path.Combine(ArtDir, "meshes.import.json"));
        var first = MeshImporter.Import(spec.Request);
        var second = MeshImporter.Import(MeshSpec.Load(Path.Combine(ArtDir, "meshes.import.json")).Request);

        Assert.Equal(first.Code, second.Code);
        Assert.DoesNotContain(CoreTests.RepoRoot(), first.Code, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first.Code);
        Assert.Equal(File.ReadAllText(spec.OutputPath), first.Code);
        Assert.StartsWith($"// <auto-generated>\n//   ukiyo-import mesh v{MeshImporter.Version} (glTF 2.0).", first.Code, StringComparison.Ordinal);
        Assert.Contains("//   Options: normals=required doubleSided=reject\n", first.Code, StringComparison.Ordinal);
        Assert.Equal(["suzanne.gltf", "suzanne.bin", "pedestal.glb"], first.SourceHashes.Select(s => s.Path));
        Assert.Contains($"suzanne.bin   {first.SourceHashes[1].Sha256}", first.Code, StringComparison.Ordinal);
        Assert.True(MeshSpec.Describes(Path.Combine(ArtDir, "meshes.import.json")));
        Assert.False(MeshSpec.Describes(Path.Combine(CoreTests.RepoRoot(), "samples", "TexturedQuad", "art", "quad.import.json")));
    }

    [Fact]
    public void Gltf_with_a_data_uri_glb_and_external_bin_give_the_same_mesh()
    {
        var model = RightTriangle with { Indices = [0, 1, 2] };
        var (json, bin) = model.Build("tri.bin");
        var external = new MeshSource("tri.gltf", Encoding.UTF8.GetBytes(json), uri => uri == "tri.bin" ? bin : null);

        var fromData = ImportOne(model.Gltf("tri.gltf"));
        var fromGlb = ImportOne(model.Glb("tri.glb"));
        var fromFile = ImportOne(external);
        AssertSameMesh(fromData.Data, fromGlb.Data);
        AssertSameMesh(fromData.Data, fromFile.Data);
        // The content hash covers name and data, not the container: all three are the same mesh to the game.
        Assert.Equal(fromData.ContentHash, fromGlb.ContentHash);
        Assert.Equal(fromData.ContentHash, fromFile.ContentHash);
        Assert.Equal(ImportErrorCode.MissingFile, Code(() => ImportOne(new MeshSource("tri.gltf", Encoding.UTF8.GetBytes(json), _ => null))));
    }

    [Fact]
    public void Positions_winding_normals_and_colors_are_copied_in_engine_conventions()
    {
        var mesh = ImportOne(RightTriangle.Gltf()).Data;
        Assert.Equal([new(new Vector3(0.2f, -0.5f, 0f), Vector3.UnitX), new(new Vector3(0.9f, -0.5f, 0f), Vector3.UnitY), new(new Vector3(0.2f, 0.5f, 0f), Vector3.UnitZ)], mesh.Vertices);
        Assert.Equal([0u, 1u, 2u], mesh.Indices);
        Assert.Equal([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], mesh.Normals!);
    }

    [Fact]
    public async Task An_imported_triangle_is_drawn_on_the_right_from_the_front_and_culled_from_behind()
    {
        // Handedness and winding end to end: glTF +X must land on the right of a camera at +Z, and the CCW face must
        // be culled from behind. A mirrored axis or a flipped winding would move or hide the triangle.
        var mesh = ImportOne(RightTriangle.Gltf()).Data;
        var front = await Render(mesh, new Vector3(0, 0, 3));
        Assert.NotEqual(Clear, Pixel(front, 0.4f, -0.2f));
        Assert.Equal(Clear, Pixel(front, -0.4f, -0.2f));
        var behind = await Render(mesh, new Vector3(0, 0, -3));
        Assert.All(Enumerable.Range(0, Size * Size), i => Assert.Equal(Clear, behind.Rgba.AsSpan(i * 4, 3).ToArray()));
    }

    [Fact]
    public async Task A_mirroring_node_transform_flips_the_winding_back_so_the_face_stays_visible()
    {
        var mirrored = ImportOne((RightTriangle with { Node = "\"scale\":[-1,1,1]," }).Gltf()).Data;
        Assert.Equal(new Vector3(-0.2f, -0.5f, 0f), mirrored.Vertices[0].Position);
        Assert.Equal([0u, 2u, 1u], mirrored.Indices);
        Assert.Equal([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], mirrored.Normals!);
        var front = await Render(mirrored, new Vector3(0, 0, 3));
        Assert.NotEqual(Clear, Pixel(front, -0.4f, -0.2f));
        Assert.Equal(Clear, Pixel(front, 0.4f, -0.2f));
    }

    [Fact]
    public void Node_transforms_compose_parent_after_child_and_normals_use_the_inverse_transpose()
    {
        // Parent translates by (1, 2, 3); child scales x by 2 and rotates 90° about +Z (quaternion z = sin 45°).
        var s = MathF.Sqrt(0.5f);
        var model = new Model([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f])
        {
            Normals = [1, 1, 0, 1, 1, 0, 1, 1, 0],
        };
        var (json, _) = model.Build("data:application/octet-stream;base64," + Convert.ToBase64String(model.Build(null).Bin));
        var withParent = json
            .Replace("\"scenes\":[{\"nodes\":[0]}]", "\"scenes\":[{\"nodes\":[1]}]", StringComparison.Ordinal)
            .Replace("\"nodes\":[{\"mesh\":0}]", $"\"nodes\":[{{\"mesh\":0,\"scale\":[2,1,1],\"rotation\":[0,0,{s},{s}]}},{{\"translation\":[1,2,3],\"children\":[0]}}]", StringComparison.Ordinal);
        var mesh = ImportOne(new MeshSource("tree.gltf", Encoding.UTF8.GetBytes(withParent))).Data;

        // Row vectors: p · S · R · T. (1,0,0) → (2,0,0) → (0,2,0) → (1,4,3); (0,1,0) → (0,1,0) → (−1,0,0) → (0,2,3).
        AssertNear(new Vector3(1, 4, 3), mesh.Vertices[0].Position);
        AssertNear(new Vector3(0, 2, 3), mesh.Vertices[1].Position);
        AssertNear(new Vector3(1, 2, 4), mesh.Vertices[2].Position);

        // Normal (1,1,0) through inverse-transpose of S: (0.5, 1, 0), then R: (−1, 0.5, 0), normalized.
        AssertNear(Vector3.Normalize(new Vector3(-1f, 0.5f, 0f)), mesh.Normals![0]);
    }

    [Fact]
    public void Triangle_strips_and_fans_are_unrolled_keeping_counter_clockwise_winding()
    {
        // A unit quad as a strip (0,1,2,3 zig-zag) and as a fan around vertex 0; both must face +Z.
        float[] quad = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0];
        var strip = ImportOne((new Model(quad) { Primitive = "\"mode\":5,", Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1] }).Gltf()).Data;
        Assert.Equal([0u, 1u, 2u, 1u, 3u, 2u], strip.Indices);
        float[] fanQuad = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0];
        var fan = ImportOne((new Model(fanQuad) { Primitive = "\"mode\":6,", Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1] }).Gltf()).Data;
        Assert.Equal([1u, 2u, 0u, 2u, 3u, 0u], fan.Indices);
        foreach (var mesh in new[] { strip, fan })
        {
            for (var i = 0; i < mesh.Indices.Length; i += 3)
            {
                var (a, b, c) = (mesh.Vertices[mesh.Indices[i]].Position, mesh.Vertices[mesh.Indices[i + 1]].Position, mesh.Vertices[mesh.Indices[i + 2]].Position);
                Assert.True(Vector3.Cross(b - a, c - a).Z > 0, $"triangle {i / 3} is clockwise");
            }
        }
    }

    [Fact]
    public void Normalized_byte_vertex_colors_are_decoded_and_multiplied_by_the_base_color()
    {
        var model = RightTriangle with
        {
            Colors = null,
            ColorBytes = [255, 51, 0, 255, 0, 255, 102, 255, 255, 255, 255, 255],
            Material = """{"pbrMetallicRoughness":{"baseColorFactor":[0.5,1,0.25,1],"metallicFactor":0}}""",
        };
        var mesh = ImportOne(model.Gltf()).Data;
        Assert.Equal(new Vector3(1f * 0.5f, 51 / 255f, 0f), mesh.Vertices[0].Color);
        Assert.Equal(new Vector3(0f, 1f, 102 / 255f * 0.25f), mesh.Vertices[1].Color);
        Assert.Equal(new Vector3(0.5f, 1f, 0.25f), mesh.Vertices[2].Color);
    }

    [Fact]
    public void Normals_are_required_by_default_and_can_be_generated_flat_or_dropped_on_request()
    {
        var bare = RightTriangle with { Normals = null };
        var missing = Assert.Throws<ImportException>(() => ImportOne(bare.Gltf()));
        Assert.Equal(ImportErrorCode.MissingNormals, missing.Code);
        Assert.Contains("\"normals\": \"flat\"", missing.Message, StringComparison.Ordinal);

        var flat = ImportOne((bare with { Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0], Colors = null, Indices = [0, 1, 2, 2, 1, 3] }).Gltf(), NormalsMode.Flat).Data;
        Assert.Equal(6, flat.Vertices.Length);
        Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u], flat.Indices);
        Assert.All(flat.Normals!, n => Assert.Equal(Vector3.UnitZ, n));
        Assert.Equal(new Vector3(1, 0, 0), flat.Vertices[4].Position);

        var unlit = ImportOne(RightTriangle.Gltf(), NormalsMode.None);
        Assert.Null(unlit.Data.Normals);
        Assert.Contains("public const bool HasNormals = false;", MeshImporter.Import(new MeshImportRequest("T", "N", [RightTriangle.Gltf()]) { Normals = NormalsMode.None }).Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meshes_over_the_protocol_limits_fail_at_import()
    {
        // Accessors without a buffer view are all zeros (glTF), which makes huge counts cheap to describe.
        string Zeros(int vertices, int indices) => $$"""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":0}{{(indices > 0 ? ",\"indices\":1" : "")}}}]}],
             "accessors":[{"componentType":5126,"count":{{vertices}},"type":"VEC3"},{"componentType":5125,"count":{{Math.Max(indices, 1)}},"type":"SCALAR"}]}
            """;
        var tooManyVertices = new MeshSource("big.gltf", Encoding.UTF8.GetBytes(Zeros(RenderValidation.MaxVertices + 1, 0)));
        Assert.Equal(ImportErrorCode.MeshTooLarge, Code(() => ImportOne(tooManyVertices, NormalsMode.None)));
        var tooManyIndices = new MeshSource("big.gltf", Encoding.UTF8.GetBytes(Zeros(3, RenderValidation.MaxIndices + 3)));
        Assert.Equal(ImportErrorCode.MeshTooLarge, Code(() => ImportOne(tooManyIndices, NormalsMode.None)));
        // Flat normals unweld every corner, so 2^20 / 3 + 1 triangles over 3 shared vertices is already too many vertices.
        var tooManyCorners = new MeshSource("big.gltf", Encoding.UTF8.GetBytes(Zeros(3, (RenderValidation.MaxVertices / 3 + 1) * 3)));
        Assert.Equal(ImportErrorCode.MeshTooLarge, Code(() => ImportOne(tooManyCorners, NormalsMode.Flat)));
    }

    [Fact]
    public void Malformed_files_fail_with_invalid_mesh()
    {
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne(new MeshSource("bad.gltf", "{not json"u8.ToArray()))));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne(new MeshSource("bad.glb", "glTF but not really"u8.ToArray()))));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne(new MeshSource("bad.gltf", """{"asset":{"version":"2.0"},"meshes":[]}"""u8.ToArray()))));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Indices = [0, 1, 3] }).Gltf())));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Indices = [0, 1] }).Gltf())));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Positions = [0, 0, 0, 1, 0, 0, float.NaN, 1, 0] }).Gltf())));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Normals = [0, 0, 0, 0, 0, 1, 0, 0, 1] }).Gltf())));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Node = "\"scale\":[0,1,1]," }).Gltf())));
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne((RightTriangle with { Normals = null, Positions = [0, 0, 0, 1, 0, 0, 2, 0, 0] }).Gltf(), NormalsMode.Flat)));

        // An accessor that reaches past its buffer view.
        var json = RightTriangle.Build("data:application/octet-stream;base64," + Convert.ToBase64String(RightTriangle.Build(null).Bin)).Json;
        var overrun = json.Replace("\"count\":3,\"type\":\"VEC3\"}", "\"count\":4,\"type\":\"VEC3\"}", StringComparison.Ordinal);
        Assert.Equal(ImportErrorCode.InvalidMesh, Code(() => ImportOne(new MeshSource("overrun.gltf", Encoding.UTF8.GetBytes(overrun)))));
    }

    [Fact]
    public void Valid_gltf_features_the_engine_cannot_draw_fail_with_unsupported_feature_instead_of_being_dropped()
    {
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Root = "\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]," }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Material = """{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}""" }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Material = """{"normalTexture":{"index":0}}""" }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Material = """{"alphaMode":"BLEND"}""" }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Material = """{"emissiveFactor":[1,0,0]}""" }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Node = "\"skin\":0," }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Primitive = "\"mode\":1," }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Primitive = "\"targets\":[{\"POSITION\":0}]," }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Primitive = "\"extensions\":{\"KHR_draco_mesh_compression\":{}}," }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne((RightTriangle with { Colors = null, ColorBytes = [255, 0, 0, 128, 0, 255, 0, 255, 0, 0, 255, 255] }).Gltf())));
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne(new MeshSource("old.gltf", """{"asset":{"version":"1.0"}}"""u8.ToArray()))));

        // Double-sided materials: rejected by default, imported single-sided when the spec says so.
        var doubleSided = RightTriangle with { Material = """{"doubleSided":true}""" };
        Assert.Equal(ImportErrorCode.UnsupportedFeature, Code(() => ImportOne(doubleSided.Gltf())));
        Assert.Equal(3, ImportOne(doubleSided.Gltf(), doubleSided: DoubleSidedMode.Cull).VertexCount);
    }

    [Fact]
    public void Names_and_specs_are_validated()
    {
        MeshImportResult Import(params MeshSource[] sources) => MeshImporter.Import(new MeshImportRequest("TestMeshes", "Ukiyo.Tests.Gen", sources));
        Assert.Equal(ImportErrorCode.DuplicateName, Code(() => Import(RightTriangle.Gltf("a/rock.gltf"), RightTriangle.Glb("b/rock.glb"))));
        Assert.Equal(ImportErrorCode.DuplicateName, Code(() => Import(RightTriangle.Gltf("street_lamp.gltf"), RightTriangle.Gltf("street-lamp.gltf"))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => Import(RightTriangle.Gltf("names.gltf"))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => Import(RightTriangle.Gltf("test_meshes.gltf"))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => Import(RightTriangle.Gltf("has space.gltf"))));
        Assert.Equal("StreetLampV2", Assert.Single(Import(RightTriangle.Gltf("street_lamp-v2.gltf")).Meshes).MemberName);

        var dir = Directory.CreateTempSubdirectory("ukiyo-mesh-import-");
        try
        {
            var spec = Path.Combine(dir.FullName, "m.import.json");
            File.WriteAllText(spec, """{"class":"M","namespace":"N","output":"M.g.cs","inputs":["a.gltf","b.png"]}""");
            Assert.Equal(ImportErrorCode.InvalidSpec, Code(() => MeshSpec.Describes(spec)));
            File.WriteAllText(spec, """{"class":"M","namespace":"N","output":"M.g.cs","inputs":["a.gltf"],"extrude":1}""");
            Assert.Equal(ImportErrorCode.InvalidSpec, Code(() => MeshSpec.Load(spec)));
            File.WriteAllText(spec, """{"class":"M","namespace":"N","output":"M.g.cs","inputs":["a.gltf"],"normals":"smooth"}""");
            Assert.Equal(ImportErrorCode.InvalidSpec, Code(() => MeshSpec.Load(spec)));
            File.WriteAllText(spec, """{"class":"M","namespace":"N","output":"M.g.cs","inputs":["a.gltf"]}""");
            Assert.Equal(ImportErrorCode.MissingFile, Code(() => MeshSpec.Load(spec)));
            File.WriteAllBytes(Path.Combine(dir.FullName, "a.gltf"), Encoding.UTF8.GetBytes(RightTriangle.Build("a.bin").Json));
            File.WriteAllBytes(Path.Combine(dir.FullName, "a.bin"), RightTriangle.Build(null).Bin);
            File.WriteAllText(spec, """{"class":"M","namespace":"N","output":"M.g.cs","inputs":["a.gltf"],"normals":"none","doubleSided":"cull"}""");
            var loaded = MeshSpec.Load(spec);
            Assert.Equal((NormalsMode.None, DoubleSidedMode.Cull), (loaded.Request.Normals, loaded.Request.DoubleSided));
            Assert.Equal(["a.gltf", "a.bin"], MeshImporter.Import(loaded.Request).SourceHashes.Select(s => s.Path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportedMesh_draws_the_lit_imported_meshes_and_reports_asset_counts_in_its_snapshot()
    {
        var game = new ImportedMeshGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        var extent = new RenderExtent(960, 640, 1f);
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent, RenderProfile.G1Lit));
        runtime.StepTo(60);
        runtime.RenderFrame(extent);
        var capture = PngDecoder.Decode((await runtime.CaptureAsync())!.Png);
        var clear = capture.Rgba.AsSpan(0, 3).ToArray();

        // Suzanne's forehead (warm gradient) and the pedestal's front face (grey stone) are drawn, not the clear color.
        var forehead = capture.Rgba.AsSpan((200 * 960 + 480) * 4, 3).ToArray();
        var pedestal = capture.Rgba.AsSpan((440 * 960 + 480) * 4, 3).ToArray();
        Assert.NotEqual(clear, forehead);
        Assert.NotEqual(clear, pedestal);
        Assert.True(forehead[0] > forehead[2], "the top of Suzanne carries the warm end of her vertex gradient");

        using var snapshot = JsonDocument.Parse(runtime.SnapshotJson());
        var values = snapshot.RootElement.GetProperty("values");
        Assert.Equal((2, 555, 996), (values.GetProperty("meshes").GetInt32(), values.GetProperty("meshVertices").GetInt32(), values.GetProperty("meshTriangles").GetInt32()));

        // The game code itself never reads files or parses models.
        var code = File.ReadAllText(Path.Combine(CoreTests.RepoRoot(), "samples", "ImportedMesh", "Shared", "ImportedMeshGame.cs"));
        foreach (var forbidden in new[] { "File.", "Assets.", "Parse", "Json", "Stream" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    private const int Size = 96;
    private static readonly byte[] Clear = [0, 0, 0];

    /// <summary>Unlit view of one mesh on a black background through the CPU renderer.</summary>
    private static async Task<DecodedImage> Render(MeshData mesh, Vector3 eye)
    {
        var runtime = new GameRuntime(new MeshView(mesh, eye), new SourceIdentity(new Dictionary<string, string>()));
        var extent = new RenderExtent(Size, Size, 1f);
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent, RenderProfile.G1Lit));
        runtime.RenderFrame(extent);
        return PngDecoder.Decode((await runtime.CaptureAsync())!.Png);
    }

    /// <summary>Pixel showing world point (x, y, 0) from the camera at distance 3 on the Z axis (60° vertical FOV, square viewport).</summary>
    private static byte[] Pixel(DecodedImage image, float x, float y)
    {
        var half = 3f * MathF.Tan(MathF.PI / 6f);
        var px = (int)(Size / 2f + x / half * Size / 2f);
        var py = (int)(Size / 2f - y / half * Size / 2f);
        return image.Rgba.AsSpan((py * Size + px) * 4, 3).ToArray();
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");

    private sealed class MeshView(MeshData mesh, Vector3 eye) : IGame
    {
        private ResourceHandle _mesh;
        private ResourceHandle _material;

        public string Name => "MeshView";

        public RenderProfile Profile => RenderProfile.G1Lit;

        public void Initialize(GameContext context)
        {
            _mesh = context.CreateMesh(mesh);
            _material = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true));
        }

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame)
        {
            frame.ClearColor = new Vector4(0, 0, 0, 1);
            frame.Camera = FrameBuilder.LookAt(eye, Vector3.Zero, MathF.PI / 3f, 0.1f, 100f);
            frame.Draw(_mesh, _material, Matrix4x4.Identity);
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }
}
