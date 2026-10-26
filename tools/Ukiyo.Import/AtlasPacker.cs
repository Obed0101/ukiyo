using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Import;

/// <summary>
/// Deterministic shelf packer with edge extrusion (rules in docs/assets.md):
/// cells are frames grown by <c>extrude</c> pixels on each side; they are placed tallest first, then widest, then by
/// name (ordinal), left to right on shelves no wider than the shelf limit; the extrusion ring repeats the frame's
/// outermost pixels so nearest and linear sampling at a frame edge never reads a neighbor.
/// </summary>
internal static class AtlasPacker
{
    public const int MaxExtrude = 8;

    public static PackedAtlas Pack(IReadOnlyList<SourceFrame> frames, IReadOnlyList<PackedAnimation> animations, int extrude, int maxWidth)
    {
        if (extrude is < 0 or > MaxExtrude)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"extrude {extrude} outside 0..{MaxExtrude}");
        }

        if (maxWidth is < 1 or > RenderValidation.MaxTextureSize)
        {
            throw new ImportException(ImportErrorCode.InvalidSpec, $"maxWidth {maxWidth} outside 1..{RenderValidation.MaxTextureSize}");
        }

        var order = frames.OrderByDescending(f => f.Height).ThenByDescending(f => f.Width).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        var shelfLimit = ShelfLimit(order, extrude, maxWidth);
        var placed = new List<(SourceFrame Frame, int X, int Y)>(order.Count);
        int x = 0, y = 0, shelfHeight = 0, width = 0;
        foreach (var frame in order)
        {
            int cellWidth = frame.Width + 2 * extrude, cellHeight = frame.Height + 2 * extrude;
            if (x > 0 && x + cellWidth > shelfLimit)
            {
                (x, y, shelfHeight) = (0, y + shelfHeight, 0);
            }

            placed.Add((frame, x + extrude, y + extrude));
            x += cellWidth;
            shelfHeight = Math.Max(shelfHeight, cellHeight);
            width = Math.Max(width, x);
        }

        var height = y + shelfHeight;
        if (height > RenderValidation.MaxTextureSize)
        {
            throw new ImportException(ImportErrorCode.AtlasTooLarge, $"packed atlas is {width}x{height}, over the {RenderValidation.MaxTextureSize} px texture limit; split the inputs into several imports");
        }

        var rgba = new byte[width * height * 4];
        foreach (var (frame, frameX, frameY) in placed)
        {
            CopyExtruded(frame, rgba, width, frameX, frameY, extrude);
        }

        return new PackedAtlas(
            width,
            height,
            rgba,
            extrude,
            [.. placed.Select(p => new PackedFrame(p.Frame.Name, p.X, p.Y, p.Frame.Width, p.Frame.Height)).OrderBy(f => f.Name, StringComparer.Ordinal)],
            [.. animations.OrderBy(a => a.Name, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Shelf width: the smallest power of two at least the square root of the total cell area (a near-square atlas),
    /// never narrower than the widest cell and never wider than <paramref name="maxWidth"/>. The image is then trimmed
    /// to the cells actually placed, so a single frame with no extrusion keeps its exact size.
    /// </summary>
    private static int ShelfLimit(IReadOnlyList<SourceFrame> frames, int extrude, int maxWidth)
    {
        long area = 0;
        var widest = 0;
        foreach (var frame in frames)
        {
            int cellWidth = frame.Width + 2 * extrude, cellHeight = frame.Height + 2 * extrude;
            area += (long)cellWidth * cellHeight;
            widest = Math.Max(widest, cellWidth);
        }

        if (widest > maxWidth)
        {
            throw new ImportException(ImportErrorCode.AtlasTooLarge, $"a frame needs {widest} px with extrusion, wider than maxWidth {maxWidth}");
        }

        var square = (int)Math.Min(int.MaxValue / 2, (long)Math.Ceiling(Math.Sqrt(area)));
        return Math.Min(maxWidth, Math.Max(widest, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, square))));
    }

    /// <summary>Copies a frame and fills its extrusion ring with the nearest frame pixel (clamp to the frame's edge).</summary>
    private static void CopyExtruded(SourceFrame frame, byte[] atlas, int atlasWidth, int frameX, int frameY, int extrude)
    {
        var source = frame.Image;
        for (var dy = -extrude; dy < frame.Height + extrude; dy++)
        {
            var sy = frame.Y + Math.Clamp(dy, 0, frame.Height - 1);
            for (var dx = -extrude; dx < frame.Width + extrude; dx++)
            {
                var sx = frame.X + Math.Clamp(dx, 0, frame.Width - 1);
                Array.Copy(source.Rgba, (sy * source.Width + sx) * 4, atlas, ((frameY + dy) * atlasWidth + frameX + dx) * 4, 4);
            }
        }
    }
}
