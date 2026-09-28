using System.Buffers.Binary;
using System.Numerics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.RotatingCube;
using Xunit;

namespace Ukiyo.Tests;

public sealed class BridgeTests
{
    private static readonly ResourceHandle Mesh1 = new(ResourceKind.Mesh, 0, 1);
    private static readonly ResourceHandle Material1 = new(ResourceKind.Material, 0, 1);

    private static MeshData Triangle() => new(
        [new VertexPositionColor(Vector3.Zero, Vector3.One), new VertexPositionColor(Vector3.UnitX, Vector3.One), new VertexPositionColor(Vector3.UnitY, Vector3.One)],
        [0, 1, 2]);

    private static RenderPacket Frame(long tick, Matrix4x4 world) => new(
        7, tick, new RenderExtent(640, 480, 1f), new Vector4(0, 0, 0, 1),
        new CameraState(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f),
        [new RenderInstance(Mesh1, Material1, world)]);

    [Fact]
    public void Resource_batch_round_trips_through_the_binary_protocol()
    {
        var batch = new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Triangle()), ResourceCommand.CreateMaterial(Material1, new MaterialData(new Vector4(1, 0.5f, 0.25f, 1), true)), ResourceCommand.Destroy(Mesh1)]);
        var decoded = PacketCodec.DecodeResources(PacketCodec.Encode(batch));

        Assert.Equal(3, decoded.Commands.Count);
        Assert.Equal(Triangle().Vertices, decoded.Commands[0].Mesh!.Vertices);
        Assert.Equal(Triangle().Indices, decoded.Commands[0].Mesh!.Indices);
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1), decoded.Commands[1].Material!.BaseColor);
        Assert.Equal(ResourceCommandKind.Destroy, decoded.Commands[2].Kind);
    }

    [Fact]
    public void Frame_round_trips_including_64_bit_ticks_beyond_32_bits()
    {
        var tick = (1L << 40) + 5;
        var world = Matrix4x4.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f) * Matrix4x4.CreateTranslation(1, 2, 3);
        var decoded = PacketCodec.DecodeFrame(PacketCodec.Encode(Frame(tick, world)));

        Assert.Equal(tick, decoded.Tick);
        Assert.Equal(world, decoded.Instances[0].World);
        Assert.Equal(7u, decoded.Sequence);
    }

    [Fact]
    public void Matrix_is_serialized_in_field_order_so_translation_lands_at_column_major_indices_12_to_14()
    {
        var bytes = PacketCodec.Encode(Frame(0, Matrix4x4.CreateTranslation(4, 5, 6)));
        var matrixOffset = bytes.Length - 64;
        float At(int index) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(matrixOffset + index * 4));

        Assert.Equal((4f, 5f, 6f, 1f), (At(12), At(13), At(14), At(15)));
    }

    [Fact]
    public void Truncated_payload_is_rejected_not_dropped()
    {
        var bytes = PacketCodec.Encode(Frame(1, Matrix4x4.Identity));
        var error = Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Equal(RenderErrorCode.TruncatedPayload, error.Code);
    }

    [Fact]
    public void Unknown_protocol_version_is_rejected()
    {
        var bytes = PacketCodec.Encode(Frame(1, Matrix4x4.Identity));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        Assert.Equal(RenderErrorCode.UnsupportedVersion, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);
    }

    [Fact]
    public void Bad_magic_is_rejected()
    {
        var bytes = PacketCodec.Encode(Frame(1, Matrix4x4.Identity));
        bytes[0] ^= 0xFF;
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);
    }

    [Fact]
    public void Non_finite_matrix_values_are_rejected_before_crossing_the_bridge()
    {
        var world = Matrix4x4.Identity;
        world.M41 = float.NaN;
        Assert.Equal(RenderErrorCode.NonFiniteValue, Assert.Throws<RenderException>(() => PacketCodec.Encode(Frame(1, world))).Code);
    }

    [Fact]
    public void Index_outside_vertex_range_is_rejected()
    {
        var mesh = new MeshData(Triangle().Vertices, [0, 1, 3]);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(mesh)).Code);
    }

    [Fact]
    public void Material_handle_used_as_mesh_is_rejected()
    {
        var packet = Frame(1, Matrix4x4.Identity) with { Instances = [new RenderInstance(Material1, Material1, Matrix4x4.Identity)] };
        Assert.Equal(RenderErrorCode.WrongResourceKind, Assert.Throws<RenderException>(() => RenderValidation.Validate(packet)).Code);
    }

    [Fact]
    public async Task Stale_handle_after_destroy_and_recreate_is_rejected_by_the_renderer()
    {
        using var renderer = new NullRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", new RenderExtent(64, 64, 1f)), CancellationToken.None);
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Triangle()), ResourceCommand.CreateMaterial(Material1, new MaterialData(Vector4.One, true))]));
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.Destroy(Mesh1), ResourceCommand.CreateMesh(Mesh1 with { Generation = 2 }, Triangle())]));

        var error = Assert.Throws<RenderException>(() => renderer.Render(Frame(1, Matrix4x4.Identity)));
        Assert.Equal(RenderErrorCode.StaleHandle, error.Code);
    }

    [Fact]
    public async Task Unknown_handle_and_use_before_initialize_are_controlled_errors()
    {
        using var uninitialized = new NullRenderer();
        Assert.Equal(RenderErrorCode.NotInitialized, Assert.Throws<RenderException>(() => uninitialized.Render(Frame(1, Matrix4x4.Identity))).Code);

        using var renderer = new NullRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", new RenderExtent(64, 64, 1f)), CancellationToken.None);
        Assert.Equal(RenderErrorCode.UnknownHandle, Assert.Throws<RenderException>(() => renderer.Render(Frame(1, Matrix4x4.Identity))).Code);
    }

    [Fact]
    public async Task Destroying_resources_releases_them_in_the_renderer()
    {
        using var renderer = new NullRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", new RenderExtent(64, 64, 1f)), CancellationToken.None);
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Triangle())]));
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.Destroy(Mesh1)]));
        Assert.Equal(0, renderer.LiveMeshes);
        Assert.Throws<RenderException>(() => renderer.ApplyResources(new ResourceBatch([ResourceCommand.Destroy(Mesh1)])));
    }

    /// <summary>
    /// The JS decoder (web/three-adapter/protocol.test.js) reads these same golden files, so both sides of the
    /// bridge are pinned to identical bytes. Regenerate with UKIYO_UPDATE_GOLDEN=1 only for a deliberate protocol change.
    /// </summary>
    [Fact]
    public async Task Cube_packets_match_the_golden_files_shared_with_the_js_decoder()
    {
        var fixtures = Path.Combine(CoreTests.RepoRoot(), "web", "three-adapter", "fixtures");
        var (runtime, renderer) = await StartGoldenCubeAsync();
        var resources = renderer.Resources!;
        runtime.StepTo(60);
        var frame = PacketCodec.Encode(runtime.RenderFrame(new RenderExtent(640, 480, 1f)));
        var resourceBytes = PacketCodec.Encode(resources);

        if (Environment.GetEnvironmentVariable("UKIYO_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(fixtures);
            await File.WriteAllBytesAsync(Path.Combine(fixtures, "cube-resources.bin"), resourceBytes);
            await File.WriteAllBytesAsync(Path.Combine(fixtures, "cube-frame-tick60.bin"), frame);
        }

        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixtures, "cube-resources.bin")), resourceBytes);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixtures, "cube-frame-tick60.bin")), frame);
        Assert.Equal(1, renderer.FramesRendered);
    }

    private static async Task<(GameRuntime, CapturingRenderer)> StartGoldenCubeAsync()
    {
        var renderer = new CapturingRenderer();
        var runtime = new GameRuntime(new CubeGame(), new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(renderer, new RenderConfiguration("golden", new RenderExtent(640, 480, 1f)));
        return (runtime, renderer);
    }

    /// <summary>Null renderer that also keeps the resource batch it received, to encode it for the golden file.</summary>
    private sealed class CapturingRenderer : IRenderer
    {
        private readonly NullRenderer _inner = new();

        public ResourceBatch? Resources { get; private set; }

        public long FramesRendered => _inner.FramesRendered;

        public RenderCapabilities Capabilities => _inner.Capabilities;

        public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken) => _inner.InitializeAsync(configuration, cancellationToken);

        public void ApplyResources(ResourceBatch resources)
        {
            Resources = resources;
            _inner.ApplyResources(resources);
        }

        public void Resize(RenderExtent extent) => _inner.Resize(extent);

        public void Render(RenderPacket packet) => _inner.Render(packet);

        public void Dispose() => _inner.Dispose();
    }
}
