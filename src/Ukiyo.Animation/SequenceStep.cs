namespace Ukiyo.Animation;

/// <summary>
/// One step of a <see cref="Sequence"/>. <c>Run</c> returns true when the step is finished. <c>tickAvailable</c> says
/// whether this advance's tick is still unspent: a step that takes time spends it (sets it false) and a step that
/// needs a tick it does not have waits for the next advance without changing its state.
/// </summary>
internal abstract class SequenceStep
{
    public abstract bool Run(ref bool tickAvailable);

    public virtual void Cancel()
    {
    }

    public abstract void Reset();

    public virtual void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
    }
}

internal sealed class WaitStep(int ticks) : SequenceStep
{
    private int _elapsed;

    public override bool Run(ref bool tickAvailable)
    {
        if (_elapsed >= ticks)
        {
            return true;
        }

        if (!tickAvailable)
        {
            return false;
        }

        tickAvailable = false;
        _elapsed++;
        return _elapsed >= ticks;
    }

    public override void Reset() => _elapsed = 0;

    public override void WriteSnapshot(string prefix, IDictionary<string, double> values) => values[$"{prefix}.ticks"] = _elapsed;
}

internal sealed class WaitUntilStep(Func<bool> condition) : SequenceStep
{
    private int _waited;

    public override bool Run(ref bool tickAvailable)
    {
        if (condition())
        {
            return true;
        }

        if (tickAvailable)
        {
            tickAvailable = false;
            _waited++;
        }

        return false;
    }

    public override void Reset() => _waited = 0;

    public override void WriteSnapshot(string prefix, IDictionary<string, double> values) => values[$"{prefix}.ticks"] = _waited;
}

internal sealed class CallStep(Action action) : SequenceStep
{
    public override bool Run(ref bool tickAvailable)
    {
        action();
        return true;
    }

    public override void Reset()
    {
    }
}

internal sealed class TweenStep<T>(Tween<T> tween, Action<T> apply) : SequenceStep
{
    private int _elapsed;

    public override bool Run(ref bool tickAvailable)
    {
        if (tween.DurationTicks == 0)
        {
            apply(tween.To);
            return true;
        }

        if (!tickAvailable)
        {
            return false;
        }

        tickAvailable = false;
        _elapsed++;
        apply(tween.ValueAt(_elapsed));
        return _elapsed >= tween.DurationTicks;
    }

    public override void Reset() => _elapsed = 0;

    public override void WriteSnapshot(string prefix, IDictionary<string, double> values) => values[$"{prefix}.ticks"] = _elapsed;
}

/// <summary>Runs branches side by side and finishes when every branch has finished.</summary>
internal sealed class ParallelStep(Sequence[] branches) : SequenceStep
{
    public override bool Run(ref bool tickAvailable)
    {
        var spent = false;
        var allDone = true;
        foreach (var branch in branches)
        {
            if (branch.Status != SequenceStatus.Running)
            {
                continue;
            }

            var branchTick = tickAvailable;
            branch.Run(ref branchTick);
            allDone &= branch.Status != SequenceStatus.Running;
            spent |= tickAvailable && !branchTick;
        }

        if (spent)
        {
            tickAvailable = false;
        }

        return allDone;
    }

    public override void Cancel()
    {
        foreach (var branch in branches)
        {
            branch.Cancel();
        }
    }

    public override void Reset()
    {
        foreach (var branch in branches)
        {
            branch.Restart();
        }
    }

    public override void WriteSnapshot(string prefix, IDictionary<string, double> values)
    {
        for (var i = 0; i < branches.Length; i++)
        {
            branches[i].WriteSnapshot($"{prefix}.b{i}", values);
        }
    }
}
