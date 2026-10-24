using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>Points joined by straight segments, evaluated by fraction of the total length (lengths through <see cref="DetMath"/>).</summary>
internal sealed class PolylinePath
{
    private readonly Vector3[] _points;
    private readonly float[] _cumulative;

    public PolylinePath(IReadOnlyList<Vector3> points, string name)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            throw new ArgumentException($"[CAMERA]: {name} needs at least two points", nameof(points));
        }

        _points = [.. points];
        _cumulative = new float[_points.Length];
        for (var i = 1; i < _points.Length; i++)
        {
            _cumulative[i] = _cumulative[i - 1] + DetMath.Length(_points[i] - _points[i - 1]);
        }
    }

    public Vector3 At(float fraction)
    {
        var total = _cumulative[^1];
        if (!(fraction > 0f) || !(total > 0f))
        {
            return _points[0];
        }

        if (fraction >= 1f)
        {
            return _points[^1];
        }

        var distance = fraction * total;
        var next = 1;
        while (next < _cumulative.Length - 1 && _cumulative[next] < distance)
        {
            next++;
        }

        var start = _cumulative[next - 1];
        var length = _cumulative[next] - start;
        var local = length > 0f ? (distance - start) / length : 1f;
        return Interpolation.Lerp(_points[next - 1], _points[next], local);
    }
}
