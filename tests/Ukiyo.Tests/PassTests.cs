using System.Buffers.Binary;
using System.Numerics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.LitCube;
using Ukiyo.Samples.Minimap;
using Ukiyo.Samples.RotatingCube;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>Multi-camera passes: protocol (frame flag 0x4), validation, frame assembly, CPU composition and per-pass captures.</summary>
public sealed class PassTests
{
    private static readonly RenderExtent Extent = new(160, 90, 1f);
    private static readonly RenderExtent GoldenExtent = new(640, 480, 1f);
    private static readonly ResourceHandle White = new(ResourceKind.Texture, 0, 1);
    private static readonly Vector4 Red = new(1, 0, 0, 1);
    private static readonly Vector4 Blue = new(0, 0, 1, 1);
    private static readonly Vector4 Green = new(0, 1, 0, 1);
    private static readonly PassRect Corner = new(100, 10, 50, 40);
    private static readonly CameraState Camera = new(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f);

    private static readonly SourceIdentity NoSource = new(new Dictionary<string, string>());

    [Fact]
    public async Task Single_camera_games_send_one_packet_without_a_pass_section_so_their_bytes_do_not_change()
    {
        foreach (IGame game in new IGame[] { new CubeGame(), new LitCubeGame() })
        {
            var runtime = new GameRuntime(game, NoSource);
            using var renderer = new NullRenderer();
            await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
            var packet = runtime.RenderFrame(Extent);

            Assert.Single(runtime.LastFrame);
            Assert.Null(packet.Pass);
            Assert.Equal(1, runtime.PassCount);
            Assert.Equal(0, runtime.PrimaryPass);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(PacketCodec.Encode(packet).AsSpan(6)) & PacketCodec.FlagPasses);
            Assert.Equal(1, renderer.FramesRendered);
            Assert.Equal(0, renderer.PassesRendered);
        }
    }

    [Fact]
    public async Task Minimap_frame_is_two_consecutive_packets_and_the_pass_section_is_the_last_32_bytes()
    {
        var (_, packets) = await MinimapFrameAsync(GoldenExtent, tick: 60);
        Assert.Equal(2, packets.Length);
        Assert.Equal(packets[0].Sequence + 1, packets[1].Sequence);
        Assert.Equal(new FramePass(0, 2, 0, PassRect.Covering(GoldenExtent)), packets[0].Pass);
        Assert.Equal(new FramePass(1, 2, 0, MinimapGame.MinimapRect(GoldenExtent)), packets[1].Pass);

        var main = PacketCodec.Encode(packets[0]);
        var map = PacketCodec.Encode(packets[1]);
        Assert.Equal(PacketCodec.FlagLighting | PacketCodec.FlagPasses, BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(6)));
        Assert.Equal(PacketCodec.FlagSprites | PacketCodec.FlagLighting | PacketCodec.FlagPasses, BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(6)));

        var rect = MinimapGame.MinimapRect(GoldenExtent);
        uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(map.Length - PacketCodec.PassSectionSize + index * 4));
        Assert.Equal([1u, 2u, 0u, 0u, (uint)rect.X, (uint)rect.Y, (uint)rect.Width, (uint)rect.Height], Enumerable.Range(0, 8).Select(Word));

        var decoded = PacketCodec.DecodeFrame(map);
        Assert.Equal(packets[1].Pass, decoded.Pass);
        Assert.Equal(packets[1].Sprites, decoded.Sprites);
        Assert.Equal(MinimapGame.MapLighting.Ambient, decoded.Lighting!.Ambient);
    }

    /// <summary>
    /// The JS decoder (web/three-adapter/passes.test.js) reads these same files. Regenerate with UKIYO_UPDATE_GOLDEN=1
    /// only for a deliberate protocol change; the G0 and lit fixtures must never change (BridgeTests, LightingTests).
    /// </summary>
    [Fact]
    public async Task Minimap_packets_match_the_golden_files_shared_with_the_js_decoder()
    {
        var fixtures = Path.Combine(CoreTests.RepoRoot(), "web", "three-adapter", "fixtures");
        var recorder = new RecordingRenderer();
        var runtime = new GameRuntime(new MinimapGame(), NoSource);
        await runtime.StartAsync(recorder, new RenderConfiguration("golden", GoldenExtent));
        runtime.StepTo(60);
        runtime.RenderFrame(GoldenExtent);
        var files = new Dictionary<string, byte[]>
        {
            ["minimap-resources.bin"] = PacketCodec.Encode(recorder.Resources!),
            ["minimap-frame-tick60-pass0.bin"] = PacketCodec.Encode(runtime.LastFrame[0]),
            ["minimap-frame-tick60-pass1.bin"] = PacketCodec.Encode(runtime.LastFrame[1]),
        };

        foreach (var (name, bytes) in files)
        {
            if (Environment.GetEnvironmentVariable("UKIYO_UPDATE_GOLDEN") == "1")
            {
                await File.WriteAllBytesAsync(Path.Combine(fixtures, name), bytes);
            }

            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixtures, name)), bytes);
        }
    }

    [Theory]
    [InlineData(0, 0, 1, 0, 0, 10, 10)]      // count 0
    [InlineData(0, 5, 0, 0, 0, 10, 10)]      // count above MaxPasses
    [InlineData(2, 2, 0, 0, 0, 10, 10)]      // id >= count
    [InlineData(0, 2, 2, 0, 0, 10, 10)]      // primary >= count
    [InlineData(0, 1, 0, -1, 0, 10, 10)]     // negative x
    [InlineData(0, 1, 0, 0, 0, 0, 10)]       // empty rect
    [InlineData(0, 1, 0, 151, 0, 10, 10)]    // right edge outside 160
    [InlineData(0, 1, 0, 0, 81, 10, 10)]     // bottom edge outside 90
    public void Invalid_pass_ids_counts_and_rects_fail_with_out_of_range(int id, int count, int primary, int x, int y, int width, int height)
    {
        var packet = Packet(1, new FramePass(id, count, primary, new PassRect(x, y, width, height)), Red);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(packet)).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.Encode(packet)).Code);
    }

    [Fact]
    public async Task Decoding_malformed_pass_sections_fails_with_typed_errors()
    {
        var golden = (await MinimapFrameAsync(GoldenExtent, tick: 60)).Packets[1];
        byte[] Mutated(int word, uint value)
        {
            var bytes = PacketCodec.Encode(golden);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - PacketCodec.PassSectionSize + word * 4), value);
            return bytes;
        }

        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(Mutated(1, 5))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(Mutated(2, 7))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(Mutated(6, 10_000))).Code);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(Mutated(4, 0x8000_0000))).Code);

        var unknownFlag = PacketCodec.Encode(golden);
        BinaryPrimitives.WriteUInt16LittleEndian(unknownFlag.AsSpan(6), (ushort)(PacketCodec.FlagSprites | PacketCodec.FlagLighting | PacketCodec.FlagPasses | 0x8));
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(unknownFlag)).Code);

        var truncated = PacketCodec.Encode(golden)[..^4];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(12), (uint)truncated.Length);
        Assert.Equal(RenderErrorCode.TruncatedPayload, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(truncated)).Code);
    }

    [Fact]
    public void An_empty_drawing_buffer_skips_the_rect_checks_because_nothing_is_drawn()
    {
        var minimized = Packet(1, new FramePass(1, 2, 0, MinimapGame.MinimapRect(default)), Red) with { Viewport = default };
        RenderValidation.Validate(minimized);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(minimized with { Pass = new FramePass(0, 2, 0, new PassRect(-1, 0, 0, 0)) })).Code);
    }

    [Fact]
    public async Task Passes_out_of_order_or_interrupted_fail_with_invalid_packet_on_every_renderer()
    {
        foreach (var renderer in await RenderersAsync())
        {
            using (renderer)
            {
                var full = PassRect.Covering(Extent);
                RenderPacket Pass(uint sequence, int id, long tick = 1, int count = 2) => Packet(sequence, new FramePass(id, count, 0, id == 0 ? full : Corner), Red) with { Tick = tick };

                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Pass(1, 1))).Code);

                renderer.Render(Pass(2, 0));
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Packet(3, null, Red))).Code);

                renderer.Render(Pass(4, 0));
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Pass(6, 1))).Code);

                renderer.Render(Pass(7, 0));
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Pass(8, 1, tick: 2))).Code);

                renderer.Render(Pass(9, 0, count: 3));
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Pass(10, 1, count: 2))).Code);

                renderer.Render(Pass(11, 0));
                Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => renderer.Render(Pass(12, 0))).Code);

                // After a rejected frame the next one starts clean.
                renderer.Render(Pass(13, 0));
                renderer.Render(Pass(14, 1));
                renderer.Render(Packet(15, null, Red));
            }
        }
    }

    [Fact]
    public async Task A_lit_material_in_a_pass_needs_that_pass_own_lighting_section()
    {
        var runtime = new GameRuntime(new PassGame(lightMap: false), NoSource);
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => runtime.RenderFrame(Extent)).Code);

        var lit = new GameRuntime(new PassGame(lightMap: true), NoSource);
        using var litRenderer = new SoftwareRenderer();
        await lit.StartAsync(litRenderer, new RenderConfiguration("test", Extent));
        lit.RenderFrame(Extent);
    }

    [Fact]
    public async Task Cpu_renderer_composes_passes_over_pass_0_clear_and_keeps_each_pass_image_whole()
    {
        using var renderer = await StartSoftwareAsync();
        var left = new PassRect(0, 0, 80, 90);
        renderer.Render(Packet(1, new FramePass(0, 3, 1, left), Red));
        renderer.Render(Packet(2, new FramePass(1, 3, 1, new PassRect(60, 20, 60, 40)), Blue));
        renderer.Render(Packet(3, new FramePass(2, 3, 1, Corner), Green));

        var frame = await CaptureAsync(renderer, pass: null);
        Assert.Equal((160, 90), (frame.Width, frame.Height));
        Assert.Equal((255, 0, 0), Pixel(frame, 10, 10));     // pass 0
        Assert.Equal((0, 0, 255), Pixel(frame, 70, 30));     // pass 1 over pass 0
        Assert.Equal((0, 255, 0), Pixel(frame, 110, 30));    // pass 2 over pass 1
        Assert.Equal((255, 0, 0), Pixel(frame, 155, 85));    // covered by no pass: pass 0's clear color

        var main = await CaptureAsync(renderer, pass: 0);
        var middle = await CaptureAsync(renderer, pass: 1);
        Assert.Equal((80, 90), (main.Width, main.Height));
        Assert.Equal((255, 0, 0), Pixel(main, 70, 30));      // pass 0 alone, under pass 1
        Assert.Equal((60, 40), (middle.Width, middle.Height));
        Assert.Equal((0, 0, 255), Pixel(middle, 55, 15));    // pass 1 alone, under pass 2
        Assert.Equal(RenderErrorCode.OutOfRange, (await Assert.ThrowsAsync<RenderException>(() => CaptureAsync(renderer, pass: 3))).Code);
    }

    [Fact]
    public async Task Screen_sprites_of_a_pass_are_in_pass_local_pixels()
    {
        using var renderer = await StartSoftwareAsync();
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateTexture(White, new TextureData(1, 1, [255, 255, 255, 255]))]));
        var marker = new SpriteInstance(White, SpriteSpace.Screen, Vector2.Zero, new Vector2(4, 4), Vector2.Zero, 0f, SpriteInstance.FullUv, Vector4.One, 0);
        renderer.Render(Packet(1, new FramePass(0, 2, 0, PassRect.Covering(Extent)), Red));
        renderer.Render(Packet(2, new FramePass(1, 2, 0, Corner), Blue) with { Sprites = [marker] });

        var frame = await CaptureAsync(renderer, pass: null);
        Assert.Equal((255, 255, 255), Pixel(frame, Corner.X + 1, Corner.Y + 1));
        Assert.Equal((0, 0, 255), Pixel(frame, Corner.X + 5, Corner.Y + 5));
        Assert.Equal((255, 0, 0), Pixel(frame, 1, 1));
    }

    [Fact]
    public async Task A_pass_image_is_the_frame_a_single_camera_of_that_size_would_render()
    {
        var (runtime, packets) = await MinimapFrameAsync(new RenderExtent(480, 320, 1f), tick: 180);
        var map = packets[1];
        var rect = map.Pass!.Value.Rect;
        var passImage = await CaptureAsync((SoftwareRenderer)runtime.Renderer, pass: 1, tick: map.Tick);

        // The same packet without its pass section, on a renderer whose whole drawing buffer is the rect's size.
        var alone = new GameRuntime(new MinimapGame(), NoSource);
        using var single = new SoftwareRenderer();
        var size = new RenderExtent(rect.Width, rect.Height, 1f);
        await alone.StartAsync(single, new RenderConfiguration("test", size));
        single.Render(map with { Sequence = 1, Viewport = size, Pass = null });
        var singleImage = await CaptureAsync(single, pass: null, tick: map.Tick);

        Assert.Equal((rect.Width, rect.Height), (passImage.Width, passImage.Height));
        Assert.Equal(singleImage.Rgba, passImage.Rgba);
    }

    [Fact]
    public async Task Capture_rules_incomplete_frames_and_single_camera_pass_0()
    {
        using var renderer = await StartSoftwareAsync();
        renderer.Render(Packet(1, null, Red));
        Assert.Equal((await CaptureAsync(renderer, pass: null)).Rgba, (await CaptureAsync(renderer, pass: 0)).Rgba);
        Assert.Equal(RenderErrorCode.OutOfRange, (await Assert.ThrowsAsync<RenderException>(() => CaptureAsync(renderer, pass: 1))).Code);

        renderer.Render(Packet(2, new FramePass(0, 2, 0, PassRect.Covering(Extent)), Red));
        Assert.Equal(RenderErrorCode.InvalidPacket, (await Assert.ThrowsAsync<RenderException>(() => CaptureAsync(renderer, pass: null))).Code);
    }

    [Fact]
    public async Task Frame_builder_limits_passes_and_gives_each_its_own_viewport()
    {
        var checkedBuilder = false;
        var runtime = new GameRuntime(new ExtractGame(frame =>
        {
            var map = frame.AddPass(Corner);
            Assert.Equal(new RenderExtent(Corner.Width, Corner.Height, 1f), map.Viewport);
            Assert.Equal(Extent, map.DrawingBuffer);
            Assert.Equal(Extent, frame.Viewport);
            Assert.Throws<InvalidOperationException>(() => map.AddPass(Corner));

            frame.AddPass(Corner);
            frame.AddPass(Corner);
            Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => frame.AddPass(Corner)).Code);
            checkedBuilder = true;
        }), NoSource);
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        runtime.RenderFrame(Extent);

        Assert.True(checkedBuilder);
        Assert.Equal(RenderValidation.MaxPasses, runtime.LastFrame.Count);
        Assert.Equal(RenderValidation.MaxPasses, renderer.PassesRendered);
    }

    private sealed class ExtractGame(Action<FrameBuilder> extract) : IGame
    {
        public string Name => "ExtractGame";

        public void Initialize(GameContext context)
        {
        }

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame) => extract(frame);

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    [Fact]
    public async Task A_primary_pass_outside_the_frame_fails_with_out_of_range()
    {
        var runtime = new GameRuntime(new PassGame(lightMap: true) { Primary = 2 }, NoSource);
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => runtime.RenderFrame(Extent)).Code);
    }

    [Fact]
    public async Task A_renderer_without_pass_support_refuses_multi_camera_frames_with_unsupported_feature()
    {
        var runtime = new GameRuntime(new MinimapGame(), NoSource);
        await runtime.StartAsync(new NoPassesRenderer(), new RenderConfiguration("test", Extent));
        Assert.Equal(RenderErrorCode.UnsupportedFeature, Assert.Throws<RenderException>(() => runtime.RenderFrame(Extent)).Code);
    }

    [Fact]
    public async Task Minimap_sample_is_deterministic_with_two_passes_and_the_main_view_as_primary()
    {
        var extent = new RenderExtent(320, 200, 1f);
        async Task<(string Snapshot, byte[] Frame, byte[] Map, int Passes, int Primary)> Run()
        {
            var (runtime, _) = await MinimapFrameAsync(extent, tick: 120);
            var frame = (await runtime.CaptureAsync())!;
            var map = (await runtime.CaptureAsync(pass: 1))!;
            Assert.Equal((320, 200), (frame.Width, frame.Height));
            Assert.Equal((MinimapGame.MinimapRect(extent).Width, MinimapGame.MinimapRect(extent).Height), (map.Width, map.Height));
            return (runtime.SnapshotJson(), PngDecoder.Decode(frame.Png).Rgba, PngDecoder.Decode(map.Png).Rgba, runtime.PassCount, runtime.PrimaryPass);
        }

        var a = await Run();
        var b = await Run();
        Assert.Equal(a.Snapshot, b.Snapshot);
        Assert.Equal(a.Frame, b.Frame);
        Assert.Equal(a.Map, b.Map);
        Assert.Equal((2, 0), (a.Passes, a.Primary));
        Assert.Contains("\"rover\"", a.Snapshot);
    }

    [Fact]
    public async Task Render_frame_returns_the_primary_pass_and_null_renderer_counts_frames_and_passes()
    {
        var runtime = new GameRuntime(new PassGame(lightMap: true) { Primary = 1 }, NoSource);
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        var primary = runtime.RenderFrame(Extent);

        Assert.Equal(1, primary.Pass!.Value.Id);
        Assert.True(primary.Pass.Value.IsPrimary);
        Assert.Equal(1, renderer.FramesRendered);
        Assert.Equal(2, renderer.PassesRendered);
    }

    private static RenderPacket Packet(uint sequence, FramePass? pass, Vector4 clear) =>
        new(sequence, 1, Extent, clear, Camera, []) { Pass = pass };

    private static async Task<(GameRuntime Runtime, RenderPacket[] Packets)> MinimapFrameAsync(RenderExtent extent, long tick)
    {
        var runtime = new GameRuntime(new MinimapGame(), NoSource);
        var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent));
        runtime.StepTo(tick);
        runtime.RenderFrame(extent);
        return (runtime, [.. runtime.LastFrame]);
    }

    private static async Task<IRenderer[]> RenderersAsync()
    {
        IRenderer[] renderers = [new SoftwareRenderer(), new NullRenderer()];
        foreach (var renderer in renderers)
        {
            await renderer.InitializeAsync(new RenderConfiguration("test", Extent), CancellationToken.None);
        }

        return renderers;
    }

    private static async Task<SoftwareRenderer> StartSoftwareAsync()
    {
        var renderer = new SoftwareRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", Extent), CancellationToken.None);
        return renderer;
    }

    private static async Task<DecodedImage> CaptureAsync(SoftwareRenderer renderer, int? pass, long tick = 1) =>
        PngDecoder.Decode((await renderer.CaptureAsync(new CaptureRequest(tick) { Pass = pass }, CancellationToken.None)).Png);

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        var p = (y * image.Width + x) * 4;
        return (image.Rgba[p], image.Rgba[p + 1], image.Rgba[p + 2]);
    }

    /// <summary>Two passes of one lit quad; the second pass has lighting only when <paramref name="lightMap"/> is set.</summary>
    private sealed class PassGame(bool lightMap) : IGame
    {
        private ResourceHandle _mesh;
        private ResourceHandle _material;

        public int Primary { get; init; }

        public string Name => "PassGame";

        public RenderProfile Profile => RenderProfile.G1Lit;

        public void Initialize(GameContext context)
        {
            _mesh = context.CreateMesh(new MeshData(
                [new(new Vector3(-1, 1, 0), Vector3.One), new(new Vector3(-1, -1, 0), Vector3.One), new(new Vector3(1, -1, 0), Vector3.One), new(new Vector3(1, 1, 0), Vector3.One)],
                [0, 1, 2, 0, 2, 3])
            {
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            });
            _material = context.CreateMaterial(new MaterialData(Vector4.One, false) { Lit = true });
        }

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame)
        {
            frame.Camera = Camera;
            frame.Lighting = new SceneLighting(new Vector3(0.5f), []);
            frame.Draw(_mesh, _material, Matrix4x4.Identity);
            frame.PrimaryPass = Primary;
            var map = frame.AddPass(Corner);
            map.Camera = Camera;
            map.Lighting = lightMap ? new SceneLighting(new Vector3(0.2f), []) : null;
            map.Draw(_mesh, _material, Matrix4x4.Identity);
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    /// <summary>Keeps the resource batch to encode it for the golden file.</summary>
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

    /// <summary>A third-party style renderer that never declared <see cref="RenderCapabilities.SupportsPasses"/>.</summary>
    private sealed class NoPassesRenderer : IRenderer
    {
        public RenderCapabilities Capabilities { get; } = new("NoPasses", "None", "none", RenderProfile.G1Lit, SupportsCapture: false) { SupportsSprites = true };

        public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void ApplyResources(ResourceBatch resources)
        {
        }

        public void Resize(RenderExtent extent)
        {
        }

        public void Render(RenderPacket packet) => throw new InvalidOperationException("the runtime must refuse the frame before it reaches the renderer");

        public void Dispose()
        {
        }
    }
}
