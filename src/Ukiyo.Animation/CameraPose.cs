using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Animation;

/// <summary>Where a camera is, what it looks at and its lens: the inputs of <see cref="FrameBuilder.LookAt"/>.</summary>
public readonly record struct CameraPose(Vector3 Position, Vector3 Target, float FieldOfViewY = MathF.PI / 3f, float NearPlane = 0.1f, float FarPlane = 100f)
{
    private const float MinHorizontalDistanceSquared = 1e-10f;

    /// <summary>
    /// The camera for <see cref="FrameBuilder.Camera"/>, through <see cref="FrameBuilder.LookAt"/> (scalar math, same bits
    /// on every target). LookAt derives "right" from world up, so a camera exactly above or below its target, or on top of
    /// it, has no orientation: that is an error here instead of a NaN rotation in the packet.
    /// </summary>
    public CameraState ToState()
    {
        var offset = Position - Target;
        if (!(offset.X * offset.X + offset.Z * offset.Z > MinHorizontalDistanceSquared))
        {
            throw new InvalidOperationException(
                $"[CAMERA]: position {Position} is on the vertical line through target {Target}, so the view direction is degenerate; keep orbit pitch below ±{OrbitCamera.MaxPitch} rad");
        }

        return FrameBuilder.LookAt(Position, Target, FieldOfViewY, NearPlane, FarPlane);
    }

    /// <summary>Linear blend of every field; <paramref name="t"/> 0 is <paramref name="a"/>, 1 is <paramref name="b"/> exactly.</summary>
    public static CameraPose Blend(CameraPose a, CameraPose b, float t) => new(
        Interpolation.Lerp(a.Position, b.Position, t),
        Interpolation.Lerp(a.Target, b.Target, t),
        Interpolation.Lerp(a.FieldOfViewY, b.FieldOfViewY, t),
        Interpolation.Lerp(a.NearPlane, b.NearPlane, t),
        Interpolation.Lerp(a.FarPlane, b.FarPlane, t));

    /// <summary>Writes <c>prefix.px/py/pz</c> (position), <c>tx/ty/tz</c> (target) and <c>fov</c>.</summary>
    public void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values[$"{prefix}.px"] = Position.X;
        values[$"{prefix}.py"] = Position.Y;
        values[$"{prefix}.pz"] = Position.Z;
        values[$"{prefix}.tx"] = Target.X;
        values[$"{prefix}.ty"] = Target.Y;
        values[$"{prefix}.tz"] = Target.Z;
        values[$"{prefix}.fov"] = FieldOfViewY;
    }
}

/// <summary>Anything that can say where a camera is right now; rigs advance once per tick and a <see cref="CameraDirector"/> picks among them.</summary>
public interface ICameraSource
{
    CameraPose Pose { get; }
}
