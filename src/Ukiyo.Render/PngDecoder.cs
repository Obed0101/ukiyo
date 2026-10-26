using System.Buffers.Binary;
using System.IO.Compression;

namespace Ukiyo.Rendering;

public sealed record DecodedImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// PNG reader for game assets: non-interlaced, bit depth 8 for every color type plus 1/2/4-bit palettes and grayscale,
/// tRNS transparency. Output is RGBA8 exactly as stored (sRGB, straight alpha). Anything else is a typed error, never a
/// guess. Dependency-free (ZLibStream) so it runs the same on CoreCLR, NativeAOT and WebAssembly.
/// </summary>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static TextureData DecodeTexture(ReadOnlySpan<byte> png, TextureFilter filter = TextureFilter.Nearest)
    {
        var image = Decode(png);
        return new TextureData(image.Width, image.Height, image.Rgba, filter);
    }

    public static DecodedImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length + 12 || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("[PNG]: not a PNG file (bad signature)");
        }

        var offset = Signature.Length;
        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();
        var sawEnd = false;
        while (offset + 12 <= png.Length && !sawEnd)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                throw new InvalidDataException($"[PNG]: chunk at {offset} overruns the file");
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13)
                {
                    throw new InvalidDataException($"[PNG]: IHDR has {length} bytes, expected 13");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                if (data[12] != 0)
                {
                    throw new InvalidDataException("[PNG]: interlaced images are not supported; re-export without Adam7");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
            }

            offset += 12 + length;
        }

        if (width <= 0 || height <= 0 || width > RenderValidation.MaxTextureSize || height > RenderValidation.MaxTextureSize)
        {
            throw new InvalidDataException($"[PNG]: size {width}x{height} outside 1..{RenderValidation.MaxTextureSize}");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"[PNG]: unknown color type {colorType}"),
        };
        var depthOk = bitDepth == 8 || (colorType is 0 or 3 && bitDepth is 1 or 2 or 4);
        if (!depthOk)
        {
            throw new InvalidDataException($"[PNG]: bit depth {bitDepth} with color type {colorType} is not supported (use 8-bit)");
        }

        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("[PNG]: palette image without PLTE");
        }

        var bitsPerPixel = channels * bitDepth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var raw = Inflate(compressed.ToArray(), (stride + 1) * height);
        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filterType = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(current);
            Unfilter(filterType, current, previous, bytesPerPixel);
            WriteRow(current, rgba.AsSpan(y * width * 4, width * 4), width, colorType, bitDepth, palette, transparency);
            (previous, current) = (current, previous);
        }

        return new DecodedImage(width, height, rgba);
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expected];
        var read = 0;
        while (read < expected)
        {
            var n = zlib.Read(output, read, expected - read);
            if (n == 0)
            {
                throw new InvalidDataException($"[PNG]: image data ends after {read} of {expected} bytes");
            }

            read += n;
        }

        return output;
    }

    private static void Unfilter(byte filterType, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filterType)
        {
            case 0:
                return;
            case 1:
                for (var i = bpp; i < row.Length; i++)
                {
                    row[i] += row[i - bpp];
                }

                return;
            case 2:
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] += previous[i];
                }

                return;
            case 3:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= bpp ? row[i - bpp] : 0;
                    row[i] += (byte)((left + previous[i]) >> 1);
                }

                return;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    var a = i >= bpp ? row[i - bpp] : 0;
                    var b = previous[i];
                    var c = i >= bpp ? previous[i - bpp] : 0;
                    row[i] += Paeth(a, b, c);
                }

                return;
            default:
                throw new InvalidDataException($"[PNG]: unknown row filter {filterType}");
        }
    }

    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static void WriteRow(ReadOnlySpan<byte> row, Span<byte> destination, int width, int colorType, int bitDepth, byte[]? palette, byte[]? transparency)
    {
        for (var x = 0; x < width; x++)
        {
            byte r, g, b, a = 255;
            switch (colorType)
            {
                case 0:
                {
                    var value = Sample(row, x, bitDepth);
                    var gray = (byte)(value * 255 / ((1 << bitDepth) - 1));
                    r = g = b = gray;
                    if (transparency is { Length: >= 2 } && BinaryPrimitives.ReadUInt16BigEndian(transparency) == value)
                    {
                        a = 0;
                    }

                    break;
                }

                case 2:
                    r = row[x * 3];
                    g = row[x * 3 + 1];
                    b = row[x * 3 + 2];
                    if (transparency is { Length: >= 6 }
                        && BinaryPrimitives.ReadUInt16BigEndian(transparency) == r
                        && BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) == g
                        && BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)) == b)
                    {
                        a = 0;
                    }

                    break;
                case 3:
                {
                    var index = Sample(row, x, bitDepth);
                    if (index * 3 + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException($"[PNG]: palette index {index} outside {palette.Length / 3} entries");
                    }

                    r = palette[index * 3];
                    g = palette[index * 3 + 1];
                    b = palette[index * 3 + 2];
                    if (transparency is not null && index < transparency.Length)
                    {
                        a = transparency[index];
                    }

                    break;
                }

                case 4:
                    r = g = b = row[x * 2];
                    a = row[x * 2 + 1];
                    break;
                default:
                    r = row[x * 4];
                    g = row[x * 4 + 1];
                    b = row[x * 4 + 2];
                    a = row[x * 4 + 3];
                    break;
            }

            destination[x * 4] = r;
            destination[x * 4 + 1] = g;
            destination[x * 4 + 2] = b;
            destination[x * 4 + 3] = a;
        }
    }

    private static int Sample(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        if (bitDepth == 8)
        {
            return row[x];
        }

        var perByte = 8 / bitDepth;
        var shift = 8 - bitDepth * (x % perByte + 1);
        return (row[x / perByte] >> shift) & ((1 << bitDepth) - 1);
    }
}
