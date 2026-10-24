namespace Ukiyo.Animation;

/// <summary>Shape of a transition from 0 to 1. Every curve maps exactly 0 to 0 and 1 to 1; some overshoot in between.</summary>
public enum Ease : byte
{
    Linear = 0,
    SmoothStep,
    QuadIn,
    QuadOut,
    QuadInOut,
    CubicIn,
    CubicOut,
    CubicInOut,
    QuartIn,
    QuartOut,
    QuartInOut,
    SineIn,
    SineOut,
    SineInOut,
    ExpoIn,
    ExpoOut,
    BackOut,
    ElasticOut,
    BounceOut,
}

/// <summary>
/// Easing functions built from <see cref="DetMath"/> and plain IEEE operations, so a curve returns the same bits on
/// every CPU and runtime (no <c>MathF.Sin/Pow</c>).
/// </summary>
public static class Easing
{
    private const float HalfPi = MathF.PI * 0.5f;
    private const float BackOvershoot = 1.70158f;
    private const float ElasticPeriod = 2f * MathF.PI / 3f;

    /// <summary>The eased value for linear progress <paramref name="t"/>; t is clamped to 0..1.</summary>
    public static float Evaluate(Ease ease, float t)
    {
        if (!(t > 0f))
        {
            return 0f;
        }

        if (t >= 1f)
        {
            return 1f;
        }

        var value = ease switch
        {
            Ease.Linear => t,
            Ease.SmoothStep => t * t * (3f - 2f * t),
            Ease.QuadIn => t * t,
            Ease.QuadOut => 1f - (1f - t) * (1f - t),
            Ease.QuadInOut => t < 0.5f ? 2f * t * t : 1f - Square(-2f * t + 2f) * 0.5f,
            Ease.CubicIn => t * t * t,
            Ease.CubicOut => 1f - Cube(1f - t),
            Ease.CubicInOut => t < 0.5f ? 4f * t * t * t : 1f - Cube(-2f * t + 2f) * 0.5f,
            Ease.QuartIn => Square(t * t),
            Ease.QuartOut => 1f - Square(Square(1f - t)),
            Ease.QuartInOut => t < 0.5f ? 8f * Square(t * t) : 1f - Square(Square(-2f * t + 2f)) * 0.5f,
            Ease.SineIn => 1f - DetMath.SinCos(t * HalfPi).Cos,
            Ease.SineOut => DetMath.SinCos(t * HalfPi).Sin,
            Ease.SineInOut => -(DetMath.SinCos(MathF.PI * t).Cos - 1f) * 0.5f,
            Ease.ExpoIn => DetMath.Pow(2f, 10f * t - 10f),
            Ease.ExpoOut => 1f - DetMath.Pow(2f, -10f * t),
            Ease.BackOut => BackOut(t),
            Ease.ElasticOut => DetMath.Pow(2f, -10f * t) * DetMath.SinCos((t * 10f - 0.75f) * ElasticPeriod).Sin + 1f,
            Ease.BounceOut => BounceOut(t),
            _ => throw new ArgumentOutOfRangeException(nameof(ease), ease, "[ANIM]: unknown easing"),
        };
        return DetMath.PositiveZero(value);
    }

    private static float Square(float x) => x * x;

    private static float Cube(float x) => x * x * x;

    private static float BackOut(float t)
    {
        var u = t - 1f;
        return 1f + (BackOvershoot + 1f) * u * u * u + BackOvershoot * u * u;
    }

    private static float BounceOut(float t)
    {
        const float n = 7.5625f;
        const float d = 2.75f;
        if (t < 1f / d)
        {
            return n * t * t;
        }

        if (t < 2f / d)
        {
            var u = t - 1.5f / d;
            return n * u * u + 0.75f;
        }

        if (t < 2.5f / d)
        {
            var u = t - 2.25f / d;
            return n * u * u + 0.9375f;
        }

        var v = t - 2.625f / d;
        return n * v * v + 0.984375f;
    }
}
