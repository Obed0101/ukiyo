using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>Fixed simulation rate. Tests advance exact tick counts; wall clock never enters game state.</summary>
public static class Simulation
{
    public const int TickRate = 60;
    public const double TickSeconds = 1.0 / TickRate;
}

/// <summary>Index of the tick being produced. After N updates the world is at tick N.</summary>
public readonly record struct TickInfo(long Tick, double DeltaSeconds);

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
}

public sealed record GameSnapshot(long Tick, IReadOnlyDictionary<string, EntityPose> Entities);

/// <summary>Resource creation for games. Handles are allocated here; uploads are queued and flushed once.</summary>
public sealed class GameContext
{
    private readonly HandleAllocator _handles = new();
    private readonly List<ResourceCommand> _pending = [];

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

    public CameraState Camera { get; set; } = new(new Vector3(0, 0, 5), Quaternion.Identity, MathF.PI / 3, 0.1f, 100f);

    public Vector4 ClearColor { get; set; } = new(0.02f, 0.02f, 0.025f, 1f);

    public void Draw(ResourceHandle mesh, ResourceHandle material, Matrix4x4 world) => _instances.Add(new RenderInstance(mesh, material, world));

    internal RenderPacket Build(uint sequence, long tick, RenderExtent viewport) =>
        new(sequence, tick, viewport, ClearColor, Camera, [.. _instances]);

    /// <summary>Camera at <paramref name="position"/> looking at <paramref name="target"/> (right-handed, +Y up, looks down local -Z).</summary>
    public static CameraState LookAt(Vector3 position, Vector3 target, float fieldOfViewY, float near, float far)
    {
        var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
        Matrix4x4.Invert(view, out var world);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
        return new CameraState(position, rotation, fieldOfViewY, near, far);
    }
}
