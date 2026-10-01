using System.Numerics;

namespace Ukiyo.Rendering.Software;

/// <summary>
/// Deterministic CPU rasterizer. Same camera math, winding, culling and sprite geometry as the GPU renderers, so an
/// agent gets real frames with no window, GPU or browser. It is a reference, not a performance path: meshes are
/// clipped against the near plane only, there is no MSAA, and sprites use affine UVs (they are screen-aligned quads).
/// </summary>
public sealed class SoftwareRenderer : IRenderer, IRenderCapture
{
    private readonly ResourceTable<MeshData> _meshes = new();
    private readonly ResourceTable<MaterialData> _materials = new();
    private readonly ResourceTable<LinearTexture> _textures = new();
    private RenderExtent _extent;
    private float[] _color = [];
    private float[] _depth = [];
    private long? _lastTick;
    private bool _initialized;
    private bool _disposed;

    public RenderCapabilities Capabilities { get; } = new("SoftwareRenderer", "CPU", "managed rasterizer", RenderProfile.G0Unlit, SupportsCapture: true) { SupportsSprites = true };

    public long FramesRendered { get; private set; }

    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _initialized = true;
        Resize(configuration.InitialExtent);
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
        RenderValidation.Validate(resources);
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
        var pixels = extent.IsEmpty ? 0 : extent.Width * extent.Height;
        _color = new float[pixels * 3];
        _depth = new float[pixels];
    }

    public void Render(RenderPacket packet)
    {
        RequireReady();
        RenderValidation.Validate(packet);
        if (_extent.IsEmpty)
        {
            return;
        }

        for (var i = 0; i < _depth.Length; i++)
        {
            _color[i * 3] = packet.ClearColor.X;
            _color[i * 3 + 1] = packet.ClearColor.Y;
            _color[i * 3 + 2] = packet.ClearColor.Z;
            _depth[i] = 1f;
        }

        var viewProjection = ViewProjection(packet.Camera, _extent.Width / (float)_extent.Height);
        foreach (var instance in packet.Instances)
        {
            DrawMesh(_meshes.Get(instance.Mesh), _materials.Get(instance.Material), instance.World * viewProjection);
        }

        Span<SpriteVertex> quad = stackalloc SpriteVertex[4];
        foreach (var index in SpriteGeometry.DrawOrder(packet.Sprites))
        {
            var sprite = packet.Sprites[index];
            var texture = _textures.Get(sprite.Texture);
            SpriteGeometry.Quad(sprite, packet.Camera2D, _extent, quad);
            DrawSpriteTriangle(quad[0], quad[1], quad[2], texture, sprite.Color, ownsEdgeAB: true);
            DrawSpriteTriangle(quad[0], quad[2], quad[3], texture, sprite.Color, ownsEdgeAB: false);
        }

        _lastTick = packet.Tick;
        FramesRendered++;
    }

    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        RequireReady();
        if (_lastTick != request.Tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture for tick {request.Tick} but last frame is tick {_lastTick}");
        }

        var rgba = new byte[_depth.Length * 4];
        for (var i = 0; i < _depth.Length; i++)
        {
            rgba[i * 4] = ColorSpace.LinearToSrgb(_color[i * 3]);
            rgba[i * 4 + 1] = ColorSpace.LinearToSrgb(_color[i * 3 + 1]);
            rgba[i * 4 + 2] = ColorSpace.LinearToSrgb(_color[i * 3 + 2]);
            rgba[i * 4 + 3] = 255;
        }

        var png = PngEncoder.EncodeRgba(rgba, _extent.Width, _extent.Height);
        return ValueTask.FromResult(new CaptureResult(CaptureStatus.Captured, png, _extent.Width, _extent.Height, Capabilities.Backend));
    }

    public void Dispose()
    {
        _disposed = true;
        _initialized = false;
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

    private void DrawMesh(MeshData mesh, MaterialData material, Matrix4x4 mvp)
    {
        var clip = new ClipVertex[mesh.Vertices.Length];
        for (var i = 0; i < clip.Length; i++)
        {
            var v = mesh.Vertices[i];
            var tint = material.UseVertexColors ? v.Color : Vector3.One;
            var color = tint * new Vector3(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z);
            clip[i] = new ClipVertex(Vector4.Transform(new Vector4(v.Position, 1f), mvp), color);
        }

        Span<ClipVertex> polygon = stackalloc ClipVertex[4];
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var count = ClipNear(clip[mesh.Indices[t]], clip[mesh.Indices[t + 1]], clip[mesh.Indices[t + 2]], polygon);
            for (var k = 1; k + 1 < count; k++)
            {
                DrawTriangle(polygon[0], polygon[k], polygon[k + 1]);
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
                output[count++] = new ClipVertex(Vector4.Lerp(current.Position, next.Position, t), Vector3.Lerp(current.Color, next.Color, t));
            }
        }

        return count;
    }

    private void DrawTriangle(ClipVertex a, ClipVertex b, ClipVertex c)
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

        var width = _extent.Width;
        var height = _extent.Height;
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
                if (depth < 0 || depth > 1 || !(depth < _depth[pixel]))
                {
                    continue;
                }

                _depth[pixel] = depth;
                var pw0 = w0 * invW.X;
                var pw1 = w1 * invW.Y;
                var pw2 = w2 * invW.Z;
                var sum = pw0 + pw1 + pw2;
                var color = (a.Color * pw0 + b.Color * pw1 + c.Color * pw2) / sum;
                _color[pixel * 3] = color.X;
                _color[pixel * 3 + 1] = color.Y;
                _color[pixel * 3 + 2] = color.Z;
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

        var width = _extent.Width;
        var height = _extent.Height;
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
                _color[pixel] = texel.X * alpha + _color[pixel] * (1 - alpha);
                _color[pixel + 1] = texel.Y * alpha + _color[pixel + 1] * (1 - alpha);
                _color[pixel + 2] = texel.Z * alpha + _color[pixel + 2] * (1 - alpha);
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

    private readonly record struct ClipVertex(Vector4 Position, Vector3 Color);

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
