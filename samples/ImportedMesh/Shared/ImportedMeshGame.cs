using System.Numerics;
using Ukiyo.Physics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.ImportedMesh;

/// <summary>
/// G1Lit sample for imported meshes. Blender's Suzanne (smooth normals, a painted vertex-color gradient) turns slowly
/// on a flat-shaded stone pedestal; both come from glTF files in <c>art/</c> that the importer turned into
/// <c>gen/SceneMeshes.g.cs</c>. This file never parses a model or opens a file: it creates the meshes from the compiled
/// data and lights them with an ambient term, a key light and an orange point light that orbits the pedestal (drawn as
/// a tiny unlit copy of the pedestal). Everything is a pure function of the tick, with <see cref="DetMath"/>
/// trigonometry, so every target sees the same bits.
/// </summary>
public sealed class ImportedMeshGame : IGame
{
    public const float RadiansPerSecond = 0.5f;
    public const float LampRadiansPerSecond = 0.6f;
    public const float LampOrbitRadius = 1.3f;
    public const float LampHeight = 0.9f;
    public const float LampScale = 0.12f;

    /// <summary>Suzanne rests on the pedestal's top (y = 0): lifted by the distance from her origin to her lowest point.</summary>
    public static readonly Vector3 SuzannePosition = new(0f, -SceneMeshes.Suzanne.BoundsMin.Y + 0.02f, 0f);

    public static readonly Vector3 Ambient = new(0.03f, 0.035f, 0.05f);
    public static readonly Light KeyLight = Light.Directional(new Vector3(-0.5f, -0.9f, -0.7f), new Vector3(1f, 0.95f, 0.88f), 0.9f);
    public static readonly Vector3 LampColor = new(1f, 0.55f, 0.2f);
    public const float LampIntensity = 1.6f;
    public const float LampRange = 3.5f;

    private ResourceHandle _suzanne;
    private ResourceHandle _pedestal;
    private ResourceHandle _litVertexColors;
    private ResourceHandle _lampMaterial;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _lamp;

    public string Name => "ImportedMesh";

    public RenderProfile Profile => RenderProfile.G1Lit;

    public static Quaternion RotationAt(long tick)
    {
        var (sin, cos) = DetMath.SinCos(0.5f * RadiansPerSecond * (float)(tick * Simulation.TickSeconds));
        return new Quaternion(0f, sin, 0f, cos);
    }

    public static Vector3 LampAt(long tick)
    {
        var (sin, cos) = DetMath.SinCos(LampRadiansPerSecond * (float)(tick * Simulation.TickSeconds));
        return new Vector3(LampOrbitRadius * cos, LampHeight, LampOrbitRadius * sin);
    }

    public void Initialize(GameContext context)
    {
        _suzanne = context.CreateMesh(SceneMeshes.Suzanne.Create());
        _pedestal = context.CreateMesh(SceneMeshes.Pedestal.Create());

        // The importer baked each model's base color into its vertex colors, so one white material draws both.
        _litVertexColors = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true) { Lit = true });
        _lampMaterial = context.CreateMaterial(new MaterialData(new Vector4(LampColor, 1f), UseVertexColors: false));
        _rotation = RotationAt(0);
        _lamp = LampAt(0);
    }

    public void Update(in TickInfo tick)
    {
        _rotation = RotationAt(tick.Tick + 1);
        _lamp = LampAt(tick.Tick + 1);
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = new Vector4(0.012f, 0.014f, 0.02f, 1f);
        frame.Camera = FrameBuilder.LookAt(new Vector3(0f, 1.1f, 2.6f), new Vector3(0f, 0.25f, 0f), MathF.PI / 3f, 0.1f, 100f);
        frame.Lighting = new SceneLighting(Ambient, [KeyLight, Light.Point(_lamp, LampColor, LampIntensity, LampRange)]);
        frame.Draw(_pedestal, _litVertexColors, Matrix4x4.Identity);
        frame.Draw(_suzanne, _litVertexColors, SuzannePose.ToMatrix());
        frame.Draw(_pedestal, _lampMaterial, Matrix4x4.CreateScale(LampScale) * Matrix4x4.CreateTranslation(_lamp));
    }

    /// <summary>Asset counts come from the generated class, so every target's snapshot shows what it compiled in.</summary>
    public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>
    {
        ["suzanne"] = SuzannePose,
        ["lamp"] = new(_lamp, Quaternion.Identity, Vector3.One),
    })
    {
        Values = new Dictionary<string, double>
        {
            ["lights"] = 2,
            ["meshes"] = SceneMeshes.Count,
            ["meshVertices"] = SceneMeshes.VertexCount,
            ["meshTriangles"] = SceneMeshes.TriangleCount,
        },
    };

    private EntityPose SuzannePose => new(SuzannePosition, _rotation, Vector3.One);
}
