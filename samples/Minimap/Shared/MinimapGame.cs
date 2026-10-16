using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.Minimap;

/// <summary>
/// Multi-camera sample (docs/rendering-passes.md): a lit field with colored pillars and a rover that drives in a circle.
/// Pass 0 (primary) is a chase camera over the whole drawing buffer. Pass 1 is a top-down minimap in a square at the
/// top-right corner with its own clear color, its own lighting (bright overhead) and a white frame drawn with
/// screen-space sprites in pass-local pixels. Everything is a pure function of the tick, with <see cref="DetMath"/>
/// trigonometry, so every target sees the same bits.
/// </summary>
public sealed class MinimapGame : IGame
{
    public const float RoverRadiansPerSecond = 0.35f;
    public const float RoverPathRadius = 3f;
    public const float RoverHalfSize = 0.3f;
    public const float FieldHalfSize = 6f;

    /// <summary>Minimap side and margin as fractions of the drawing buffer's shorter side, in integer pixels.</summary>
    public const int MinimapSidePer50 = 17;
    public const int MinimapMarginPer100 = 3;

    public static readonly Vector4 SkyColor = new(0.1f, 0.13f, 0.18f, 1f);
    /// <summary>Encodes to sRGB 33/39/51 almost exactly; a value next to a rounding boundary lands 1/255 apart on Metal.</summary>
    public static readonly Vector4 MapBackground = new(0.0152f, 0.0203f, 0.0331f, 1f);
    public static readonly Vector4 FrameColor = new(0.9f, 0.92f, 0.95f, 1f);
    public static readonly SceneLighting MainLighting = new(new Vector3(0.06f, 0.065f, 0.08f), [Light.Directional(new Vector3(-0.5f, -1f, -0.35f), new Vector3(1f, 0.95f, 0.85f), 0.9f)]);
    public static readonly SceneLighting MapLighting = new(new Vector3(0.35f, 0.35f, 0.38f), [Light.Directional(new Vector3(0f, -1f, 0f), Vector3.One, 0.6f)]);

    /// <summary>Looks straight down (−Y) with −Z up on screen: a −90° turn about +X, written exactly so no trigonometry runs.</summary>
    public static readonly Quaternion TopDown = new(-MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f));

    private static readonly (Vector3 Position, float Height, Vector3 Color)[] Pillars =
    [
        (new Vector3(-4f, 0f, -4f), 1.6f, new Vector3(0.8f, 0.08f, 0.06f)),
        (new Vector3(4f, 0f, -4f), 2.2f, new Vector3(0.06f, 0.55f, 0.12f)),
        (new Vector3(4f, 0f, 4f), 1.2f, new Vector3(0.08f, 0.2f, 0.8f)),
        (new Vector3(-4f, 0f, 4f), 1.9f, new Vector3(0.8f, 0.62f, 0.05f)),
        (new Vector3(0f, 0f, -1.2f), 0.8f, new Vector3(0.55f, 0.08f, 0.6f)),
    ];

    private ResourceHandle _cube;
    private ResourceHandle _floor;
    private ResourceHandle _floorMaterial;
    private ResourceHandle _roverMaterial;
    private ResourceHandle[] _pillarMaterials = [];
    private SpriteFrame _white;
    private Vector3 _rover;
    private Quaternion _heading = Quaternion.Identity;
    private Vector3 _forward = Vector3.UnitZ;

    public string Name => "Minimap";

    public RenderProfile Profile => RenderProfile.G1Lit;

    /// <summary>Square at the top-right corner of the drawing buffer; integer math, so every target gets the same rect.</summary>
    public static PassRect MinimapRect(RenderExtent drawingBuffer)
    {
        var shorter = Math.Max(0, Math.Min(drawingBuffer.Width, drawingBuffer.Height));
        var side = shorter * MinimapSidePer50 / 50;
        var margin = shorter * MinimapMarginPer100 / 100;
        return new PassRect(Math.Max(0, drawingBuffer.Width - side - margin), margin, side, side);
    }

    public static (Vector3 Position, Vector3 Forward) RoverAt(long tick)
    {
        var (sin, cos) = DetMath.SinCos(RoverRadiansPerSecond * (float)(tick * Simulation.TickSeconds));
        return (new Vector3(RoverPathRadius * cos, RoverHalfSize, RoverPathRadius * sin), new Vector3(-sin, 0f, cos));
    }

    public void Initialize(GameContext context)
    {
        _cube = context.CreateMesh(BuildCube());
        _floor = context.CreateMesh(BuildFloor());
        _floorMaterial = context.CreateMaterial(new MaterialData(new Vector4(0.42f, 0.44f, 0.4f, 1f), UseVertexColors: false) { Lit = true });
        _roverMaterial = context.CreateMaterial(new MaterialData(new Vector4(0.95f, 0.95f, 0.92f, 1f), UseVertexColors: false) { Lit = true });
        _pillarMaterials = [.. Pillars.Select(pillar => context.CreateMaterial(new MaterialData(new Vector4(pillar.Color, 1f), UseVertexColors: false) { Lit = true }))];

        // A 1×1 white texel, tinted per sprite: the minimap frame needs flat bars, not art.
        _white = SpriteFrame.Whole(context.CreateTexture(new TextureData(1, 1, [255, 255, 255, 255])), 1, 1);
    }

    public void Update(in TickInfo tick)
    {
        (_rover, _forward) = RoverAt(tick.Tick + 1);

        // Yaw so the rover's local +Z points along its path: rotation about +Y by atan2(forward.x, forward.z), built from
        // the half-angle identities so only sqrt runs.
        var cosYaw = _forward.Z;
        var sinYaw = _forward.X;
        var halfCos = MathF.Sqrt(MathF.Max(0f, (1f + cosYaw) * 0.5f));
        var halfSin = MathF.Sqrt(MathF.Max(0f, (1f - cosYaw) * 0.5f)) * (sinYaw < 0 ? -1f : 1f);
        _heading = new Quaternion(0f, halfSin, 0f, halfCos);
    }

    public void Extract(FrameBuilder frame)
    {
        // Pass 0, primary: chase camera behind and above the rover.
        frame.ClearColor = SkyColor;
        frame.Camera = FrameBuilder.LookAt(_rover - _forward * 3.6f + new Vector3(0f, 2.3f, 0f), _rover + _forward * 2f, MathF.PI / 3f, 0.1f, 60f);
        frame.Lighting = MainLighting;
        DrawField(frame);
        frame.PrimaryPass = 0;

        // Pass 1: top-down minimap with its own background, lighting and a frame in pass-local pixels.
        var map = frame.AddPass(MinimapRect(frame.DrawingBuffer));
        map.ClearColor = MapBackground;
        // Height and field of view keep the field's edges away from pixel centers at the sizes the parity run uses
        // (217 and 435 px), where CPU and GPU rasterizers break ties differently.
        map.Camera = new CameraState(new Vector3(0f, 17f, 0f), TopDown, 0.8f, 1f, 40f);
        map.Lighting = MapLighting;
        DrawField(map);
        DrawFrame(map);
    }

    public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>
    {
        ["rover"] = new(_rover, _heading, new Vector3(RoverHalfSize * 2f)),
    })
    {
        Values = new Dictionary<string, double> { ["passes"] = 2, ["primaryPass"] = 0 },
    };

    private void DrawField(FrameBuilder pass)
    {
        pass.Draw(_floor, _floorMaterial, Matrix4x4.Identity);
        for (var i = 0; i < Pillars.Length; i++)
        {
            var (position, height, _) = Pillars[i];
            pass.Draw(_cube, _pillarMaterials[i], DetMath.Compose(new Vector3(0.7f, height, 0.7f), Quaternion.Identity, position + new Vector3(0f, height * 0.5f, 0f)));
        }

        pass.Draw(_cube, _roverMaterial, DetMath.Compose(new Vector3(RoverHalfSize * 2f), _heading, _rover));
    }

    /// <summary>Four bars along the pass's own edges: screen sprites are in pass-local pixels, origin at its top-left.</summary>
    private void DrawFrame(FrameBuilder pass)
    {
        var size = new Vector2(pass.Viewport.Width, pass.Viewport.Height);
        var thickness = MathF.Max(2f, MathF.Round(size.Y / 72f));
        pass.DrawScreenSprite(_white, Vector2.Zero, new Vector2(size.X, thickness), tint: FrameColor);
        pass.DrawScreenSprite(_white, new Vector2(0f, size.Y - thickness), new Vector2(size.X, thickness), tint: FrameColor);
        pass.DrawScreenSprite(_white, new Vector2(0f, thickness), new Vector2(thickness, size.Y - 2f * thickness), tint: FrameColor);
        pass.DrawScreenSprite(_white, new Vector2(size.X - thickness, thickness), new Vector2(thickness, size.Y - 2f * thickness), tint: FrameColor);
    }

    /// <summary>Unit cube centered on the origin, white vertices, one flat normal per face, counter-clockwise from outside.</summary>
    private static MeshData BuildCube()
    {
        var vertices = new List<VertexPositionColor>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        Face(Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitY, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY);
        return new MeshData([.. vertices], [.. indices]) { Normals = [.. normals] };

        void Face(Vector3 normal, Vector3 up)
        {
            var right = DetMath.Cross(up, normal);
            var center = normal * 0.5f;
            var topLeft = center - right * 0.5f + up * 0.5f;
            var start = (uint)vertices.Count;
            vertices.Add(new VertexPositionColor(topLeft, Vector3.One));
            vertices.Add(new VertexPositionColor(topLeft - up, Vector3.One));
            vertices.Add(new VertexPositionColor(topLeft - up + right, Vector3.One));
            vertices.Add(new VertexPositionColor(topLeft + right, Vector3.One));
            normals.AddRange([normal, normal, normal, normal]);
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }

    /// <summary>Square field at y = 0 facing +Y, split into 12×12 quads.</summary>
    private static MeshData BuildFloor()
    {
        const int cells = 12;
        var vertices = new List<VertexPositionColor>();
        var indices = new List<uint>();
        for (var z = 0; z <= cells; z++)
        {
            for (var x = 0; x <= cells; x++)
            {
                vertices.Add(new VertexPositionColor(new Vector3(-FieldHalfSize + x * (2 * FieldHalfSize / cells), 0f, -FieldHalfSize + z * (2 * FieldHalfSize / cells)), Vector3.One));
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
