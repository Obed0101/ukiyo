namespace Ukiyo.Particles;

/// <summary>
/// SplitMix64: integer-only, so every target produces the same stream for the same seed (no <c>System.Random</c>, whose
/// algorithm is not a contract). One instance per emitter; the stream only advances when the emitter creates a particle.
/// </summary>
public struct ParticleRng
{
    private ulong _state;

    public ParticleRng(ulong seed) => _state = seed;

    public ulong State => _state;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1) with 24 bits, so the conversion to float is exact.</summary>
    public float NextFloat() => (uint)(NextUInt64() >> 40) * (1f / 16777216f);

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>Uniform integer in [min, max] (inclusive).</summary>
    public int RangeInt(int min, int max)
    {
        var span = (ulong)((long)max - min + 1);
        return (int)(min + (long)(NextUInt64() % span));
    }
}
