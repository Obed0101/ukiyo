using System.Numerics;

namespace Ukiyo.Animation;

public sealed record RailCameraSettings
{
    /// <summary>Positions the camera travels through, at least two, joined by straight segments.</summary>
    public required IReadOnlyList<Vector3> Path { get; init; }

    /// <summary>Optional second rail the camera aims at, travelled in step with <see cref="Path"/>; when null the camera aims at <see cref="LookAt"/>.</summary>
    public IReadOnlyList<Vector3>? TargetPath { get; init; }

    public Vector3 LookAt { get; init; }

    /// <summary>Ticks to go from the first point to the last; 0 starts at the end.</summary>
    public int DurationTicks { get; init; } = 120;

    public Ease Ease { get; init; } = Ease.SmoothStep;

    public float FieldOfViewY { get; init; } = MathF.PI / 3f;

    public float NearPlane { get; init; } = 0.1f;

    public float FarPlane { get; init; } = 100f;
}

/// <summary>A camera on a track: its position is a function of how many ticks it has been advanced (constant speed along the path, shaped by the easing).</summary>
public sealed class RailCamera : ICameraSource
{
    private readonly RailCameraSettings _settings;
    private readonly PolylinePath _path;
    private readonly PolylinePath? _targetPath;

    public RailCamera(RailCameraSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DurationTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.DurationTicks, "[CAMERA]: rail duration must be zero or more ticks");
        }

        _settings = settings;
        _path = new PolylinePath(settings.Path, "rail path");
        _targetPath = settings.TargetPath is null ? null : new PolylinePath(settings.TargetPath, "rail target path");
    }

    /// <summary>Ticks travelled so far, at most the duration.</summary>
    public int Ticks { get; private set; }

    public bool IsFinished => Ticks >= _settings.DurationTicks;

    public CameraPose Pose
    {
        get
        {
            var progress = Easing.Evaluate(_settings.Ease, _settings.DurationTicks == 0 ? 1f : (float)Ticks / _settings.DurationTicks);
            var target = _targetPath?.At(progress) ?? _settings.LookAt;
            return new CameraPose(_path.At(progress), target, _settings.FieldOfViewY, _settings.NearPlane, _settings.FarPlane);
        }
    }

    public void Advance()
    {
        if (Ticks < _settings.DurationTicks)
        {
            Ticks++;
        }
    }

    public void Restart() => Ticks = 0;
}
