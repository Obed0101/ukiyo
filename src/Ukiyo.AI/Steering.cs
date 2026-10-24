using System.Numerics;

namespace Ukiyo.AI;

/// <summary>
/// Steering behaviors as pure functions from positions to a desired velocity (units per second). A
/// <see cref="SteeringAgent"/> follows it with a bounded acceleration on the fixed timestep. Only <see cref="DetMath"/>
/// and exact scalar operations are used, so every target moves an NPC identically.
/// </summary>
public static class Steering
{
    /// <summary>Full speed straight at the target.</summary>
    public static Vector2 Seek(Vector2 position, Vector2 target, float maxSpeed)
    {
        var toTarget = target - position;
        var distance = DetMath.Length(toTarget);
        return distance <= 0f ? Vector2.Zero : toTarget * (maxSpeed / distance);
    }

    /// <summary>Full speed away from the threat; zero when on top of it.</summary>
    public static Vector2 Flee(Vector2 position, Vector2 threat, float maxSpeed) => -Seek(position, threat, maxSpeed);

    /// <summary>Like <see cref="Seek"/>, but slows linearly inside <paramref name="slowRadius"/> and stops on the target.</summary>
    public static Vector2 Arrive(Vector2 position, Vector2 target, float maxSpeed, float slowRadius)
    {
        var toTarget = target - position;
        var distance = DetMath.Length(toTarget);
        if (distance <= 0f)
        {
            return Vector2.Zero;
        }

        var speed = slowRadius > 0f && distance < slowRadius ? maxSpeed * (distance / slowRadius) : maxSpeed;
        return toTarget * (speed / distance);
    }
}

/// <summary>A point mass moved by desired velocities: acceleration is capped, so turns are smooth and tick-count exact.</summary>
public sealed class SteeringAgent
{
    public SteeringAgent(Vector2 position, float maxSpeed, float maxAcceleration)
    {
        if (!(maxSpeed > 0f) || !(maxAcceleration > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpeed), "[AI]: max speed and acceleration must be positive");
        }

        Position = position;
        _maxSpeed = maxSpeed;
        MaxAcceleration = maxAcceleration;
    }

    private float _maxSpeed;

    public Vector2 Position { get; set; }

    public Vector2 Velocity { get; set; }

    /// <summary>Speed cap; behaviors change it (walk, run) between ticks.</summary>
    public float MaxSpeed
    {
        get => _maxSpeed;
        set => _maxSpeed = value > 0f ? value : throw new ArgumentOutOfRangeException(nameof(value), "[AI]: max speed must be positive");
    }

    public float MaxAcceleration { get; }

    /// <summary>Moves toward <paramref name="desiredVelocity"/> by at most MaxAcceleration * dt, then advances the position.</summary>
    public void Step(Vector2 desiredVelocity, float deltaSeconds)
    {
        var change = desiredVelocity - Velocity;
        var limit = MaxAcceleration * deltaSeconds;
        var length = DetMath.Length(change);
        if (length > limit && length > 0f)
        {
            change *= limit / length;
        }

        var velocity = Velocity + change;
        var speed = DetMath.Length(velocity);
        if (speed > MaxSpeed)
        {
            velocity *= MaxSpeed / speed;
        }

        Velocity = velocity;
        Position += velocity * deltaSeconds;
    }
}

/// <summary>
/// Walks a <see cref="SteeringAgent"/> along grid-path waypoints: seeks the current waypoint, advances when within
/// <see cref="WaypointRadius"/>, and arrives at the last one. Set a new route with <see cref="Follow"/>.
/// </summary>
public sealed class PathFollower
{
    private readonly List<Vector2> _waypoints = [];
    private int _next;

    public PathFollower(SteeringAgent agent, float waypointRadius = 0.15f, float arriveRadius = 0.8f)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Agent = agent;
        WaypointRadius = waypointRadius;
        ArriveRadius = arriveRadius;
    }

    public SteeringAgent Agent { get; }

    public float WaypointRadius { get; }

    public float ArriveRadius { get; }

    /// <summary>True when there is no route or the last waypoint was reached.</summary>
    public bool Finished => _next >= _waypoints.Count;

    /// <summary>Index of the waypoint being approached, for snapshots.</summary>
    public int NextWaypoint => _next;

    public int WaypointCount => _waypoints.Count;

    /// <summary>World-space route being followed (for debug drawing).</summary>
    public IReadOnlyList<Vector2> Route => _waypoints;

    /// <summary>Replaces the route with the centers of <paramref name="path"/>'s cells; the first cell (where the agent stands) is skipped.</summary>
    public void Follow(GridMap map, IReadOnlyList<GridCell> path)
    {
        _waypoints.Clear();
        for (var i = path.Count > 1 ? 1 : 0; i < path.Count; i++)
        {
            _waypoints.Add(map.CellCenter(path[i]));
        }

        _next = 0;
    }

    public void Stop()
    {
        _waypoints.Clear();
        _next = 0;
    }

    public void Step(float deltaSeconds)
    {
        if (Finished)
        {
            Agent.Step(Vector2.Zero, deltaSeconds);
            return;
        }

        var last = _next == _waypoints.Count - 1;
        var target = _waypoints[_next];
        if (!last && DetMath.Length(target - Agent.Position) <= WaypointRadius)
        {
            _next++;
            target = _waypoints[_next];
            last = _next == _waypoints.Count - 1;
        }

        var desired = last
            ? Steering.Arrive(Agent.Position, target, Agent.MaxSpeed, ArriveRadius)
            : Steering.Seek(Agent.Position, target, Agent.MaxSpeed);
        Agent.Step(desired, deltaSeconds);
        if (last && DetMath.Length(target - Agent.Position) <= WaypointRadius * 0.25f && DetMath.Length(Agent.Velocity) < 0.05f * Agent.MaxSpeed)
        {
            Agent.Position = target;
            Agent.Velocity = Vector2.Zero;
            _next = _waypoints.Count;
        }
    }
}
