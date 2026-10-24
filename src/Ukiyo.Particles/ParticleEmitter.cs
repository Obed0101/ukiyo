using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Particles;

/// <summary>
/// One emitter and its particles. Advance it only through <see cref="ParticleSystem.Step"/> (once per game tick);
/// nothing here reads a clock. Particle storage is allocated once at <see cref="EmitterConfig.MaxParticles"/>.
/// </summary>
public sealed class ParticleEmitter
{
    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public int Age;
        public int Life;
    }

    private readonly Particle[] _particles;
    private ParticleRng _rng;
    private float _rateRemainder;
    private long _manualBurst;
    private long _ticks;
    private bool _overflowedThisTick;

    internal ParticleEmitter(EmitterConfig config)
    {
        config.Validate();
        Config = config;
        _particles = new Particle[config.MaxParticles];
        _rng = new ParticleRng(config.Seed);
    }

    public EmitterConfig Config { get; }

    public string Name => Config.Name;

    /// <summary>World position particles spawn at (plus the shape's offset); move it from <c>Update</c>, never from <c>Extract</c>.</summary>
    public Vector2 Position { get; set; }

    /// <summary>Rate and scheduled bursts only run while true; <see cref="Burst"/> works regardless.</summary>
    public bool Emitting { get; set; } = true;

    public int Live { get; private set; }

    /// <summary>Particles created since the emitter was added.</summary>
    public long Spawned { get; private set; }

    /// <summary>Requested particles refused by a full budget (this emitter's or the system's).</summary>
    public long Dropped { get; private set; }

    /// <summary>Live particles removed early by <see cref="OverflowPolicy.ReplaceOldest"/>.</summary>
    public long Evicted { get; private set; }

    /// <summary>Ticks in which a request did not fit (some particle was dropped or evicted).</summary>
    public long OverflowTicks { get; private set; }

    /// <summary>Particles that reached the end of their life.</summary>
    public long Expired { get; private set; }

    /// <summary>Steps taken since the emitter was added; scheduled bursts are relative to it.</summary>
    public long Ticks => _ticks;

    /// <summary>Queues <paramref name="count"/> particles for the next step (several calls add up).</summary>
    public void Burst(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _manualBurst += count;
    }

    /// <summary>Second half of a tick (after <see cref="Advance"/>): spawn what the rate, bursts and <see cref="Burst"/> ask for, with at most <paramref name="allowedLive"/> alive afterwards.</summary>
    internal void Emit(int allowedLive)
    {
        _overflowedThisTick = false;
        Spawn(RequestedThisTick(), allowedLive);
        if (_overflowedThisTick)
        {
            OverflowTicks++;
        }

        _ticks++;
    }

    /// <summary>First half of a tick: age, move and expire the live particles.</summary>
    internal void Advance()
    {
        var gravity = Config.Gravity * Simulation.TickSecondsF;
        var keep = 1f - Config.Drag;
        var kept = 0;
        for (var i = 0; i < Live; i++)
        {
            var p = _particles[i];
            p.Age++;
            if (p.Age >= p.Life)
            {
                Expired++;
                continue;
            }

            p.Velocity = (p.Velocity + gravity) * keep;
            p.Position += p.Velocity * Simulation.TickSecondsF;
            _particles[kept++] = p;
        }

        Live = kept;
    }

    private long RequestedThisTick()
    {
        var requested = _manualBurst;
        _manualBurst = 0;
        var running = Emitting && (Config.DurationTicks == 0 || _ticks < Config.DurationTicks);
        if (!running)
        {
            return requested;
        }

        _rateRemainder += Config.RatePerTick;
        var whole = MathF.Floor(_rateRemainder);
        _rateRemainder -= whole;
        requested += (long)whole;
        foreach (var burst in Config.Bursts)
        {
            if (burst.Tick == _ticks)
            {
                requested += burst.Count;
            }
        }

        return requested;
    }

    private void Spawn(long requested, int allowedLive)
    {
        if (requested <= 0)
        {
            return;
        }

        // Live particles after this tick cannot exceed `cap`: the emitter's own budget or what the system leaves it.
        var cap = Math.Max(Live, Math.Min(Config.MaxParticles, allowedLive));
        var create = (int)Math.Min(requested, cap - Live);
        var evict = 0;
        if (Config.Overflow == OverflowPolicy.ReplaceOldest)
        {
            create = (int)Math.Min(requested, cap);
            evict = Math.Max(0, Live + create - cap);
        }

        if (evict > 0)
        {
            Array.Copy(_particles, evict, _particles, 0, Live - evict);
            Live -= evict;
            Evicted += evict;
        }

        if (requested > create)
        {
            Dropped += requested - create;
        }

        if (requested > create || evict > 0)
        {
            _overflowedThisTick = true;
        }

        for (var i = 0; i < create; i++)
        {
            _particles[Live++] = Create();
        }

        Spawned += create;
    }

    private Particle Create()
    {
        var shape = Config.Shape;
        var life = _rng.RangeInt(Config.LifetimeMinTicks, Config.LifetimeMaxTicks);
        var offset = Vector2.Zero;
        var angle = 0f;
        switch (shape.Kind)
        {
            case ShapeKind.Point:
                angle = _rng.Range(0f, 2f * MathF.PI);
                break;
            case ShapeKind.Circle:
                var around = _rng.Range(0f, 2f * MathF.PI);
                var radius = shape.Radius * MathF.Sqrt(_rng.NextFloat());
                var (aroundSin, aroundCos) = DetMath.SinCos(around);
                offset = new Vector2(aroundCos * radius, aroundSin * radius);
                angle = around;
                break;
            case ShapeKind.Cone:
                angle = shape.DirectionAngle + shape.HalfAngle * (2f * _rng.NextFloat() - 1f);
                break;
            case ShapeKind.Box:
                offset = new Vector2(shape.HalfExtents.X * (2f * _rng.NextFloat() - 1f), shape.HalfExtents.Y * (2f * _rng.NextFloat() - 1f));
                angle = shape.DirectionAngle + shape.HalfAngle * (2f * _rng.NextFloat() - 1f);
                break;
        }

        var speed = _rng.Range(Config.SpeedMin, Config.SpeedMax);
        var (sin, cos) = DetMath.SinCos(angle);
        return new Particle
        {
            Position = Position + offset,
            Velocity = new Vector2(cos * speed, sin * speed),
            Age = 0,
            Life = life,
        };
    }

    internal void Draw(FrameBuilder frame, SpriteFrame sprite)
    {
        for (var i = 0; i < Live; i++)
        {
            var p = _particles[i];
            var t = (float)p.Age / p.Life;
            var size = Config.SizeOverLife.Evaluate(t);
            var color = Config.ColorOverLife.Evaluate(t);
            if (!(size > 0f) || !(color.W > 0f))
            {
                continue;
            }

            frame.DrawSprite(sprite, p.Position, new Vector2(size, size), 0f, Config.Layer, color);
        }
    }

    internal ulong Hash(ulong hash)
    {
        hash = Mix(hash, (ulong)Live);
        hash = Mix(hash, _rng.State);
        hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(DetMath.PositiveZero(_rateRemainder)));
        for (var i = 0; i < Live; i++)
        {
            var p = _particles[i];
            hash = Mix(hash, Bits(p.Position.X));
            hash = Mix(hash, Bits(p.Position.Y));
            hash = Mix(hash, Bits(p.Velocity.X));
            hash = Mix(hash, Bits(p.Velocity.Y));
            hash = Mix(hash, (ulong)(uint)p.Age);
            hash = Mix(hash, (ulong)(uint)p.Life);
        }

        return hash;
    }

    private static ulong Bits(float value) => (uint)BitConverter.SingleToInt32Bits(DetMath.PositiveZero(value));

    internal static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                hash = (hash ^ ((value >> shift) & 0xFF)) * 0x100000001B3UL;
            }

            return hash;
        }
    }

    /// <summary>Copy of the live particle positions in draw order (tests and tools; allocates).</summary>
    public Vector2[] Positions()
    {
        var result = new Vector2[Live];
        for (var i = 0; i < Live; i++)
        {
            result[i] = _particles[i].Position;
        }

        return result;
    }
}
