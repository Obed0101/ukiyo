using System.Numerics;

namespace Ukiyo.Rendering;

// Data conventions (shared by every renderer; see docs/conventions.md):
//   units: meters, seconds, radians. Right-handed, +Y up, cameras look down their local -Z.
//   Quaternions are XYZW. Colors are linear; each renderer encodes to its output color space.
//   Front faces are counter-clockwise; back faces are culled.
//   Matrices use System.Numerics (row vectors: v' = v * M). Serialized in field order M11..M44,
//   which is the column-major array of the equivalent column-vector matrix (WGSL, Three.js).

/// <summary>Rendering profile a renderer implements. G0 only needs indexed unlit meshes.</summary>
public enum RenderProfile
{
    G0Unlit = 1,
}

public enum ResourceKind : byte
{
    Mesh = 1,
    Material = 2,
    Texture = 3,
}

/// <summary>Generational handle: index into a renderer-side table plus a generation that detects stale use.</summary>
public readonly record struct ResourceHandle(ResourceKind Kind, uint Index, uint Generation)
{
    public bool IsValid => Generation != 0;
    public override string ToString() => $"{Kind}#{Index}.{Generation}";
}

public readonly record struct RenderExtent(int Width, int Height, float PixelRatio)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record RenderConfiguration(string ApplicationName, RenderExtent InitialExtent, RenderProfile Profile = RenderProfile.G0Unlit);

/// <summary>What a renderer actually runs on. Backend is the effective API (e.g. "Metal", "WebGL2"), never a guess.</summary>
public sealed record RenderCapabilities(string RendererName, string Backend, string Device, RenderProfile Profile, bool SupportsCapture)
{
    /// <summary>Textures and the 2D sprite layer. A packet with sprites fails on a renderer without it.</summary>
    public bool SupportsSprites { get; init; }
}

/// <summary>Vertex layout for <see cref="RenderProfile.G0Unlit"/>: position (xyz) and linear color (rgb), 24 bytes.</summary>
public readonly record struct VertexPositionColor(Vector3 Position, Vector3 Color)
{
    public const int SizeInBytes = 24;
}

public sealed record MeshData(VertexPositionColor[] Vertices, uint[] Indices);

/// <summary>Unlit material: final color = vertex color (when enabled) * base color. Colors are linear.</summary>
public sealed record MaterialData(Vector4 BaseColor, bool UseVertexColors);

public sealed record CameraState(Vector3 Position, Quaternion Rotation, float FieldOfViewY, float NearPlane, float FarPlane);

public readonly record struct RenderInstance(ResourceHandle Mesh, ResourceHandle Material, Matrix4x4 World);

public enum TextureFilter : byte
{
    /// <summary>Pixel art: every texel stays a hard square.</summary>
    Nearest = 1,
    Linear = 2,
}

/// <summary>
/// RGBA8 image, rows top to bottom, sRGB-encoded color with straight (non-premultiplied) alpha — what a PNG holds.
/// Renderers decode to linear before tinting and blending.
/// </summary>
public sealed record TextureData(int Width, int Height, byte[] Rgba, TextureFilter Filter = TextureFilter.Nearest);

public enum SpriteSpace : byte
{
    /// <summary>World units through <see cref="Camera2D"/>: +Y up, centered on the camera.</summary>
    World = 1,

    /// <summary>Drawing-buffer pixels: origin top-left, +Y down. Used by game UI.</summary>
    Screen = 2,
}

/// <summary>Orthographic 2D camera for world-space sprites. <see cref="ViewHeight"/> world units fill the viewport height.</summary>
public readonly record struct Camera2D(Vector2 Center, float ViewHeight)
{
    public static Camera2D Default => new(Vector2.Zero, 10f);
}

/// <summary>
/// One textured quad. <see cref="Position"/> is where the pivot lands; <see cref="Pivot"/> is normalized in image space
/// ((0,0) top-left, (1,1) bottom-right); <see cref="Size"/> is in world units or pixels depending on <see cref="Space"/>.
/// <see cref="Rotation"/> is counter-clockwise on screen, in radians. <see cref="Uv"/> is (u0, v0, u1, v1) with v down;
/// a flipped rect mirrors the image. <see cref="Color"/> is a linear RGBA tint multiplied with the texel.
/// Order: 3D instances, then world sprites, then screen sprites; within a space by <see cref="Layer"/>, then submission.
/// </summary>
public readonly record struct SpriteInstance(
    ResourceHandle Texture,
    SpriteSpace Space,
    Vector2 Position,
    Vector2 Size,
    Vector2 Pivot,
    float Rotation,
    Vector4 Uv,
    Vector4 Color,
    int Layer)
{
    public static readonly Vector4 FullUv = new(0, 0, 1, 1);
}

/// <summary>One frame of presentation data. Contains no gameplay logic; renderers must copy what they keep.</summary>
public sealed record RenderPacket(uint Sequence, long Tick, RenderExtent Viewport, Vector4 ClearColor, CameraState Camera, IReadOnlyList<RenderInstance> Instances)
{
    public Camera2D Camera2D { get; init; } = Camera2D.Default;

    public IReadOnlyList<SpriteInstance> Sprites { get; init; } = [];
}

public enum ResourceCommandKind : byte
{
    CreateMesh = 1,
    CreateMaterial = 2,
    Destroy = 3,
    CreateTexture = 4,
}

public readonly record struct ResourceCommand(ResourceCommandKind Kind, ResourceHandle Handle, MeshData? Mesh, MaterialData? Material, TextureData? Texture = null)
{
    public static ResourceCommand CreateMesh(ResourceHandle handle, MeshData mesh) => new(ResourceCommandKind.CreateMesh, handle, mesh, null);
    public static ResourceCommand CreateMaterial(ResourceHandle handle, MaterialData material) => new(ResourceCommandKind.CreateMaterial, handle, null, material);
    public static ResourceCommand CreateTexture(ResourceHandle handle, TextureData texture) => new(ResourceCommandKind.CreateTexture, handle, null, null, texture);
    public static ResourceCommand Destroy(ResourceHandle handle) => new(ResourceCommandKind.Destroy, handle, null, null);
}

/// <summary>Create/destroy resources by handle. Uploads are sent once, not every frame.</summary>
public sealed record ResourceBatch(IReadOnlyList<ResourceCommand> Commands);

public interface IRenderer : IDisposable
{
    RenderCapabilities Capabilities { get; }

    ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken);

    void ApplyResources(ResourceBatch resources);

    void Resize(RenderExtent extent);

    void Render(RenderPacket packet);
}

public enum CaptureStatus
{
    Captured = 1,
    UnsupportedCapture = 2,
}

public sealed record CaptureRequest(long Tick);

/// <summary>PNG bytes of what the renderer presented. Renderers without images return <see cref="CaptureStatus.UnsupportedCapture"/>.</summary>
public sealed record CaptureResult(CaptureStatus Status, byte[] Png, int Width, int Height, string Backend)
{
    public static CaptureResult Unsupported(string backend) => new(CaptureStatus.UnsupportedCapture, [], 0, 0, backend);
}

/// <summary>Optional capability. Callers must check for it; nothing fakes an image.</summary>
public interface IRenderCapture
{
    ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken);
}

public enum RenderErrorCode
{
    InvalidPacket = 1,
    UnsupportedVersion = 2,
    TruncatedPayload = 3,
    StaleHandle = 4,
    UnknownHandle = 5,
    WrongResourceKind = 6,
    NonFiniteValue = 7,
    OutOfRange = 8,
    DuplicateHandle = 9,
    NotInitialized = 10,
    BackendFailure = 11,
    UnsupportedFeature = 12,
}

/// <summary>Controlled failure at the render boundary. Never swallowed as a dropped frame.</summary>
public sealed class RenderException(RenderErrorCode code, string message) : Exception($"[RENDER:{code}]: {message}")
{
    public RenderErrorCode Code { get; } = code;
}
