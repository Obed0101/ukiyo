using System.Buffers.Binary;
using System.Numerics;
using Ukiyo.Rendering;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// DetMath replaces libm and the SIMD paths of System.Numerics in simulation and packet code. These keep it equal in
/// value to what it replaces (so games behave the same) and pin the properties the cross-platform goldens rely on.
/// </summary>
public sealed class DetMathTests
{
    private static int UlpDistance(float a, float b) => Math.Abs(BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b));

    [Fact]
    public void Pow_is_within_one_ulp_of_the_double_precision_result()
    {
        var random = new Random(11);
        for (var i = 0; i < 20_000; i++)
        {
            var x = (float)(random.NextDouble() * 50);
            var y = (float)(random.NextDouble() * 8 - 4);
            var expected = (float)Math.Pow(x, y);
            Assert.True(UlpDistance(expected, DetMath.Pow(x, y)) <= 1, $"pow({x:R}, {y:R}): {DetMath.Pow(x, y):R} vs {expected:R}");
        }
    }

    [Theory]
    [InlineData(0f, 2f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1f, 1e9f, 1f)]
    [InlineData(4f, 0.5f, 2f)]
    [InlineData(2f, 10f, 1024f)]
    [InlineData(0f, -1f, float.PositiveInfinity)]
    [InlineData(-8f, 0.5f, float.NaN)]
    public void Pow_handles_the_edges_like_the_platform(float x, float y, float expected) => Assert.Equal(expected, DetMath.Pow(x, y));

    [Fact]
    public void Srgb_lookup_equals_the_formula_it_tabulates()
    {
        // The lookup was compared with ColorSpace.Encode for all 1,065,353,215 floats in (0, 1) when it was written; this
        // is a strided sample of the same check plus the neighbours of every decoded byte value.
        for (var bits = 1; bits < 0x3F80_0000; bits += 4093)
        {
            var value = BitConverter.Int32BitsToSingle(bits);
            Assert.Equal(ColorSpace.Encode(value), ColorSpace.LinearToSrgb(value));
        }

        for (var i = 1; i < 255; i++)
        {
            var linear = ColorSpace.SrgbToLinear((byte)i);
            foreach (var value in new[] { MathF.BitDecrement(linear), linear, MathF.BitIncrement(linear) })
            {
                Assert.Equal(ColorSpace.Encode(value), ColorSpace.LinearToSrgb(value));
            }

            Assert.Equal(i, ColorSpace.LinearToSrgb(linear));
        }

        Assert.Equal((0, 0, 255, 255), (ColorSpace.LinearToSrgb(-1f), ColorSpace.LinearToSrgb(float.NaN), ColorSpace.LinearToSrgb(1f), ColorSpace.LinearToSrgb(7f)));
    }

    [Fact]
    public void Srgb_decode_table_is_the_correctly_rounded_formula()
    {
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            var expected = c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);
            Assert.True(UlpDistance(expected, ColorSpace.SrgbToLinear((byte)i)) <= 1, $"byte {i}");
        }
    }

    [Fact]
    public void Compose_has_the_values_of_the_system_numerics_product()
    {
        var random = new Random(5);
        float Next() => (float)(random.NextDouble() * 4 - 2);
        for (var i = 0; i < 2_000; i++)
        {
            var rotation = Quaternion.Normalize(new Quaternion(Next(), Next(), Next(), Next()));
            var scale = new Vector3(Next(), Next(), Next());
            var translation = new Vector3(Next(), Next(), Next());
            var expected = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

            // On arm64 the two are equal as values (the product only adds exact zeros; the sign of a zero may differ and
            // is normalized on write). Asserted within float precision so no CPU's own product can fail it.
            AssertClose(expected, DetMath.Compose(scale, rotation, translation));
            AssertClose(Matrix4x4.CreateFromQuaternion(rotation), DetMath.Rotation(rotation));
        }
    }

    [Fact]
    public void Multiply_matches_system_numerics_within_float_precision()
    {
        var random = new Random(9);
        float Next() => (float)(random.NextDouble() * 4 - 2);
        Matrix4x4 Random4x4() => new(Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next());
        for (var i = 0; i < 2_000; i++)
        {
            var (a, b) = (Random4x4(), Random4x4());
            AssertClose(a * b, DetMath.Multiply(a, b));
        }
    }

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                Assert.Equal(expected[row, column], actual[row, column], tolerance: 1e-5f);
            }
        }
    }

    [Fact]
    public void Vector_helpers_and_axis_angle_match_system_numerics_in_value()
    {
        var random = new Random(3);
        float Next() => (float)(random.NextDouble() * 6 - 3);
        for (var i = 0; i < 2_000; i++)
        {
            var (a, b) = (new Vector3(Next(), Next(), Next()), new Vector3(Next(), Next(), Next()));
            Assert.Equal(Vector3.Dot(a, b), DetMath.Dot(a, b), 4);
            Assert.Equal(Vector2.Dot(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)), DetMath.Dot(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)), 4);
            Assert.True(Vector3.Distance(Vector3.Cross(a, b), DetMath.Cross(a, b)) < 1e-5f);
            Assert.True(Vector3.Distance(Vector3.Normalize(a), DetMath.Normalize(a)) < 1e-6f);

            var axis = DetMath.Normalize(a);
            var angle = Next() * 10;
            Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromAxisAngle(axis, angle), DetMath.AxisAngle(axis, angle))) > 1 - 1e-6f);
        }
    }

    [Fact]
    public void Planar_poses_use_the_deterministic_half_angle_and_positive_zeros()
    {
        var pose = EntityPose.Planar(new Vector2(1, 2), -0.75f);
        var (sin, cos) = DetMath.SinCos(-0.375f);

        Assert.Equal(new Quaternion(0f, 0f, sin, cos), pose.Rotation);
        Assert.False(float.IsNegative(pose.Rotation.X) || float.IsNegative(pose.Rotation.Y));
    }

    [Fact]
    public void Snapshots_and_packets_never_carry_a_negative_zero()
    {
        var pose = new EntityPose(new Vector3(-0f, 1f, -0f), new Quaternion(-0f, 0f, -0f, 1f), Vector3.One);
        var snapshot = new GameSnapshot(3, new Dictionary<string, EntityPose> { ["e"] = pose }) { Values = new Dictionary<string, double> { ["v"] = -0d } };
        var json = SnapshotWriter.Write(snapshot, new SourceIdentity(new Dictionary<string, string>()), new RenderCapabilities("test", "None", "none", RenderProfile.G0Unlit, SupportsCapture: false));
        Assert.DoesNotContain("-0", json);
        Assert.Contains("\"position\":[0,1,0]", json);

        var camera = new CameraState(new Vector3(-0f, 0f, 3f), new Quaternion(-0f, -0f, -0f, 1f), 1f, 0.1f, 100f);
        var packet = new RenderPacket(1, 3, new RenderExtent(32, 32, 1f), new Vector4(-0f, 0f, 0f, 1f), camera, []);
        var bytes = PacketCodec.Encode(packet);
        for (var offset = PacketCodec.HeaderSize; offset + 4 <= bytes.Length; offset += 4)
        {
            Assert.NotEqual(0x8000_0000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        }

        Assert.Equal(packet.Camera, PacketCodec.DecodeFrame(bytes).Camera);
    }
}
