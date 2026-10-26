using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>
/// Interpolation written component by component in scalar operations (no <c>Vector3.Lerp</c>, no <c>Quaternion.Slerp</c>),
/// so the bits do not depend on SIMD or libm. <c>t = 1</c> returns the end value exactly.
/// </summary>
public static class Interpolation
{
    public static float Lerp(float a, float b, float t) => t >= 1f ? b : a + (b - a) * t;

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) =>
        new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Z, b.Z, t));

    /// <summary>Linear RGBA (the engine's color space); alpha is interpolated like the other channels.</summary>
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) =>
        new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Z, b.Z, t), Lerp(a.W, b.W, t));

    /// <summary>
    /// Normalized linear interpolation along the shorter arc. Unlike slerp it needs no trigonometry, so it is exact
    /// across platforms; the angular speed is not constant, which is invisible for the small arcs cameras and props turn.
    /// </summary>
    public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
    {
        if (t >= 1f)
        {
            return b;
        }

        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        var sign = dot < 0f ? -1f : 1f;
        var x = Lerp(a.X, b.X * sign, t);
        var y = Lerp(a.Y, b.Y * sign, t);
        var z = Lerp(a.Z, b.Z * sign, t);
        var w = Lerp(a.W, b.W * sign, t);
        var length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (!(length > 0f))
        {
            return a;
        }

        return new Quaternion(x / length, y / length, z / length, w / length);
    }
}
