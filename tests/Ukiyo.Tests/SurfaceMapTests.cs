using System.Numerics;
using Ukiyo.Hosting;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.ImportedMesh;
using Ukiyo.Samples.Minimap;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// The CPU renderer's debug surface map: geometric edge masks for pixel-parity gates. Silhouettes must be edges even
/// where both sides have the same color, and the inside of a smooth mesh must stay interior across its triangles.
/// </summary>
public sealed class SurfaceMapTests
{
    private static readonly RenderExtent Extent = new(160, 90, 1f);
    private static readonly ResourceHandle Mesh1 = new(ResourceKind.Mesh, 0, 1);
    private static readonly ResourceHandle Material1 = new(ResourceKind.Material, 0, 1);
    private static readonly SourceIdentity Source = new(new Dictionary<string, string>());

    // Camera at z = 3 looking down −Z: the unit quad at z = 0 covers x 52.5..107.5 and y 17.5..72.5, centered on (80, 45).
    private static RenderPacket Frame(long tick, int instances = 1) =>
        new(1, tick, Extent, new Vector4(0, 0, 0, 1), new CameraState(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f),
            [.. Enumerable.Repeat(new RenderInstance(Mesh1, Material1, Matrix4x4.Identity), instances)])
        {
            Lighting = new SceneLighting(new Vector3(0.2f), [Light.Directional(new Vector3(-0.3f, -0.2f, -1f), Vector3.One, 0.8f)]),
        };

    private static VertexPositionColor White(float x, float y, float z) => new(new Vector3(x, y, z), Vector3.One);

    private static async Task<SoftwareRenderer> StartAsync(MeshData mesh, bool lit, bool recordSurfaces = true)
    {
        var renderer = new SoftwareRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", Extent, RenderProfile.G1Lit), CancellationToken.None);
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, mesh), ResourceCommand.CreateMaterial(Material1, new MaterialData(Vector4.One, false) { Lit = lit })]));
        renderer.Surfaces = recordSurfaces ? new SurfaceMap() : null;
        return renderer;
    }

    private static byte At(byte[] mask, int x, int y) => mask[y * Extent.Width + x];

    private static int EdgesInRow(byte[] mask, int y, int fromX, int toX) =>
        Enumerable.Range(fromX, toX - fromX + 1).Count(x => At(mask, x, y) == SurfaceMap.Edge);

    /// <summary>The quad as two triangles that share the diagonal's vertices (smooth), or with split vertices.</summary>
    private static MeshData Quad(Vector3 normalA, Vector3 normalB, bool split) => split
        ? new([White(-1, 1, 0), White(-1, -1, 0), White(1, -1, 0), White(-1, 1, 0), White(1, -1, 0), White(1, 1, 0)], [0, 1, 2, 3, 4, 5])
        {
            Normals = [normalA, normalA, normalA, normalB, normalB, normalB],
        }
        : new([White(-1, 1, 0), White(-1, -1, 0), White(1, -1, 0), White(1, 1, 0)], [0, 1, 2, 0, 2, 3])
        {
            Normals = [normalA, normalA, normalA, normalA],
        };

    [Fact]
    public async Task Triangles_that_share_vertices_are_one_interior_and_the_outline_against_the_clear_color_is_an_edge()
    {
        using var renderer = await StartAsync(Quad(Vector3.UnitZ, Vector3.UnitZ, split: false), lit: true);
        renderer.Render(Frame(1));
        var mask = renderer.Surfaces!.Classify();

        Assert.Equal(SurfaceMap.Interior, At(mask, 80, 45));      // on the diagonal: both triangles in the neighborhood
        Assert.Equal(SurfaceMap.Interior, At(mask, 60, 45));
        Assert.Equal(SurfaceMap.Background, At(mask, 10, 45));
        Assert.InRange(EdgesInRow(mask, 45, 48, 57), 2, 3);         // left silhouette: one pixel each side of x = 52.5
        Assert.InRange(EdgesInRow(mask, 45, 103, 112), 2, 3);
        Assert.Equal(0, EdgesInRow(mask, 45, 58, 102));
    }

    [Fact]
    public async Task Duplicated_vertices_with_equal_attributes_are_welded_but_a_crease_between_normals_is_an_edge()
    {
        using var welded = await StartAsync(Quad(Vector3.UnitZ, Vector3.UnitZ, split: true), lit: true);
        welded.Render(Frame(1));
        Assert.Equal(0, EdgesInRow(welded.Surfaces!.Classify(), 45, 58, 102));

        var tilted = Vector3.Normalize(new Vector3(0.4f, 0, 1));
        using var crease = await StartAsync(Quad(Vector3.UnitZ, tilted, split: true), lit: true);
        crease.Render(Frame(1));
        var mask = crease.Surfaces!.Classify();
        Assert.Equal(SurfaceMap.Edge, At(mask, 80, 45));
        Assert.Equal(SurfaceMap.Interior, At(mask, 60, 45));
    }

    [Fact]
    public async Task A_part_of_a_mesh_in_front_of_another_part_is_an_edge_even_where_both_have_the_same_color()
    {
        // One mesh, two unconnected quads: the small one 0.5 m in front of the large one. Unlit white, so the image has
        // no contrast at all across the small quad's outline; only the geometry knows it is a silhouette.
        var back = Quad(Vector3.UnitZ, Vector3.UnitZ, split: false);
        VertexPositionColor[] front = [White(-0.3f, 0.3f, 0.5f), White(-0.3f, -0.3f, 0.5f), White(0.3f, -0.3f, 0.5f), White(0.3f, 0.3f, 0.5f)];
        var mesh = new MeshData([.. back.Vertices, .. front], [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]) { Normals = [.. Enumerable.Repeat(Vector3.UnitZ, 8)] };
        using var renderer = await StartAsync(mesh, lit: false);
        renderer.Render(Frame(2));
        var image = PngDecoder.Decode((await renderer.CaptureAsync(new CaptureRequest(2), CancellationToken.None)).Png);
        var mask = renderer.Surfaces!.Classify();

        Assert.Equal(image.Rgba[(45 * Extent.Width + 60) * 4], image.Rgba[(45 * Extent.Width + 80) * 4]);
        Assert.Equal(SurfaceMap.Interior, At(mask, 80, 45));
        Assert.Equal(SurfaceMap.Interior, At(mask, 60, 45));
        Assert.InRange(EdgesInRow(mask, 45, 64, 75), 2, 3);        // the front quad's left outline (x ≈ 69.9)
        Assert.InRange(EdgesInRow(mask, 45, 85, 96), 2, 3);
    }

    [Fact]
    public async Task Triangles_that_meet_only_at_a_vertex_whose_fan_faces_the_camera_are_one_surface()
    {
        // A square pyramid with its apex toward the camera and smooth (welded) vertices: opposite sides share only the
        // apex. Its fan is closed and all four sides face the camera, so the apex is inside one smooth sheet.
        VertexPositionColor[] vertices = [White(0, 0, 0.8f), White(-1, -1, 0), White(1, -1, 0), White(1, 1, 0), White(-1, 1, 0)];
        uint[] indices = [0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 1, 4, 3, 1, 3, 2];
        var normals = vertices.Select(v => Vector3.Normalize(v.Position - new Vector3(0, 0, -0.5f))).ToArray();
        using var renderer = await StartAsync(new MeshData(vertices, indices) { Normals = normals }, lit: true);
        renderer.Render(Frame(4));
        var mask = renderer.Surfaces!.Classify();

        Assert.Equal(0, Enumerable.Range(37, 17).Sum(y => EdgesInRow(mask, y, 72, 88)));   // around the apex at (80, 45)
        Assert.Equal(SurfaceMap.Interior, At(mask, 80, 45));
    }

    [Fact]
    public async Task The_low_contrast_ear_rim_that_failed_the_color_rule_is_an_edge()
    {
        // ImportedMesh tick 60 at the gate size: (343, 261) on Suzanne's ear rim differs from every neighbor by at most
        // 24 levels, so the color rule called it interior, and WebGL2 drew it 39 levels off. The geometry says rim.
        var (png, surfaces) = await CaptureImportedMesh(recordSurfaces: true, tick: 60, new RenderExtent(960, 640, 1f));
        var image = PngDecoder.Decode(png);
        int Channel(int x, int y, int c) => image.Rgba[(y * image.Width + x) * 4 + c];
        var contrast = (from dy in new[] { -1, 0, 1 } from dx in new[] { -1, 0, 1 } from c in new[] { 0, 1, 2 } select Math.Abs(Channel(343 + dx, 261 + dy, c) - Channel(343, 261, c))).Max();

        Assert.InRange(contrast, 1, 24);
        Assert.Equal(SurfaceMap.Edge, surfaces!.Classify()[261 * 960 + 343]);
    }

    [Fact]
    public async Task Two_draws_of_one_mesh_are_two_surfaces()
    {
        using var renderer = await StartAsync(Quad(Vector3.UnitZ, Vector3.UnitZ, split: false), lit: false);
        var frame = Frame(3, instances: 2);
        renderer.Render(frame with { Instances = [frame.Instances[0], frame.Instances[1] with { World = Matrix4x4.CreateScale(0.4f) * Matrix4x4.CreateTranslation(0, 0, 0.5f) }] });
        var mask = renderer.Surfaces!.Classify();

        Assert.Equal(SurfaceMap.Interior, At(mask, 80, 45));
        Assert.Equal(SurfaceMap.Interior, At(mask, 60, 45));
        Assert.InRange(EdgesInRow(mask, 45, 60, 75), 2, 3);
    }

    [Fact]
    public async Task The_surface_map_never_changes_the_frame_and_is_bound_to_the_tick_it_describes()
    {
        var gateSize = new RenderExtent(960, 640, 1f);
        var plain = await CaptureImportedMesh(recordSurfaces: false, tick: 60, gateSize);
        var recorded = await CaptureImportedMesh(recordSurfaces: true, tick: 60, gateSize);
        Assert.Equal(plain.Png, recorded.Png);

        var mask = recorded.Surfaces!.Classify();
        Assert.Contains(SurfaceMap.Edge, mask);
        Assert.Contains(SurfaceMap.Background, mask);
        // Suzanne (about a thousand smooth triangles) and the pedestal are mostly interior: triangle borders are not edges.
        Assert.True(mask.Count(m => m == SurfaceMap.Interior) > 4 * mask.Count(m => m == SurfaceMap.Edge));
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => recorded.Surfaces.EncodePng(59)).Code);
        var png = PngDecoder.Decode(recorded.Surfaces.EncodePng(60));
        Assert.Equal((gateSize.Width, gateSize.Height), (png.Width, png.Height));
    }

    [Fact]
    public async Task Multi_camera_frames_get_a_mask_per_pass_and_pass_borders_are_edges()
    {
        var size = new RenderExtent(480, 320, 1f);
        var captures = new Dictionary<bool, List<byte[]>>();
        SurfaceMap? surfaces = null;
        foreach (var record in new[] { false, true })
        {
            var runtime = new GameRuntime(new MinimapGame(), Source);
            using var renderer = new SoftwareRenderer { Surfaces = record ? new SurfaceMap() : null };
            await runtime.StartAsync(renderer, new RenderConfiguration("test", size));
            runtime.StepTo(60);
            runtime.RenderFrame(size);
            captures[record] = [(await renderer.CaptureAsync(new CaptureRequest(60), CancellationToken.None)).Png];
            for (var pass = 0; pass < runtime.PassCount; pass++)
            {
                captures[record].Add((await renderer.CaptureAsync(new CaptureRequest(60) { Pass = pass }, CancellationToken.None)).Png);
            }

            surfaces = renderer.Surfaces;
        }

        Assert.Equal(captures[false], captures[true]);
        Assert.Equal(2, surfaces!.PassCount);
        var rect = MinimapGame.MinimapRect(size);
        var minimap = PngDecoder.Decode(surfaces.EncodePng(60, pass: 1));
        Assert.Equal((rect.Width, rect.Height), (minimap.Width, minimap.Height));
        Assert.Equal((size.Width, size.Height), (PngDecoder.Decode(surfaces.EncodePng(60, pass: 0)).Width, PngDecoder.Decode(surfaces.EncodePng(60, pass: 0)).Height));

        var frame = surfaces.Classify();
        var midY = rect.Y + rect.Height / 2;
        Assert.Equal(SurfaceMap.Edge, frame[midY * size.Width + rect.X]);       // the minimap's left border in the frame
        Assert.Equal(SurfaceMap.Edge, frame[midY * size.Width + rect.X - 1]);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => surfaces.Classify(pass: 2)).Code);
    }

    private static async Task<(byte[] Png, SurfaceMap? Surfaces)> CaptureImportedMesh(bool recordSurfaces, long tick, RenderExtent extent)
    {
        var runtime = new GameRuntime(new ImportedMeshGame(), Source);
        using var renderer = new SoftwareRenderer { Surfaces = recordSurfaces ? new SurfaceMap() : null };
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent));
        runtime.StepTo(tick);
        runtime.RenderFrame(extent);
        return ((await renderer.CaptureAsync(new CaptureRequest(tick), CancellationToken.None)).Png, renderer.Surfaces);
    }

    [Fact]
    public async Task Headless_edge_masks_need_captures_and_are_written_next_to_them()
    {
        Assert.StartsWith("[HEADLESS]:", Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse(["--edge-masks", "masks"])).Message);
        Assert.StartsWith("[HEADLESS]:", Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse(["--capture", "frames", "--edge-masks"])).Message);
        Assert.Null(HeadlessOptions.Parse(["--capture", "frames"]).EdgeMaskDirectory);

        var directory = Directory.CreateTempSubdirectory("ukiyo-edge-masks-").FullName;
        try
        {
            var frames = Path.Combine(directory, "frames");
            var masks = Path.Combine(directory, "masks");
            var stdout = new StringWriter();
            var host = new HeadlessHost(HeadlessOptions.Parse(["--checkpoints", "0,60", "--capture", frames, "--edge-masks", masks, "--size", "160x90"]), stdout, TextWriter.Null);
            Assert.Equal(0, await host.RunAsync(new GameRuntime(new ImportedMeshGame(), Source)));

            Assert.Equal(["edges-tick-000000.png", "edges-tick-000060.png"], Directory.GetFiles(masks).Select(Path.GetFileName).Order());
            Assert.Contains($"edges {Path.Combine(masks, "edges-tick-000060.png")}", stdout.ToString());
            Assert.Equal((await CaptureImportedMesh(recordSurfaces: false, tick: 60, Extent)).Png, await File.ReadAllBytesAsync(Path.Combine(frames, "headless-tick-000060.png")));
            Assert.DoesNotContain(Directory.GetFiles(frames), f => Path.GetFileName(f).StartsWith("edges", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
