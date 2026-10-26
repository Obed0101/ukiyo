using System.Numerics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.RotatingCube;
using Xunit;

namespace Ukiyo.Tests;

public sealed class SoftwareRendererTests
{
    private static readonly RenderExtent Extent = new(160, 90, 1f);
    private static readonly ResourceHandle Texture1 = new(ResourceKind.Texture, 0, 1);

    private static async Task<SoftwareRenderer> StartAsync()
    {
        var renderer = new SoftwareRenderer();
        await renderer.InitializeAsync(new RenderConfiguration("test", Extent), CancellationToken.None);
        return renderer;
    }

    private static RenderPacket Empty(long tick, Vector4 clear) =>
        new(1, tick, Extent, clear, new CameraState(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f), []);

    private static async Task<DecodedImage> CaptureAsync(SoftwareRenderer renderer, long tick) =>
        PngDecoder.Decode((await renderer.CaptureAsync(new CaptureRequest(tick), CancellationToken.None)).Png);

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        var p = (y * image.Width + x) * 4;
        return (image.Rgba[p], image.Rgba[p + 1], image.Rgba[p + 2]);
    }

    [Fact]
    public async Task Clear_color_is_encoded_to_srgb_in_the_capture()
    {
        using var renderer = await StartAsync();
        // Linear 0.2159 is just above sRGB 128/255 (0.21586), so it must encode to 128.
        renderer.Render(Empty(0, new Vector4(0.2159f, 0.0f, 1f, 1f)));
        var image = await CaptureAsync(renderer, 0);

        Assert.Equal((Extent.Width, Extent.Height), (image.Width, image.Height));
        Assert.Equal(((byte)128, (byte)0, (byte)255), Pixel(image, 5, 5));
    }

    [Fact]
    public async Task Cube_covers_the_center_and_leaves_the_corners_clear()
    {
        var runtime = new GameRuntime(new CubeGame(), new SourceIdentity(new Dictionary<string, string>()));
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        runtime.StepTo(60);
        runtime.RenderFrame(Extent);
        var image = await CaptureAsync(renderer, 60);

        Assert.NotEqual(Pixel(image, 0, 0), Pixel(image, Extent.Width / 2, Extent.Height / 2));
        Assert.Equal(Pixel(image, 0, 0), Pixel(image, Extent.Width - 1, Extent.Height - 1));
    }

    [Fact]
    public async Task Screen_sprite_paints_its_rect_with_nearest_texels_and_alpha_blends()
    {
        using var renderer = await StartAsync();
        byte[] rgba = [255, 0, 0, 255, 0, 0, 255, 0];
        renderer.ApplyResources(new ResourceBatch([ResourceCommand.CreateTexture(Texture1, new TextureData(2, 1, rgba))]));
        var sprite = new SpriteInstance(Texture1, SpriteSpace.Screen, new Vector2(10, 10), new Vector2(20, 10), Vector2.Zero, 0f, SpriteInstance.FullUv, Vector4.One, 0);
        renderer.Render(Empty(7, new Vector4(0, 1, 0, 1)) with { Sprites = [sprite] });
        var image = await CaptureAsync(renderer, 7);

        Assert.Equal(((byte)255, (byte)0, (byte)0), Pixel(image, 12, 14));   // left texel: opaque red
        Assert.Equal(((byte)0, (byte)255, (byte)0), Pixel(image, 25, 14));   // right texel: transparent, clear shows
        Assert.Equal(((byte)0, (byte)255, (byte)0), Pixel(image, 40, 40));   // outside the sprite
    }

    [Fact]
    public async Task Capture_of_a_tick_that_was_not_rendered_is_rejected()
    {
        using var renderer = await StartAsync();
        renderer.Render(Empty(3, Vector4.One));
        await Assert.ThrowsAsync<RenderException>(async () => await renderer.CaptureAsync(new CaptureRequest(4), CancellationToken.None));
    }

    [Fact]
    public async Task Sprites_on_a_renderer_without_the_2d_layer_fail_loudly()
    {
        var runtime = new GameRuntime(new SpriteOnlyGame(), new SourceIdentity(new Dictionary<string, string>()));
        using var renderer = new NoSpriteRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", Extent));
        Assert.Equal(RenderErrorCode.UnsupportedFeature, Assert.Throws<RenderException>(() => runtime.RenderFrame(Extent)).Code);
    }

    private sealed class SpriteOnlyGame : IGame
    {
        private ResourceHandle _texture;

        public string Name => "SpriteOnly";

        public void Initialize(GameContext context) => _texture = context.CreateTexture(new TextureData(1, 1, [255, 255, 255, 255]));

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame) => frame.DrawScreenSprite(SpriteFrame.Whole(_texture, 1, 1), Vector2.Zero, Vector2.One);

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    /// <summary>A renderer that, like a G0-only backend, does not declare the sprite layer.</summary>
    private sealed class NoSpriteRenderer : IRenderer
    {
        public RenderCapabilities Capabilities { get; } = new("NoSprite", "None", "none", RenderProfile.G0Unlit, SupportsCapture: false);

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
