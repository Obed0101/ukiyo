using System.Numerics;

namespace Ukiyo.Rendering;

/// <summary>Checks every renderer runs before touching data: finite numbers, index ranges, handle kinds.</summary>
public static class RenderValidation
{
    public const int MaxVertices = 1 << 20;
    public const int MaxIndices = 3 << 20;
    public const int MaxInstances = 1 << 16;

    public static void Validate(MeshData mesh)
    {
        if (mesh.Vertices.Length is 0 or > MaxVertices)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"mesh vertex count {mesh.Vertices.Length} outside 1..{MaxVertices}");
        }

        if (mesh.Indices.Length is 0 or > MaxIndices || mesh.Indices.Length % 3 != 0)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"mesh index count {mesh.Indices.Length} must be a positive multiple of 3 up to {MaxIndices}");
        }

        foreach (var vertex in mesh.Vertices)
        {
            RequireFinite(vertex.Position, "vertex position");
            RequireFinite(vertex.Color, "vertex color");
        }

        foreach (var index in mesh.Indices)
        {
            if (index >= mesh.Vertices.Length)
            {
                throw new RenderException(RenderErrorCode.OutOfRange, $"index {index} >= vertex count {mesh.Vertices.Length}");
            }
        }
    }

    public static void Validate(MaterialData material) => RequireFinite(material.BaseColor, "material base color");

    public static void Validate(ResourceBatch batch)
    {
        foreach (var command in batch.Commands)
        {
            switch (command.Kind)
            {
                case ResourceCommandKind.CreateMesh:
                    RequireKind(command.Handle, ResourceKind.Mesh);
                    Validate(command.Mesh ?? throw new RenderException(RenderErrorCode.InvalidPacket, "CreateMesh without mesh data"));
                    break;
                case ResourceCommandKind.CreateMaterial:
                    RequireKind(command.Handle, ResourceKind.Material);
                    Validate(command.Material ?? throw new RenderException(RenderErrorCode.InvalidPacket, "CreateMaterial without material data"));
                    break;
                case ResourceCommandKind.Destroy:
                    break;
                default:
                    throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown resource command {(byte)command.Kind}");
            }
        }
    }

    public static void Validate(RenderPacket packet)
    {
        if (packet.Viewport.Width < 0 || packet.Viewport.Height < 0)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"negative viewport {packet.Viewport}");
        }

        if (packet.Instances.Count > MaxInstances)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"{packet.Instances.Count} instances exceed {MaxInstances}");
        }

        RequireFinite(packet.ClearColor, "clear color");
        RequireFinite(packet.Camera.Position, "camera position");
        RequireFinite(new Vector4(packet.Camera.Rotation.X, packet.Camera.Rotation.Y, packet.Camera.Rotation.Z, packet.Camera.Rotation.W), "camera rotation");
        if (!(packet.Camera.FieldOfViewY > 0 && packet.Camera.FieldOfViewY < MathF.PI) || !(packet.Camera.NearPlane > 0) || !(packet.Camera.FarPlane > packet.Camera.NearPlane))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"invalid camera projection fov={packet.Camera.FieldOfViewY} near={packet.Camera.NearPlane} far={packet.Camera.FarPlane}");
        }

        foreach (var instance in packet.Instances)
        {
            RequireKind(instance.Mesh, ResourceKind.Mesh);
            RequireKind(instance.Material, ResourceKind.Material);
            RequireFinite(instance.World, "instance world matrix");
        }
    }

    public static void RequireKind(ResourceHandle handle, ResourceKind kind)
    {
        if (handle.Kind != kind)
        {
            throw new RenderException(RenderErrorCode.WrongResourceKind, $"{handle} used where a {kind} is required");
        }
    }

    private static void RequireFinite(Vector3 value, string what)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new RenderException(RenderErrorCode.NonFiniteValue, $"{what} is not finite: {value}");
        }
    }

    private static void RequireFinite(Vector4 value, string what)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new RenderException(RenderErrorCode.NonFiniteValue, $"{what} is not finite: {value}");
        }
    }

    private static void RequireFinite(Matrix4x4 m, string what)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                if (!float.IsFinite(m[row, column]))
                {
                    throw new RenderException(RenderErrorCode.NonFiniteValue, $"{what} has a non-finite element at [{row},{column}]");
                }
            }
        }
    }
}
