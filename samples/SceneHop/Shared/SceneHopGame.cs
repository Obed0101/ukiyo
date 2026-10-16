using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.SceneHop;

/// <summary>A scene entity: a glowing square that either stays home (lantern) or drifts sideways and wraps (drifter).</summary>
public sealed class Mote(string kind, Vector2 home, float size)
{
    public string Kind { get; } = kind;

    public Vector2 Home { get; set; } = home;

    public Vector2 Position { get; set; } = home;

    public float Size { get; set; } = size;

    public float Speed { get; set; }

    public float Glow { get; set; } = 1f;
}

/// <summary>
/// The smallest game with two scenes. Prefabs are functions (<see cref="Lantern"/>, <see cref="Drifter"/>); scenes are
/// JSON under Shared/assets/scenes; each scene runs <see cref="SceneTicks"/> ticks and then asks for the other one,
/// which the registry loads at the next tick boundary. Patches (spawn, dispose, retarget) go through the same registry.
/// </summary>
public sealed class SceneHopGame : IGame
{
    public const int SceneTicks = 60;
    public static readonly string[] SceneOrder = ["meadow", "cavern"];

    private SceneRegistry<Mote> _scenes = null!;
    private SpriteFrame _pixel;

    public string Name => "SceneHop";

    public SceneRegistry<Mote> Scenes => _scenes;

    public void Initialize(GameContext context)
    {
        var white = PixelArt.FromRows(["w"], new Dictionary<char, uint> { ['w'] = PixelArt.Rgb(0xFFFFFF) });
        _pixel = SpriteFrame.Whole(context.CreateTexture(white), white.Width, white.Height);

        _scenes = new SceneRegistry<Mote>(context);
        _scenes.Register(Lantern());
        _scenes.Register(Drifter());
        foreach (var scene in SceneOrder)
        {
            _scenes.RegisterJson(context.Assets.ReadText($"scenes/{scene}.json"));
        }

        _scenes.LoadScene(SceneOrder[0]);
    }

    /// <summary>A lantern stays where content puts it; retarget moves it and changes its glow in place.</summary>
    public static Prefab<Mote> Lantern() =>
        new("lantern", args => new Mote("lantern", Planar(args.Position), args.Float("size")) { Glow = args.Float("glow") })
        {
            Parameters = [new PrefabParameter("glow", 0, 1, 1), new PrefabParameter("size", 0.25, 3, 1)],
            Retarget = (mote, args) =>
            {
                mote.Home = mote.Position = Planar(args.Position);
                mote.Glow = args.Float("glow");
                mote.Size = args.Float("size");
            },
        };

    /// <summary>A drifter moves along X at <c>speed</c> m/s. No retarget hook: a retarget rebuilds it at its new home.</summary>
    public static Prefab<Mote> Drifter() =>
        new("drifter", args => new Mote("drifter", Planar(args.Position), args.Float("size")) { Speed = args.Float("speed") })
        {
            Parameters = [new PrefabParameter("speed", -8, 8, 2), new PrefabParameter("size", 0.25, 3, 0.5)],
        };

    public void Update(in TickInfo tick)
    {
        var bounds = _scenes.Active?.Bounds;
        foreach (var entity in _scenes.Entities)
        {
            var mote = entity.Instance;
            if (mote.Speed == 0)
            {
                continue;
            }

            var x = mote.Position.X + mote.Speed * Simulation.TickSecondsF;
            if (bounds is { } box)
            {
                x = x > box.Max.X ? box.Min.X : x < box.Min.X ? box.Max.X : x;
            }

            mote.Position = mote.Position with { X = x };
        }

        // Asking during tick N makes the other scene live from tick N+1; nothing changes mid-tick.
        if (tick.Tick + 1 - _scenes.LoadedAt >= SceneTicks)
        {
            var next = (Array.IndexOf(SceneOrder, _scenes.ActiveScene) + 1) % SceneOrder.Length;
            _scenes.LoadScene(SceneOrder[next]);
        }
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = _scenes.ActiveScene == "cavern" ? new Vector4(0.03f, 0.015f, 0.05f, 1f) : new Vector4(0.015f, 0.04f, 0.025f, 1f);
        frame.Camera2D = new Camera2D(Vector2.Zero, 9f);
        foreach (var entity in _scenes.Entities)
        {
            var mote = entity.Instance;
            var tint = mote.Kind == "lantern"
                ? new Vector4(0.95f * mote.Glow, 0.55f * mote.Glow, 0.18f * mote.Glow, 1f)
                : new Vector4(0.25f, 0.75f, 0.85f, 1f);
            frame.DrawSprite(_pixel, mote.Position, new Vector2(mote.Size), layer: mote.Kind == "lantern" ? 0 : 1, tint: tint);
        }
    }

    public GameSnapshot Snapshot(long tick)
    {
        var entities = new Dictionary<string, EntityPose>(StringComparer.Ordinal);
        var glow = 0.0;
        foreach (var entity in _scenes.Entities)
        {
            entities[entity.Id] = EntityPose.Planar(entity.Instance.Position, 0f, entity.Instance.Size);
            glow += entity.Instance.Kind == "lantern" ? entity.Instance.Glow : 0;
        }

        return new GameSnapshot(tick, entities)
        {
            Values = new Dictionary<string, double>
            {
                ["entities"] = entities.Count,
                ["sceneTicks"] = tick - _scenes.LoadedAt,
                ["glow"] = glow,
            },
        };
    }

    private static Vector2 Planar(Vector3 position) => new(position.X, position.Y);
}
