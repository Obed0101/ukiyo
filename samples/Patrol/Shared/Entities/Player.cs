using System.Numerics;
using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

/// <summary>The player: arrows or WASD move a box that the walls stop, one axis at a time.</summary>
public sealed class Player
{
    public const float Speed = 4f;
    public const float HalfSize = 0.3f;

    private readonly GridMap _map;

    public Player(GridMap map, GridCell start)
    {
        _map = map;
        Start = map.CellCenter(start);
        Position = Start;
    }

    public Vector2 Start { get; }

    public Vector2 Position { get; private set; }

    public void Respawn() => Position = Start;

    public void Step(Vector2 direction, float deltaSeconds)
    {
        var step = direction * (Speed * deltaSeconds);
        var x = new Vector2(Position.X + step.X, Position.Y);
        if (Fits(x))
        {
            Position = x;
        }

        var y = new Vector2(Position.X, Position.Y + step.Y);
        if (Fits(y))
        {
            Position = y;
        }
    }

    private bool Fits(Vector2 center) =>
        _map.IsWalkable(_map.CellAt(new Vector2(center.X - HalfSize, center.Y - HalfSize)))
        && _map.IsWalkable(_map.CellAt(new Vector2(center.X + HalfSize, center.Y - HalfSize)))
        && _map.IsWalkable(_map.CellAt(new Vector2(center.X - HalfSize, center.Y + HalfSize)))
        && _map.IsWalkable(_map.CellAt(new Vector2(center.X + HalfSize, center.Y + HalfSize)));
}
