using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>
/// A value moving from <see cref="From"/> to <see cref="To"/> over whole ticks. It holds no state of its own: the value
/// is a pure function of the elapsed tick count, so a tween is as deterministic as the tick counter that drives it.
/// A zero duration is a jump to <see cref="To"/>.
/// </summary>
public readonly struct Tween<T>
{
    private readonly Func<T, T, float, T> _lerp;

    public Tween(T from, T to, int durationTicks, Ease ease, Func<T, T, float, T> lerp)
    {
        if (durationTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationTicks), durationTicks, "[ANIM]: tween duration must be zero or more ticks");
        }

        ArgumentNullException.ThrowIfNull(lerp);
        From = from;
        To = to;
        DurationTicks = durationTicks;
        Ease = ease;
        _lerp = lerp;
    }

    public T From { get; }

    public T To { get; }

    public int DurationTicks { get; }

    public Ease Ease { get; }

    /// <summary>Linear progress 0..1 after <paramref name="elapsedTicks"/> ticks (1 for a zero duration).</summary>
    public float Progress(int elapsedTicks) =>
        DurationTicks == 0 || elapsedTicks >= DurationTicks ? 1f : elapsedTicks <= 0 ? 0f : (float)elapsedTicks / DurationTicks;

    public T ValueAt(int elapsedTicks) => _lerp(From, To, Easing.Evaluate(Ease, Progress(elapsedTicks)));
}

/// <summary>Constructors for the value types games animate: floats, positions, rotations and linear RGBA colors.</summary>
public static class Tween
{
    private static readonly Func<float, float, float, float> FloatLerp = Interpolation.Lerp;
    private static readonly Func<Vector3, Vector3, float, Vector3> VectorLerp = Interpolation.Lerp;
    private static readonly Func<Vector4, Vector4, float, Vector4> ColorLerp = Interpolation.Lerp;
    private static readonly Func<Quaternion, Quaternion, float, Quaternion> RotationLerp = Interpolation.Nlerp;

    public static Tween<float> Float(float from, float to, int durationTicks, Ease ease = Ease.Linear) =>
        new(from, to, durationTicks, ease, FloatLerp);

    public static Tween<Vector3> Vector(Vector3 from, Vector3 to, int durationTicks, Ease ease = Ease.Linear) =>
        new(from, to, durationTicks, ease, VectorLerp);

    public static Tween<Vector4> Color(Vector4 from, Vector4 to, int durationTicks, Ease ease = Ease.Linear) =>
        new(from, to, durationTicks, ease, ColorLerp);

    /// <summary>Rotation by normalized linear interpolation (shorter arc).</summary>
    public static Tween<Quaternion> Rotation(Quaternion from, Quaternion to, int durationTicks, Ease ease = Ease.Linear) =>
        new(from, to, durationTicks, ease, RotationLerp);
}
