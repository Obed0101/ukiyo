using System.Numerics;

namespace Ukiyo.Rendering;

/// <summary>One sprite corner in drawing-buffer pixels (origin top-left, +Y down) with its texture coordinate.</summary>
public readonly record struct SpriteVertex(Vector2 Pixel, Vector2 Uv);

/// <summary>
/// The single definition of where a sprite lands. Every renderer (software, wgpu, the JS adapter mirrors it in
/// sprite-geometry.js) turns sprites into pixel-space quads with this math, so the 2D layer matches across targets.
/// </summary>
public static class SpriteGeometry
{
    private static readonly Vector2[] Corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    /// <summary>Quad corners in order top-left, top-right, bottom-right, bottom-left of the image. Triangles: (0,1,2), (0,2,3).</summary>
    public static void Quad(in SpriteInstance sprite, Camera2D camera, RenderExtent viewport, Span<SpriteVertex> destination)
    {
        if (destination.Length < 4)
        {
            throw new ArgumentException("[SPRITE]: destination needs 4 vertices", nameof(destination));
        }

        var (sin, cos) = MathF.SinCos(sprite.Rotation);
        var pixelsPerUnit = viewport.Height / camera.ViewHeight;
        for (var i = 0; i < 4; i++)
        {
            var corner = Corners[i];
            var offset = (corner - sprite.Pivot) * sprite.Size;
            Vector2 pixel;
            if (sprite.Space == SpriteSpace.Screen)
            {
                // +Y down: counter-clockwise on screen is the mirrored rotation.
                pixel = sprite.Position + new Vector2(offset.X * cos + offset.Y * sin, -offset.X * sin + offset.Y * cos);
            }
            else
            {
                var up = new Vector2(offset.X, -offset.Y);
                var world = sprite.Position + new Vector2(up.X * cos - up.Y * sin, up.X * sin + up.Y * cos);
                pixel = new Vector2(
                    (world.X - camera.Center.X) * pixelsPerUnit + viewport.Width * 0.5f,
                    viewport.Height * 0.5f - (world.Y - camera.Center.Y) * pixelsPerUnit);
            }

            var uv = new Vector2(
                sprite.Uv.X + (sprite.Uv.Z - sprite.Uv.X) * corner.X,
                sprite.Uv.Y + (sprite.Uv.W - sprite.Uv.Y) * corner.Y);
            destination[i] = new SpriteVertex(pixel, uv);
        }
    }

    /// <summary>Indices of <paramref name="sprites"/> in draw order: world before screen, then layer, then submission.</summary>
    public static int[] DrawOrder(IReadOnlyList<SpriteInstance> sprites)
    {
        var order = new int[sprites.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Array.Sort is not stable; the index tiebreak makes it so.
        Array.Sort(order, (a, b) =>
        {
            var sa = sprites[a];
            var sb = sprites[b];
            var bySpace = ((byte)sa.Space).CompareTo((byte)sb.Space);
            if (bySpace != 0)
            {
                return bySpace;
            }

            var byLayer = sa.Layer.CompareTo(sb.Layer);
            return byLayer != 0 ? byLayer : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>World position under a drawing-buffer pixel for a 2D camera (inverse of the world branch of <see cref="Quad"/>).</summary>
    public static Vector2 ScreenToWorld(Vector2 pixel, Camera2D camera, RenderExtent viewport)
    {
        var pixelsPerUnit = viewport.Height / camera.ViewHeight;
        return new Vector2(
            (pixel.X - viewport.Width * 0.5f) / pixelsPerUnit + camera.Center.X,
            (viewport.Height * 0.5f - pixel.Y) / pixelsPerUnit + camera.Center.Y);
    }
}

/// <summary>sRGB transfer functions (IEC 61966-2-1) used by CPU paths; GPU paths use *Srgb formats.</summary>
public static class ColorSpace
{
    private static readonly float[] SrgbToLinearTable = BuildTable();

    public static float SrgbToLinear(byte value) => SrgbToLinearTable[value];

    public static byte LinearToSrgb(float value)
    {
        if (!(value > 0))
        {
            return 0;
        }

        if (value >= 1)
        {
            return 255;
        }

        var encoded = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        return (byte)MathF.Round(encoded * 255f);
    }

    private static float[] BuildTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }
}
