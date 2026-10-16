using System.Numerics;
using Ukiyo.Physics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.LitCube;

/// <summary>
/// G1Lit sample: the RotatingCube faces (one color per face, a marker near the top-left of +Z) lit by an ambient term,
/// a warm directional key light and an orange point light that orbits the cube, standing on a lit floor. The lamp is
/// a small unlit cube at the point light, so one frame mixes the lit and unlit pipelines. Everything is a pure
/// function of the tick, with <see cref="DetMath"/> trigonometry, so every target sees the same bits.
/// </summary>
public sealed class LitCubeGame : IGame
{
    public static readonly Vector3 RotationAxis = Vector3.Normalize(new Vector3(0.35f, 1f, 0.2f));
    public const float RadiansPerSecond = 0.7f;
    public const float LampRadiansPerSecond = 0.45f;
    public const float LampOrbitRadius = 1.5f;
    public const float LampHeight = 0.85f;
    public const float FloorY = -0.95f;
    public const float HalfSize = 0.5f;

    public static readonly Vector3 Ambient = new(0.035f, 0.04f, 0.055f);
    public static readonly Light KeyLight = Light.Directional(new Vector3(-0.45f, -1f, -0.6f), new Vector3(1f, 0.93f, 0.82f), 0.85f);
    public static readonly Vector3 LampColor = new(1f, 0.52f, 0.18f);
    public const float LampIntensity = 1.8f;
    public const float LampRange = 4.2f;

    private ResourceHandle _cube;
    private ResourceHandle _floor;
    private ResourceHandle _lampMesh;
    private ResourceHandle _litVertexColors;
    private ResourceHandle _floorMaterial;
    private ResourceHandle _lampMaterial;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _lamp;

    public string Name => "LitCube";

    public RenderProfile Profile => RenderProfile.G1Lit;

    public static Quaternion RotationAt(long tick) => AxisAngle(RotationAxis, RadiansPerSecond * (float)(tick * Simulation.TickSeconds));

    public static Vector3 LampAt(long tick)
    {
        var (sin, cos) = DetMath.SinCos(LampRadiansPerSecond * (float)(tick * Simulation.TickSeconds));
        return new Vector3(LampOrbitRadius * cos, LampHeight, LampOrbitRadius * sin);
    }

    public void Initialize(GameContext context)
    {
        _cube = context.CreateMesh(BuildCube(withNormals: true, HalfSize, FaceColors));
        _floor = context.CreateMesh(BuildFloor());
        _lampMesh = context.CreateMesh(BuildCube(withNormals: false, 0.07f, [Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One]));
        _litVertexColors = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true) { Lit = true });
        _floorMaterial = context.CreateMaterial(new MaterialData(new Vector4(0.62f, 0.6f, 0.56f, 1f), UseVertexColors: false) { Lit = true });
        _lampMaterial = context.CreateMaterial(new MaterialData(new Vector4(LampColor, 1f), UseVertexColors: false));
    }

    public void Update(in TickInfo tick)
    {
        _rotation = RotationAt(tick.Tick + 1);
        _lamp = LampAt(tick.Tick + 1);
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = new Vector4(0.012f, 0.014f, 0.02f, 1f);
        frame.Camera = FrameBuilder.LookAt(new Vector3(0f, 1.35f, 3.7f), new Vector3(0f, -0.15f, 0f), MathF.PI / 3f, 0.1f, 100f);
        frame.Lighting = new SceneLighting(Ambient, [KeyLight, Light.Point(_lamp, LampColor, LampIntensity, LampRange)]);
        frame.Draw(_floor, _floorMaterial, Matrix4x4.Identity);
        frame.Draw(_cube, _litVertexColors, CubePose.ToMatrix());
        frame.Draw(_lampMesh, _lampMaterial, Matrix4x4.CreateTranslation(_lamp));
    }

    public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>
    {
        ["cube"] = CubePose,
        ["lamp"] = new(_lamp, Quaternion.Identity, Vector3.One),
    })
    {
        Values = new Dictionary<string, double> { ["lights"] = 2 },
    };

    private EntityPose CubePose => new(Vector3.Zero, _rotation, Vector3.One);

    /// <summary>Linear colors per face: +X red, −X cyan, +Y green, −Y magenta, +Z blue, −Z yellow (as RotatingCube).</summary>
    private static readonly Vector3[] FaceColors =
    [
        new(0.8f, 0.05f, 0.04f), new(0.03f, 0.55f, 0.6f), new(0.06f, 0.6f, 0.08f),
        new(0.55f, 0.04f, 0.5f), new(0.05f, 0.12f, 0.75f), new(0.75f, 0.6f, 0.03f),
    ];

    private static Quaternion AxisAngle(Vector3 axis, float angle)
    {
        var (sin, cos) = DetMath.SinCos(angle * 0.5f);
        return new Quaternion(axis * sin, cos);
    }

    /// <summary>Cube with one flat normal per face (four vertices each) and, on the lit cube, a white marker on +Z.</summary>
    private static MeshData BuildCube(bool withNormals, float halfSize, Vector3[] colors)
    {
        var vertices = new List<VertexPositionColor>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        Face(Vector3.UnitX, Vector3.UnitY, colors[0]);
        Face(-Vector3.UnitX, Vector3.UnitY, colors[1]);
        Face(Vector3.UnitY, -Vector3.UnitZ, colors[2]);
        Face(-Vector3.UnitY, Vector3.UnitZ, colors[3]);
        Face(Vector3.UnitZ, Vector3.UnitY, colors[4]);
        Face(-Vector3.UnitZ, Vector3.UnitY, colors[5]);
        if (withNormals)
        {
            var size = halfSize * 0.44f;
            Quad(new Vector3(-halfSize * 0.76f, halfSize * 0.76f, halfSize + 0.004f), Vector3.UnitX * size, -Vector3.UnitY * size, Vector3.UnitZ, Vector3.One);
        }

        return new MeshData([.. vertices], [.. indices]) { Normals = withNormals ? [.. normals] : null };

        void Face(Vector3 normal, Vector3 up, Vector3 color)
        {
            var right = Vector3.Cross(up, normal);
            var center = normal * halfSize;
            Quad(center - right * halfSize + up * halfSize, right * (2 * halfSize), -up * (2 * halfSize), normal, color);
        }

        // Quad from its top-left corner along right and down; counter-clockwise seen from the front.
        void Quad(Vector3 topLeft, Vector3 right, Vector3 down, Vector3 normal, Vector3 color)
        {
            var start = (uint)vertices.Count;
            vertices.Add(new VertexPositionColor(topLeft, color));
            vertices.Add(new VertexPositionColor(topLeft + down, color));
            vertices.Add(new VertexPositionColor(topLeft + down + right, color));
            vertices.Add(new VertexPositionColor(topLeft + right, color));
            normals.AddRange([normal, normal, normal, normal]);
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }

    /// <summary>4 m square at <see cref="FloorY"/>, facing +Y, split into 8×8 quads so it is not two huge triangles.</summary>
    private static MeshData BuildFloor()
    {
        const int cells = 8;
        const float half = 2f;
        var vertices = new List<VertexPositionColor>();
        var indices = new List<uint>();
        for (var z = 0; z <= cells; z++)
        {
            for (var x = 0; x <= cells; x++)
            {
                vertices.Add(new VertexPositionColor(new Vector3(-half + x * (2 * half / cells), FloorY, -half + z * (2 * half / cells)), Vector3.One));
            }
        }

        for (var z = 0; z < cells; z++)
        {
            for (var x = 0; x < cells; x++)
            {
                var a = (uint)(z * (cells + 1) + x);
                var b = a + 1;
                var c = a + cells + 1;
                var d = c + 1;

                // Seen from above (+Y), a → c → d is counter-clockwise.
                indices.AddRange([a, c, d, a, d, b]);
            }
        }

        return new MeshData([.. vertices], [.. indices]) { Normals = [.. Enumerable.Repeat(Vector3.UnitY, vertices.Count)] };
    }
}
