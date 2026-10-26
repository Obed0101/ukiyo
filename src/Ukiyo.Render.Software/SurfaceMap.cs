using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ukiyo.Rendering.Software;

/// <summary>
/// Debug side output of <see cref="SoftwareRenderer"/> for pixel-parity gates, off unless a caller sets
/// <see cref="SoftwareRenderer.Surfaces"/>: which surface the CPU reference drew at every pixel of the last frame (and of
/// each pass of a multi-camera frame), and from that a geometric edge mask. GPUs place silhouettes by their own
/// rasterization rules, so a boundary may move by a pixel and differ by any amount there; inside a surface only the
/// shading decides the color. The mask separates the two from geometry instead of guessing from color contrast, which
/// misses low-contrast silhouettes.
/// <para>
/// A pixel is <see cref="Edge"/> when its 3×3 neighborhood holds more than one surface: the clear color and a draw, two
/// draws, a sprite and anything else, two passes, or two triangles of one draw that are not neighbors on one smooth
/// sheet. Vertices are welded first (equal position, color and normal), so attributes are continuous across shared
/// vertices. Two front-facing triangles that share an edge lie side by side on screen and interpolate the same values
/// along it: one surface. Two that share only a vertex are one surface when the fan around that vertex is closed and
/// entirely front-facing in this frame; otherwise the vertex sits on a contour (a fold seen edge-on, like Suzanne's brow
/// over her eyes), an open border or a flat-shaded crease, and the triangles may be far apart in depth. Every pair in
/// the window is checked, because this continuity is not transitive (a sliver across a fold touches both sides).
/// </para>
/// </summary>
public sealed class SurfaceMap
{
    /// <summary>Mask value of a pixel whose whole neighborhood is the clear color.</summary>
    public const byte Background = 0;

    /// <summary>Mask value of a pixel whose whole neighborhood is one continuous surface.</summary>
    public const byte Interior = 128;

    /// <summary>Mask value of a pixel whose neighborhood spans more than one surface.</summary>
    public const byte Edge = 255;

    private readonly Recording _frame = new();
    private readonly Recording?[] _passes = new Recording?[RenderValidation.MaxPasses];
    private readonly ConditionalWeakTable<MeshData, Topology> _topology = new();
    private Recording _current;
    private int _passCount;

    public SurfaceMap() => _current = _frame;

    /// <summary>Tick of the frame the map describes; null before the first frame.</summary>
    public long? Tick { get; private set; }

    public int Width => _frame.Width;

    public int Height => _frame.Height;

    /// <summary>Passes of the last frame; 0 for a single-camera frame.</summary>
    public int PassCount => _passCount;

    internal void BeginFrame(long tick, int width, int height)
    {
        _frame.Begin(width, height);
        _current = _frame;
        _passCount = 0;
        Tick = tick;
    }

    internal void BeginPass(int id, int width, int height) => _current = (_passes[id] ??= new Recording()).Begin(width, height);

    /// <summary>The pass image was copied into the frame at (x, y): so is its map, as surfaces of their own.</summary>
    internal void EndPass(int id, int x, int y)
    {
        _frame.Paste(_passes[id]!, id, x, y);
        _current = _frame;
        _passCount = id + 1;
    }

    /// <summary>Called once per instance, in packet order, before its triangles are drawn.</summary>
    internal void BeginInstance(MeshData mesh) => _current.Instances.Add(new InstanceSurfaces(_topology.GetValue(mesh, Topology.Of)));

    /// <summary>The triangle passed back-face culling in this frame (whether or not any pixel of it ends up visible).</summary>
    internal void FrontFacing(int instance, int triangle) => _current.Instances[instance].Front[triangle] = true;

    internal void Cover(int pixel, int instance, int triangle) => _current.Cover(pixel, instance, triangle);

    internal void CoverSprite(int pixel, int sprite) => _current.Owner[pixel] = Recording.SpriteOwner(sprite);

    /// <summary>
    /// One <see cref="Background"/>, <see cref="Interior"/> or <see cref="Edge"/> value per pixel, row-major from the top:
    /// of the whole frame, or of one pass at its own size (pass 0 of a single-camera frame is the frame, as in captures).
    /// </summary>
    public byte[] Classify(int? pass = null) => Image(pass).Classify();

    /// <summary>The mask of the frame (or pass) rendered for <paramref name="tick"/> as a grayscale PNG (black, gray, white).</summary>
    public byte[] EncodePng(long tick, int? pass = null)
    {
        if (Tick != tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"surface map for tick {tick} but last frame is tick {Tick}");
        }

        var image = Image(pass);
        var mask = image.Classify();
        var rgba = new byte[mask.Length * 4];
        for (var i = 0; i < mask.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = mask[i];
            rgba[i * 4 + 3] = 255;
        }

        return PngEncoder.EncodeRgba(rgba, image.Width, image.Height);
    }

    private Recording Image(int? pass)
    {
        if (pass is not { } id)
        {
            return _frame;
        }

        if (id < 0 || id >= Math.Max(1, _passCount))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"surface map for pass {id} but the last frame has {Math.Max(1, _passCount)}");
        }

        return _passCount == 0 ? _frame : _passes[id]!;
    }

    /// <summary>Who covers each pixel of one image (the frame or one pass) and the draws that did.</summary>
    private sealed class Recording
    {
        private const int ClearColor = 0;

        // Owner per pixel: ClearColor; instance index + 1 for a 3D draw; SpriteOwner for a sprite drawn over it. In a frame
        // with passes, each pass's owners are shifted by PassOwner so that no two passes share a surface.
        private const int PassShift = 1 << 26;

        public int[] Owner { get; private set; } = [];

        public int[] Triangle { get; private set; } = [];

        public List<InstanceSurfaces> Instances { get; } = [];

        public int Width { get; private set; }

        public int Height { get; private set; }

        public static int SpriteOwner(int sprite) => -(sprite + 1);

        public Recording Begin(int width, int height)
        {
            if (Owner.Length != width * height)
            {
                Owner = new int[width * height];
                Triangle = new int[width * height];
            }

            Array.Clear(Owner);
            Instances.Clear();
            (Width, Height) = (width, height);
            return this;
        }

        public void Cover(int pixel, int instance, int triangle)
        {
            Owner[pixel] = instance + 1;
            Triangle[pixel] = triangle;
        }

        public void Paste(Recording pass, int id, int x, int y)
        {
            var instanceBase = Instances.Count;
            Instances.AddRange(pass.Instances);
            for (var row = 0; row < pass.Height; row++)
            {
                for (var column = 0; column < pass.Width; column++)
                {
                    var source = row * pass.Width + column;
                    var target = (y + row) * Width + x + column;
                    var owner = pass.Owner[source];
                    Owner[target] = owner > 0 ? owner + instanceBase : owner - (id + 1) * PassShift;
                    Triangle[target] = pass.Triangle[source];
                }
            }
        }

        public byte[] Classify()
        {
            foreach (var instance in Instances)
            {
                instance.FindSmoothVertices();
            }

            var mask = new byte[Owner.Length];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var pixel = y * Width + x;
                    mask[pixel] = SpansSurfaces(x, y) ? Edge : IsClearColor(Owner[pixel]) ? Background : Interior;
                }
            }

            return mask;
        }

        private static bool IsClearColor(int owner) => owner == ClearColor || (owner < 0 && owner % PassShift == 0);

        private bool SpansSurfaces(int x, int y)
        {
            Span<int> window = stackalloc int[9];
            var count = 0;
            for (var ny = Math.Max(0, y - 1); ny <= Math.Min(Height - 1, y + 1); ny++)
            {
                for (var nx = Math.Max(0, x - 1); nx <= Math.Min(Width - 1, x + 1); nx++)
                {
                    window[count++] = ny * Width + nx;
                }
            }

            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    if (!SameSurface(window[i], window[j]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool SameSurface(int a, int b)
        {
            var owner = Owner[a];
            if (owner != Owner[b])
            {
                return false;
            }

            return owner <= 0 || Triangle[a] == Triangle[b] || Instances[owner - 1].Continuous(Triangle[a], Triangle[b]);
        }
    }

    /// <summary>One draw in the current frame: its mesh topology and which of its triangles faced the camera.</summary>
    private sealed class InstanceSurfaces(Topology topology)
    {
        private bool[] _smooth = [];

        public Topology Topology { get; } = topology;

        public bool[] Front { get; } = new bool[topology.Corners.Length / 3];

        /// <summary>A welded vertex is smooth when its fan is closed and every triangle in it faces the camera.</summary>
        public void FindSmoothVertices()
        {
            _smooth = new bool[Topology.Fans.Length];
            for (var v = 0; v < _smooth.Length; v++)
            {
                _smooth[v] = !Topology.OnBorder[v] && Topology.Fans[v].Length > 0 && Array.TrueForAll(Topology.Fans[v], t => Front[t]);
            }
        }

        public bool Continuous(int ta, int tb)
        {
            var corners = Topology.Corners;
            var shared = 0;
            var last = -1;
            for (var i = 0; i < 3; i++)
            {
                var v = corners[ta * 3 + i];
                if (v == corners[tb * 3] || v == corners[tb * 3 + 1] || v == corners[tb * 3 + 2])
                {
                    shared++;
                    last = v;
                }
            }

            return shared >= 2 || (shared == 1 && _smooth[last]);
        }
    }

    /// <summary>Per mesh, computed once: welded corners per triangle, the triangles around each welded vertex, and borders.</summary>
    private sealed class Topology(int[] corners, int[][] fans, bool[] onBorder)
    {
        /// <summary>Three welded vertex ids per triangle (a welded id is the first vertex with the same attributes).</summary>
        public int[] Corners { get; } = corners;

        /// <summary>Indexed by welded vertex id: the triangles that use it. Empty for vertices that are not welded ids.</summary>
        public int[][] Fans { get; } = fans;

        /// <summary>The vertex is on an edge used by other than two triangles: an open border, a crease or non-manifold.</summary>
        public bool[] OnBorder { get; } = onBorder;

        public static Topology Of(MeshData mesh)
        {
            var first = new Dictionary<(Vector3, Vector3, Vector3), int>();
            var welded = new int[mesh.Vertices.Length];
            for (var i = 0; i < welded.Length; i++)
            {
                var key = (mesh.Vertices[i].Position, mesh.Vertices[i].Color, mesh.Normals?[i] ?? Vector3.Zero);
                if (!first.TryGetValue(key, out var id))
                {
                    first[key] = id = i;
                }

                welded[i] = id;
            }

            var corners = Array.ConvertAll(mesh.Indices, index => welded[index]);
            var fans = new List<int>[welded.Length];
            var edgeUses = new Dictionary<(int, int), int>();
            for (var t = 0; t < corners.Length / 3; t++)
            {
                for (var i = 0; i < 3; i++)
                {
                    var v = corners[t * 3 + i];
                    var w = corners[t * 3 + (i + 1) % 3];
                    (fans[v] ??= []).Add(t);
                    var edge = v < w ? (v, w) : (w, v);
                    edgeUses[edge] = edgeUses.GetValueOrDefault(edge) + 1;
                }
            }

            var onBorder = new bool[welded.Length];
            foreach (var ((v, w), uses) in edgeUses)
            {
                if (uses != 2)
                {
                    onBorder[v] = onBorder[w] = true;
                }
            }

            return new Topology(corners, Array.ConvertAll(fans, fan => fan?.ToArray() ?? []), onBorder);
        }
    }
}
