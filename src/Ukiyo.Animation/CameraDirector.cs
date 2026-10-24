using Ukiyo.Rendering;

namespace Ukiyo.Animation;

/// <summary>
/// Picks which camera the game sees and blends between cameras over a number of ticks, then adds shake. Rigs are advanced
/// by the game (a follow camera needs its subject); the director advances only the blend and the shake. Typical use:
/// <c>director.BlendTo(orbit, 45)</c> in <c>Update</c>, <c>director.Advance()</c> once per tick, and
/// <c>frame.Camera = director.State</c> in <c>Extract</c>.
/// </summary>
public sealed class CameraDirector
{
    private ICameraSource _from;
    private ICameraSource _to;
    private int _blendTicks;
    private Ease _blendEase = Ease.SmoothStep;

    public CameraDirector(ICameraSource initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _from = initial;
        _to = initial;
    }

    /// <summary>The camera the director is moving to (or already on).</summary>
    public ICameraSource Active => _to;

    public bool IsBlending => BlendElapsed < _blendTicks;

    /// <summary>Ticks into the current blend; equals the blend length once it is over.</summary>
    public int BlendElapsed { get; private set; }

    /// <summary>Linear progress of the blend, 1 when none is running.</summary>
    public float BlendProgress => _blendTicks == 0 ? 1f : (float)BlendElapsed / _blendTicks;

    /// <summary>Optional shake added to the final pose; the director advances it.</summary>
    public CameraShake? Shake { get; set; }

    /// <summary>The blended pose, shake included.</summary>
    public CameraPose Pose
    {
        get
        {
            var pose = Blended();
            return Shake is null ? pose : Shake.Apply(pose);
        }
    }

    /// <summary>The camera to hand to <c>FrameBuilder.Camera</c>.</summary>
    public CameraState State => Pose.ToState();

    /// <summary>Switches to <paramref name="camera"/> at once, dropping any blend in progress.</summary>
    public void CutTo(ICameraSource camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _from = camera;
        _to = camera;
        _blendTicks = 0;
        BlendElapsed = 0;
    }

    /// <summary>
    /// Moves to <paramref name="camera"/> over <paramref name="ticks"/> ticks (0 cuts). Asked again for the camera it is
    /// already heading to, nothing changes. Asked for another camera in mid-blend, the new blend starts from the pose
    /// on screen right now (frozen there), so the picture never jumps; the old camera stops mattering.
    /// </summary>
    public void BlendTo(ICameraSource camera, int ticks, Ease ease = Ease.SmoothStep)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "[CAMERA]: blend must be zero or more ticks");
        }

        if (ReferenceEquals(camera, _to))
        {
            return;
        }

        if (ticks == 0)
        {
            CutTo(camera);
            return;
        }

        _from = IsBlending ? new FixedCamera(Blended()) : _to;
        _to = camera;
        _blendTicks = ticks;
        _blendEase = ease;
        BlendElapsed = 0;
    }

    /// <summary>One tick: moves the blend forward and the shake.</summary>
    public void Advance()
    {
        if (IsBlending)
        {
            BlendElapsed++;
            if (!IsBlending)
            {
                _from = _to;
            }
        }

        Shake?.Advance();
    }

    public void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values[$"{prefix}.blending"] = IsBlending ? 1 : 0;
        values[$"{prefix}.blendElapsed"] = BlendElapsed;
        values[$"{prefix}.blendTicks"] = _blendTicks;
        Pose.WriteSnapshot(prefix, values);
        Shake?.WriteSnapshot($"{prefix}.shake", values);
    }

    private CameraPose Blended() =>
        IsBlending ? CameraPose.Blend(_from.Pose, _to.Pose, Easing.Evaluate(_blendEase, BlendProgress)) : _to.Pose;
}
