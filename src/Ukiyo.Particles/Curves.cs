using System.Numerics;

namespace Ukiyo.Particles;

/// <summary>One control point of a <see cref="Curve"/>: <see cref="T"/> is the fraction of the particle's life (0..1).</summary>
public readonly record struct CurveStop(float T, float Value);

/// <summary>One control point of a <see cref="ColorCurve"/>: linear RGBA, straight alpha.</summary>
public readonly record struct ColorStop(float T, Vector4 Color);

/// <summary>
/// Piecewise-linear scalar curve over a particle's life. Before the first stop and after the last the value is held.
/// Evaluation uses plain scalar IEEE operations, so it is bit-identical on every target.
/// </summary>
public sealed class Curve
{
    private readonly CurveStop[] _stops;

    public Curve(params CurveStop[] stops)
    {
        ParticleValidation.CheckStops(stops.Select(s => s.T).ToArray(), nameof(Curve));
        if (stops.Any(s => !float.IsFinite(s.Value)))
        {
            throw new ArgumentException("[PARTICLES]: curve values must be finite", nameof(stops));
        }

        _stops = [.. stops];
    }

    public static Curve Constant(float value) => new(new CurveStop(0f, value));

    public static Curve Linear(float start, float end) => new(new CurveStop(0f, start), new CurveStop(1f, end));

    public IReadOnlyList<CurveStop> Stops => _stops;

    public float Evaluate(float t)
    {
        var stops = _stops;
        if (t <= stops[0].T)
        {
            return stops[0].Value;
        }

        for (var i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].T)
            {
                var a = stops[i - 1];
                var b = stops[i];
                return a.Value + (b.Value - a.Value) * ((t - a.T) / (b.T - a.T));
            }
        }

        return stops[^1].Value;
    }
}

/// <summary>Piecewise-linear color curve over a particle's life (see <see cref="Curve"/>); colors are linear RGBA.</summary>
public sealed class ColorCurve
{
    private readonly ColorStop[] _stops;

    public ColorCurve(params ColorStop[] stops)
    {
        ParticleValidation.CheckStops(stops.Select(s => s.T).ToArray(), nameof(ColorCurve));
        if (stops.Any(s => !float.IsFinite(s.Color.X) || !float.IsFinite(s.Color.Y) || !float.IsFinite(s.Color.Z) || !float.IsFinite(s.Color.W)))
        {
            throw new ArgumentException("[PARTICLES]: curve colors must be finite", nameof(stops));
        }

        _stops = [.. stops];
    }

    public static ColorCurve Constant(Vector4 color) => new(new ColorStop(0f, color));

    public static ColorCurve Linear(Vector4 start, Vector4 end) => new(new ColorStop(0f, start), new ColorStop(1f, end));

    public IReadOnlyList<ColorStop> Stops => _stops;

    public Vector4 Evaluate(float t)
    {
        var stops = _stops;
        if (t <= stops[0].T)
        {
            return stops[0].Color;
        }

        for (var i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].T)
            {
                var a = stops[i - 1];
                var b = stops[i];
                var u = (t - a.T) / (b.T - a.T);
                return new Vector4(
                    a.Color.X + (b.Color.X - a.Color.X) * u,
                    a.Color.Y + (b.Color.Y - a.Color.Y) * u,
                    a.Color.Z + (b.Color.Z - a.Color.Z) * u,
                    a.Color.W + (b.Color.W - a.Color.W) * u);
            }
        }

        return stops[^1].Color;
    }
}

internal static class ParticleValidation
{
    internal static void CheckStops(float[] times, string what)
    {
        if (times.Length == 0)
        {
            throw new ArgumentException($"[PARTICLES]: a {what} needs at least one stop", nameof(times));
        }

        for (var i = 0; i < times.Length; i++)
        {
            if (!(times[i] >= 0f && times[i] <= 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(times), $"[PARTICLES]: {what} stop {i} is at {times[i]}, outside 0..1");
            }

            if (i > 0 && !(times[i] > times[i - 1]))
            {
                throw new ArgumentException($"[PARTICLES]: {what} stops must be strictly ascending (stop {i})", nameof(times));
            }
        }
    }
}
