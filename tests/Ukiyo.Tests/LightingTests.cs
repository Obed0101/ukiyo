using System.Buffers.Binary;
using System.Numerics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.LitCube;
using Ukiyo.Samples.RotatingCube;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>G1Lit: protocol (flag 0x2, command 5, material flag 2), validation, profile gating and CPU pixels.</summary>
public sealed class LightingTests
{
    private static readonly RenderExtent Extent = new(160, 90, 1f);
    private static readonly ResourceHandle Mesh1 = new(ResourceKind.Mesh, 0, 1);
    private static readonly ResourceHandle Material1 = new(ResourceKind.Material, 0, 1);

    // Byte offsets in a frame with 3 instances and no sprites (see docs/protocol.md).
    private const int LitFrameLightingOffset = 16 + 8 + 8 + 16 + 40 + 4 + 3 * 96;
    private const int LitFrameLightCountOffset = LitFrameLightingOffset + 12;
    private const int LitFrameFirstLightOffset = LitFrameLightCountOffset + 4;

    private static RenderRequestBuilder Scene => new();

    [Fact]
    public void Lit_resources_round_trip_with_normals_and_the_lit_material_flag()
    {
        var mesh = Quad(withNormals: true);
        var batch = new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, mesh), ResourceCommand.CreateMaterial(Material1, new MaterialData(new Vector4(0.5f, 0.25f, 1, 1), true) { Lit = true })]);
        var bytes = PacketCodec.Encode(batch);
        var decoded = PacketCodec.DecodeResources(bytes);

        Assert.Equal(PacketCodec.CreateMeshWithNormalsCommand, bytes[PacketCodec.HeaderSize]);
        Assert.Equal(mesh.Normals, decoded.Commands[0].Mesh!.Normals);
        Assert.Equal(mesh.Vertices, decoded.Commands[0].Mesh!.Vertices);
        Assert.True(decoded.Commands[1].Material!.Lit);
        Assert.True(decoded.Commands[1].Material!.UseVertexColors);
    }

    [Fact]
    public void Unlit_resources_keep_command_1_and_material_flag_1_so_g0_bytes_do_not_change()
    {
        var batch = new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Quad(withNormals: false)), ResourceCommand.CreateMaterial(Material1, new MaterialData(Vector4.One, true))]);
        var bytes = PacketCodec.Encode(batch);
        var materialFlagsOffset = bytes.Length - 4;

        Assert.Equal((byte)ResourceCommandKind.CreateMesh, bytes[PacketCodec.HeaderSize]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(materialFlagsOffset)));
        Assert.Null(PacketCodec.DecodeResources(bytes).Commands[0].Mesh!.Normals);
    }

    [Fact]
    public void Lighting_section_round_trips_and_sets_frame_flag_0x2_only_when_present()
    {
        var lighting = new SceneLighting(new Vector3(0.1f, 0.2f, 0.3f), [Light.Directional(new Vector3(0, -1, 0), Vector3.One, 0.5f), Light.Point(new Vector3(1, 2, 3), new Vector3(1, 0.5f, 0), 2f, 6f)]);
        var lit = Scene.Packet() with { Lighting = lighting };
        var litBytes = PacketCodec.Encode(lit);
        var decoded = PacketCodec.DecodeFrame(litBytes);

        Assert.Equal(PacketCodec.FlagLighting, BinaryPrimitives.ReadUInt16LittleEndian(litBytes.AsSpan(6)));
        Assert.Equal(lighting.Ambient, decoded.Lighting!.Ambient);
        Assert.Equal(lighting.Lights, decoded.Lighting.Lights);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(PacketCodec.Encode(Scene.Packet()).AsSpan(6)));
        Assert.Null(PacketCodec.DecodeFrame(PacketCodec.Encode(Scene.Packet())).Lighting);
    }

    [Fact]
    public void Eight_lights_are_accepted_and_a_ninth_is_rejected_with_out_of_range()
    {
        var eight = Enumerable.Range(0, RenderValidation.MaxLights).Select(i => Light.Point(new Vector3(i, 1, 0), Vector3.One, 1, 3)).ToArray();
        RenderValidation.Validate(new SceneLighting(Vector3.Zero, eight));

        var nine = new SceneLighting(Vector3.Zero, [.. eight, Light.Directional(-Vector3.UnitY, Vector3.One)]);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.Encode(Scene.Packet() with { Lighting = nine })).Code);
    }

    [Fact]
    public async Task Decoding_a_lit_frame_that_declares_nine_lights_fails_with_out_of_range()
    {
        var bytes = await GoldenLitFrameAsync();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LitFrameLightCountOffset), 9);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);
    }

    [Fact]
    public async Task Non_finite_light_values_fail_with_non_finite_value_on_encode_and_decode()
    {
        foreach (var bad in new[]
        {
            Light.Point(new Vector3(float.NaN, 0, 0), Vector3.One, 1, 3),
            Light.Directional(-Vector3.UnitY, new Vector3(1, float.PositiveInfinity, 1)),
            Light.Directional(-Vector3.UnitY, Vector3.One, float.NaN),
        })
        {
            var packet = Scene.Packet() with { Lighting = new SceneLighting(Vector3.Zero, [bad]) };
            Assert.Equal(RenderErrorCode.NonFiniteValue, Assert.Throws<RenderException>(() => PacketCodec.Encode(packet)).Code);
        }

        Assert.Equal(RenderErrorCode.NonFiniteValue, Assert.Throws<RenderException>(() => RenderValidation.Validate(new SceneLighting(new Vector3(0, float.NaN, 0), []))).Code);

        // Intensity of the first light: kind (4) + vector (12) + color (12).
        var bytes = await GoldenLitFrameAsync();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(LitFrameFirstLightOffset + 28), float.NaN);
        Assert.Equal(RenderErrorCode.NonFiniteValue, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);
    }

    [Fact]
    public async Task Malformed_light_kinds_ranges_and_material_flags_are_typed_errors()
    {
        var bytes = await GoldenLitFrameAsync();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LitFrameFirstLightOffset), 7);
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);

        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(Light.Point(Vector3.Zero, Vector3.One, 1, 0))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(Light.Directional(Vector3.Zero, Vector3.One))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(Light.Directional(-Vector3.UnitY, new Vector3(-1, 0, 0)))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(Quad(withNormals: true) with { Normals = [Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ] })).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(Quad(withNormals: true) with { Normals = [Vector3.UnitZ] })).Code);

        var resources = PacketCodec.Encode(new ResourceBatch([ResourceCommand.CreateMaterial(Material1, new MaterialData(Vector4.One, true))]));
        BinaryPrimitives.WriteUInt32LittleEndian(resources.AsSpan(resources.Length - 4), 0x4);
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => PacketCodec.DecodeResources(resources)).Code);
    }

    [Fact]
    public async Task Lit_data_on_a_renderer_initialized_with_g0_unlit_fails_with_unsupported_feature()
    {
        foreach (IRenderer renderer in new IRenderer[] { new SoftwareRenderer(), new NullRenderer() })
        {
            using (renderer)
            {
                await renderer.InitializeAsync(new RenderConfiguration("test", Extent, RenderProfile.G0Unlit), CancellationToken.None);
                Assert.Equal(RenderProfile.G0Unlit, renderer.Capabilities.Profile);
                var litMesh = new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Quad(withNormals: true))]);
                Assert.Equal(RenderErrorCode.UnsupportedFeature, Assert.Throws<RenderException>(() => renderer.ApplyResources(litMesh)).Code);
                var litMaterial = new ResourceBatch([ResourceCommand.CreateMaterial(Material1, new MaterialData(Vector4.One, false) { Lit = true })]);
                Assert.Equal(RenderErrorCode.UnsupportedFeature, Assert.Throws<RenderException>(() => renderer.ApplyResources(litMaterial)).Code);
                var litFrame = Scene.Packet() with { Lighting = new SceneLighting(Vector3.One, []) };
                Assert.Equal(RenderErrorCode.UnsupportedFeature, Assert.Throws<RenderException>(() => renderer.Render(litFrame)).Code);
            }
        }
    }

    [Fact]
    public async Task A_lit_game_on_a_renderer_that_only_implements_g0_fails_at_start_with_unsupported_feature()
    {
        var runtime = new GameRuntime(new LitCubeGame(), new SourceIdentity(new Dictionary<string, string>()));
        var error = await Assert.ThrowsAsync<RenderException>(async () => await runtime.StartAsync(new G0OnlyRenderer(), new RenderConfiguration("test", Extent)));
        Assert.Equal(RenderErrorCode.UnsupportedFeature, error.Code);
    }

    [Fact]
    public async Task Renderers_refuse_profiles_they_do_not_implement()
    {
        using var renderer = new SoftwareRenderer();
        var error = await Assert.ThrowsAsync<RenderException>(async () => await renderer.InitializeAsync(new RenderConfiguration("test", Extent, (RenderProfile)99), CancellationToken.None));
        Assert.Equal(RenderErrorCode.UnsupportedFeature, error.Code);
    }

    [Fact]
    public async Task The_runtime_starts_renderers_with_the_game_profile_so_old_games_stay_g0_unlit()
    {
        using var unlit = new NullRenderer();
        await new GameRuntime(new CubeGame(), new SourceIdentity(new Dictionary<string, string>())).StartAsync(unlit, new RenderConfiguration("test", Extent));
        using var lit = new NullRenderer();
        await new GameRuntime(new LitCubeGame(), new SourceIdentity(new Dictionary<string, string>())).StartAsync(lit, new RenderConfiguration("test", Extent));

        Assert.Equal(RenderProfile.G0Unlit, unlit.Capabilities.Profile);
        Assert.Equal(RenderProfile.G1Lit, lit.Capabilities.Profile);
    }

    [Fact]
    public async Task A_lit_material_needs_mesh_normals_and_a_lighting_section()
    {
        foreach (IRenderer renderer in new IRenderer[] { new SoftwareRenderer(), new NullRenderer() })
        {
            using (renderer)
            {
                await renderer.InitializeAsync(new RenderConfiguration("test", Extent, RenderProfile.G1Lit), CancellationToken.None);
                var litMaterial = new MaterialData(Vector4.One, false) { Lit = true };
                renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateMesh(Mesh1, Quad(withNormals: false)), ResourceCommand.CreateMaterial(Material1, litMaterial)]));
                var withLights = Scene.Packet() with { Lighting = new SceneLighting(Vector3.One, []) };
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(withLights)).Code);

                var normalsMesh = Mesh1 with { Index = 1 };
                renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateMesh(normalsMesh, Quad(withNormals: true))]));
                var noLights = Scene.Packet(normalsMesh);
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(noLights)).Code);
                renderer.Render(Scene.Packet(normalsMesh) with { Lighting = new SceneLighting(Vector3.One, []) });
            }
        }
    }

    [Fact]
    public async Task Cpu_renderer_lights_a_facing_quad_with_ambient_plus_a_head_on_directional_light()
    {
        using var renderer = await StartLitQuadAsync(albedo: new Vector3(0.5f));
        renderer.Render(Scene.Packet() with { Lighting = new SceneLighting(new Vector3(0.1f), [Light.Directional(-Vector3.UnitZ, Vector3.One, 0.4f)]) });
        var image = await CaptureAsync(renderer, 1);

        // 0.5 * (0.1 + 1 * 0.4 * N·L(=1)) = 0.25 linear.
        var expected = ColorSpace.LinearToSrgb(0.25f);
        Assert.Equal((expected, expected, expected), Pixel(image, 80, 45));
        Assert.Equal((expected, expected, expected), Pixel(image, 70, 40));
    }

    [Fact]
    public async Task Cpu_renderer_gives_back_facing_lights_and_lights_out_of_range_no_contribution()
    {
        using var renderer = await StartLitQuadAsync(albedo: Vector3.One);
        var behind = Light.Directional(Vector3.UnitZ, Vector3.One, 5f);
        var tooFar = Light.Point(new Vector3(0, 0, 3), Vector3.One, 5f, 2.5f);
        renderer.Render(Scene.Packet() with { Lighting = new SceneLighting(new Vector3(0.2f, 0.1f, 0.05f), [behind, tooFar]) });
        var image = await CaptureAsync(renderer, 1);

        Assert.Equal((ColorSpace.LinearToSrgb(0.2f), ColorSpace.LinearToSrgb(0.1f), ColorSpace.LinearToSrgb(0.05f)), Pixel(image, 80, 45));
    }

    [Fact]
    public async Task Cpu_renderer_point_light_falls_off_with_the_squared_window()
    {
        using var renderer = await StartLitQuadAsync(albedo: Vector3.One);
        renderer.Render(Scene.Packet() with { Lighting = new SceneLighting(Vector3.Zero, [Light.Point(new Vector3(0, 0, 1), Vector3.One, 1f, 2f)]) });
        var image = await CaptureAsync(renderer, 1);

        // At the quad center d² = 1: t = 1 − 1/4 = 0.75, attenuation 0.5625, N·L = 1.
        var center = Pixel(image, 80, 45);
        Assert.InRange(center.R, ColorSpace.LinearToSrgb(0.5625f) - 1, ColorSpace.LinearToSrgb(0.5625f) + 1);

        // Farther from the light along the quad the light gets dimmer.
        Assert.True(Pixel(image, 50, 45).R < center.R);
    }

    [Fact]
    public async Task Lit_cube_sample_is_lit_deterministic_and_keeps_the_unlit_lamp_flat()
    {
        async Task<(string Snapshot, DecodedImage Image)> Run()
        {
            var runtime = new GameRuntime(new LitCubeGame(), new SourceIdentity(new Dictionary<string, string>()));
            using var renderer = new SoftwareRenderer();
            await runtime.StartAsync(renderer, new RenderConfiguration("test", new RenderExtent(320, 200, 1f)));
            runtime.StepTo(60);
            runtime.RenderFrame(new RenderExtent(320, 200, 1f));
            var png = (await runtime.CaptureAsync())!.Png;
            return (runtime.SnapshotJson(), PngDecoder.Decode(png));
        }

        var (snapshotA, imageA) = await Run();
        var (snapshotB, imageB) = await Run();
        Assert.Equal(snapshotA, snapshotB);
        Assert.Equal(imageA.Rgba, imageB.Rgba);
        Assert.Contains("\"lamp\"", snapshotA);
        Assert.NotEqual(Pixel(imageA, 0, 0), Pixel(imageA, 160, 100));
    }

    [Fact]
    public async Task Frame_builder_copies_the_light_list_so_later_game_edits_do_not_reach_the_packet()
    {
        var lights = new List<Light> { Light.Directional(-Vector3.UnitY, Vector3.One) };
        var runtime = new GameRuntime(new BuilderGame(new SceneLighting(Vector3.Zero, lights)), new SourceIdentity(new Dictionary<string, string>()));
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        var packet = runtime.RenderFrame(Extent);
        lights.Add(Light.Directional(Vector3.UnitY, Vector3.One));

        Assert.Single(packet.Lighting!.Lights);
        Assert.Equal(1, renderer.LightsSubmitted);
    }

    /// <summary>
    /// The JS decoder (web/three-adapter/lighting.test.js) reads these same files. Regenerate with UKIYO_UPDATE_GOLDEN=1
    /// only for a deliberate protocol change; the G0 cube fixtures must never change (BridgeTests).
    /// </summary>
    [Fact]
    public async Task Lit_cube_packets_match_the_golden_files_shared_with_the_js_decoder()
    {
        var fixtures = Path.Combine(CoreTests.RepoRoot(), "web", "three-adapter", "fixtures");
        var (resources, frame) = await LitCubeBytesAsync();
        if (Environment.GetEnvironmentVariable("UKIYO_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllBytesAsync(Path.Combine(fixtures, "lit-cube-resources.bin"), resources);
            await File.WriteAllBytesAsync(Path.Combine(fixtures, "lit-cube-frame-tick60.bin"), frame);
        }

        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixtures, "lit-cube-resources.bin")), resources);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixtures, "lit-cube-frame-tick60.bin")), frame);
        Assert.Equal(LitFrameLightingOffset + 16 + 2 * PacketCodec.LightSize, frame.Length);
    }

    private static async Task<byte[]> GoldenLitFrameAsync() => (await LitCubeBytesAsync()).Frame;

    private static async Task<(byte[] Resources, byte[] Frame)> LitCubeBytesAsync()
    {
        var renderer = new RecordingRenderer();
        var runtime = new GameRuntime(new LitCubeGame(), new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(renderer, new RenderConfiguration("golden", new RenderExtent(640, 480, 1f)));
        runtime.StepTo(60);
        var frame = PacketCodec.Encode(runtime.RenderFrame(new RenderExtent(640, 480, 1f)));
        return (PacketCodec.Encode(renderer.Resources!), frame);
    }

    private static async Task<SoftwareRenderer> StartLitQuadAsync(Vector3 albedo)
    {
        var renderer = new SoftwareRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", Extent, RenderProfile.G1Lit), CancellationToken.None);
        renderer.ApplyResources(new ResourceBatch([
            ResourceCommand.CreateMesh(Mesh1, Quad(withNormals: true)),
            ResourceCommand.CreateMaterial(Material1, new MaterialData(new Vector4(albedo, 1), false) { Lit = true }),
        ]));
        return renderer;
    }

    /// <summary>2×2 quad at z = 0 facing +Z (towards the default test camera at z = 3), counter-clockwise.</summary>
    private static MeshData Quad(bool withNormals) => new(
        [
            new VertexPositionColor(new Vector3(-1, 1, 0), Vector3.One), new VertexPositionColor(new Vector3(-1, -1, 0), Vector3.One),
            new VertexPositionColor(new Vector3(1, -1, 0), Vector3.One), new VertexPositionColor(new Vector3(1, 1, 0), Vector3.One),
        ],
        [0, 1, 2, 0, 2, 3])
    {
        Normals = withNormals ? [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ] : null,
    };

    private static async Task<DecodedImage> CaptureAsync(SoftwareRenderer renderer, long tick) =>
        PngDecoder.Decode((await renderer.CaptureAsync(new CaptureRequest(tick), CancellationToken.None)).Png);

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        var p = (y * image.Width + x) * 4;
        return (image.Rgba[p], image.Rgba[p + 1], image.Rgba[p + 2]);
    }

    private sealed class RenderRequestBuilder
    {
        public RenderPacket Packet(ResourceHandle? mesh = null) => new(
            1, 1, Extent, new Vector4(0, 0, 0, 1), new CameraState(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f),
            [new RenderInstance(mesh ?? Mesh1, Material1, Matrix4x4.Identity)]);
    }

    private sealed class BuilderGame(SceneLighting lighting) : IGame
    {
        public string Name => "Builder";

        public RenderProfile Profile => RenderProfile.G1Lit;

        public void Initialize(GameContext context)
        {
        }

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame) => frame.Lighting = lighting;

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    /// <summary>Null renderer that keeps the resource batch, to encode it for the golden file.</summary>
    private sealed class RecordingRenderer : IRenderer
    {
        private readonly NullRenderer _inner = new();

        public ResourceBatch? Resources { get; private set; }

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

    /// <summary>A third-party style renderer that only implements G0Unlit and ignores the requested profile.</summary>
    private sealed class G0OnlyRenderer : IRenderer
    {
        public RenderCapabilities Capabilities { get; } = new("G0Only", "None", "none", RenderProfile.G0Unlit, SupportsCapture: false) { SupportsSprites = true };

        public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void ApplyResources(ResourceBatch resources)
        {
        }

        public void Resize(RenderExtent extent)
        {
        }

        public void Render(RenderPacket packet)
        {
        }

        public void Dispose()
        {
        }
    }
}
