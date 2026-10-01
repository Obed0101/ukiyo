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

/// <summary>Collects presentation data for one frame.</summary>
public sealed class FrameBuilder
{
    private readonly List<RenderInstance> _instances = [];
    private readonly List<SpriteInstance> _sprites = [];

    /// <summary>Drawing-buffer size of the frame being built. Screen-space sprites and UI lay out against it.</summary>
    public RenderExtent Viewport { get; internal set; }

    public CameraState Camera { get; set; } = new(new Vector3(0, 0, 5), Quaternion.Identity, MathF.PI / 3, 0.1f, 100f);

    /// <summary>Camera for world-space sprites (+Y up, <see cref="Camera2D.ViewHeight"/> units tall).</summary>
    public Camera2D Camera2D { get; set; } = Camera2D.Default;

    public Vector4 ClearColor { get; set; } = new(0.02f, 0.02f, 0.025f, 1f);

    public void Draw(ResourceHandle mesh, ResourceHandle material, Matrix4x4 world) => _instances.Add(new RenderInstance(mesh, material, world));

    public void DrawSprite(in SpriteInstance sprite) => _sprites.Add(sprite);

    /// <summary>World-space sprite centered on <paramref name="position"/>. Flip mirrors horizontally.</summary>
    public void DrawSprite(SpriteFrame sprite, Vector2 position, Vector2 size, float rotation = 0f, int layer = 0, Vector4? tint = null, bool flipX = false) =>
        _sprites.Add(new SpriteInstance(sprite.Texture, SpriteSpace.World, position, size, new Vector2(0.5f), rotation, flipX ? sprite.FlippedUv : sprite.Uv, tint ?? Vector4.One, layer));

    /// <summary>Screen-space sprite (pixels, top-left origin) with its top-left corner at <paramref name="topLeft"/>.</summary>
    public void DrawScreenSprite(SpriteFrame sprite, Vector2 topLeft, Vector2 size, int layer = 0, Vector4? tint = null) =>
        _sprites.Add(new SpriteInstance(sprite.Texture, SpriteSpace.Screen, topLeft, size, Vector2.Zero, 0f, sprite.Uv, tint ?? Vector4.One, layer));

    internal RenderPacket Build(uint sequence, long tick, RenderExtent viewport) =>
        new(sequence, tick, viewport, ClearColor, Camera, [.. _instances]) { Camera2D = Camera2D, Sprites = [.. _sprites] };

    /// <summary>Camera at <paramref name="position"/> looking at <paramref name="target"/> (right-handed, +Y up, looks down local -Z).</summary>
    public static CameraState LookAt(Vector3 position, Vector3 target, float fieldOfViewY, float near, float far)
    {
        var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
        Matrix4x4.Invert(view, out var world);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
        return new CameraState(position, rotation, fieldOfViewY, near, far);
    }
}
