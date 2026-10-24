using Ukiyo.Rendering;

namespace Ukiyo.Particles;

/// <summary>
/// A set of emitters under one global budget. Call <see cref="Step"/> once per tick from <c>Update</c> and
/// <see cref="Draw"/> from <c>Extract</c>. Particles draw as tinted world sprites of one white texel, through the
/// existing sprite path: every renderer already draws them identically, so there is no protocol feature to carry.
/// </summary>
/// <remarks>
/// Budgets: each emitter holds at most <see cref="EmitterConfig.MaxParticles"/> live particles and the system at most
/// <see cref="MaxLive"/>. Emitters step in the order they were added, so when the system budget is the limit, earlier
/// emitters get first claim. The excess is dropped or, with <see cref="OverflowPolicy.ReplaceOldest"/>, replaces the
/// emitter's own oldest particles. Both are counted and show up in <see cref="AddSnapshotValues"/>.
/// Every live particle is one sprite, and a frame holds at most <see cref="RenderValidation.MaxSprites"/>, which is the
/// largest system budget; leave room for the sprites the rest of the game draws.
/// </remarks>
public sealed class ParticleSystem
{
    public const int DefaultMaxLive = 16384;

    private readonly List<ParticleEmitter> _emitters = [];

    public ParticleSystem(int maxLive = DefaultMaxLive)
    {
        if (maxLive < 1 || maxLive > RenderValidation.MaxSprites)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLive), $"[PARTICLES]: maxLive {maxLive} is outside 1..{RenderValidation.MaxSprites}");
        }

        MaxLive = maxLive;
    }

    public int MaxLive { get; }

    public IReadOnlyList<ParticleEmitter> Emitters => _emitters;

    public int Live => _emitters.Sum(e => e.Live);

    public long Spawned => _emitters.Sum(e => e.Spawned);

    public long Dropped => _emitters.Sum(e => e.Dropped);

    public long Evicted => _emitters.Sum(e => e.Evicted);

    public long OverflowTicks => _emitters.Sum(e => e.OverflowTicks);

    /// <summary>Creates the 1x1 white texture particles are drawn with; call once in <c>Initialize</c>.</summary>
    public static SpriteFrame CreateSprite(GameContext context) =>
        SpriteFrame.Whole(context.CreateTexture(new TextureData(1, 1, [255, 255, 255, 255])), 1, 1);

    public ParticleEmitter Add(EmitterConfig config)
    {
        var emitter = new ParticleEmitter(config);
        if (_emitters.Any(e => e.Name == config.Name))
        {
            throw new ArgumentException($"[PARTICLES]: an emitter named '{config.Name}' already exists", nameof(config));
        }

        _emitters.Add(emitter);
        return emitter;
    }

    public ParticleEmitter this[string name] =>
        _emitters.FirstOrDefault(e => e.Name == name) ?? throw new KeyNotFoundException($"[PARTICLES]: no emitter named '{name}'");

    /// <summary>Advances every emitter by one tick: age and move the live particles, then spawn.</summary>
    public void Step()
    {
        foreach (var emitter in _emitters)
        {
            emitter.Advance();
        }

        // Spawn after every emitter has expired its dead, so a particle about to die never blocks a later emitter.
        var live = Live;
        foreach (var emitter in _emitters)
        {
            var before = emitter.Live;
            emitter.Emit(MaxLive - (live - before));
            live += emitter.Live - before;
        }
    }

    public void Draw(FrameBuilder frame, SpriteFrame sprite)
    {
        foreach (var emitter in _emitters)
        {
            emitter.Draw(frame, sprite);
        }
    }

    /// <summary>Hash of every emitter's particles, random state and counters; equal states give equal hashes on every target.</summary>
    public ulong StateHash()
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var emitter in _emitters)
        {
            hash = emitter.Hash(hash);
            hash = ParticleEmitter.Mix(hash, (ulong)emitter.Spawned);
            hash = ParticleEmitter.Mix(hash, (ulong)emitter.Dropped);
            hash = ParticleEmitter.Mix(hash, (ulong)emitter.Evicted);
            hash = ParticleEmitter.Mix(hash, (ulong)emitter.OverflowTicks);
        }

        return hash;
    }

    /// <summary>
    /// Adds the counters agents read in <c>ukiyo_run</c> to a snapshot's <c>Values</c>: <c>particles.live|spawned|dropped|
    /// evicted|overflowTicks|maxLive|hash</c> and the same per emitter as <c>particles.&lt;name&gt;.live</c> etc. The hash is
    /// the low 52 bits of <see cref="StateHash"/> so it survives the double.
    /// </summary>
    public void AddSnapshotValues(IDictionary<string, double> values, string prefix = "particles")
    {
        values[$"{prefix}.live"] = Live;
        values[$"{prefix}.maxLive"] = MaxLive;
        values[$"{prefix}.spawned"] = Spawned;
        values[$"{prefix}.dropped"] = Dropped;
        values[$"{prefix}.evicted"] = Evicted;
        values[$"{prefix}.overflowTicks"] = OverflowTicks;
        values[$"{prefix}.hash"] = StateHash() & ((1UL << 52) - 1);
        foreach (var e in _emitters)
        {
            values[$"{prefix}.{e.Name}.live"] = e.Live;
            values[$"{prefix}.{e.Name}.spawned"] = e.Spawned;
            values[$"{prefix}.{e.Name}.dropped"] = e.Dropped;
            values[$"{prefix}.{e.Name}.evicted"] = e.Evicted;
            values[$"{prefix}.{e.Name}.overflowTicks"] = e.OverflowTicks;
        }
    }
}
