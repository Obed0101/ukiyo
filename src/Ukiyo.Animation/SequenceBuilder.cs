namespace Ukiyo.Animation;

/// <summary>Fluent description of a <see cref="Sequence"/>; steps run in the order they are added.</summary>
public sealed class SequenceBuilder
{
    private readonly List<SequenceStep> _steps = [];

    /// <summary>Waits <paramref name="ticks"/> advances (0 is no wait).</summary>
    public SequenceBuilder Wait(int ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "[ANIM]: wait must be zero or more ticks");
        }

        _steps.Add(new WaitStep(ticks));
        return this;
    }

    /// <summary>Holds until <paramref name="condition"/> is true, checked at the start of every advance; completes in that advance without spending its tick.</summary>
    public SequenceBuilder WaitUntil(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _steps.Add(new WaitUntilStep(condition));
        return this;
    }

    /// <summary>Runs <paramref name="action"/> once, taking no time.</summary>
    public SequenceBuilder Call(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _steps.Add(new CallStep(action));
        return this;
    }

    /// <summary>Feeds <paramref name="apply"/> the tween's value on each of its ticks, the last one exactly the end value; a zero-length tween applies the end value at once.</summary>
    public SequenceBuilder Play<T>(Tween<T> tween, Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _steps.Add(new TweenStep<T>(tween, apply));
        return this;
    }

    /// <summary>Runs one branch per builder callback side by side; the step ends when all branches have ended.</summary>
    public SequenceBuilder Parallel(params Action<SequenceBuilder>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        var built = new Sequence[branches.Length];
        for (var i = 0; i < branches.Length; i++)
        {
            var builder = new SequenceBuilder();
            branches[i](builder);
            built[i] = builder.Build();
        }

        _steps.Add(new ParallelStep(built));
        return this;
    }

    public Sequence Build() => new([.. _steps]);
}
