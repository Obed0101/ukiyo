using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>A value at a tick; <see cref="EaseToNext"/> shapes the move to the following key.</summary>
public readonly record struct Keyframe<T>(long Tick, T Value, Ease EaseToNext = Ease.Linear);

/// <summary>
/// Keyframes sorted by strictly increasing tick, evaluated by tick: before the first key the first value holds, after
/// the last the last value holds, or with <c>loop</c> the track repeats every <c>lastTick - firstTick</c> ticks.
/// Like <see cref="Tween{T}"/> it is stateless; pass it the tick you want.
/// </summary>
public sealed class Track<T>
{
    private readonly Keyframe<T>[] _keys;
    private readonly Func<T, T, float, T> _lerp;

    public Track(IEnumerable<Keyframe<T>> keys, Func<T, T, float, T> lerp, bool loop = false)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(lerp);
        _keys = [.. keys];
        _lerp = lerp;
        Loop = loop;
        if (_keys.Length == 0)
        {
            throw new ArgumentException("[ANIM]: a track needs at least one keyframe", nameof(keys));
        }

        for (var i = 1; i < _keys.Length; i++)
        {
            if (_keys[i].Tick <= _keys[i - 1].Tick)
            {
                throw new ArgumentException($"[ANIM]: keyframe ticks must strictly increase (key {i} at tick {_keys[i].Tick} follows tick {_keys[i - 1].Tick})", nameof(keys));
            }
        }
    }

    public bool Loop { get; }

    public int KeyCount => _keys.Length;

    public long FirstTick => _keys[0].Tick;

    public long LastTick => _keys[^1].Tick;

    public T Evaluate(long tick)
    {
        var span = LastTick - FirstTick;
        if (Loop && span > 0 && tick >= FirstTick)
        {
            tick = FirstTick + (tick - FirstTick) % span;
        }

        if (tick <= FirstTick)
        {
            return _keys[0].Value;
        }

        if (tick >= LastTick)
        {
            return _keys[^1].Value;
        }

        var next = 1;
        while (_keys[next].Tick <= tick)
        {
            next++;
        }

        var a = _keys[next - 1];
        var b = _keys[next];
        var t = (float)(tick - a.Tick) / (b.Tick - a.Tick);
        return _lerp(a.Value, b.Value, Easing.Evaluate(a.EaseToNext, t));
    }
}

/// <summary>Tracks for the usual value types.</summary>
public static class Track
{
    public static Track<float> Float(IEnumerable<Keyframe<float>> keys, bool loop = false) => new(keys, Interpolation.Lerp, loop);

    public static Track<Vector3> Vector(IEnumerable<Keyframe<Vector3>> keys, bool loop = false) => new(keys, Interpolation.Lerp, loop);

    public static Track<Vector4> Color(IEnumerable<Keyframe<Vector4>> keys, bool loop = false) => new(keys, Interpolation.Lerp, loop);

    public static Track<Quaternion> Rotation(IEnumerable<Keyframe<Quaternion>> keys, bool loop = false) => new(keys, Interpolation.Nlerp, loop);
}
