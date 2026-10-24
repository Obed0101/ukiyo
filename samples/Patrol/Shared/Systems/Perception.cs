using System.Numerics;
using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

/// <summary>Sight: a target is seen when it is close enough and nothing solid lies on the grid line between the two.</summary>
public static class Perception
{
    public const float SightRange = 6f;

    public static bool Sees(GridMap map, Vector2 from, Vector2 to) =>
        DetMath.LengthSquared(to - from) <= SightRange * SightRange && map.HasLineOfSight(map.CellAt(from), map.CellAt(to));
}
