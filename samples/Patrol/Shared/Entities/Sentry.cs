using System.Numerics;
using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

/// <summary>
/// Behavior-tree guard. Priority 1: while alerted, pursue the last known player position and raise the alarm at most
/// every 3 seconds. Priority 2: walk the route leg by leg, pausing at each corner. The alert timer lives on the
/// blackboard, refreshed by sight and counted down by the brain.
/// </summary>
public sealed class Sentry : Npc
{
    public const int AlertTicks = 120;
    public const int AlarmCooldownTicks = 180;
    public const int RepathTicks = 10;

    private readonly BehaviorTree _tree;
    private readonly BlackboardKey _alert;
    private readonly BlackboardKey _target;
    private readonly BlackboardKey _leg;
    private readonly BlackboardKey _alarms;
    private readonly GridCell[] _route;
    private long _lastRepath = -RepathTicks;

    public Sentry(GridMap map, GridPathfinder pathfinder, GridCell[] route) : base("sentry", map, pathfinder, route[0])
    {
        _route = route;
        _alert = Blackboard.Declare("alert");
        _target = Blackboard.Declare("target");
        _leg = Blackboard.Declare("leg");
        _alarms = Blackboard.Declare("alarms");
        _tree = new BehaviorTree(Bt.Selector(
            "Brain",
            Bt.Guard(
                "Alerted",
                context => context.Blackboard.GetNumber(_alert) > 0,
                Bt.Parallel(
                    ParallelPolicy.RequireAll,
                    ParallelPolicy.RequireOne,
                    Bt.Action("Pursue", Pursue),
                    Bt.RepeatForever(Bt.Succeeder(Bt.Cooldown(AlarmCooldownTicks, Bt.Action("Alarm", RaiseAlarm)))))),
            Bt.Sequence(
                "Patrol",
                Bt.Action("GoToLeg", GoToLeg),
                Bt.Action("NextLeg", NextLeg),
                Bt.Wait("Pause", 20))));
    }

    public override bool Alerted => Blackboard.GetNumber(_alert) > 0;

    public BehaviorTree Tree => _tree;

    public override void Think(in Senses senses)
    {
        if (senses.PlayerVisible)
        {
            Blackboard.SetNumber(_alert, AlertTicks);
            Blackboard.SetPoint(_target, senses.PlayerPosition);
        }
        else if (Blackboard.GetNumber(_alert) > 0)
        {
            Blackboard.SetNumber(_alert, Blackboard.GetNumber(_alert) - 1);
        }

        _tree.Tick(new BtContext(Blackboard, senses.Tick));
    }

    public override void WriteSnapshot(IDictionary<string, double> values)
    {
        _tree.WriteSnapshot(values, Name);
        Blackboard.WriteSnapshot(values, Name);
    }

    private BtStatus Pursue(BtContext context)
    {
        Agent.MaxSpeed = RunSpeed;
        var goal = Map.CellAt(context.Blackboard.GetPoint(_target));
        if (goal != Goal && context.Tick - _lastRepath >= RepathTicks)
        {
            _lastRepath = context.Tick;
            MoveTo(goal);
        }

        return BtStatus.Running;
    }

    private BtStatus RaiseAlarm(BtContext context)
    {
        Blackboard.SetNumber(_alarms, Blackboard.GetNumber(_alarms) + 1);
        return BtStatus.Success;
    }

    private BtStatus GoToLeg(BtContext context)
    {
        Agent.MaxSpeed = WalkSpeed;
        var goal = _route[(int)Blackboard.GetNumber(_leg)];
        if (goal != Goal)
        {
            MoveTo(goal);
        }

        return ArrivedAtGoal ? BtStatus.Success : BtStatus.Running;
    }

    private BtStatus NextLeg(BtContext context)
    {
        Blackboard.SetNumber(_leg, (Blackboard.GetNumber(_leg) + 1) % _route.Length);
        return BtStatus.Success;
    }
}
