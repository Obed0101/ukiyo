using System.Numerics;

namespace Ukiyo.Animation;

public sealed record FollowCameraSettings
{
    /// <summary>Camera position relative to the focus point (default: behind and above, looking down the -Z axis).</summary>
    public Vector3 Offset { get; init; } = new(0f, 4f, 8f);

    /// <summary>Where the camera aims, relative to the focus point.</summary>
    public Vector3 LookOffset { get; init; } = Vector3.Zero;

    /// <summary>Share of the remaining distance covered each tick, in (0, 1]; 1 follows without lag.</summary>
    public float Damping { get; init; } = 0.15f;

    /// <summary>Half-size per axis of the box around the focus point inside which the subject can move without the camera following.</summary>
    public Vector3 Deadzone { get; init; } = Vector3.Zero;

    public float FieldOfViewY { get; init; } = MathF.PI / 3f;

    public float NearPlane { get; init; } = 0.1f;

    public float FarPlane { get; init; } = 100f;
}

/// <summary>
/// Chases a subject with a fixed offset. Each tick the focus point moves a fraction of the way toward the part of the
/// subject that sticks out of the deadzone box; that is a per-tick exponential approach, so the result depends only on the
/// subject's positions tick by tick, not on any clock.
/// </summary>
public sealed class FollowCamera : ICameraSource
{
    private readonly FollowCameraSettings _settings;

    public FollowCamera(FollowCameraSettings settings, Vector3 subject)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!(settings.Damping > 0f && settings.Damping <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Damping, "[CAMERA]: follow damping must be in (0, 1]");
        }

        if (settings.Deadzone.X < 0f || settings.Deadzone.Y < 0f || settings.Deadzone.Z < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Deadzone, "[CAMERA]: follow deadzone must not be negative");
        }

        _settings = settings;
        Focus = subject;
    }

    /// <summary>The point the camera is centered on right now.</summary>
    public Vector3 Focus { get; private set; }

    public CameraPose Pose => new(Focus + _settings.Offset, Focus + _settings.LookOffset, _settings.FieldOfViewY, _settings.NearPlane, _settings.FarPlane);

    /// <summary>Jumps to <paramref name="subject"/> with no easing (a teleport, a scene change).</summary>
    public void Snap(Vector3 subject) => Focus = subject;

    /// <summary>One tick of following <paramref name="subject"/>.</summary>
    public void Advance(Vector3 subject)
    {
        var delta = subject - Focus;
        var zone = _settings.Deadzone;
        var excess = new Vector3(Outside(delta.X, zone.X), Outside(delta.Y, zone.Y), Outside(delta.Z, zone.Z));
        Focus = _settings.Damping >= 1f ? Focus + excess : Focus + excess * _settings.Damping;
    }

    private static float Outside(float delta, float half) => delta > half ? delta - half : delta < -half ? delta + half : 0f;
}
