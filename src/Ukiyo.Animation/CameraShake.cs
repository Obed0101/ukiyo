using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>
/// Seeded camera shake. The offsets are smooth noise from an integer hash of (seed, tick, axis), scaled by an envelope
/// that decays to zero over the shake's duration, so the same seed and the same <see cref="Advance"/> calls always
/// give the same offsets (no <c>System.Random</c>, no wall clock). <see cref="Offset"/> moves the camera and its target
/// together (the view slides); <see cref="AimOffset"/> moves only the target (the view rotates a little).
/// </summary>
public sealed class CameraShake
{
    private readonly uint _seed;
    private readonly int _periodTicks;
    private long _noiseTick;
    private int _elapsed;
    private int _duration;
    private float _amplitude;
    private float _aimAmplitude;
    private Ease _decay = Ease.QuadOut;

    /// <param name="seed">Picks the noise pattern.</param>
    /// <param name="periodTicks">Ticks between random values; smaller is more violent, larger is a slow wobble.</param>
    public CameraShake(uint seed, int periodTicks = 3)
    {
        if (periodTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(periodTicks), periodTicks, "[CAMERA]: shake period must be at least one tick");
        }

        _seed = seed;
        _periodTicks = periodTicks;
    }

    public bool IsActive => _elapsed < _duration;

    /// <summary>Ticks since the current shake started (0 when idle).</summary>
    public int ElapsedTicks => IsActive ? _elapsed : 0;

    /// <summary>Position offset for the tick just advanced; zero when idle.</summary>
    public Vector3 Offset { get; private set; }

    /// <summary>Target-only offset for the tick just advanced; zero when idle.</summary>
    public Vector3 AimOffset { get; private set; }

    /// <summary>
    /// Starts a shake of <paramref name="amplitude"/> meters (and <paramref name="aimAmplitude"/> at the aim point) that
    /// fades out over <paramref name="durationTicks"/>. While another shake is still stronger than this one the call
    /// is ignored, so a burst of small hits cannot cut a big one short. A zero duration is no shake.
    /// </summary>
    public void Trigger(float amplitude, int durationTicks, float aimAmplitude = 0f, Ease decay = Ease.QuadOut)
    {
        if (durationTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationTicks), durationTicks, "[CAMERA]: shake duration must be zero or more ticks");
        }

        if (!(amplitude >= 0f) || !(aimAmplitude >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "[CAMERA]: shake amplitudes must be zero or more");
        }

        if (durationTicks == 0 || (IsActive && amplitude < _amplitude * Remaining()))
        {
            return;
        }

        _amplitude = amplitude;
        _aimAmplitude = aimAmplitude;
        _duration = durationTicks;
        _elapsed = 0;
        _decay = decay;
    }

    /// <summary>Stops the shake at once.</summary>
    public void Cancel()
    {
        _elapsed = _duration;
        Offset = Vector3.Zero;
        AimOffset = Vector3.Zero;
    }

    /// <summary>One tick; call it once per <c>Update</c>, after any <see cref="Trigger"/> of this tick.</summary>
    public void Advance()
    {
        _noiseTick++;
        if (IsActive)
        {
            _elapsed++;
        }

        if (!IsActive)
        {
            Offset = Vector3.Zero;
            AimOffset = Vector3.Zero;
            return;
        }

        var remaining = Remaining();
        Offset = new Vector3(Sample(0), Sample(1), Sample(2)) * (_amplitude * remaining);
        AimOffset = _aimAmplitude > 0f ? new Vector3(Sample(3), Sample(4), Sample(5)) * (_aimAmplitude * remaining) : Vector3.Zero;
    }

    /// <summary><paramref name="pose"/> with the current offsets applied.</summary>
    public CameraPose Apply(CameraPose pose) => pose with { Position = pose.Position + Offset, Target = pose.Target + Offset + AimOffset };

    public void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values[$"{prefix}.active"] = IsActive ? 1 : 0;
        values[$"{prefix}.elapsed"] = ElapsedTicks;
        values[$"{prefix}.ox"] = Offset.X;
        values[$"{prefix}.oy"] = Offset.Y;
        values[$"{prefix}.oz"] = Offset.Z;
    }

    private float Remaining() =>
        1f - Easing.Evaluate(_decay, _duration == 0 ? 1f : (float)_elapsed / _duration);

    private float Sample(int channel)
    {
        var lattice = (int)(_noiseTick / _periodTicks);
        var fraction = (float)(_noiseTick % _periodTicks) / _periodTicks;
        var smooth = fraction * fraction * (3f - 2f * fraction);
        return Interpolation.Lerp(Hash(lattice, channel), Hash(lattice + 1, channel), smooth);
    }

    /// <summary>A value in [-1, 1) from the integer hash; 24 bits convert to float exactly.</summary>
    private float Hash(int lattice, int channel)
    {
        unchecked
        {
            var x = Mix(_seed ^ Mix((uint)lattice * 0x9E3779B1u + (uint)channel * 0x85EBCA6Bu));
            return (x >> 8) / 8388608f - 1f;
        }
    }

    private static uint Mix(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
}
