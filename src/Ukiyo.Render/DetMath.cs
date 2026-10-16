using System.Numerics;

namespace Ukiyo;

/// <summary>
/// Math for simulation and packet paths, built only from IEEE-754 add, subtract, multiply, divide, square root and
/// rounding, evaluated one operation at a time, so CoreCLR and NativeAOT on x64 and arm64 and WebAssembly all produce
/// the same bits. Two things in the platform do not: libm (<c>MathF.Sin</c>, <c>MathF.Pow</c>… differ by an ulp
/// between macOS, glibc and the WebAssembly runtime) and the SIMD paths of System.Numerics (reductions such as
/// <c>Vector3.Dot</c>, <c>Matrix4x4</c> products and <c>Vector4.Transform</c> are fused multiply-adds on CPUs that have
/// them and separate operations elsewhere, and sum lanes in their own order, which also decides the sign of a zero).
/// Element-wise vector add, subtract, multiply and divide are exact and stay as they are. Lives in the lowest assembly
/// (Ukiyo.Render, namespace <c>Ukiyo</c>) so Core, Physics, UI, the renderers' shared geometry and games all use the
/// same functions. Serializers write zeros through <see cref="PositiveZero(float)"/>.
/// </summary>
public static class DetMath
{
    private const double TwoOverPi = 0.63661977236758134308;
    private const double PiOver2Hi = 1.5707963267341256; // Cody–Waite split of pi/2
    private const double PiOver2Lo = 6.077100506506192e-11;
    private const double Ln2Hi = 6.93147180369123816490e-01; // fdlibm split of ln 2: k * Ln2Hi is exact for |k| < 2^11
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double InverseLn2 = 1.44269504088896338700;
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary>Sine and cosine, accurate to ~1e-7 for |angle| up to a few thousand radians.</summary>
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

    /// <summary>
    /// <paramref name="x"/> to the power <paramref name="y"/> for x ≥ 0 (NaN for negative or NaN x), evaluated in
    /// double precision and rounded once, so it is the correctly rounded float for all practical inputs.
    /// </summary>
    public static float Pow(float x, float y)
    {
        if (!(x >= 0) || float.IsNaN(y))
        {
            return float.NaN;
        }

        if (y == 0 || x == 1)
        {
            return 1;
        }

        if (x == 0)
        {
            return y > 0 ? 0 : float.PositiveInfinity;
        }

        return float.IsPositiveInfinity(x) ? (y > 0 ? float.PositiveInfinity : 0) : (float)Exp(y * Log(x));
    }

    /// <summary>Rotation of <paramref name="angle"/> radians about a unit <paramref name="axis"/> (Quaternion.CreateFromAxisAngle without libm).</summary>
    public static Quaternion AxisAngle(Vector3 axis, float angle)
    {
        var (sin, cos) = SinCos(angle * 0.5f);
        return new Quaternion(axis.X * sin, axis.Y * sin, axis.Z * sin, cos);
    }

    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static float LengthSquared(Vector2 v) => v.X * v.X + v.Y * v.Y;

    public static float LengthSquared(Vector3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;

    public static float Length(Vector2 v) => MathF.Sqrt(LengthSquared(v));

    public static float Length(Vector3 v) => MathF.Sqrt(LengthSquared(v));

    public static Vector2 Normalize(Vector2 v)
    {
        var length = Length(v);
        return new Vector2(v.X / length, v.Y / length);
    }

    public static Vector3 Normalize(Vector3 v)
    {
        var length = Length(v);
        return new Vector3(v.X / length, v.Y / length, v.Z / length);
    }

    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Vector2 Rotate(Vector2 v, float sin, float cos) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);

    public static Vector2 RotateInverse(Vector2 v, float sin, float cos) => new(v.X * cos + v.Y * sin, -v.X * sin + v.Y * cos);

    /// <summary>2D cross product (z of the 3D cross).</summary>
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Cross of a scalar angular velocity with a vector: ω × r.</summary>
    public static Vector2 Cross(float w, Vector2 r) => new(-w * r.Y, w * r.X);

    /// <summary>
    /// <c>scale * rotation * translation</c> (row vectors, docs/conventions.md) written out: row i of the rotation
    /// scaled by scale_i, translation in the last row. The same values as the System.Numerics product, which only adds
    /// zeros to them, without depending on how that product is fused or vectorized.
    /// </summary>
    public static Matrix4x4 Compose(Vector3 scale, Quaternion rotation, Vector3 translation)
    {
        var r = Rotation(rotation);
        return new Matrix4x4(
            scale.X * r.M11, scale.X * r.M12, scale.X * r.M13, 0f,
            scale.Y * r.M21, scale.Y * r.M22, scale.Y * r.M23, 0f,
            scale.Z * r.M31, scale.Z * r.M32, scale.Z * r.M33, 0f,
            translation.X, translation.Y, translation.Z, 1f);
    }

    /// <summary>Rotation matrix of a unit quaternion, in the operation order of Matrix4x4.CreateFromQuaternion.</summary>
    public static Matrix4x4 Rotation(Quaternion q)
    {
        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        float xy = q.X * q.Y, wz = q.Z * q.W, xz = q.Z * q.X, wy = q.Y * q.W, yz = q.Y * q.Z, wx = q.X * q.W;
        return new Matrix4x4(
            1f - 2f * (yy + zz), 2f * (xy + wz), 2f * (xz - wy), 0f,
            2f * (xy - wz), 1f - 2f * (zz + xx), 2f * (yz + wx), 0f,
            2f * (xz + wy), 2f * (yz - wx), 1f - 2f * (yy + xx), 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary><c>a * b</c> for row vectors, each element summed left to right without fused multiply-adds.</summary>
    public static Matrix4x4 Multiply(in Matrix4x4 a, in Matrix4x4 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
        a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

    /// <summary>
    /// −0 becomes +0. Two machines that agree on a value can still disagree on the sign of a zero (a SIMD sum adds +0
    /// lanes, a scalar one does not), and serialized bytes and JSON ("-0") would differ; every writer passes floats here.
    /// </summary>
    public static float PositiveZero(float value) => value == 0f ? 0f : value;

    /// <inheritdoc cref="PositiveZero(float)"/>
    public static double PositiveZero(double value) => value == 0d ? 0d : value;

    /// <summary>Natural logarithm of a positive finite double: x = m·2^e with m in [√½, √2), ln m = 2·atanh((m−1)/(m+1)).</summary>
    private static double Log(double x)
    {
        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        if (exponent == 0)
        {
            // Subnormal: scale into the normal range first (exact).
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0); // 2^54
            exponent = (int)((bits >> 52) & 0x7FF) - 54;
        }

        var e = exponent - 1023;
        var m = BitConverter.Int64BitsToDouble((bits & 0x000F_FFFF_FFFF_FFFFL) | 0x3FF0_0000_0000_0000L);
        if (m > Sqrt2)
        {
            m *= 0.5;
            e++;
        }

        var s = (m - 1) / (m + 1);
        var s2 = s * s;

        // |s| ≤ 0.1716, so s2^12 / 25 < 1e-19: the series is exhausted at double precision.
        var series = 1 + s2 * (1.0 / 3 + s2 * (1.0 / 5 + s2 * (1.0 / 7 + s2 * (1.0 / 9 + s2 * (1.0 / 11 + s2 * (1.0 / 13
            + s2 * (1.0 / 15 + s2 * (1.0 / 17 + s2 * (1.0 / 19 + s2 * (1.0 / 21 + s2 * (1.0 / 23 + s2 * (1.0 / 25))))))))))));
        return e * Ln2Hi + (e * Ln2Lo + 2 * s * series);
    }

    /// <summary>e^z: z = k·ln 2 + r with |r| ≤ ½ ln 2, a degree-14 Taylor polynomial for e^r, then an exact scale by 2^k.</summary>
    private static double Exp(double z)
    {
        if (z > 709.78)
        {
            return double.PositiveInfinity;
        }

        if (z < -745.2)
        {
            return 0;
        }

        var k = Math.Round(z * InverseLn2, MidpointRounding.ToEven);
        var r = (z - k * Ln2Hi) - k * Ln2Lo;
        var p = 1 + r * (1 + r * (1.0 / 2 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720 + r * (1.0 / 5040
            + r * (1.0 / 40320 + r * (1.0 / 362880 + r * (1.0 / 3628800 + r * (1.0 / 39916800 + r * (1.0 / 479001600
            + r * (1.0 / 6227020800 + r * (1.0 / 87178291200))))))))))))));
        return Math.ScaleB(p, (int)k);
    }
}
