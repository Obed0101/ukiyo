using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>A region of a texture: what a sprite draws. <see cref="PixelSize"/> keeps pixel art at integer scale.</summary>
public readonly record struct SpriteFrame(ResourceHandle Texture, Vector4 Uv, Vector2 PixelSize)
{
    public Vector4 FlippedUv => new(Uv.Z, Uv.Y, Uv.X, Uv.W);

    public static SpriteFrame Whole(ResourceHandle texture, int width, int height) => new(texture, SpriteInstance.FullUv, new Vector2(width, height));

    public static SpriteFrame FromPixels(ResourceHandle texture, int textureWidth, int textureHeight, int x, int y, int width, int height) =>
        new(texture, new Vector4(x / (float)textureWidth, y / (float)textureHeight, (x + width) / (float)textureWidth, (y + height) / (float)textureHeight), new Vector2(width, height));
}

/// <summary>Frames played at a fixed rate. Evaluated from ticks only, so animation is deterministic and replayable.</summary>
public sealed record SpriteAnimation(string Name, IReadOnlyList<SpriteFrame> Frames, float FramesPerSecond, bool Loop = true)
{
    public SpriteFrame At(long ticksSinceStart)
    {
        if (Frames.Count == 0)
        {
            throw new InvalidOperationException($"[SPRITE]: animation \"{Name}\" has no frames");
        }

        var index = (long)MathF.Floor(Math.Max(0, ticksSinceStart) * FramesPerSecond / Simulation.TickRate);
        return Frames[(int)(Loop ? index % Frames.Count : Math.Min(index, Frames.Count - 1))];
    }

    public bool IsFinished(long ticksSinceStart) =>
        !Loop && ticksSinceStart * FramesPerSecond / Simulation.TickRate >= Frames.Count;
}

/// <summary>
/// A texture plus named frames and animations, loaded from the atlas JSON written by the ukiyo sprite tools:
/// <c>{"image":"hero.png","frames":{"idle_0":{"x":0,"y":0,"w":16,"h":16}},"animations":{"idle":{"frames":["idle_0"],"fps":6,"loop":true}}}</c>.
/// A sheet without JSON can be cut into a grid with <see cref="FromGrid"/>.
/// </summary>
public sealed class SpriteSheet
{
    private readonly Dictionary<string, SpriteFrame> _frames;
    private readonly Dictionary<string, SpriteAnimation> _animations;

    private SpriteSheet(ResourceHandle texture, int width, int height, Dictionary<string, SpriteFrame> frames, Dictionary<string, SpriteAnimation> animations)
    {
        Texture = texture;
        Width = width;
        Height = height;
        _frames = frames;
        _animations = animations;
    }

    public ResourceHandle Texture { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyDictionary<string, SpriteFrame> Frames => _frames;

    public IReadOnlyDictionary<string, SpriteAnimation> Animations => _animations;

    public SpriteFrame Frame(string name) =>
        _frames.TryGetValue(name, out var frame) ? frame : throw new KeyNotFoundException($"[SPRITE]: frame \"{name}\" not in sheet ({string.Join(", ", _frames.Keys)})");

    public SpriteAnimation Animation(string name) =>
        _animations.TryGetValue(name, out var animation) ? animation : throw new KeyNotFoundException($"[SPRITE]: animation \"{name}\" not in sheet ({string.Join(", ", _animations.Keys)})");

    /// <summary>Loads <c>{name}.png</c> and <c>{name}.json</c> from the game's embedded assets.</summary>
    public static SpriteSheet Load(GameContext context, string name, TextureFilter filter = TextureFilter.Nearest)
    {
        var png = context.Assets.Read($"{name}.png");
        var image = PngDecoder.Decode(png);
        var texture = context.CreateTexture(new TextureData(image.Width, image.Height, image.Rgba, filter));
        return Parse(texture, image.Width, image.Height, context.Assets.ReadText($"{name}.json"));
    }

    public static SpriteSheet Parse(ResourceHandle texture, int width, int height, string atlasJson)
    {
        using var document = JsonDocument.Parse(atlasJson);
        var root = document.RootElement;
        var frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        if (root.TryGetProperty("frames", out var framesElement))
        {
            foreach (var frame in framesElement.EnumerateObject())
            {
                var rect = frame.Value;
                var x = rect.GetProperty("x").GetInt32();
                var y = rect.GetProperty("y").GetInt32();
                var w = rect.GetProperty("w").GetInt32();
                var h = rect.GetProperty("h").GetInt32();
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height)
                {
                    throw new InvalidDataException($"[SPRITE]: frame \"{frame.Name}\" ({x},{y},{w},{h}) is outside the {width}x{height} image");
                }

                frames[frame.Name] = SpriteFrame.FromPixels(texture, width, height, x, y, w, h);
            }
        }

        var animations = new Dictionary<string, SpriteAnimation>(StringComparer.Ordinal);
        if (root.TryGetProperty("animations", out var animationsElement))
        {
            foreach (var animation in animationsElement.EnumerateObject())
            {
                var names = animation.Value.GetProperty("frames").EnumerateArray().Select(n => n.GetString() ?? "").ToArray();
                var sequence = names.Select(n => frames.TryGetValue(n, out var f) ? f : throw new InvalidDataException($"[SPRITE]: animation \"{animation.Name}\" uses unknown frame \"{n}\"")).ToArray();
                var fps = animation.Value.TryGetProperty("fps", out var fpsElement) ? fpsElement.GetSingle() : 8f;
                var loop = !animation.Value.TryGetProperty("loop", out var loopElement) || loopElement.ValueKind != JsonValueKind.False;
                if (!(fps > 0))
                {
                    throw new InvalidDataException($"[SPRITE]: animation \"{animation.Name}\" needs fps > 0");
                }

                animations[animation.Name] = new SpriteAnimation(animation.Name, sequence, fps, loop);
            }
        }

        return new SpriteSheet(texture, width, height, frames, animations);
    }

    /// <summary>Cuts a uniform grid; frames are named <c>{prefix}_{index}</c> row by row.</summary>
    public static SpriteSheet FromGrid(ResourceHandle texture, int width, int height, int cellWidth, int cellHeight, string prefix = "frame")
    {
        if (cellWidth <= 0 || cellHeight <= 0 || width % cellWidth != 0 || height % cellHeight != 0)
        {
            throw new ArgumentException($"[SPRITE]: {width}x{height} is not a whole grid of {cellWidth}x{cellHeight} cells");
        }

        var frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        var index = 0;
        for (var y = 0; y < height; y += cellHeight)
        {
            for (var x = 0; x < width; x += cellWidth)
            {
                frames[$"{prefix}_{index++}"] = SpriteFrame.FromPixels(texture, width, height, x, y, cellWidth, cellHeight);
            }
        }

        return new SpriteSheet(texture, width, height, frames, new Dictionary<string, SpriteAnimation>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Read-only files embedded in the game assembly from <c>Shared/assets</c> (see Directory.Build.targets). Embedding keeps
/// "games ship alone": the same bytes reach the desktop binary, the web folder and headless runs, with no file I/O.
/// </summary>
public sealed class GameAssets
{
    private const string Prefix = "assets/";
    private readonly Assembly? _assembly;

    private GameAssets(Assembly? assembly)
    {
        _assembly = assembly;
    }

    public static GameAssets Empty { get; } = new(null);

    public static GameAssets FromAssembly(Assembly assembly) => new(assembly);

    public IEnumerable<string> List() =>
        (_assembly?.GetManifestResourceNames() ?? []).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..]).Order(StringComparer.Ordinal);

    public bool Exists(string path) => _assembly?.GetManifestResourceInfo(Prefix + Normalize(path)) is not null;

    public byte[] Read(string path)
    {
        using var stream = _assembly?.GetManifestResourceStream(Prefix + Normalize(path))
            ?? throw new FileNotFoundException($"[ASSETS]: \"{path}\" is not embedded. Put it under Shared/assets/ and rebuild. Embedded: {string.Join(", ", List())}");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public string ReadText(string path) => System.Text.Encoding.UTF8.GetString(Read(path));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
