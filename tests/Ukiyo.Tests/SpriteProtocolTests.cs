using System.Buffers.Binary;
using System.Numerics;
using Ukiyo.Rendering;
using Xunit;

namespace Ukiyo.Tests;

public sealed class SpriteProtocolTests
{
    private static readonly ResourceHandle Texture1 = new(ResourceKind.Texture, 0, 1);

    private static TextureData Checker() => new(2, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128], TextureFilter.Nearest);

    private static SpriteInstance Sprite(SpriteSpace space = SpriteSpace.Screen, int layer = 0) =>
        new(Texture1, space, new Vector2(10, 20), new Vector2(16, 16), Vector2.Zero, 0.25f, new Vector4(0, 0, 1, 1), new Vector4(1, 0.5f, 0.25f, 1), layer);

    private static RenderPacket Frame(params SpriteInstance[] sprites) =>
        new RenderPacket(3, 42, new RenderExtent(320, 180, 1f), Vector4.One, new CameraState(new Vector3(0, 0, 3), Quaternion.Identity, 1f, 0.1f, 100f), [])
        {
            Camera2D = new Camera2D(new Vector2(4, 5), 12f),
            Sprites = sprites,
        };

    [Fact]
    public void Texture_resource_round_trips_pixels_size_and_filter()
    {
        var batch = new ResourceBatch([ResourceCommand.CreateTexture(Texture1, Checker() with { Filter = TextureFilter.Linear })]);
        var decoded = PacketCodec.DecodeResources(PacketCodec.Encode(batch)).Commands[0];

        Assert.Equal(ResourceCommandKind.CreateTexture, decoded.Kind);
        Assert.Equal(Checker().Rgba, decoded.Texture!.Rgba);
        Assert.Equal((2, 2, TextureFilter.Linear), (decoded.Texture.Width, decoded.Texture.Height, decoded.Texture.Filter));
    }

    [Fact]
    public void Frame_with_sprites_sets_the_flag_and_round_trips_every_field()
    {
        var bytes = PacketCodec.Encode(Frame(Sprite(SpriteSpace.World, layer: -3), Sprite()));
        Assert.Equal(PacketCodec.FlagSprites, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));

        var decoded = PacketCodec.DecodeFrame(bytes);
        Assert.Equal(new Camera2D(new Vector2(4, 5), 12f), decoded.Camera2D);
        Assert.Equal(new[] { Sprite(SpriteSpace.World, layer: -3), Sprite() }, decoded.Sprites.ToArray());
    }

    [Fact]
    public void Frame_without_sprites_keeps_the_exact_g0_layout()
    {
        var bytes = PacketCodec.Encode(Frame());
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(PacketCodec.HeaderSize + 16 + 16 + 40 + 4, bytes.Length);
    }

    [Fact]
    public void Unknown_header_flags_are_rejected()
    {
        var bytes = PacketCodec.Encode(Frame());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0x8);
        Assert.Equal(RenderErrorCode.InvalidPacket, Assert.Throws<RenderException>(() => PacketCodec.DecodeFrame(bytes)).Code);
    }

    [Fact]
    public void Texture_with_wrong_byte_count_is_rejected()
    {
        var texture = new TextureData(4, 4, new byte[10]);
        Assert.Equal(RenderErrorCode.OutOfRange, Assert.Throws<RenderException>(() => RenderValidation.Validate(texture)).Code);
    }

    [Fact]
    public void Sprite_using_a_mesh_handle_as_texture_is_rejected()
    {
        var sprite = Sprite() with { Texture = new ResourceHandle(ResourceKind.Mesh, 0, 1) };
        Assert.Equal(RenderErrorCode.WrongResourceKind, Assert.Throws<RenderException>(() => RenderValidation.Validate(Frame(sprite))).Code);
    }

    [Fact]
    public void Non_finite_sprite_values_are_rejected()
    {
        var sprite = Sprite() with { Rotation = float.NaN };
        Assert.Equal(RenderErrorCode.NonFiniteValue, Assert.Throws<RenderException>(() => RenderValidation.Validate(Frame(sprite))).Code);
    }

    [Fact]
    public void Png_encoder_output_decodes_back_to_the_same_pixels()
    {
        var texture = Checker();
        var image = PngDecoder.Decode(PngEncoder.EncodeRgba(texture.Rgba, 2, 2));
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal(texture.Rgba, image.Rgba);
    }

    [Fact]
    public void Png_decoder_rejects_non_png_bytes()
    {
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode("not a png at all, really"u8));
    }

    [Fact]
    public void Screen_sprite_quad_lands_on_its_pixels_and_world_sprite_follows_the_2d_camera()
    {
        Span<SpriteVertex> quad = stackalloc SpriteVertex[4];
        var viewport = new RenderExtent(200, 100, 1f);
        var screen = Sprite() with { Rotation = 0f };
        SpriteGeometry.Quad(screen, Camera2D.Default, viewport, quad);
        Assert.Equal(new Vector2(10, 20), quad[0].Pixel);
        Assert.Equal(new Vector2(26, 36), quad[2].Pixel);

        var world = new SpriteInstance(Texture1, SpriteSpace.World, Vector2.Zero, new Vector2(2, 2), new Vector2(0.5f), 0f, SpriteInstance.FullUv, Vector4.One, 0);
        SpriteGeometry.Quad(world, new Camera2D(Vector2.Zero, 10f), viewport, quad);

        // 10 units tall in 100 px: 10 px per unit. Top-left of the image is up-left of the center.
        Assert.Equal(new Vector2(90, 40), quad[0].Pixel);
        Assert.Equal(new Vector2(110, 60), quad[2].Pixel);
        Assert.Equal(Vector2.Zero, SpriteGeometry.ScreenToWorld(new Vector2(100, 50), new Camera2D(Vector2.Zero, 10f), viewport));
    }

    [Fact]
    public void Draw_order_is_world_then_screen_then_layer_then_submission()
    {
        var sprites = new[] { Sprite(SpriteSpace.Screen, 0), Sprite(SpriteSpace.World, 5), Sprite(SpriteSpace.World, 1), Sprite(SpriteSpace.Screen, 0) };
        Assert.Equal(new[] { 2, 1, 0, 3 }, SpriteGeometry.DrawOrder(sprites));
    }
}
