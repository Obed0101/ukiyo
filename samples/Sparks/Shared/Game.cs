using System.Numerics;
using Ukiyo.Particles;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.Sparks;

/// <summary>
/// Particle sample (docs: skills/ukiyo-particles). Four emitters under one budget: a cone fountain, a ring of circle
/// bursts every second, a box of rising embers and a tiny two-emitter overflow demo (one drops new particles, one
/// replaces the oldest). Everything is a pure function of the tick and the seeds, so every target agrees.
/// </summary>
public sealed class SparksGame : IGame
{
    public const int SystemBudget = 2048;
    public const int BurstPeriodTicks = 60;
    public const float ViewHeight = 10f;

    public static readonly Vector4 Background = new(0.008f, 0.01f, 0.02f, 1f);

    private readonly ParticleSystem _particles = new(SystemBudget);
    private SpriteFrame _sprite;

    public string Name => "Sparks";

    public ParticleSystem Particles => _particles;

    /// <summary>The emitters this sample runs; also used by tests that need the same setup without a runtime.</summary>
    public static IReadOnlyList<EmitterConfig> EmitterConfigs()
    {
        var bursts = new List<Burst>();
        for (var tick = 0; tick < 600; tick += BurstPeriodTicks)
        {
            bursts.Add(new Burst(tick, 120));
        }

        return
        [
            new EmitterConfig
            {
                Name = "fountain",
                Seed = 11,
                Shape = ParticleShape.Cone(MathF.PI / 2f, 0.28f),
                RatePerTick = 3f,
                LifetimeMinTicks = 70,
                LifetimeMaxTicks = 100,
                SpeedMin = 6.5f,
                SpeedMax = 8.5f,
                Gravity = new Vector2(0f, -9.8f),
                Drag = 0.004f,
                SizeOverLife = Curve.Linear(0.16f, 0.05f),
                ColorOverLife = new ColorCurve(
                    new ColorStop(0f, new Vector4(1f, 0.85f, 0.35f, 1f)),
                    new ColorStop(0.5f, new Vector4(1f, 0.3f, 0.04f, 0.9f)),
                    new ColorStop(1f, new Vector4(0.4f, 0.02f, 0.01f, 0f))),
                MaxParticles = 512,
                Layer = 1,
            },
            new EmitterConfig
            {
                Name = "ring",
                Seed = 22,
                Shape = ParticleShape.Circle(0.4f),
                Bursts = bursts,
                LifetimeMinTicks = 66,
                LifetimeMaxTicks = 84,
                SpeedMin = 2f,
                SpeedMax = 4.5f,
                Drag = 0.03f,
                SizeOverLife = Curve.Linear(0.22f, 0.06f),
                ColorOverLife = new ColorCurve(
                    new ColorStop(0f, new Vector4(0.3f, 0.9f, 1f, 1f)),
                    new ColorStop(0.6f, new Vector4(0.1f, 0.5f, 1f, 1f)),
                    new ColorStop(1f, new Vector4(0.05f, 0.2f, 1f, 0f))),
                MaxParticles = 400,
                Layer = 2,
            },
            new EmitterConfig
            {
                Name = "embers",
                Seed = 33,
                Shape = ParticleShape.Box(new Vector2(7f, 0.2f), MathF.PI / 2f, 0.4f),
                RatePerTick = 1.5f,
                LifetimeMinTicks = 120,
                LifetimeMaxTicks = 200,
                SpeedMin = 0.4f,
                SpeedMax = 1.2f,
                Gravity = new Vector2(0f, 0.6f),
                SizeOverLife = Curve.Linear(0.07f, 0.02f),
                ColorOverLife = new ColorCurve(
                    new ColorStop(0f, new Vector4(0.9f, 0.35f, 0.05f, 0f)),
                    new ColorStop(0.2f, new Vector4(0.9f, 0.35f, 0.05f, 0.8f)),
                    new ColorStop(1f, new Vector4(0.2f, 0.05f, 0.02f, 0f))),
                MaxParticles = 600,
                Layer = 0,
            },
            new EmitterConfig
            {
                Name = "dropper",
                Seed = 44,
                Shape = ParticleShape.Point(),
                RatePerTick = 4f,
                LifetimeMinTicks = 90,
                LifetimeMaxTicks = 90,
                SpeedMin = 0.8f,
                SpeedMax = 1.4f,
                SizeOverLife = Curve.Constant(0.18f),
                ColorOverLife = ColorCurve.Constant(new Vector4(0.2f, 1f, 0.3f, 1f)),
                MaxParticles = 24,
                Overflow = OverflowPolicy.DropNew,
                Layer = 3,
            },
            new EmitterConfig
            {
                Name = "replacer",
                Seed = 55,
                Shape = ParticleShape.Point(),
                RatePerTick = 4f,
                LifetimeMinTicks = 90,
                LifetimeMaxTicks = 90,
                SpeedMin = 0.8f,
                SpeedMax = 1.4f,
                SizeOverLife = Curve.Constant(0.18f),
                ColorOverLife = ColorCurve.Constant(new Vector4(1f, 0.25f, 0.9f, 1f)),
                MaxParticles = 24,
                Overflow = OverflowPolicy.ReplaceOldest,
                Layer = 3,
            },
        ];
    }

    public void Initialize(GameContext context)
    {
        _sprite = ParticleSystem.CreateSprite(context);
        foreach (var config in EmitterConfigs())
        {
            _particles.Add(config);
        }

        _particles["fountain"].Position = new Vector2(-4.5f, -4f);
        _particles["ring"].Position = new Vector2(2f, 0.5f);
        _particles["embers"].Position = new Vector2(0f, -4.6f);
        _particles["dropper"].Position = new Vector2(-7f, 3f);
        _particles["replacer"].Position = new Vector2(7f, 3f);
    }

    public void Update(in TickInfo tick) => _particles.Step();

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = Background;
        frame.Camera2D = new Camera2D(Vector2.Zero, ViewHeight);
        _particles.Draw(frame, _sprite);
    }

    public GameSnapshot Snapshot(long tick)
    {
        var values = new Dictionary<string, double>();
        _particles.AddSnapshotValues(values);
        return new GameSnapshot(tick, new Dictionary<string, EntityPose>()) { Values = values };
    }
}
