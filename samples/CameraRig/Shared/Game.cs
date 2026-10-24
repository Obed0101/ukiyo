using System.Numerics;
using Ukiyo.Animation;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.CameraRig;

/// <summary>
/// Gameplay cameras and tick-based sequencing (Ukiyo.Animation). A runner loops around a checkered yard of pillars and
/// one <see cref="Sequence"/> directs the show: the follow camera chases the runner, then a 40-tick blend hands over to an
/// orbit camera, the runner pulses (a tween in a parallel branch) while the camera shakes (seeded), a rail camera flies
/// across, and a last blend returns to the follow camera before the sequence restarts. Everything is a function of the
/// tick and goes through <see cref="DetMath"/>, so every target sees the same bits.
/// </summary>
public sealed class CameraRigGame : IGame
{
    public const float RunnerHalfSize = 0.5f;
    public const uint ShakeSeed = 7u;
    public static readonly Vector4 SkyColor = new(0.05f, 0.06f, 0.09f, 1f);

    private static readonly (Vector3 Position, float Height, Vector4 Color)[] Pillars =
    [
        (new Vector3(-7f, 0f, -5f), 3.0f, new Vector4(0.85f, 0.2f, 0.15f, 1f)),
        (new Vector3(-2f, 0f, -7f), 2.0f, new Vector4(0.2f, 0.65f, 0.3f, 1f)),
        (new Vector3(4f, 0f, -6f), 3.5f, new Vector4(0.2f, 0.4f, 0.9f, 1f)),
        (new Vector3(8f, 0f, 1f), 2.2f, new Vector4(0.9f, 0.7f, 0.15f, 1f)),
        (new Vector3(3f, 0f, 7f), 2.8f, new Vector4(0.7f, 0.25f, 0.8f, 1f)),
        (new Vector3(-6f, 0f, 6f), 1.8f, new Vector4(0.15f, 0.75f, 0.8f, 1f)),
    ];

    private readonly FollowCamera _follow = new(new FollowCameraSettings
    {
        Offset = new Vector3(0f, 3.5f, 7f),
        LookOffset = new Vector3(0f, 0.5f, 0f),
        Damping = 0.12f,
        Deadzone = new Vector3(0.5f, 0f, 0.5f),
    }, RunnerAt(0));

    private readonly OrbitCamera _orbit = new()
    {
        Center = new Vector3(0f, 0.5f, 0f),
        Distance = 14f,
        Pitch = 0.5f,
        YawPerTick = 0.02f,
    };

    private readonly RailCamera _rail = new(new RailCameraSettings
    {
        Path = [new Vector3(-10f, 3f, 10f), new Vector3(0f, 7f, 11f), new Vector3(10f, 3f, 7f), new Vector3(11f, 2f, -3f)],
        LookAt = new Vector3(0f, 0.5f, 0f),
        DurationTicks = 120,
    });

    private readonly CameraDirector _director;
    private readonly Sequence _show;
    private ResourceHandle _cube;
    private ResourceHandle _floor;
    private ResourceHandle _white;
    private ResourceHandle[] _pillarMaterials = [];
    private ResourceHandle _runnerMaterial;
    private Vector3 _runner = RunnerAt(0);
    private float _pulse = 1f;

    public CameraRigGame()
    {
        _director = new CameraDirector(_follow) { Shake = new CameraShake(ShakeSeed) };
        _show = Sequence.Create()
            .Wait(50)
            .Call(() => _director.BlendTo(_orbit, 40))
            .Wait(80)
            .Call(() => _director.Shake!.Trigger(0.35f, 45, aimAmplitude: 0.2f))
            .Parallel(
                branch => branch
                    .Play(Tween.Float(1f, 1.9f, 12, Ease.BackOut), value => _pulse = value)
                    .Play(Tween.Float(1.9f, 1f, 24, Ease.CubicInOut), value => _pulse = value),
                branch => branch.Wait(45))
            .Call(() =>
            {
                _rail.Restart();
                _director.BlendTo(_rail, 50);
            })
            .Wait(130)
            .Call(() => _director.BlendTo(_follow, 40))
            .Wait(60)
            .Build();
    }

    public string Name => "CameraRig";

    /// <summary>The runner's position after <paramref name="tick"/> ticks: a stretched loop around the yard.</summary>
    public static Vector3 RunnerAt(long tick)
    {
        var (sin, cos) = DetMath.SinCos(0.03f * tick);
        return new Vector3(6f * cos, RunnerHalfSize, 4f * sin);
    }

    public void Initialize(GameContext context)
    {
        _cube = context.CreateMesh(BuildCube());
        _floor = context.CreateMesh(BuildFloor());
        _white = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true));
        _pillarMaterials = [.. Pillars.Select(pillar => context.CreateMaterial(new MaterialData(pillar.Color, UseVertexColors: true)))];
        _runnerMaterial = context.CreateMaterial(new MaterialData(new Vector4(1f, 0.95f, 0.75f, 1f), UseVertexColors: true));
    }

    public void Update(in TickInfo tick)
    {
        if (!_show.IsRunning)
        {
            _show.Restart();
        }

        _runner = RunnerAt(tick.Tick + 1);
        _follow.Advance(_runner);
        _orbit.Advance();
        _rail.Advance();
        _show.Advance();
        _director.Advance();
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = SkyColor;
        frame.Camera = _director.State;
        frame.Draw(_floor, _white, Matrix4x4.Identity);
        for (var i = 0; i < Pillars.Length; i++)
        {
            var (position, height, _) = Pillars[i];
            frame.Draw(_cube, _pillarMaterials[i], DetMath.Compose(new Vector3(1.2f, height, 1.2f), Quaternion.Identity, position + new Vector3(0f, height * 0.5f, 0f)));
        }

        var size = RunnerHalfSize * 2f * _pulse;
        frame.Draw(_cube, _runnerMaterial, DetMath.Compose(new Vector3(size), Quaternion.Identity, new Vector3(_runner.X, size * 0.5f, _runner.Z)));
    }

    public GameSnapshot Snapshot(long tick)
    {
        var values = new Dictionary<string, double> { ["pulse"] = _pulse };
        _director.WriteSnapshot("camera", values);
        _show.WriteSnapshot("show", values);
        return new GameSnapshot(tick, new Dictionary<string, EntityPose>
        {
            ["runner"] = new(_runner, Quaternion.Identity, new Vector3(RunnerHalfSize * 2f * _pulse)),
        })
        {
            Values = values,
        };
    }

    /// <summary>Unit cube, white vertices shaded per face so its shape reads without lighting, counter-clockwise from outside.</summary>
    private static MeshData BuildCube()
    {
        var vertices = new List<VertexPositionColor>();
        var indices = new List<uint>();
        Face(Vector3.UnitX, Vector3.UnitY, 0.8f);
        Face(-Vector3.UnitX, Vector3.UnitY, 0.6f);
        Face(Vector3.UnitY, -Vector3.UnitZ, 1f);
        Face(-Vector3.UnitY, Vector3.UnitZ, 0.35f);
        Face(Vector3.UnitZ, Vector3.UnitY, 0.9f);
        Face(-Vector3.UnitZ, Vector3.UnitY, 0.5f);
        return new MeshData([.. vertices], [.. indices]);

        void Face(Vector3 normal, Vector3 up, float shade)
        {
            var right = DetMath.Cross(up, normal);
            var topLeft = normal * 0.5f - right * 0.5f + up * 0.5f;
            Quad(vertices, indices, topLeft, right, -up, new Vector3(shade));
        }
    }

    /// <summary>A 16 x 16 yard of one-meter tiles in two grays with a darker border ring, centered on the origin, facing up.</summary>
    private static MeshData BuildFloor()
    {
        const int tiles = 16;
        var vertices = new List<VertexPositionColor>();
        var indices = new List<uint>();
        for (var z = 0; z < tiles; z++)
        {
            for (var x = 0; x < tiles; x++)
            {
                var edge = x == 0 || z == 0 || x == tiles - 1 || z == tiles - 1;
                var shade = edge ? 0.22f : (x + z) % 2 == 0 ? 0.62f : 0.42f;
                var topLeft = new Vector3(x - tiles / 2f, 0f, z - tiles / 2f);
                Quad(vertices, indices, topLeft, Vector3.UnitX, Vector3.UnitZ, new Vector3(shade));
            }
        }

        return new MeshData([.. vertices], [.. indices]);
    }

    /// <summary>Quad from its top-left corner along right and down; emitted counter-clockwise seen from the front.</summary>
    private static void Quad(List<VertexPositionColor> vertices, List<uint> indices, Vector3 topLeft, Vector3 right, Vector3 down, Vector3 color)
    {
        var start = (uint)vertices.Count;
        vertices.Add(new VertexPositionColor(topLeft, color));
        vertices.Add(new VertexPositionColor(topLeft + down, color));
        vertices.Add(new VertexPositionColor(topLeft + down + right, color));
        vertices.Add(new VertexPositionColor(topLeft + right, color));
        indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
    }
}
