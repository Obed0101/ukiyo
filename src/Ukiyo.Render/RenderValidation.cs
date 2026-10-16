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

    /// <summary>G1Lit light list limit per frame. Fixed so every shader can use a constant-size uniform array.</summary>
    public const int MaxLights = 8;

    /// <summary>Camera passes per frame (main view, minimap, split screen for up to four players).</summary>
    public const int MaxPasses = 4;

    /// <summary>
    /// Pass ids, count, primary and rect. The rect must lie inside the drawing buffer and be at least 1×1; with an empty
    /// drawing buffer (a minimized window) nothing is drawn, so only the ids and non-negative rect values are checked.
    /// </summary>
    public static void Validate(FramePass pass, RenderExtent drawingBuffer)
    {
        if (pass.Count is < 1 or > MaxPasses)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"pass count {pass.Count} outside 1..{MaxPasses}");
        }

        if (pass.Id < 0 || pass.Id >= pass.Count || pass.PrimaryId < 0 || pass.PrimaryId >= pass.Count)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"pass id {pass.Id} or primary {pass.PrimaryId} outside 0..{pass.Count - 1}");
        }

        var rect = pass.Rect;
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 0 || rect.Height < 0)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"pass {pass.Id} rect {rect} has a negative value");
        }

        if (drawingBuffer.IsEmpty)
        {
            return;
        }

        RequirePassInside(pass, drawingBuffer);
    }

    /// <summary>Renderers check the rect against their own drawing buffer too, which is what they write into.</summary>
    public static void RequirePassInside(FramePass pass, RenderExtent drawingBuffer)
    {
        var rect = pass.Rect;
        if (rect.Width < 1 || rect.Height < 1 || (long)rect.X + rect.Width > drawingBuffer.Width || (long)rect.Y + rect.Height > drawingBuffer.Height)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"pass {pass.Id} rect {rect} is empty or outside the {drawingBuffer.Width}x{drawingBuffer.Height} drawing buffer");
        }
    }

    /// <summary>Capture of pass <paramref name="pass"/> on a frame with <paramref name="passCount"/> passes (1 for a single-camera frame).</summary>
    public static void RequireCapturablePass(int? pass, int passCount)
    {
        if (pass is { } id && (id < 0 || id >= passCount))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture of pass {id}, but the last frame has {passCount} pass(es)");
        }
    }

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

        if (mesh.Normals is { } normals)
        {
            if (normals.Length != mesh.Vertices.Length)
            {
                throw new RenderException(RenderErrorCode.OutOfRange, $"mesh has {normals.Length} normals for {mesh.Vertices.Length} vertices");
            }

            foreach (var normal in normals)
            {
                RequireFinite(normal, "vertex normal");
                if (normal == Vector3.Zero)
                {
                    throw new RenderException(RenderErrorCode.OutOfRange, "vertex normal has zero length");
                }
            }
        }
    }

    public static void Validate(SceneLighting lighting)
    {
        RequireFinite(lighting.Ambient, "ambient light");
        RequireNonNegative(lighting.Ambient, "ambient light");
        if (lighting.Lights.Count > MaxLights)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"{lighting.Lights.Count} lights exceed {MaxLights}");
        }

        foreach (var light in lighting.Lights)
        {
            Validate(light);
        }
    }

    public static void Validate(Light light)
    {
        RequireFinite(light.PositionOrDirection, "light position/direction");
        RequireFinite(light.Color, "light color");
        RequireFinite(new Vector3(light.Intensity, light.Range, 0f), "light intensity/range");
        RequireNonNegative(light.Color, "light color");
        if (light.Intensity < 0)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"light intensity {light.Intensity} is negative");
        }

        switch (light.Kind)
        {
            case LightKind.Directional:
                if (light.PositionOrDirection == Vector3.Zero || light.Range != 0)
                {
                    throw new RenderException(RenderErrorCode.OutOfRange, $"directional light needs a non-zero direction and range 0, got {light.PositionOrDirection} range {light.Range}");
                }

                break;
            case LightKind.Point:
                if (!(light.Range > 0))
                {
                    throw new RenderException(RenderErrorCode.OutOfRange, $"point light range {light.Range} must be positive");
                }

                break;
            default:
                throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown light kind {(byte)light.Kind}");
        }
    }

    /// <summary>True when the batch carries G1Lit data (normals or lit materials).</summary>
    public static bool UsesLighting(ResourceBatch batch)
    {
        foreach (var command in batch.Commands)
        {
            if (command.Mesh?.Normals is not null || command.Material?.Lit == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rejects G1Lit data on a renderer (or game) running a lower profile, instead of drawing it unlit.</summary>
    public static void RequireProfile(RenderProfile active, ResourceBatch batch)
    {
        if (!active.Includes(RenderProfile.G1Lit) && UsesLighting(batch))
        {
            throw new RenderException(RenderErrorCode.UnsupportedFeature, $"resources with normals or lit materials need profile {RenderProfile.G1Lit}; the renderer runs {active}");
        }
    }

    public static void RequireProfile(RenderProfile active, RenderPacket packet)
    {
        if (!active.Includes(RenderProfile.G1Lit) && packet.Lighting is not null)
        {
            throw new RenderException(RenderErrorCode.UnsupportedFeature, $"a lighting section needs profile {RenderProfile.G1Lit}; the renderer runs {active}");
        }
    }

    /// <summary>For InitializeAsync: the requested profile must be one this renderer implements.</summary>
    public static void RequireImplemented(RenderProfile requested, RenderProfile highest, string renderer)
    {
        if (!RenderProfiles.IsDefined(requested) || !highest.Includes(requested))
        {
            throw new RenderException(RenderErrorCode.UnsupportedFeature, $"{renderer} implements up to {highest}; profile {requested} was requested");
        }
    }

    /// <summary>Draw-time check every renderer runs: a lit material needs mesh normals and a lighting section in the frame.</summary>
    public static void RequireLitInputs(bool meshHasNormals, MaterialData material, RenderPacket packet, in RenderInstance instance)
    {
        if (!material.Lit)
        {
            return;
        }

        if (!meshHasNormals)
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"lit material {instance.Material} drawn on mesh {instance.Mesh} without normals");
        }

        if (packet.Lighting is null)
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"lit material {instance.Material} drawn in frame {packet.Sequence} without a lighting section");
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

        if (packet.Lighting is { } lighting)
        {
            Validate(lighting);
        }

        if (packet.Pass is { } pass)
        {
            Validate(pass, packet.Viewport);
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

    private static void RequireNonNegative(Vector3 value, string what)
    {
        if (value.X < 0 || value.Y < 0 || value.Z < 0)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"{what} has a negative component: {value}");
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
