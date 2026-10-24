using System.Numerics;
using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

/// <summary>What an NPC perceives this tick. Perception is computed once by the game, so brains stay pure decisions.</summary>
public readonly record struct Senses(long Tick, Vector2 PlayerPosition, bool PlayerVisible);

/// <summary>
/// A guard: a steering body on the grid plus a brain. The brain (<see cref="Think"/>) decides and picks goals through
/// <see cref="MoveTo"/>; the game then moves the body along the A* route. Subclasses differ only in the brain.
/// </summary>
public abstract class Npc
{
    public const float WalkSpeed = 2.2f;
    public const float RunSpeed = 3.3f;
    public const int Acceleration = 14;

    private readonly GridPathfinder _pathfinder;
    private readonly List<GridCell> _path = [];

    protected Npc(string name, GridMap map, GridPathfinder pathfinder, GridCell home)
    {
        Name = name;
        Map = map;
        _pathfinder = pathfinder;
        Agent = new SteeringAgent(map.CellCenter(home), WalkSpeed, Acceleration);
        Follower = new PathFollower(Agent);
    }

    public string Name { get; }

    protected GridMap Map { get; }

    public SteeringAgent Agent { get; }

    public PathFollower Follower { get; }

    public Blackboard Blackboard { get; } = new();

    /// <summary>The cell the last <see cref="MoveTo"/> aimed at; the sentinel (-1, -1) before the first one.</summary>
    public GridCell Goal { get; private set; } = new(-1, -1);

    public GridCell Cell => Map.CellAt(Agent.Position);

    public bool ArrivedAtGoal => Follower.Finished && Cell == Goal;

    /// <summary>True while the NPC knows where the player is (chasing or searching); tints it in the frame.</summary>
    public abstract bool Alerted { get; }

    public abstract void Think(in Senses senses);

    public abstract void WriteSnapshot(IDictionary<string, double> values);

    /// <summary>Plans an 8-direction route to the goal. An unreachable goal leaves the NPC standing and reports false.</summary>
    protected bool MoveTo(GridCell goal)
    {
        Goal = goal;
        var result = _pathfinder.FindPath(Cell, goal, _path, new PathOptions(Diagonals: true));
        if (!result.Found)
        {
            Follower.Stop();
            return false;
        }

        Follower.Follow(Map, _path);
        return true;
    }
}
