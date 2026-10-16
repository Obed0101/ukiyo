using System.Numerics;

namespace Ukiyo.Rendering.Software;

/// <summary>
/// Deterministic CPU rasterizer. Same camera math, winding, culling and sprite geometry as the GPU renderers, so an
/// agent gets real frames with no window, GPU or browser. It is a reference, not a performance path: meshes are
/// clipped against the near plane only, there is no MSAA, and sprites use affine UVs (they are screen-aligned quads).
/// Lit materials (G1Lit) are shaded per pixel with <see cref="LightingMath.Shade"/>, the model the shaders copy.
/// Multi-camera frames (docs/rendering-passes.md): each pass is rasterized into its own image at its rect size, exactly
/// like a single-camera frame of that size, then copied into the frame at its rect; captures read either one.
/// </summary>
public sealed class SoftwareRenderer : IRenderer, IRenderCapture
{
    private readonly ResourceTable<MeshData> _meshes = new();
    private readonly ResourceTable<MaterialData> _materials = new();
    private readonly ResourceTable<LinearTexture> _textures = new();
    private readonly PassSequencer _passes = new();
    private readonly Surface?[] _passImages = new Surface?[RenderValidation.MaxPasses];
    private RenderExtent _extent;
    private Surface _frame = new(0, 0);
    private Surface _target = new(0, 0);
    private int _lastPassCount = 1;
    private bool _lastFrameHasPasses;
    private long? _lastTick;
    private bool _initialized;
    private bool _disposed;

    /// <summary>The CPU renderer is the pixel reference for headless captures, so it implements G1Lit with the shared <see cref="LightingMath"/>.</summary>
    private const RenderProfile HighestProfile = RenderProfile.G1Lit;

    public RenderCapabilities Capabilities { get; private set; } = new("SoftwareRenderer", "CPU", "managed rasterizer", RenderProfile.G0Unlit, SupportsCapture: true) { SupportsSprites = true, SupportsPasses = true };

    public long FramesRendered { get; private set; }

    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderValidation.RequireImplemented(configuration.Profile, HighestProfile, "SoftwareRenderer");
        Capabilities = Capabilities with { Profile = configuration.Profile };
        _initialized = true;
        Resize(configuration.InitialExtent);
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
        RenderValidation.Validate(resources);
        RenderValidation.RequireProfile(Capabilities.Profile, resources);
        foreach (var command in resources.Commands)
        {
            switch (command.Kind)
            {
                case ResourceCommandKind.CreateMesh:
                    _meshes.Add(command.Handle, command.Mesh!);
                    break;
                case ResourceCommandKind.CreateMaterial:
                    _materials.Add(command.Handle, command.Material!);
                    break;
                case ResourceCommandKind.CreateTexture:
                    _textures.Add(command.Handle, new LinearTexture(command.Texture!));
                    break;
                case ResourceCommandKind.Destroy when command.Handle.Kind == ResourceKind.Mesh:
                    _meshes.Remove(command.Handle);
                    break;
                case ResourceCommandKind.Destroy when command.Handle.Kind == ResourceKind.Texture:
                    _textures.Remove(command.Handle);
                    break;
                case ResourceCommandKind.Destroy:
                    _materials.Remove(command.Handle);
                    break;
            }
        }
    }

    public void Resize(RenderExtent extent)
    {
        RequireReady();
        _extent = extent;
        _frame = extent.IsEmpty ? new Surface(0, 0) : new Surface(extent.Width, extent.Height);
    }

    public void Render(RenderPacket packet)
    {
        RequireReady();
        RenderValidation.Validate(packet);
        RenderValidation.RequireProfile(Capabilities.Profile, packet);
        var completesFrame = _passes.Accept(packet);
        if (packet.Pass is { Id: 0 })
        {
            _lastTick = null;
        }

        if (_extent.IsEmpty)
        {
            return;
        }

        try
        {
            if (packet.Pass is not { } pass)
            {
                Draw(packet, _frame);
                _lastPassCount = 1;
                _lastFrameHasPasses = false;
            }
            else
            {
                RenderValidation.RequirePassInside(pass, _extent);
                if (pass.Id == 0)
                {
                    _frame.Clear(packet.ClearColor);
                }

                var image = PassImage(pass);
                Draw(packet, image);
                _frame.CopyFrom(image, pass.Rect.X, pass.Rect.Y);
                _lastPassCount = pass.Count;
                _lastFrameHasPasses = true;
            }
        }
        catch
        {
            _passes.Abort();
            throw;
        }

        if (completesFrame)
        {
            _lastTick = packet.Tick;
            FramesRendered++;
        }
    }

    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        RequireReady();
        _passes.RequireComplete();
        if (_lastTick != request.Tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture for tick {request.Tick} but last frame is tick {_lastTick}");
        }

        RenderValidation.RequireCapturablePass(request.Pass, _lastPassCount);
        var surface = request.Pass is { } id && _lastFrameHasPasses ? _passImages[id]! : _frame;
        var rgba = surface.ToSrgbRgba();
        var png = PngEncoder.EncodeRgba(rgba, surface.Width, surface.Height);
        return ValueTask.FromResult(new CaptureResult(CaptureStatus.Captured, png, surface.Width, surface.Height, Capabilities.Backend));
    }

    public void Dispose()
    {
        _disposed = true;
        _initialized = false;
    }

    /// <summary>One camera into <paramref name="target"/>: clear, meshes, sprites. Its size is the drawing buffer the packet sees.</summary>
    private void Draw(RenderPacket packet, Surface target)
    {
        _target = target;
        target.Clear(packet.ClearColor);
        var extent = new RenderExtent(target.Width, target.Height, 1f);
        var viewProjection = ViewProjection(packet.Camera, target.Width / (float)target.Height);
        foreach (var instance in packet.Instances)
        {
            var mesh = _meshes.Get(instance.Mesh);
            var material = _materials.Get(instance.Material);
            RenderValidation.RequireLitInputs(mesh.Normals is not null, material, packet, instance);
            DrawMesh(mesh, material, instance.World, viewProjection, material.Lit ? packet.Lighting : null);
        }

        Span<SpriteVertex> quad = stackalloc SpriteVertex[4];
        foreach (var index in SpriteGeometry.DrawOrder(packet.Sprites))
        {
            var sprite = packet.Sprites[index];
            var texture = _textures.Get(sprite.Texture);
            SpriteGeometry.Quad(sprite, packet.Camera2D, extent, quad);
            DrawSpriteTriangle(quad[0], quad[1], quad[2], texture, sprite.Color, ownsEdgeAB: true);
            DrawSpriteTriangle(quad[0], quad[2], quad[3], texture, sprite.Color, ownsEdgeAB: false);
        }
    }

    /// <summary>The image pass <paramref name="pass"/> draws into, kept after the frame for per-pass captures.</summary>
    private Surface PassImage(FramePass pass)
    {
        var image = _passImages[pass.Id];
        if (image is null || image.Width != pass.Rect.Width || image.Height != pass.Rect.Height)
        {
            image = new Surface(pass.Rect.Width, pass.Rect.Height);
            _passImages[pass.Id] = image;
        }

        return image;
    }

    /// <summary>Identical to the wgpu path: inverse camera transform, right-handed perspective with 0..1 depth.</summary>
    internal static Matrix4x4 ViewProjection(CameraState camera, float aspect)
    {
        var cameraWorld = Matrix4x4.CreateFromQuaternion(camera.Rotation) * Matrix4x4.CreateTranslation(camera.Position);
        if (!Matrix4x4.Invert(cameraWorld, out var view))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, "camera transform is not invertible");
        }

        return view * Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfViewY, aspect, camera.NearPlane, camera.FarPlane);
    }

    /// <param name="lighting">Set for lit materials: world position and normal are interpolated and shaded per pixel.</param>
    private void DrawMesh(MeshData mesh, MaterialData material, Matrix4x4 world, Matrix4x4 viewProjection, SceneLighting? lighting)
    {
        var mvp = world * viewProjection;
        var normalMatrix = lighting is null ? default : LightingMath.NormalMatrix(world);
        var clip = new ClipVertex[mesh.Vertices.Length];
        for (var i = 0; i < clip.Length; i++)
        {
            var v = mesh.Vertices[i];
            var tint = material.UseVertexColors ? v.Color : Vector3.One;
            var color = tint * new Vector3(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z);
            clip[i] = lighting is null
                ? new ClipVertex(Vector4.Transform(new Vector4(v.Position, 1f), mvp), color, default, default)
                : new ClipVertex(Vector4.Transform(new Vector4(v.Position, 1f), mvp), color, Vector3.Transform(v.Position, world), Vector3.TransformNormal(mesh.Normals![i], normalMatrix));
        }

        Span<ClipVertex> polygon = stackalloc ClipVertex[4];
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var count = ClipNear(clip[mesh.Indices[t]], clip[mesh.Indices[t + 1]], clip[mesh.Indices[t + 2]], polygon);
            for (var k = 1; k + 1 < count; k++)
            {
                DrawTriangle(polygon[0], polygon[k], polygon[k + 1], lighting);
            }
        }
    }

    /// <summary>Sutherland–Hodgman against the WebGPU near plane (z ≥ 0 in clip space). Returns 0, 3 or 4 vertices.</summary>
    private static int ClipNear(ClipVertex a, ClipVertex b, ClipVertex c, Span<ClipVertex> output)
    {
        ReadOnlySpan<ClipVertex> input = [a, b, c];
        var count = 0;
        for (var i = 0; i < 3; i++)
        {
            var current = input[i];
            var next = input[(i + 1) % 3];
            var currentInside = current.Position.Z >= 0;
            var nextInside = next.Position.Z >= 0;
            if (currentInside)
            {
                output[count++] = current;
            }

            if (currentInside != nextInside)
            {
                var t = current.Position.Z / (current.Position.Z - next.Position.Z);
                output[count++] = new ClipVertex(
                    Vector4.Lerp(current.Position, next.Position, t),
                    Vector3.Lerp(current.Color, next.Color, t),
                    Vector3.Lerp(current.World, next.World, t),
                    Vector3.Lerp(current.Normal, next.Normal, t));
            }
        }

        return count;
    }

    private void DrawTriangle(ClipVertex a, ClipVertex b, ClipVertex c, SceneLighting? lighting)
    {
        if (a.Position.W <= 0 || b.Position.W <= 0 || c.Position.W <= 0)
        {
            return;
        }

        var na = Ndc(a.Position);
        var nb = Ndc(b.Position);
        var nc = Ndc(c.Position);

        // Counter-clockwise in NDC (+Y up) is a front face; everything else is culled like the GPU paths.
        var ndcArea = (nb.X - na.X) * (nc.Y - na.Y) - (nc.X - na.X) * (nb.Y - na.Y);
        if (ndcArea <= 0)
        {
            return;
        }

        var width = _target.Width;
        var height = _target.Height;
        var colors = _target.Color;
        var sa = ToPixel(na, width, height);
        var sb = ToPixel(nb, width, height);
        var sc = ToPixel(nc, width, height);
        var area = Edge(sa, sb, sc);
        if (area == 0)
        {
            return;
        }

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
        var invW = new Vector3(1f / a.Position.W, 1f / b.Position.W, 1f / c.Position.W);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = Edge(sb, sc, p) / area;
                var w1 = Edge(sc, sa, p) / area;
                var w2 = Edge(sa, sb, p) / area;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    continue;
                }

                var depth = w0 * na.Z + w1 * nb.Z + w2 * nc.Z;
                var pixel = y * width + x;
                if (depth < 0 || depth > 1 || !(depth < _target.Depth[pixel]))
                {
                    continue;
                }

                _target.Depth[pixel] = depth;
                var pw0 = w0 * invW.X;
                var pw1 = w1 * invW.Y;
                var pw2 = w2 * invW.Z;
                var sum = pw0 + pw1 + pw2;
                var color = (a.Color * pw0 + b.Color * pw1 + c.Color * pw2) / sum;
                if (lighting is not null)
                {
                    var world = (a.World * pw0 + b.World * pw1 + c.World * pw2) / sum;
                    var normal = (a.Normal * pw0 + b.Normal * pw1 + c.Normal * pw2) / sum;
                    color = LightingMath.Shade(color, normal, world, lighting);
                }

                colors[pixel * 3] = color.X;
                colors[pixel * 3 + 1] = color.Y;
                colors[pixel * 3 + 2] = color.Z;
            }
        }
    }

    /// <param name="ownsEdgeAB">The quad diagonal is shared; only one triangle may cover pixels exactly on it, or alpha blends twice.</param>
    private void DrawSpriteTriangle(SpriteVertex a, SpriteVertex b, SpriteVertex c, LinearTexture texture, Vector4 tint, bool ownsEdgeAB)
    {
        var area = Edge(a.Pixel, b.Pixel, c.Pixel);
        if (area == 0)
        {
            return;
        }

        var width = _target.Width;
        var height = _target.Height;
        var colors = _target.Color;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Pixel.X, MathF.Min(b.Pixel.X, c.Pixel.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.Pixel.X, MathF.Max(b.Pixel.X, c.Pixel.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Pixel.Y, MathF.Min(b.Pixel.Y, c.Pixel.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Pixel.Y, MathF.Max(b.Pixel.Y, c.Pixel.Y))));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = Edge(b.Pixel, c.Pixel, p) / area;
                var w1 = Edge(c.Pixel, a.Pixel, p) / area;
                var w2 = Edge(a.Pixel, b.Pixel, p) / area;

                // Both windings are valid for sprites (negative sizes mirror them).
                if (w0 < 0 || w1 < 0 || w2 < 0 || (w2 == 0 && !ownsEdgeAB))
                {
                    continue;
                }

                var uv = a.Uv * w0 + b.Uv * w1 + c.Uv * w2;
                var texel = texture.Sample(uv) * tint;
                if (texel.W <= 0)
                {
                    continue;
                }

                var pixel = (y * width + x) * 3;
                var alpha = MathF.Min(1f, texel.W);
                colors[pixel] = texel.X * alpha + colors[pixel] * (1 - alpha);
                colors[pixel + 1] = texel.Y * alpha + colors[pixel + 1] * (1 - alpha);
                colors[pixel + 2] = texel.Z * alpha + colors[pixel + 2] * (1 - alpha);
            }
        }
    }

    private static Vector3 Ndc(Vector4 clip) => new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);

    private static Vector2 ToPixel(Vector3 ndc, int width, int height) => new((ndc.X + 1f) * 0.5f * width, (1f - ndc.Y) * 0.5f * height);

    private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new RenderException(RenderErrorCode.NotInitialized, "SoftwareRenderer used before InitializeAsync");
        }
    }

    /// <summary>Linear RGB color and depth for one image: the drawing buffer, or one pass at its rect size.</summary>
    private sealed class Surface(int width, int height)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public float[] Color { get; } = new float[width * height * 3];

        public float[] Depth { get; } = new float[width * height];

        public void Clear(Vector4 color)
        {
            for (var i = 0; i < Depth.Length; i++)
            {
                Color[i * 3] = color.X;
                Color[i * 3 + 1] = color.Y;
                Color[i * 3 + 2] = color.Z;
                Depth[i] = 1f;
            }
        }

        /// <summary>Opaque copy of <paramref name="source"/> with its top-left at (x, y); passes never blend with each other.</summary>
        public void CopyFrom(Surface source, int x, int y)
        {
            for (var row = 0; row < source.Height; row++)
            {
                Array.Copy(source.Color, row * source.Width * 3, Color, ((y + row) * Width + x) * 3, source.Width * 3);
            }
        }

        public byte[] ToSrgbRgba()
        {
            var rgba = new byte[Depth.Length * 4];
            for (var i = 0; i < Depth.Length; i++)
            {
                rgba[i * 4] = ColorSpace.LinearToSrgb(Color[i * 3]);
                rgba[i * 4 + 1] = ColorSpace.LinearToSrgb(Color[i * 3 + 1]);
                rgba[i * 4 + 2] = ColorSpace.LinearToSrgb(Color[i * 3 + 2]);
                rgba[i * 4 + 3] = 255;
            }

            return rgba;
        }
    }

    /// <summary>Clip-space position, linear albedo and, for lit draws, world position and world normal.</summary>
    private readonly record struct ClipVertex(Vector4 Position, Vector3 Color, Vector3 World, Vector3 Normal);

    /// <summary>Texture decoded once to linear RGBA floats. Sampling clamps to the edge, like the GPU samplers.</summary>
    private sealed class LinearTexture
    {
        private readonly float[] _texels;

        public LinearTexture(TextureData data)
        {
            Width = data.Width;
            Height = data.Height;
            Filter = data.Filter;
            _texels = new float[data.Width * data.Height * 4];
            for (var i = 0; i < data.Width * data.Height; i++)
            {
                _texels[i * 4] = ColorSpace.SrgbToLinear(data.Rgba[i * 4]);
                _texels[i * 4 + 1] = ColorSpace.SrgbToLinear(data.Rgba[i * 4 + 1]);
                _texels[i * 4 + 2] = ColorSpace.SrgbToLinear(data.Rgba[i * 4 + 2]);
                _texels[i * 4 + 3] = data.Rgba[i * 4 + 3] / 255f;
            }
        }

        public int Width { get; }

        public int Height { get; }

        public TextureFilter Filter { get; }

        public Vector4 Sample(Vector2 uv)
        {
            if (Filter == TextureFilter.Nearest)
            {
                return Texel((int)MathF.Floor(uv.X * Width), (int)MathF.Floor(uv.Y * Height));
            }

            var x = uv.X * Width - 0.5f;
            var y = uv.Y * Height - 0.5f;
            var x0 = (int)MathF.Floor(x);
            var y0 = (int)MathF.Floor(y);
            var fx = x - x0;
            var fy = y - y0;
            var top = Vector4.Lerp(Texel(x0, y0), Texel(x0 + 1, y0), fx);
            var bottom = Vector4.Lerp(Texel(x0, y0 + 1), Texel(x0 + 1, y0 + 1), fx);
            return Vector4.Lerp(top, bottom, fy);
        }

        private Vector4 Texel(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            var i = (y * Width + x) * 4;
            return new Vector4(_texels[i], _texels[i + 1], _texels[i + 2], _texels[i + 3]);
        }
    }
}
