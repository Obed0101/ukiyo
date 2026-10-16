using System.Numerics;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// FrameBuilder.LookAt is written in scalar float math so packets are byte-identical on arm64 and x64 (CI found a
/// -0 vs +0 quaternion component from the SIMD System.Numerics path). These keep it equal to that path in value.
/// </summary>
public sealed class LookAtTests
{
    public static TheoryData<Vector3, Vector3> Views() => new()
    {
        { new Vector3(0f, 0.6f, 3.2f), Vector3.Zero },
        { new Vector3(0f, 1.35f, 3.7f), new Vector3(0f, -0.15f, 0f) },
        { new Vector3(-4f, 2.3f, 1f), new Vector3(2f, 0f, -3f) },
        { new Vector3(0f, 0f, -5f), Vector3.Zero },
        { new Vector3(3f, 0.5f, 0f), new Vector3(-1f, 0.5f, 0f) },
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void Matches_the_system_numerics_rotation(Vector3 position, Vector3 target)
    {
        var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
        Matrix4x4.Invert(view, out var world);
        var expected = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));

        var actual = FrameBuilder.LookAt(position, target, MathF.PI / 3f, 0.1f, 100f).Rotation;

        // q and -q are the same rotation; compare by the dot product.
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 1f - 1e-6f, $"expected {expected}, got {actual}");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Never_writes_a_negative_zero(Vector3 position, Vector3 target)
    {
        var q = FrameBuilder.LookAt(position, target, MathF.PI / 3f, 0.1f, 100f).Rotation;
        foreach (var component in new[] { q.X, q.Y, q.Z, q.W })
        {
            Assert.False(component == 0f && float.IsNegative(component), $"negative zero in {q}");
        }
    }
}
