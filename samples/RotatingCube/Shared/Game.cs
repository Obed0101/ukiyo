using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.RotatingCube;

/// <summary>
/// G0 game: one cube, rotation computed only here. Every face has its own color and the +Z face carries an
/// off-center white marker, so mirrored axes, wrong winding or swapped color channels are visible in any renderer.
/// </summary>
public sealed class CubeGame : IGame
{
    /// <summary>Rotation law: constant angular speed around a fixed tilted axis. Change these and every target changes.</summary>
    public static readonly Vector3 RotationAxis = Vector3.Normalize(new Vector3(0.35f, 1f, 0.2f));
    public const float RadiansPerSecond = 0.9f;
    public const float HalfSize = 0.5f;

    private ResourceHandle _mesh;
    private ResourceHandle _material;
    private Quaternion _rotation = Quaternion.Identity;

    public string Name => "RotatingCube";

    public static Quaternion ExpectedRotation(long tick) =>
        Quaternion.CreateFromAxisAngle(RotationAxis, RadiansPerSecond * (float)(tick * Simulation.TickSeconds));

    public void Initialize(GameContext context)
    {
        _mesh = context.CreateMesh(BuildCube());
        _material = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true));
    }

    public void Update(in TickInfo tick)
    {
        // State is a pure function of the tick index: no accumulated float drift, identical on every target.
        _rotation = ExpectedRotation(tick.Tick + 1);
    }

    public void Extract(FrameBuilder frame)
    {
        frame.Camera = FrameBuilder.LookAt(new Vector3(0f, 0.6f, 3.2f), Vector3.Zero, MathF.PI / 3f, 0.1f, 100f);
        frame.Draw(_mesh, _material, Pose.ToMatrix());
    }

    public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose> { ["cube"] = Pose });

    private EntityPose Pose => new(Vector3.Zero, _rotation, Vector3.One);

    private static MeshData BuildCube()
    {
        var vertices = new List<VertexPositionColor>();
        var indices = new List<uint>();

        // Linear colors per face: +X red, -X cyan, +Y green, -Y magenta, +Z blue, -Z yellow.
        Face(Vector3.UnitX, Vector3.UnitY, new Vector3(0.8f, 0.05f, 0.04f));
        Face(-Vector3.UnitX, Vector3.UnitY, new Vector3(0.03f, 0.55f, 0.6f));
        Face(Vector3.UnitY, -Vector3.UnitZ, new Vector3(0.06f, 0.6f, 0.08f));
        Face(-Vector3.UnitY, Vector3.UnitZ, new Vector3(0.55f, 0.04f, 0.5f));
        Face(Vector3.UnitZ, Vector3.UnitY, new Vector3(0.05f, 0.12f, 0.75f));
        Face(-Vector3.UnitZ, Vector3.UnitY, new Vector3(0.75f, 0.6f, 0.03f));

        // Asymmetric marker: small white square near the top-left corner of the +Z face, lifted to avoid z-fighting.
        Quad(new Vector3(-0.38f, 0.38f, HalfSize + 0.004f), Vector3.UnitX * 0.22f, -Vector3.UnitY * 0.22f, Vector3.One);

        return new MeshData([.. vertices], [.. indices]);

        void Face(Vector3 normal, Vector3 up, Vector3 color)
        {
            var right = Vector3.Cross(up, normal);
            var center = normal * HalfSize;
            Quad(center - right * HalfSize + up * HalfSize, right * (2 * HalfSize), -up * (2 * HalfSize), color);
        }

        // Quad from its top-left corner along right and down; emitted counter-clockwise seen from the front.
        void Quad(Vector3 topLeft, Vector3 right, Vector3 down, Vector3 color)
        {
            var start = (uint)vertices.Count;
            vertices.Add(new VertexPositionColor(topLeft, color));
            vertices.Add(new VertexPositionColor(topLeft + down, color));
            vertices.Add(new VertexPositionColor(topLeft + down + right, color));
            vertices.Add(new VertexPositionColor(topLeft + right, color));
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }
}
