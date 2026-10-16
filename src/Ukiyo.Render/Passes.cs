namespace Ukiyo.Rendering;

/// <summary>
/// Frame assembly rules every renderer applies to the packets it receives (the JS adapter mirrors them in
/// <c>PassSequencer</c>, web/three-adapter/protocol.js). A single-camera packet is a complete frame. A multi-camera frame
/// is pass 0, 1, … in order as consecutive packets: each next pass has sequence + 1 and the same tick, pass count,
/// primary and drawing buffer. Anything else (a missing or repeated pass, another packet in between) is
/// <see cref="RenderErrorCode.InvalidPacket"/>, never a silently dropped or half-drawn frame.
/// </summary>
public sealed class PassSequencer
{
    private RenderPacket? _open;

    /// <summary>True while a multi-camera frame has started and its last pass has not arrived.</summary>
    public bool IsOpen => _open is not null;

    /// <summary>Checks <paramref name="packet"/> against the frame in progress. Returns true when it completes a frame.</summary>
    public bool Accept(RenderPacket packet)
    {
        var pass = packet.Pass;
        if (_open is { Pass: { } expected } open)
        {
            if (pass is not { } next || next.Id != expected.Id + 1 || packet.Sequence != open.Sequence + 1 || packet.Tick != open.Tick
                || next.Count != expected.Count || next.PrimaryId != expected.PrimaryId
                || packet.Viewport.Width != open.Viewport.Width || packet.Viewport.Height != open.Viewport.Height)
            {
                _open = null;
                throw new RenderException(RenderErrorCode.InvalidPacket,
                    $"frame of {expected.Count} passes at tick {open.Tick} expects pass {expected.Id + 1} (sequence {open.Sequence + 1}) next; got {Describe(packet)}");
            }
        }
        else if (pass is { Id: not 0 } orphan)
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"pass {orphan.Id} of {orphan.Count} without the passes before it ({Describe(packet)})");
        }

        if (pass is not { } current || current.IsLast)
        {
            _open = null;
            return true;
        }

        _open = packet;
        return false;
    }

    /// <summary>Forgets a frame whose pass failed to draw, so the error is reported once and the next frame starts clean.</summary>
    public void Abort() => _open = null;

    /// <summary>Capture needs a complete frame: half of a multi-camera frame is not an image the renderer presented.</summary>
    public void RequireComplete()
    {
        if (_open is { Pass: { } pass } open)
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"capture while the frame at tick {open.Tick} is incomplete (pass {pass.Id} of {pass.Count} received)");
        }
    }

    private static string Describe(RenderPacket packet) => packet.Pass is { } pass
        ? $"pass {pass.Id} of {pass.Count} (sequence {packet.Sequence}, tick {packet.Tick})"
        : $"a single-camera packet (sequence {packet.Sequence}, tick {packet.Tick})";
}
