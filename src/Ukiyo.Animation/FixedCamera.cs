namespace Ukiyo.Animation;

/// <summary>A camera that stays where it was put (a security camera, a cutscene angle, a frozen pose mid-blend).</summary>
public sealed class FixedCamera(CameraPose pose) : ICameraSource
{
    public CameraPose Pose { get; set; } = pose;
}
