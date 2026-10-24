namespace Ukiyo.Animation;

public enum SequenceStatus : byte
{
    Running = 1,
    Completed = 2,
    Canceled = 3,
}

/// <summary>
/// A timeline of steps counted in simulation ticks: wait, tween, call, wait-until and parallel branches. Call
/// <see cref="Advance"/> exactly once per <c>IGame.Update</c>. A step that takes N ticks consumes N advances; steps that
/// take no time (a call, a zero-length wait or tween) run in the same advance as the step before them, and the first
/// timed step after a finished one starts on the next advance. So <c>Wait(3).Call(f)</c> runs <c>f</c> on the third advance.
/// A tween applies its eased value on every advance, ending on the exact end value.
/// </summary>
/// <remarks>
/// The sequence's cursor (status, elapsed ticks, current step and that step's progress) is plain numbers, written into
/// a snapshot with <see cref="WriteSnapshot"/>; the callbacks are code and live in the game. Same advances in, same
/// callbacks and values out. Build with <see cref="Create"/>.
/// </remarks>
public sealed class Sequence
{
    private readonly SequenceStep[] _steps;
    private int _index;
    private bool _advancing;

    internal Sequence(SequenceStep[] steps)
    {
        _steps = steps;
    }

    public SequenceStatus Status { get; private set; } = SequenceStatus.Running;

    /// <summary>Advances since the sequence started or was restarted, including the one that completed it.</summary>
    public long ElapsedTicks { get; private set; }

    /// <summary>Index of the step in progress (the step count once completed).</summary>
    public int StepIndex => _index;

    public int StepCount => _steps.Length;

    public bool IsRunning => Status == SequenceStatus.Running;

    public static SequenceBuilder Create() => new();

    /// <summary>Runs one tick. Does nothing once completed or canceled.</summary>
    public void Advance()
    {
        var tick = true;
        Run(ref tick);
    }

    /// <summary>
    /// Stops the sequence: no further callbacks or tween values, now or on later advances (also from inside one of its own
    /// callbacks, and for every parallel branch). Values already applied stay where they are.
    /// </summary>
    public void Cancel()
    {
        if (Status != SequenceStatus.Running)
        {
            return;
        }

        Status = SequenceStatus.Canceled;
        for (var i = _index; i < _steps.Length; i++)
        {
            _steps[i].Cancel();
        }
    }

    /// <summary>Back to the first step with all progress cleared, running again. Not allowed from inside a callback.</summary>
    public void Restart()
    {
        if (_advancing)
        {
            throw new InvalidOperationException("[ANIM]: a sequence cannot be restarted from inside its own callback");
        }

        foreach (var step in _steps)
        {
            step.Reset();
        }

        _index = 0;
        ElapsedTicks = 0;
        Status = SequenceStatus.Running;
    }

    /// <summary>
    /// Writes <c>prefix.status</c> (1 running, 2 completed, 3 canceled), <c>prefix.elapsed</c>, <c>prefix.step</c> and, while
    /// running, the current step's own numbers under <c>prefix.cur</c> (ticks into a wait or tween, every branch of a parallel step).
    /// </summary>
    public void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values[$"{prefix}.status"] = (double)Status;
        values[$"{prefix}.elapsed"] = ElapsedTicks;
        values[$"{prefix}.step"] = _index;
        if (Status == SequenceStatus.Running && _index < _steps.Length)
        {
            _steps[_index].WriteSnapshot($"{prefix}.cur", values);
        }
    }

    internal bool Run(ref bool tickAvailable)
    {
        if (Status != SequenceStatus.Running)
        {
            return Status == SequenceStatus.Completed;
        }

        var hadTick = tickAvailable;
        _advancing = true;
        try
        {
            while (Status == SequenceStatus.Running && _index < _steps.Length)
            {
                if (!_steps[_index].Run(ref tickAvailable))
                {
                    break;
                }

                if (Status == SequenceStatus.Running)
                {
                    _index++;
                }
            }

            if (Status == SequenceStatus.Running && _index >= _steps.Length)
            {
                Status = SequenceStatus.Completed;
            }
        }
        finally
        {
            _advancing = false;
        }

        if (hadTick)
        {
            ElapsedTicks++;
        }

        return Status == SequenceStatus.Completed;
    }
}
