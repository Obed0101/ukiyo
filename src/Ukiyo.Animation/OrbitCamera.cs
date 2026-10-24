using System.Numerics;

namespace Ukiyo.Animation;

/// <summary>
/// Circles a center point at a distance: yaw 0 puts the camera on +Z looking toward -Z, positive yaw swings it toward +X,
/// positive pitch raises it. <see cref="YawPerTick"/> turns it automatically; the other fields are set by the game
/// (a stick, a tween). Pitch is limited to ±<see cref="MaxPitch"/> so the view never points straight down or up.
/// </summary>
public sealed class OrbitCamera : ICameraSource
{
    public const float MaxPitch = 1.5f;

    private const float TwoPi = 2f * MathF.PI;

    private float _distance = 8f;
    private float _pitch = 0.4f;
    private float _yaw;

    public Vector3 Center { get; set; }

    public float Distance
    {
        get => _distance;
        set => _distance = value > 0f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "[CAMERA]: orbit distance must be positive");
    }

    /// <summary>Radians, kept in (-π, π].</summary>
    public float Yaw
    {
        get => _yaw;
        set => _yaw = Wrap(value);
    }

    /// <summary>Radians, clamped to ±<see cref="MaxPitch"/>.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    /// <summary>Radians added to <see cref="Yaw"/> on every <see cref="Advance"/>.</summary>
    public float YawPerTick { get; set; }

    public float FieldOfViewY { get; set; } = MathF.PI / 3f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 100f;

    public CameraPose Pose
    {
        get
        {
            var (sinYaw, cosYaw) = DetMath.SinCos(_yaw);
            var (sinPitch, cosPitch) = DetMath.SinCos(_pitch);
            var direction = new Vector3(cosPitch * sinYaw, sinPitch, cosPitch * cosYaw);
            return new CameraPose(Center + direction * _distance, Center, FieldOfViewY, NearPlane, FarPlane);
        }
    }

    public void Advance() => Yaw = _yaw + YawPerTick;

    private static float Wrap(float angle)
    {
        while (angle > MathF.PI)
        {
            angle -= TwoPi;
        }

        while (angle <= -MathF.PI)
        {
            angle += TwoPi;
        }

        return angle;
    }
}
