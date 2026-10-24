namespace Ukiyo.AI;

/// <summary>Why a path search ended. Anything but <see cref="Found"/> leaves the path list empty.</summary>
public enum PathStatus
{
    Found,
    /// <summary>Both cells are walkable but no route joins them.</summary>
    Unreachable,
    StartBlocked,
    GoalBlocked,
    /// <summary>Start or goal lies outside the map.</summary>
    OutOfBounds,
    /// <summary><see cref="PathOptions.MaxExpansions"/> was reached before the goal.</summary>
    SearchLimit,
}

/// <summary>Search settings. Four directions by default; eight never cut a corner (both orthogonal neighbors must be open). MaxExpansions 0 means no limit.</summary>
public readonly record struct PathOptions(bool Diagonals = false, int MaxExpansions = 0);

/// <summary>Outcome of <see cref="GridPathfinder.FindPath"/>: <see cref="Cost"/> is in <see cref="GridPathfinder.StraightCost"/> units per straight step.</summary>
public readonly record struct PathResult(PathStatus Status, int Cost, int Expanded, int Length)
{
    public bool Found => Status == PathStatus.Found;
}

/// <summary>
/// A* on a <see cref="GridMap"/> with integer costs (10 per straight step, 14 per diagonal, times the weight of the cell
/// entered), so the result is exact and identical on every target. Ties are broken by the lower heuristic and then the
/// lower cell index, neighbors are visited in a fixed order, and a better route replaces a known one only when strictly
/// cheaper: the same map and query always return the same path. Buffers are reused between calls (one pathfinder per
/// map, not per query); the map must keep its size. Paths include both ends.
/// </summary>
public sealed class GridPathfinder
{
    public const int StraightCost = 10;
    public const int DiagonalCost = 14;

    private static readonly (int Dx, int Dy)[] Straight = [(0, -1), (1, 0), (0, 1), (-1, 0)];
    private static readonly (int Dx, int Dy)[] Diagonal = [(1, -1), (1, 1), (-1, 1), (-1, -1)];

    private readonly GridMap _map;
    private readonly int[] _g;
    private readonly int[] _parent;
    private readonly int[] _seen;
    private readonly bool[] _closed;
    private Entry[] _heap = new Entry[64];
    private int _heapCount;
    private int _generation;

    private readonly record struct Entry(int F, int H, int Index, int G);

    public GridPathfinder(GridMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
        var cells = map.Width * map.Height;
        _g = new int[cells];
        _parent = new int[cells];
        _seen = new int[cells];
        _closed = new bool[cells];
    }

    public PathResult FindPath(GridCell start, GridCell goal, List<GridCell> path, PathOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.Clear();
        if (!_map.InBounds(start) || !_map.InBounds(goal))
        {
            return new PathResult(PathStatus.OutOfBounds, 0, 0, 0);
        }

        if (!_map.IsWalkable(start))
        {
            return new PathResult(PathStatus.StartBlocked, 0, 0, 0);
        }

        if (!_map.IsWalkable(goal))
        {
            return new PathResult(PathStatus.GoalBlocked, 0, 0, 0);
        }

        var startIndex = _map.IndexOf(start);
        var goalIndex = _map.IndexOf(goal);
        if (startIndex == goalIndex)
        {
            path.Add(start);
            return new PathResult(PathStatus.Found, 0, 0, 1);
        }

        NextGeneration();
        _heapCount = 0;
        Open(startIndex, -1, 0, Heuristic(start, goal, options.Diagonals));
        var expanded = 0;
        while (_heapCount > 0)
        {
            var entry = Pop();
            var index = entry.Index;
            if (_closed[index] || entry.G > _g[index])
            {
                continue;
            }

            if (index == goalIndex)
            {
                Rebuild(goalIndex, path);
                return new PathResult(PathStatus.Found, entry.G, expanded, path.Count);
            }

            if (options.MaxExpansions > 0 && expanded >= options.MaxExpansions)
            {
                return new PathResult(PathStatus.SearchLimit, 0, expanded, 0);
            }

            _closed[index] = true;
            expanded++;
            var cell = _map.CellOf(index);
            Expand(cell, index, entry.G, goal, options.Diagonals);
        }

        return new PathResult(PathStatus.Unreachable, 0, expanded, 0);
    }

    private void Expand(GridCell cell, int index, int g, GridCell goal, bool diagonals)
    {
        foreach (var (dx, dy) in Straight)
        {
            Relax(new GridCell(cell.X + dx, cell.Y + dy), index, g, StraightCost, goal, diagonals);
        }

        if (!diagonals)
        {
            return;
        }

        foreach (var (dx, dy) in Diagonal)
        {
            // No corner cutting: a diagonal step needs both orthogonal neighbors open.
            if (_map.IsWalkable(new GridCell(cell.X + dx, cell.Y)) && _map.IsWalkable(new GridCell(cell.X, cell.Y + dy)))
            {
                Relax(new GridCell(cell.X + dx, cell.Y + dy), index, g, DiagonalCost, goal, diagonals);
            }
        }
    }

    private void Relax(GridCell next, int from, int g, int step, GridCell goal, bool diagonals)
    {
        if (!_map.IsWalkable(next))
        {
            return;
        }

        var index = _map.IndexOf(next);
        if (_closed[index])
        {
            return;
        }

        var cost = g + step * _map.WeightAt(index);
        if (_seen[index] == _generation && cost >= _g[index])
        {
            return;
        }

        Open(index, from, cost, Heuristic(next, goal, diagonals));
    }

    private void Open(int index, int parent, int g, int h)
    {
        _seen[index] = _generation;
        _g[index] = g;
        _parent[index] = parent;
        Push(new Entry(g + h, h, index, g));
    }

    private void Rebuild(int goalIndex, List<GridCell> path)
    {
        for (var index = goalIndex; index >= 0; index = _parent[index])
        {
            path.Add(_map.CellOf(index));
        }

        path.Reverse();
    }

    /// <summary>Manhattan (4 directions) or octile (8) distance in cost units: never above the true cost, since weights are at least 1.</summary>
    private static int Heuristic(GridCell a, GridCell b, bool diagonals)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return diagonals
            ? StraightCost * Math.Max(dx, dy) + (DiagonalCost - StraightCost) * Math.Min(dx, dy)
            : StraightCost * (dx + dy);
    }

    private void NextGeneration()
    {
        _generation++;
        Array.Clear(_closed);
    }

    private static bool Less(in Entry a, in Entry b) =>
        a.F != b.F ? a.F < b.F : a.H != b.H ? a.H < b.H : a.Index < b.Index;

    private void Push(Entry entry)
    {
        if (_heapCount == _heap.Length)
        {
            Array.Resize(ref _heap, _heap.Length * 2);
        }

        var i = _heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (!Less(entry, _heap[parent]))
            {
                break;
            }

            _heap[i] = _heap[parent];
            i = parent;
        }

        _heap[i] = entry;
    }

    private Entry Pop()
    {
        var top = _heap[0];
        var last = _heap[--_heapCount];
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= _heapCount)
            {
                break;
            }

            if (child + 1 < _heapCount && Less(_heap[child + 1], _heap[child]))
            {
                child++;
            }

            if (!Less(_heap[child], last))
            {
                break;
            }

            _heap[i] = _heap[child];
            i = child;
        }

        if (_heapCount > 0)
        {
            _heap[i] = last;
        }

        return top;
    }
}

/// <summary>Post-processing for grid paths.</summary>
public static class PathSmoothing
{
    /// <summary>
    /// Drops waypoints that a straight walk can skip (<see cref="GridMap.HasLineOfSight"/>), keeping both ends. Weighted
    /// cells are not considered: only use it on maps without weights, or when a shortcut through heavy terrain is fine.
    /// </summary>
    public static void Simplify(GridMap map, List<GridCell> path)
    {
        if (path.Count < 3)
        {
            return;
        }

        var write = 1;
        var anchor = 0;
        for (var i = 2; i < path.Count; i++)
        {
            if (!map.HasLineOfSight(path[anchor], path[i]))
            {
                path[write++] = path[i - 1];
                anchor = i - 1;
            }
        }

        path[write++] = path[^1];
        path.RemoveRange(write, path.Count - write);
    }
}
