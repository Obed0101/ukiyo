using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ukiyo.Import;

/// <summary>
/// PNG (+ atlas JSON) → packed atlas → C# source. Pure: the same request always produces the same bytes, with no
/// timestamps, absolute paths or machine-specific text, so generated files can be checked in and verified with
/// <c>--check</c>.
/// </summary>
public static class AtlasImporter
{
    /// <summary>Bumped when the generated file format changes; part of the content hash.</summary>
    public const int FormatVersion = 1;

    public static ImportResult Import(ImportRequest request)
    {
        var className = Names.RequireIdentifier(request.ClassName, "class");
        var ns = Names.RequireNamespace(request.Namespace);
        var (frames, animations) = SourceReader.Read(request.Sources);
        var atlas = AtlasPacker.Pack(frames, animations, request.Extrude, request.MaxWidth);
        var sources = SourceHashes(request.Sources);
        var hash = ContentHash(atlas);
        var code = CodeEmitter.Emit(new CodeEmitter.Input(className, ns, atlas, hash, sources, request.RegenerateCommand));
        return new ImportResult(atlas, hash, sources, code);
    }

    /// <summary>SHA-256 of every source file (PNG and atlas JSON), in input order, for the generated header.</summary>
    private static List<(string Path, string Sha256)> SourceHashes(IReadOnlyList<ImportSource> sources)
    {
        var hashes = new List<(string, string)>();
        foreach (var source in sources)
        {
            hashes.Add((source.Path, Hex(SHA256.HashData(source.Png))));
            if (source.AtlasJson is { } json)
            {
                hashes.Add((Path.ChangeExtension(source.Path, ".json"), Hex(SHA256.HashData(json))));
            }
        }

        return hashes;
    }

    /// <summary>
    /// SHA-256 of what the game receives: format version, size, extrusion, frame table, animations and pixels.
    /// Independent of source file names and metadata, so re-exporting identical art keeps the hash.
    /// </summary>
    public static string ContentHash(PackedAtlas atlas)
    {
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"ukiyo-atlas/{FormatVersion}\n{atlas.Width}x{atlas.Height} extrude {atlas.Extrude}\n");
        foreach (var f in atlas.Frames)
        {
            table.Append(CultureInfo.InvariantCulture, $"frame {f.Name} {f.X} {f.Y} {f.Width} {f.Height}\n");
        }

        foreach (var a in atlas.Animations)
        {
            table.Append(CultureInfo.InvariantCulture, $"animation {a.Name} {a.FramesPerSecond.ToString("R", CultureInfo.InvariantCulture)} {(a.Loop ? "loop" : "once")} {string.Join(',', a.Frames)}\n");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(table.ToString()));
        sha.AppendData(atlas.Rgba);
        return Hex(sha.GetHashAndReset());
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
