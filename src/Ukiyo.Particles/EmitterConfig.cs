using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Particles;

/// <summary>What an emitter does when a burst or the rate asks for more particles than its (or the system's) budget allows.</summary>
public enum OverflowPolicy
{
    /// <summary>Refuse the new particles; live ones keep their place. Counted in <c>Dropped</c>.</summary>
    DropNew,

    /// <summary>Evict this emitter's oldest particles to make room. Counted in <c>Evicted</c>; requests larger than the whole budget still drop their excess (<c>Dropped</c>).</summary>
    ReplaceOldest,
}

public enum ShapeKind
{
    /// <summary>Spawn at the emitter; direction uniform over the full circle.</summary>
    Point,

    /// <summary>Spawn uniformly inside a disc; direction radially outward from the center.</summary>
    Circle,

    /// <summary>Spawn at the emitter; direction within ± half angle of the cone axis.</summary>
    Cone,

    /// <summary>Spawn uniformly inside an axis-aligned box; direction within ± half angle of the axis.</summary>
    Box,
}

/// <summary>Where particles appear and which way they first move. Angles are radians, counter-clockwise from +X; +Y is up.</summary>
public readonly record struct ParticleShape(ShapeKind Kind, float Radius, Vector2 HalfExtents, float DirectionAngle, float HalfAngle)
{
    public static ParticleShape Point() => new(ShapeKind.Point, 0f, Vector2.Zero, 0f, 0f);

    public static ParticleShape Circle(float radius) => new(ShapeKind.Circle, radius, Vector2.Zero, 0f, 0f);

    public static ParticleShape Cone(float directionAngle, float halfAngle) => new(ShapeKind.Cone, 0f, Vector2.Zero, directionAngle, halfAngle);

    public static ParticleShape Box(Vector2 halfExtents, float directionAngle = 0f, float halfAngle = 0f) => new(ShapeKind.Box, 0f, halfExtents, directionAngle, halfAngle);
}

/// <summary>Emit <see cref="Count"/> particles on the emitter's own tick <see cref="Tick"/> (0 = the first step after it was added).</summary>
public readonly record struct Burst(int Tick, int Count);

/// <summary>
/// Everything that defines one emitter. Time is in ticks (60 per second), distances in world units, speeds in units per
/// second, gravity in units per second squared, drag as the fraction of velocity lost per tick (0..1).
/// </summary>
public sealed record EmitterConfig
{
    public const int MaxParticlesPerEmitter = RenderValidation.MaxSprites;
    public const int MaxLifetimeTicks = 1 << 20;
    public const float MaxRatePerTick = 100_000f;

    public required string Name { get; init; }

    /// <summary>Seeds the emitter's private random stream; same seed and same calls give the same particles.</summary>
    public ulong Seed { get; init; }

    public ParticleShape Shape { get; init; } = ParticleShape.Point();

    /// <summary>Particles per tick; fractions accumulate (0.25 emits one every fourth tick). Zero emits nothing.</summary>
    public float RatePerTick { get; init; }

    public IReadOnlyList<Burst> Bursts { get; init; } = [];

    /// <summary>Ticks after which rate and scheduled bursts stop (0 = never). Live particles finish their lives.</summary>
    public int DurationTicks { get; init; }

    public int LifetimeMinTicks { get; init; } = 30;

    public int LifetimeMaxTicks { get; init; } = 60;

    public float SpeedMin { get; init; }

    public float SpeedMax { get; init; }

    public Vector2 Gravity { get; init; }

    public float Drag { get; init; }

    /// <summary>Particle edge length in world units over its life (t = age / lifetime).</summary>
    public Curve SizeOverLife { get; init; } = Curve.Constant(0.1f);

    /// <summary>Linear RGBA over the particle's life; alpha blends straight.</summary>
    public ColorCurve ColorOverLife { get; init; } = ColorCurve.Constant(Vector4.One);

    /// <summary>Hard cap on this emitter's live particles; the storage is allocated once at this size.</summary>
    public int MaxParticles { get; init; } = 1024;

    public OverflowPolicy Overflow { get; init; } = OverflowPolicy.DropNew;

    /// <summary>Sprite layer (higher draws over lower, within the world space).</summary>
    public int Layer { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("[PARTICLES]: an emitter needs a name", nameof(Name));
        }

        if (MaxParticles < 1 || MaxParticles > MaxParticlesPerEmitter)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxParticles), $"[PARTICLES]: {Name}: MaxParticles {MaxParticles} is outside 1..{MaxParticlesPerEmitter}");
        }

        if (!(RatePerTick >= 0f && RatePerTick <= MaxRatePerTick))
        {
            throw new ArgumentOutOfRangeException(nameof(RatePerTick), $"[PARTICLES]: {Name}: RatePerTick {RatePerTick} is outside 0..{MaxRatePerTick}");
        }

        if (LifetimeMinTicks < 1 || LifetimeMaxTicks < LifetimeMinTicks || LifetimeMaxTicks > MaxLifetimeTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(LifetimeMinTicks), $"[PARTICLES]: {Name}: lifetime {LifetimeMinTicks}..{LifetimeMaxTicks} ticks must satisfy 1 <= min <= max <= {MaxLifetimeTicks}");
        }

        if (!(SpeedMin >= 0f && SpeedMax >= SpeedMin && float.IsFinite(SpeedMax)))
        {
            throw new ArgumentOutOfRangeException(nameof(SpeedMin), $"[PARTICLES]: {Name}: speed {SpeedMin}..{SpeedMax} must satisfy 0 <= min <= max");
        }

        if (!(Drag >= 0f && Drag <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(Drag), $"[PARTICLES]: {Name}: Drag {Drag} is outside 0..1");
        }

        if (!float.IsFinite(Gravity.X) || !float.IsFinite(Gravity.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(Gravity), $"[PARTICLES]: {Name}: gravity must be finite");
        }

        if (DurationTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DurationTicks), $"[PARTICLES]: {Name}: DurationTicks {DurationTicks} is negative");
        }

        foreach (var burst in Bursts)
        {
            if (burst.Tick < 0 || burst.Count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Bursts), $"[PARTICLES]: {Name}: burst (tick {burst.Tick}, count {burst.Count}) must be non-negative");
            }
        }

        var s = Shape;
        if (!(s.Radius >= 0f && s.HalfExtents.X >= 0f && s.HalfExtents.Y >= 0f && s.HalfAngle >= 0f
              && float.IsFinite(s.Radius) && float.IsFinite(s.HalfExtents.X) && float.IsFinite(s.HalfExtents.Y)
              && float.IsFinite(s.DirectionAngle) && float.IsFinite(s.HalfAngle)))
        {
            throw new ArgumentOutOfRangeException(nameof(Shape), $"[PARTICLES]: {Name}: shape sizes and angles must be finite and non-negative");
        }
    }
}
