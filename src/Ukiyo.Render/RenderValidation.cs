using System.Numerics;

namespace Ukiyo.Rendering;

/// <summary>Checks every renderer runs before touching data: finite numbers, index ranges, handle kinds.</summary>
public static class RenderValidation
{
    public const int MaxVertices = 1 << 20;
    public const int MaxIndices = 3 << 20;
    public const int MaxInstances = 1 << 16;
    public const int MaxSprites = 1 << 16;
    public const int MaxTextureSize = 4096;

    public static void Validate(TextureData texture)
    {
        if (texture.Width is <= 0 or > MaxTextureSize || texture.Height is <= 0 or > MaxTextureSize)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"texture {texture.Width}x{texture.Height} outside 1..{MaxTextureSize}");
        }

        if (texture.Rgba.Length != texture.Width * texture.Height * 4)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"texture {texture.Width}x{texture.Height} needs {texture.Width * texture.Height * 4} RGBA bytes, got {texture.Rgba.Length}");
        }

        if (texture.Filter is not (TextureFilter.Nearest or TextureFilter.Linear))
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown texture filter {(byte)texture.Filter}");
        }
    }

    public static void Validate(Camera2D camera)
    {
        if (!float.IsFinite(camera.Center.X) || !float.IsFinite(camera.Center.Y))
        {
            throw new RenderException(RenderErrorCode.NonFiniteValue, $"2D camera center is not finite: {camera.Center}");
        }

        if (!(camera.ViewHeight > 0) || !float.IsFinite(camera.ViewHeight))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"2D camera view height {camera.ViewHeight} must be positive and finite");
        }
    }

    public static void Validate(SpriteInstance sprite)
    {
        RequireKind(sprite.Texture, ResourceKind.Texture);
        if (sprite.Space is not (SpriteSpace.World or SpriteSpace.Screen))
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown sprite space {(byte)sprite.Space}");
        }

        RequireFinite(new Vector4(sprite.Position, sprite.Size.X, sprite.Size.Y), "sprite position/size");
        RequireFinite(new Vector3(sprite.Pivot, sprite.Rotation), "sprite pivot/rotation");
        RequireFinite(sprite.Uv, "sprite uv");
        RequireFinite(sprite.Color, "sprite color");
    }

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
                case ResourceCommandKind.CreateTexture:
                    RequireKind(command.Handle, ResourceKind.Texture);
                    Validate(command.Texture ?? throw new RenderException(RenderErrorCode.InvalidPacket, "CreateTexture without texture data"));
                    break;
                case ResourceCommandKind.Destroy:
                    if (command.Handle.Kind is not (ResourceKind.Mesh or ResourceKind.Material or ResourceKind.Texture))
                    {
                        throw new RenderException(RenderErrorCode.WrongResourceKind, $"destroy of unknown resource kind {command.Handle}");
                    }

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

        if (packet.Sprites.Count > MaxSprites)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"{packet.Sprites.Count} sprites exceed {MaxSprites}");
        }

        Validate(packet.Camera2D);
        foreach (var sprite in packet.Sprites)
        {
            Validate(sprite);
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
