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
public sealed record RenderCapabilities(string RendererName, string Backend, string Device, RenderProfile Profile, bool SupportsCapture);

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

/// <summary>One frame of presentation data. Contains no gameplay logic; renderers must copy what they keep.</summary>
public sealed record RenderPacket(uint Sequence, long Tick, RenderExtent Viewport, Vector4 ClearColor, CameraState Camera, IReadOnlyList<RenderInstance> Instances);

public enum ResourceCommandKind : byte
{
    CreateMesh = 1,
    CreateMaterial = 2,
    Destroy = 3,
}

public readonly record struct ResourceCommand(ResourceCommandKind Kind, ResourceHandle Handle, MeshData? Mesh, MaterialData? Material)
{
    public static ResourceCommand CreateMesh(ResourceHandle handle, MeshData mesh) => new(ResourceCommandKind.CreateMesh, handle, mesh, null);
    public static ResourceCommand CreateMaterial(ResourceHandle handle, MaterialData material) => new(ResourceCommandKind.CreateMaterial, handle, null, material);
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
}

/// <summary>Controlled failure at the render boundary. Never swallowed as a dropped frame.</summary>
public sealed class RenderException(RenderErrorCode code, string message) : Exception($"[RENDER:{code}]: {message}")
{
    public RenderErrorCode Code { get; } = code;
}
