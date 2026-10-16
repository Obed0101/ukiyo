using System.Numerics;

namespace Ukiyo.Rendering;

// Data conventions (shared by every renderer; see docs/conventions.md):
//   units: meters, seconds, radians. Right-handed, +Y up, cameras look down their local -Z.
//   Quaternions are XYZW. Colors are linear; each renderer encodes to its output color space.
//   Front faces are counter-clockwise; back faces are culled.
//   Matrices use System.Numerics (row vectors: v' = v * M). Serialized in field order M11..M44,
//   which is the column-major array of the equivalent column-vector matrix (WGSL, Three.js).

/// <summary>
/// Rendering profile a game runs and a renderer implements. Profiles are cumulative: a higher value contains every
/// lower one, so a G1Lit renderer still draws G0Unlit packets byte for byte as before. A game declares its profile
/// (<c>IGame.Profile</c>); old games keep <see cref="G0Unlit"/> and never see lit data.
/// </summary>
public enum RenderProfile
{
    /// <summary>Indexed meshes with vertex/base colors, textures and sprites. No normals, no lights.</summary>
    G0Unlit = 1,

    /// <summary>G0Unlit plus per-pixel Lambert lighting: mesh normals, lit materials and a light list per frame (docs/rendering-lighting.md).</summary>
    G1Lit = 2,
}

public static class RenderProfiles
{
    /// <summary>The highest profile this engine version defines.</summary>
    public const RenderProfile Latest = RenderProfile.G1Lit;

    public static bool IsDefined(RenderProfile profile) => profile is RenderProfile.G0Unlit or RenderProfile.G1Lit;

    /// <summary>True when a renderer running <paramref name="active"/> can draw data that needs <paramref name="required"/>.</summary>
    public static bool Includes(this RenderProfile active, RenderProfile required) => IsDefined(active) && (int)active >= (int)required;
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

/// <summary>
/// What a renderer actually runs on. Backend is the effective API (e.g. "Metal", "WebGL2"), never a guess.
/// <see cref="Profile"/> is the profile it was initialized with; data from a higher profile fails with UnsupportedFeature.
/// </summary>
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

/// <summary>
/// Indexed triangle mesh. <see cref="Normals"/> (G1Lit) gives one object-space normal per vertex; it need not be unit
/// length (renderers normalize per pixel) but must be finite and non-zero. Lit materials require it.
/// </summary>
public sealed record MeshData(VertexPositionColor[] Vertices, uint[] Indices)
{
    public Vector3[]? Normals { get; init; }
}

/// <summary>
/// Material. Albedo = vertex color (when enabled) * base color, linear. Unlit (default): final color = albedo.
/// <see cref="Lit"/> (G1Lit): final color = albedo * (ambient + Σ lights), the model in docs/rendering-lighting.md.
/// </summary>
public sealed record MaterialData(Vector4 BaseColor, bool UseVertexColors)
{
    public bool Lit { get; init; }
}

public enum LightKind : byte
{
    /// <summary>Parallel light. <see cref="Light.PositionOrDirection"/> is the direction the light travels (world).</summary>
    Directional = 1,

    /// <summary>Omni light at <see cref="Light.PositionOrDirection"/> (world) with a smooth falloff to zero at <see cref="Light.Range"/>.</summary>
    Point = 2,
}

/// <summary>
/// One light (G1Lit). <see cref="Color"/> is linear RGB ≥ 0 and is multiplied by <see cref="Intensity"/> ≥ 0.
/// <see cref="Range"/> is in meters, &gt; 0 for point lights and 0 for directional lights.
/// </summary>
public readonly record struct Light(LightKind Kind, Vector3 PositionOrDirection, Vector3 Color, float Intensity, float Range)
{
    public static Light Directional(Vector3 direction, Vector3 color, float intensity = 1f) => new(LightKind.Directional, direction, color, intensity, 0f);

    public static Light Point(Vector3 position, Vector3 color, float intensity, float range) => new(LightKind.Point, position, color, intensity, range);
}

/// <summary>Per-frame lighting (G1Lit): linear ambient RGB plus up to <see cref="RenderValidation.MaxLights"/> lights, applied in order.</summary>
public sealed record SceneLighting(Vector3 Ambient, IReadOnlyList<Light> Lights);

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

    /// <summary>G1Lit lighting for lit materials. Null keeps the frame in the G0 format (no lighting section).</summary>
    public SceneLighting? Lighting { get; init; }
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
