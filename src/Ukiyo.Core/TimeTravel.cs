namespace Ukiyo;

/// <summary>
/// Limits of the checkpoint ring a live game keeps for rewinding. Both caps apply; the oldest checkpoint goes first, but
/// the newest is always kept, even if it alone is larger than <see cref="MaxBytes"/>.
/// </summary>
/// <param name="IntervalTicks">A checkpoint every this many ticks (60 = once a second).</param>
/// <param name="MaxBytes">Total size of the kept checkpoints.</param>
/// <param name="MaxCheckpoints">Number of kept checkpoints.</param>
public sealed record TimeTravelOptions(int IntervalTicks = 60, long MaxBytes = 32L << 20, int MaxCheckpoints = 600)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(IntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCheckpoints, 1);
    }
}

/// <summary>What the ring holds right now: the ticks a rewind can reach.</summary>
public sealed record TimeTravelWindow(long OldestTick, long NewestTick, int Checkpoints, long Bytes);

/// <summary>
/// The checkpoint ring and the journal of what came from outside the simulation since the oldest checkpoint: latched
/// input events and patches submitted through <see cref="GameRuntime.SubmitPatch"/>. A checkpoint plus the journal
/// reproduces any tick in between exactly; everything else (game logic, scene loads, game-submitted patches) is replayed
/// by the simulation itself. Memory is bounded by the options (checkpoints) and by the window they span (journal).
/// </summary>
internal sealed class TimeTravel(TimeTravelOptions options)
{
    private readonly List<Checkpoint> _checkpoints = [];
    private long _bytes;

    public TimeTravelOptions Options { get; } = options;

    public List<ScriptedInput> Inputs { get; } = [];

    public List<JournaledPatch> Patches { get; } = [];

    public int Count => _checkpoints.Count;

    public bool Has(long tick) => _checkpoints.Count > 0 && _checkpoints[^1].Tick == tick;

    public TimeTravelWindow? Window(long currentTick) =>
        _checkpoints.Count == 0 ? null : new TimeTravelWindow(_checkpoints[0].Tick, currentTick, _checkpoints.Count, _bytes);

    public long? OldestTick => _checkpoints.Count == 0 ? null : _checkpoints[0].Tick;

    public void Add(long tick, byte[] bytes)
    {
        _checkpoints.Add(new Checkpoint(tick, bytes));
        _bytes += bytes.Length;
        while (_checkpoints.Count > 1 && (_checkpoints.Count > Options.MaxCheckpoints || _bytes > Options.MaxBytes))
        {
            _bytes -= _checkpoints[0].Bytes.Length;
            _checkpoints.RemoveAt(0);
        }

        var oldest = _checkpoints[0].Tick;
        var firstKept = Inputs.FindIndex(entry => entry.Tick >= oldest);
        Inputs.RemoveRange(0, firstKept < 0 ? Inputs.Count : firstKept);
        Patches.RemoveAll(entry => entry.SubmitTick < oldest);
    }

    /// <summary>The newest checkpoint at or before <paramref name="tick"/>.</summary>
    public (long Tick, byte[] Bytes)? Nearest(long tick)
    {
        for (var i = _checkpoints.Count - 1; i >= 0; i--)
        {
            if (_checkpoints[i].Tick <= tick)
            {
                return (_checkpoints[i].Tick, _checkpoints[i].Bytes);
            }
        }

        return null;
    }

    /// <summary>Abandons the future after a rewind to <paramref name="tick"/>: later checkpoints and journal entries go.</summary>
    public void TruncateFrom(long tick)
    {
        while (_checkpoints.Count > 0 && _checkpoints[^1].Tick > tick)
        {
            _bytes -= _checkpoints[^1].Bytes.Length;
            _checkpoints.RemoveAt(_checkpoints.Count - 1);
        }

        Inputs.RemoveAll(entry => entry.Tick >= tick);
        Patches.RemoveAll(entry => entry.SubmitTick >= tick);
    }

    private readonly record struct Checkpoint(long Tick, byte[] Bytes);
}

/// <summary>A patch that came from outside the simulation, with the boundary at which it was submitted.</summary>
internal readonly record struct JournaledPatch(long SubmitTick, ScenePatch Patch);

/// <summary>Outcome of <see cref="GameRuntime.Rewind"/>: the runtime is now at <see cref="Tick"/>, rebuilt from the checkpoint at <see cref="Checkpoint"/>.</summary>
public sealed record RewindResult(long Tick, long Checkpoint, long Replayed, TimeTravelWindow Window);
