using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>
/// Sprites written as text: one string per row, one character per pixel, colors from a palette of sRGB hex values
/// ('.' is transparent unless the palette says otherwise). Lets a game or an agent author pixel art in code, diff it
/// and review it, with no image file. Frames of equal size can be laid side by side into one atlas.
/// </summary>
public static class PixelArt
{
    public static TextureData FromRows(IReadOnlyList<string> rows, IReadOnlyDictionary<char, uint> palette)
    {
        if (rows.Count == 0 || rows[0].Length == 0)
        {
            throw new ArgumentException("[PIXELART]: no rows");
        }

        var width = rows[0].Length;
        var height = rows.Count;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            if (rows[y].Length != width)
            {
                throw new ArgumentException($"[PIXELART]: row {y} has {rows[y].Length} pixels, expected {width}");
            }

            for (var x = 0; x < width; x++)
            {
                var c = rows[y][x];
                if (!palette.TryGetValue(c, out var argb))
                {
                    if (c == '.')
                    {
                        continue;
                    }

                    throw new ArgumentException($"[PIXELART]: '{c}' at ({x},{y}) is not in the palette");
                }

                var p = (y * width + x) * 4;
                rgba[p] = (byte)(argb >> 16);
                rgba[p + 1] = (byte)(argb >> 8);
                rgba[p + 2] = (byte)argb;
                rgba[p + 3] = (byte)(argb >> 24);
            }
        }

        return new TextureData(width, height, rgba, TextureFilter.Nearest);
    }

    /// <summary>Opaque color from 0xRRGGBB (sRGB) for palettes.</summary>
    public static uint Rgb(uint rgb) => 0xFF000000 | (rgb & 0xFFFFFF);

    /// <summary>Frames of identical size placed left to right in one texture (frame i at x = i * width).</summary>
    public static TextureData Strip(IReadOnlyList<IReadOnlyList<string>> frames, IReadOnlyDictionary<char, uint> palette)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("[PIXELART]: no frames");
        }

        var height = frames[0].Count;
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            rows[y] = string.Concat(frames.Select(frame => frame.Count == height
                ? frame[y]
                : throw new ArgumentException($"[PIXELART]: every frame needs {height} rows")));
        }

        return FromRows(rows, palette);
    }
}
