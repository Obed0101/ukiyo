using System.Numerics;
using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

public enum HoundState
{
    Patrol,
    Chase,
    Search,
}

/// <summary>
/// State-machine guard. Patrol walks the route; sight starts the Chase (run at the player, repathing); losing sight
/// for a moment turns it into a Search of the last known spot, which gives up after 2.5 seconds and returns to Patrol.
/// </summary>
public sealed class Hound : Npc
{
    public const int LoseSightTicks = 20;
    public const int SearchTicks = 150;
    public const int RepathTicks = 10;

    private readonly StateMachine<HoundState> _machine = new();
    private readonly GridCell[] _route;
    private int _leg;
    private bool _visible;
    private long _tick;
    private long _lastSeen;
    private long _lastRepath;
    private Vector2 _lastKnown;

    public Hound(GridMap map, GridPathfinder pathfinder, GridCell[] route) : base("hound", map, pathfinder, route[0])
    {
        _route = route;
        _machine.State(HoundState.Patrol)
            .OnEnter(() => Agent.MaxSpeed = WalkSpeed)
            .OnTick(Patrol)
            .When(() => _visible, HoundState.Chase);
        _machine.State(HoundState.Chase)
            .OnEnter(() => Agent.MaxSpeed = RunSpeed)
            .OnTick(Chase)
            .When(() => !_visible && _tick - _lastSeen > LoseSightTicks, HoundState.Search);
        _machine.State(HoundState.Search)
            .OnEnter(() =>
            {
                Agent.MaxSpeed = WalkSpeed;
                MoveTo(Map.CellAt(_lastKnown));
            })
            .When(() => _visible, HoundState.Chase)
            .When(() => _machine.TicksInState > SearchTicks, HoundState.Patrol);
        _machine.Start(HoundState.Patrol);
    }

    public HoundState State => _machine.Current;

    public override bool Alerted => _machine.Current != HoundState.Patrol;

    public override void Think(in Senses senses)
    {
        _tick = senses.Tick;
        _visible = senses.PlayerVisible;
        if (_visible)
        {
            _lastSeen = _tick;
            _lastKnown = senses.PlayerPosition;
        }

        _machine.Tick();
    }

    public override void WriteSnapshot(IDictionary<string, double> values) => _machine.WriteSnapshot(values, Name);

    private void Patrol()
    {
        var goal = _route[_leg];
        if (goal != Goal)
        {
            MoveTo(goal);
        }
        else if (ArrivedAtGoal)
        {
            _leg = (_leg + 1) % _route.Length;
            MoveTo(_route[_leg]);
        }
    }

    private void Chase()
    {
        var goal = Map.CellAt(_lastKnown);
        if (goal != Goal && _tick - _lastRepath >= RepathTicks)
        {
            _lastRepath = _tick;
            MoveTo(goal);
        }
    }
}
