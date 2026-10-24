namespace Ukiyo.AI;

/// <summary>Node constructors, so a tree reads as a nested expression.</summary>
public static class Bt
{
    public static BtNode Sequence(params BtNode[] children) => new SequenceNode("Sequence", children);

    public static BtNode Sequence(string name, params BtNode[] children) => new SequenceNode(name, children);

    /// <summary>Tries children in order every tick (a higher-priority branch interrupts a lower one that was running).</summary>
    public static BtNode Selector(params BtNode[] children) => new SelectorNode("Selector", children);

    public static BtNode Selector(string name, params BtNode[] children) => new SelectorNode(name, children);

    public static BtNode Parallel(ParallelPolicy success, ParallelPolicy failure, params BtNode[] children) =>
        new ParallelNode("Parallel", success, failure, children);

    public static BtNode Inverter(BtNode child) => new InverterNode(child);

    public static BtNode Succeeder(BtNode child) => new SucceederNode(child);

    /// <summary>Runs the child <paramref name="times"/> times in a row (one run per tick at most); fails if a run fails.</summary>
    public static BtNode Repeat(int times, BtNode child) => new RepeatNode(times, child);

    /// <summary>Runs the child forever: always Running, unless a run fails (then Failure).</summary>
    public static BtNode RepeatForever(BtNode child) => new RepeatNode(0, child);

    /// <summary>After the child finishes, fails for <paramref name="ticks"/> ticks before it may run again.</summary>
    public static BtNode Cooldown(long ticks, BtNode child) => new CooldownNode(ticks, child);

    /// <summary>Runs the child only while the condition holds; otherwise fails and aborts the child.</summary>
    public static BtNode Guard(string name, Func<BtContext, bool> condition, BtNode child) => new GuardNode(name, condition, child);

    public static BtNode Condition(string name, Func<BtContext, bool> condition) => new ConditionNode(name, condition);

    public static BtNode Action(string name, Func<BtContext, BtStatus> tick, Action? onAbort = null) => new ActionNode(name, tick, onAbort);

    /// <summary>Running for <paramref name="ticks"/> ticks, then Success.</summary>
    public static BtNode Wait(string name, long ticks) => new WaitNode(name, ticks);
}

/// <summary>How many children must succeed (or fail) for a <see cref="Bt.Parallel"/> to finish.</summary>
public enum ParallelPolicy
{
    RequireOne,
    RequireAll,
}

internal abstract class CompositeNode : BtNode
{
    private readonly BtNode[] _children;

    protected CompositeNode(string name, BtNode[] children) : base(name)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0)
        {
            throw new ArgumentException($"[AI]: '{name}' needs at least one child", nameof(children));
        }

        _children = children;
    }

    public override IReadOnlyList<BtNode> Children => _children;

    protected BtNode[] Nodes => _children;
}

/// <summary>Succeeds when every child has; remembers the running child and resumes there.</summary>
internal sealed class SequenceNode(string name, BtNode[] children) : CompositeNode(name, children)
{
    private int _index;

    protected override BtStatus OnTick(in BtContext context)
    {
        while (_index < Nodes.Length)
        {
            switch (Nodes[_index].Tick(context))
            {
                case BtStatus.Running:
                    return BtStatus.Running;
                case BtStatus.Failure:
                    _index = 0;
                    return BtStatus.Failure;
                default:
                    _index++;
                    break;
            }
        }

        _index = 0;
        return BtStatus.Success;
    }

    protected override void OnAbort() => _index = 0;
}

/// <summary>Reactive priority selector: re-evaluates from the first child each tick and aborts a lower branch it preempts.</summary>
internal sealed class SelectorNode(string name, BtNode[] children) : CompositeNode(name, children)
{
    private int _running = -1;

    protected override BtStatus OnTick(in BtContext context)
    {
        for (var i = 0; i < Nodes.Length; i++)
        {
            var status = Nodes[i].Tick(context);
            if (status == BtStatus.Failure)
            {
                continue;
            }

            if (_running >= 0 && _running != i)
            {
                Nodes[_running].Abort();
            }

            _running = status == BtStatus.Running ? i : -1;
            return status;
        }

        if (_running >= 0)
        {
            Nodes[_running].Abort();
        }

        _running = -1;
        return BtStatus.Failure;
    }

    protected override void OnAbort() => _running = -1;
}

internal sealed class ParallelNode : CompositeNode
{
    private readonly ParallelPolicy _success;
    private readonly ParallelPolicy _failure;
    private readonly BtStatus[] _done;

    public ParallelNode(string name, ParallelPolicy success, ParallelPolicy failure, BtNode[] children) : base(name, children)
    {
        _success = success;
        _failure = failure;
        _done = new BtStatus[children.Length];
    }

    protected override BtStatus OnTick(in BtContext context)
    {
        var successes = 0;
        var failures = 0;
        for (var i = 0; i < Nodes.Length; i++)
        {
            if (_done[i] == BtStatus.Idle)
            {
                var status = Nodes[i].Tick(context);
                if (status != BtStatus.Running)
                {
                    _done[i] = status;
                }
            }

            successes += _done[i] == BtStatus.Success ? 1 : 0;
            failures += _done[i] == BtStatus.Failure ? 1 : 0;
        }

        // Failure is checked first: a branch that failed outranks a simultaneous success of its siblings.
        var failed = _failure == ParallelPolicy.RequireOne ? failures >= 1 : failures == Nodes.Length;
        var succeeded = _success == ParallelPolicy.RequireOne ? successes >= 1 : successes == Nodes.Length;
        if (!failed && !succeeded)
        {
            return BtStatus.Running;
        }

        Abort();
        return failed ? BtStatus.Failure : BtStatus.Success;
    }

    protected override void OnAbort() => Array.Clear(_done);
}

internal abstract class DecoratorNode : BtNode
{
    private readonly BtNode[] _child;

    protected DecoratorNode(string name, BtNode child) : base(name)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = [child];
    }

    public override IReadOnlyList<BtNode> Children => _child;

    protected BtNode Child => _child[0];
}

internal sealed class InverterNode(BtNode child) : DecoratorNode("Inverter", child)
{
    protected override BtStatus OnTick(in BtContext context) => Child.Tick(context) switch
    {
        BtStatus.Success => BtStatus.Failure,
        BtStatus.Failure => BtStatus.Success,
        var other => other,
    };
}

internal sealed class SucceederNode(BtNode child) : DecoratorNode("Succeeder", child)
{
    protected override BtStatus OnTick(in BtContext context) => Child.Tick(context) == BtStatus.Running ? BtStatus.Running : BtStatus.Success;
}

internal sealed class RepeatNode : DecoratorNode
{
    private readonly int _times;
    private int _done;

    public RepeatNode(int times, BtNode child) : base(times == 0 ? "RepeatForever" : "Repeat", child)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(times);
        _times = times;
    }

    protected override BtStatus OnTick(in BtContext context)
    {
        switch (Child.Tick(context))
        {
            case BtStatus.Running:
                return BtStatus.Running;
            case BtStatus.Failure:
                _done = 0;
                return BtStatus.Failure;
            default:
                _done++;
                if (_times != 0 && _done >= _times)
                {
                    _done = 0;
                    return BtStatus.Success;
                }

                return BtStatus.Running;
        }
    }

    protected override void OnAbort() => _done = 0;
}

internal sealed class CooldownNode : DecoratorNode
{
    private readonly long _ticks;
    private long _readyAt;

    public CooldownNode(long ticks, BtNode child) : base("Cooldown", child)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        _ticks = ticks;
    }

    protected override BtStatus OnTick(in BtContext context)
    {
        if (context.Tick < _readyAt)
        {
            return BtStatus.Failure;
        }

        var status = Child.Tick(context);
        if (status != BtStatus.Running)
        {
            _readyAt = context.Tick + _ticks;
        }

        return status;
    }
}

internal sealed class GuardNode(string name, Func<BtContext, bool> condition, BtNode child) : DecoratorNode(name, child)
{
    protected override BtStatus OnTick(in BtContext context)
    {
        if (condition(context))
        {
            return Child.Tick(context);
        }

        Child.Abort();
        return BtStatus.Failure;
    }
}

internal sealed class ConditionNode(string name, Func<BtContext, bool> condition) : BtNode(name)
{
    protected override BtStatus OnTick(in BtContext context) => condition(context) ? BtStatus.Success : BtStatus.Failure;
}

internal sealed class ActionNode(string name, Func<BtContext, BtStatus> tick, Action? onAbort) : BtNode(name)
{
    private bool _running;

    protected override BtStatus OnTick(in BtContext context)
    {
        var status = tick(context);
        _running = status == BtStatus.Running;
        return status;
    }

    protected override void OnAbort()
    {
        if (_running)
        {
            _running = false;
            onAbort?.Invoke();
        }
    }
}

internal sealed class WaitNode : BtNode
{
    private readonly long _ticks;
    private long _elapsed;

    public WaitNode(string name, long ticks) : base(name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        _ticks = ticks;
    }

    protected override BtStatus OnTick(in BtContext context)
    {
        if (_elapsed >= _ticks)
        {
            _elapsed = 0;
            return BtStatus.Success;
        }

        _elapsed++;
        return BtStatus.Running;
    }

    protected override void OnAbort() => _elapsed = 0;
}
