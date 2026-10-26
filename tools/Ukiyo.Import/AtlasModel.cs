namespace Ukiyo.Import;

/// <summary>
/// One source image: PNG bytes, the optional atlas JSON that names its frames (the format the MCP sprite tools write),
/// and the path recorded in the generated header. Without atlas JSON the whole image is one frame named after the file.
/// </summary>
public sealed record ImportSource(string Path, byte[] Png, byte[]? AtlasJson = null);

/// <summary>What to import and how to name the generated class.</summary>
public sealed record ImportRequest(string ClassName, string Namespace, IReadOnlyList<ImportSource> Sources)
{
    /// <summary>Pixels of edge copy around every frame (0..8). See docs/assets.md, "Atlas rules".</summary>
    public int Extrude { get; init; } = 1;

    /// <summary>Widest atlas the packer may produce, in pixels (1..4096).</summary>
    public int MaxWidth { get; init; } = 1024;

    /// <summary>The command that regenerates the file, written into its header. Must not depend on the machine.</summary>
    public string? RegenerateCommand { get; init; }
}

/// <summary>A frame's rectangle inside the packed atlas, without its extrusion border.</summary>
public readonly record struct PackedFrame(string Name, int X, int Y, int Width, int Height);

public sealed record PackedAnimation(string Name, IReadOnlyList<string> Frames, float FramesPerSecond, bool Loop);

/// <summary>
/// The packed atlas exactly as the engine receives it: RGBA8, rows top to bottom, sRGB-encoded color, straight alpha
/// (the <c>CreateTexture</c> contract in docs/protocol.md). Frames and animations are sorted by name (ordinal).
/// </summary>
public sealed record PackedAtlas(int Width, int Height, byte[] Rgba, int Extrude, IReadOnlyList<PackedFrame> Frames, IReadOnlyList<PackedAnimation> Animations);

/// <summary>Generated source plus the data it encodes, so callers and tests can check one against the other.</summary>
public sealed record ImportResult(PackedAtlas Atlas, string ContentHash, IReadOnlyList<(string Path, string Sha256)> SourceHashes, string Code);
