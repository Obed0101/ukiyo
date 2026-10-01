using System.Numerics;

namespace Ukiyo.Physics;

/// <summary>
/// Trigonometry built only from IEEE-754 add/multiply/round, so every target (CoreCLR, NativeAOT, WebAssembly) produces
/// the same bits. Platform <c>MathF.Sin</c> may differ by an ulp between libm implementations, which is enough to make
/// two simulations drift apart. Accurate to ~1e-7 for |angle| up to a few thousand radians.
/// </summary>
public static class DetMath
{
    private const double TwoOverPi = 0.63661977236758134308;
    private const double PiOver2Hi = 1.5707963267341256; // Cody–Waite split of pi/2
    private const double PiOver2Lo = 6.077100506506192e-11;

    public static (float Sin, float Cos) SinCos(float angle)
    {
        double x = angle;
        var k = Math.Round(x * TwoOverPi, MidpointRounding.ToEven);
        var r = (x - k * PiOver2Hi) - k * PiOver2Lo;
        var r2 = r * r;
        var sin = r * (1 + r2 * (-1.0 / 6 + r2 * (1.0 / 120 + r2 * (-1.0 / 5040 + r2 * (1.0 / 362880 + r2 * (-1.0 / 39916800 + r2 * (1.0 / 6227020800)))))));
        var cos = 1 + r2 * (-0.5 + r2 * (1.0 / 24 + r2 * (-1.0 / 720 + r2 * (1.0 / 40320 + r2 * (-1.0 / 3628800 + r2 * (1.0 / 479001600))))));
        return ((long)k & 3) switch
        {
            0 => ((float)sin, (float)cos),
            1 => ((float)cos, (float)-sin),
            2 => ((float)-sin, (float)-cos),
            _ => ((float)-cos, (float)sin),
        };
    }

    public static Vector2 Rotate(Vector2 v, float sin, float cos) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);

    public static Vector2 RotateInverse(Vector2 v, float sin, float cos) => new(v.X * cos + v.Y * sin, -v.X * sin + v.Y * cos);

    /// <summary>2D cross product (z of the 3D cross).</summary>
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Cross of a scalar angular velocity with a vector: ω × r.</summary>
    public static Vector2 Cross(float w, Vector2 r) => new(-w * r.Y, w * r.X);
}
