using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>Fixed simulation rate. Tests advance exact tick counts; wall clock never enters game state.</summary>
public static class Simulation
{
    public const int TickRate = 60;
    public const double TickSeconds = 1.0 / TickRate;
    public const float TickSecondsF = 1f / TickRate;
}

/// <summary>Index of the tick being produced. After N updates the world is at tick N.</summary>
public readonly record struct TickInfo(long Tick, double DeltaSeconds)
{
    /// <summary>Input latched for this tick. Same events, same tick, same state on every target and in replays.</summary>
    public InputState Input { get; init; } = InputState.Empty;

    /// <summary>Sounds this tick plays: <c>tick.Audio.Play(_jump)</c>. Recorded as events; hosts render them.</summary>
    public SoundQueue Audio { get; init; } = SoundQueue.Discard;
}

/// <summary>A game written once in C# and run unchanged by every host (desktop, browser, headless).</summary>
public interface IGame
{
    string Name { get; }

    /// <summary>
    /// Rendering profile the game is written for. The runtime initializes the renderer with it and rejects data from a
    /// higher profile. Games that do not override it stay on <see cref="RenderProfile.G0Unlit"/> and keep their bytes.
    /// </summary>
    RenderProfile Profile => RenderProfile.G0Unlit;

    /// <summary>Create resources and initial state. Runs once, before the first tick.</summary>
    void Initialize(GameContext context);

    /// <summary>Advance exactly one fixed tick.</summary>
    void Update(in TickInfo tick);

    /// <summary>Describe what to present for the current state. Must not mutate game state.</summary>
    void Extract(FrameBuilder frame);

    /// <summary>Observable state for tests, agents and parity checks.</summary>
    GameSnapshot Snapshot(long tick);
}

public readonly record struct EntityPose(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>A 2D pose: position on the XY plane, rotation around +Z (counter-clockwise), uniform scale.</summary>
    public static EntityPose Planar(Vector2 position, float rotation = 0f, float scale = 1f) =>
        new(new Vector3(position, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rotation), new Vector3(scale));
}

/// <summary>Poses by entity name plus named numeric values (score, health, state ids) that agents can assert on.</summary>
public sealed record GameSnapshot(long Tick, IReadOnlyDictionary<string, EntityPose> Entities)
{
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();

    /// <summary>Active scene and patch history. Filled in by the runtime for games with a <see cref="SceneRegistry{TEntity}"/>; null otherwise.</summary>
    public SceneState? Scene { get; init; }
}

/// <summary>Resource creation for games. Handles are allocated here; uploads are queued and flushed once.</summary>
public sealed class GameContext
{
    private readonly HandleAllocator _handles = new();
    private readonly List<ResourceCommand> _pending = [];
    private readonly Dictionary<uint, AudioData> _sounds = [];

    public GameContext(GameAssets? assets = null)
    {
        Assets = assets ?? GameAssets.Empty;
    }

    /// <summary>Files embedded from the game's <c>Shared/assets</c> folder; identical on every target.</summary>
    public GameAssets Assets { get; }

    /// <summary>The game's scene registry, attached by its constructor. One per game.</summary>
    internal ISceneDirector? Scenes { get; private set; }

    internal void UseScenes(ISceneDirector scenes)
    {
        if (Scenes is not null)
        {
            throw new InvalidOperationException("[SCENE]: this game already has a SceneRegistry; create one per game");
        }

        Scenes = scenes;
    }

    public ResourceHandle CreateMesh(MeshData mesh)
    {
        RenderValidation.Validate(mesh);
        var handle = _handles.Allocate(ResourceKind.Mesh);
        _pending.Add(ResourceCommand.CreateMesh(handle, mesh));
        return handle;
    }

    public ResourceHandle CreateMaterial(MaterialData material)
    {
        RenderValidation.Validate(material);
        var handle = _handles.Allocate(ResourceKind.Material);
        _pending.Add(ResourceCommand.CreateMaterial(handle, material));
        return handle;
    }

    public ResourceHandle CreateTexture(TextureData texture)
    {
        RenderValidation.Validate(texture);
        var handle = _handles.Allocate(ResourceKind.Texture);
        _pending.Add(ResourceCommand.CreateTexture(handle, texture));
        return handle;
    }

    /// <summary>Decodes a PNG (for example <c>Assets.Read("sprites/hero.png")</c>) and uploads it as a texture.</summary>
    public ResourceHandle LoadTexture(ReadOnlySpan<byte> png, TextureFilter filter = TextureFilter.Nearest) =>
        CreateTexture(PngDecoder.DecodeTexture(png, filter));

    /// <summary>Registers PCM audio and returns its handle (see Ukiyo.Audio for WAV loading and the synthesizer).</summary>
    public SoundHandle CreateSound(AudioData sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        sound.Validate();
        var handle = new SoundHandle((uint)_sounds.Count + 1);
        _sounds.Add(handle.Id, sound);
        return handle;
    }

    internal AudioData Sound(SoundHandle handle) =>
        _sounds.TryGetValue(handle.Id, out var data) ? data : throw new KeyNotFoundException($"[AUDIO]: sound {handle.Id} was not created by this game");

    public void Destroy(ResourceHandle handle)
    {
        _handles.Release(handle);
        _pending.Add(ResourceCommand.Destroy(handle));
    }

    internal ResourceBatch? TakePending()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var batch = new ResourceBatch([.. _pending]);
        _pending.Clear();
        return batch;
    }
}

/// <summary>
/// Collects presentation data for one frame. The builder the game receives is the frame's first camera (pass 0); more
/// cameras (a minimap, split screen) are added with <see cref="AddPass"/>, each with its own camera, clear color,
/// lighting, meshes and sprites (docs/rendering-passes.md). A game that never adds a pass or sets <see cref="Rect"/>
/// produces the single-camera packet it always did.
/// </summary>
public sealed class FrameBuilder
{
    private readonly List<RenderInstance> _instances = [];
    private readonly List<SpriteInstance> _sprites = [];
    private readonly List<FrameBuilder>? _extraPasses;

    public FrameBuilder()
    {
        _extraPasses = [];
    }

    private FrameBuilder(RenderExtent drawingBuffer, PassRect rect)
    {
        DrawingBuffer = drawingBuffer;
        Rect = rect;
    }

    /// <summary>Drawing-buffer size of the whole frame, in pixels. Pass rects are laid out against it.</summary>
    public RenderExtent DrawingBuffer { get; internal set; }

    /// <summary>
    /// Size this camera draws into: its <see cref="Rect"/> or, without one, the whole drawing buffer. Screen-space sprites
    /// and UI lay out against it (pass-local pixels, origin at the rect's top-left).
    /// </summary>
    public RenderExtent Viewport => Rect is { } rect ? new RenderExtent(rect.Width, rect.Height, DrawingBuffer.PixelRatio) : DrawingBuffer;

    /// <summary>Where this camera lands in the drawing buffer (pixels, top-left origin). Null covers the whole buffer.</summary>
    public PassRect? Rect { get; set; }

    /// <summary>
    /// Index of the primary pass (0 = this builder, 1 = the first <see cref="AddPass"/>, …): the view tools show as the
    /// game (inspector preview, dev bridge <c>capture</c> with <c>"pass":"primary"</c>). It does not change drawing.
    /// Only the frame's first builder has it.
    /// </summary>
    public int PrimaryPass { get; set; }

    /// <summary>
    /// Adds a camera pass drawn after the ones before it, into <paramref name="rect"/>. Returns its builder; it starts
    /// with the same defaults as a new frame (nothing is inherited, lighting included). At most
    /// <see cref="RenderValidation.MaxPasses"/> passes per frame; one more fails with OutOfRange.
    /// </summary>
    public FrameBuilder AddPass(PassRect rect)
    {
        var passes = _extraPasses ?? throw new InvalidOperationException("[FRAME]: passes are added to the frame's first builder, not to another pass");
        if (passes.Count + 1 >= RenderValidation.MaxPasses)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"a frame has at most {RenderValidation.MaxPasses} passes");
        }

        var pass = new FrameBuilder(DrawingBuffer, rect);
        passes.Add(pass);
        return pass;
    }

    public CameraState Camera { get; set; } = new(new Vector3(0, 0, 5), Quaternion.Identity, MathF.PI / 3, 0.1f, 100f);

    /// <summary>Camera for world-space sprites (+Y up, <see cref="Camera2D.ViewHeight"/> units tall).</summary>
    public Camera2D Camera2D { get; set; } = Camera2D.Default;

    public Vector4 ClearColor { get; set; } = new(0.02f, 0.02f, 0.025f, 1f);

    /// <summary>G1Lit: ambient and up to 8 lights for lit materials this frame. Leave null in unlit games.</summary>
    public SceneLighting? Lighting { get; set; }

    public void Draw(ResourceHandle mesh, ResourceHandle material, Matrix4x4 world) => _instances.Add(new RenderInstance(mesh, material, world));

    public void DrawSprite(in SpriteInstance sprite) => _sprites.Add(sprite);

    /// <summary>World-space sprite centered on <paramref name="position"/>. Flip mirrors horizontally.</summary>
    public void DrawSprite(SpriteFrame sprite, Vector2 position, Vector2 size, float rotation = 0f, int layer = 0, Vector4? tint = null, bool flipX = false) =>
        _sprites.Add(new SpriteInstance(sprite.Texture, SpriteSpace.World, position, size, new Vector2(0.5f), rotation, flipX ? sprite.FlippedUv : sprite.Uv, tint ?? Vector4.One, layer));

    /// <summary>Screen-space sprite (pixels, top-left origin) with its top-left corner at <paramref name="topLeft"/>.</summary>
    public void DrawScreenSprite(SpriteFrame sprite, Vector2 topLeft, Vector2 size, int layer = 0, Vector4? tint = null) =>
        _sprites.Add(new SpriteInstance(sprite.Texture, SpriteSpace.Screen, topLeft, size, Vector2.Zero, 0f, sprite.Uv, tint ?? Vector4.One, layer));

    /// <summary>
    /// One packet per pass, in pass order, with consecutive sequence numbers starting after <paramref name="sequence"/>
    /// (which is advanced). Without passes, a rect or a primary other than 0, the single packet has no pass section.
    /// </summary>
    internal RenderPacket[] Build(ref uint sequence, long tick, RenderExtent viewport)
    {
        var extra = _extraPasses ?? throw new InvalidOperationException("[FRAME]: only the frame's first builder builds packets");
        if (extra.Count == 0 && Rect is null && PrimaryPass == 0)
        {
            return [Packet(++sequence, tick, viewport, pass: null)];
        }

        var count = extra.Count + 1;
        var packets = new RenderPacket[count];
        for (var id = 0; id < count; id++)
        {
            var builder = id == 0 ? this : extra[id - 1];
            packets[id] = builder.Packet(++sequence, tick, viewport, new FramePass(id, count, PrimaryPass, builder.Rect ?? PassRect.Covering(viewport)));
        }

        return packets;
    }

    private RenderPacket Packet(uint sequence, long tick, RenderExtent viewport, FramePass? pass) =>
        new(sequence, tick, viewport, ClearColor, Camera, [.. _instances])
        {
            Camera2D = Camera2D,
            Sprites = [.. _sprites],
            Lighting = Lighting is { } lighting ? lighting with { Lights = [.. lighting.Lights] } : null,
            Pass = pass,
        };

    /// <summary>
    /// Camera at <paramref name="position"/> looking at <paramref name="target"/> (right-handed, +Y up, looks down local -Z).
    /// Scalar float math only: System.Numerics matrix and quaternion helpers take different SIMD paths on arm64 and x64
    /// and gave different bits (a -0 vs +0 quaternion component), which broke byte-identical packets across platforms.
    /// </summary>
    public static CameraState LookAt(Vector3 position, Vector3 target, float fieldOfViewY, float near, float far)
    {
        // Camera basis: z points from the target back to the eye, x = up × z, y = z × x (rows of the world rotation).
        var (zx, zy, zz) = Normalized(position.X - target.X, position.Y - target.Y, position.Z - target.Z);
        var (xx, xy, xz) = Normalized(zz, 0f, -zx);
        var (yx, yy, yz) = (zy * xz - zz * xy, zz * xx - zx * xz, zx * xy - zy * xx);
        return new CameraState(position, FromRotationRows(xx, xy, xz, yx, yy, yz, zx, zy, zz), fieldOfViewY, near, far);
    }

    private static (float X, float Y, float Z) Normalized(float x, float y, float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        return (x / length, y / length, z / length);
    }

    /// <summary>The same branches as Quaternion.CreateFromRotationMatrix, written out in scalar operations.</summary>
    private static Quaternion FromRotationRows(float m11, float m12, float m13, float m21, float m22, float m23, float m31, float m32, float m33)
    {
        float x, y, z, w;
        var trace = m11 + m22 + m33;
        if (trace > 0f)
        {
            var s = MathF.Sqrt(trace + 1f);
            w = s * 0.5f;
            var inv = 0.5f / s;
            (x, y, z) = ((m23 - m32) * inv, (m31 - m13) * inv, (m12 - m21) * inv);
        }
        else if (m11 >= m22 && m11 >= m33)
        {
            var s = MathF.Sqrt(1f + m11 - m22 - m33);
            var inv = 0.5f / s;
            (x, y, z, w) = (0.5f * s, (m12 + m21) * inv, (m13 + m31) * inv, (m23 - m32) * inv);
        }
        else if (m22 > m33)
        {
            var s = MathF.Sqrt(1f + m22 - m11 - m33);
            var inv = 0.5f / s;
            (x, y, z, w) = ((m21 + m12) * inv, 0.5f * s, (m32 + m23) * inv, (m31 - m13) * inv);
        }
        else
        {
            var s = MathF.Sqrt(1f + m33 - m11 - m22);
            var inv = 0.5f / s;
            (x, y, z, w) = ((m31 + m13) * inv, (m32 + m23) * inv, 0.5f * s, (m12 - m21) * inv);
        }

        var length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion(PositiveZero(x / length), PositiveZero(y / length), PositiveZero(z / length), PositiveZero(w / length));
    }

    private static float PositiveZero(float value) => value == 0f ? 0f : value;
}
