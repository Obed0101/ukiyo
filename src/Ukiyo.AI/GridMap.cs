using System.Numerics;

namespace Ukiyo.AI;

/// <summary>A cell of a <see cref="GridMap"/>: column (x) and row (y), origin at the map's minimum corner.</summary>
public readonly record struct GridCell(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>
/// Walkability grid for A*. Each cell is blocked (weight 0) or walkable with a movement weight of 1..255 (1 = normal
/// ground; a swamp cell can weigh 3). World placement is a cell size and the world position of the minimum corner, so a
/// 2D game uses (x, y) and a 3D game walking on a plane maps world X to x and world Z to y with
/// <see cref="CellCenterXZ"/> / <see cref="CellAtXZ"/>.
/// </summary>
public sealed class GridMap
{
    private readonly byte[] _weights;

    public GridMap(int width, int height, float cellSize = 1f, Vector2 origin = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!(cellSize > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "[AI]: cell size must be positive");
        }

        Width = width;
        Height = height;
        CellSize = cellSize;
        Origin = origin;
        _weights = new byte[width * height];
        Array.Fill(_weights, (byte)1);
    }

    public int Width { get; }

    public int Height { get; }

    public float CellSize { get; }

    /// <summary>World position of the minimum corner of cell (0, 0).</summary>
    public Vector2 Origin { get; }

    /// <summary>Bumped on every change, so cached results can be invalidated cheaply.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Builds a map from text rows; the first string is row y = 0. <c>#</c> blocks, <c>.</c> (or a space, S, G, P
    /// markers) is ground, digits 2-9 are cell weights.
    /// </summary>
    public static GridMap FromRows(IReadOnlyList<string> rows, float cellSize = 1f, Vector2 origin = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || rows[0].Length == 0)
        {
            throw new ArgumentException("[AI]: a map needs at least one row of at least one column", nameof(rows));
        }

        var map = new GridMap(rows[0].Length, rows.Count, cellSize, origin);
        for (var y = 0; y < rows.Count; y++)
        {
            if (rows[y].Length != map.Width)
            {
                throw new ArgumentException($"[AI]: map row {y} has {rows[y].Length} columns, expected {map.Width}", nameof(rows));
            }

            for (var x = 0; x < map.Width; x++)
            {
                var ch = rows[y][x];
                map._weights[y * map.Width + x] = ch switch
                {
                    '#' => 0,
                    '.' or ' ' or 'S' or 'G' or 'P' => 1,
                    >= '2' and <= '9' => (byte)(ch - '0'),
                    _ => throw new ArgumentException($"[AI]: map row {y} column {x} has unknown character '{ch}'", nameof(rows)),
                };
            }
        }

        return map;
    }

    public bool InBounds(GridCell cell) => (uint)cell.X < (uint)Width && (uint)cell.Y < (uint)Height;

    public int IndexOf(GridCell cell) => cell.Y * Width + cell.X;

    public GridCell CellOf(int index) => new(index % Width, index / Width);

    public int WeightAt(GridCell cell) => InBounds(cell) ? _weights[IndexOf(cell)] : 0;

    public bool IsWalkable(GridCell cell) => InBounds(cell) && _weights[IndexOf(cell)] != 0;

    internal byte WeightAt(int index) => _weights[index];

    public void SetBlocked(GridCell cell, bool blocked = true) => SetWeight(cell, blocked ? 0 : 1);

    /// <summary>Sets a cell's weight: 0 blocks it, 1..255 is the cost multiplier of entering it.</summary>
    public void SetWeight(GridCell cell, int weight)
    {
        if (!InBounds(cell))
        {
            throw new ArgumentOutOfRangeException(nameof(cell), $"[AI]: cell {cell} is outside the {Width}x{Height} map");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 255);
        _weights[IndexOf(cell)] = (byte)weight;
        Version++;
    }

    /// <summary>Marks a rectangle blocked (or open again).</summary>
    public void FillRect(int x, int y, int width, int height, bool blocked = true)
    {
        for (var j = y; j < y + height; j++)
        {
            for (var i = x; i < x + width; i++)
            {
                SetBlocked(new GridCell(i, j), blocked);
            }
        }
    }

    public Vector2 CellCenter(GridCell cell) => new(Origin.X + (cell.X + 0.5f) * CellSize, Origin.Y + (cell.Y + 0.5f) * CellSize);

    /// <summary>The cell containing a world point (may be outside the map: check <see cref="InBounds"/>).</summary>
    public GridCell CellAt(Vector2 world) =>
        new((int)MathF.Floor((world.X - Origin.X) / CellSize), (int)MathF.Floor((world.Y - Origin.Y) / CellSize));

    /// <summary>Center of a cell as a 3D point on a horizontal plane: map y becomes world Z.</summary>
    public Vector3 CellCenterXZ(GridCell cell, float height = 0f)
    {
        var center = CellCenter(cell);
        return new Vector3(center.X, height, center.Y);
    }

    public GridCell CellAtXZ(Vector3 world) => CellAt(new Vector2(world.X, world.Z));

    /// <summary>
    /// True when an integer Bresenham walk between two cells touches only walkable cells; passing exactly through a
    /// corner needs both cells beside it open (the same no-corner-cutting rule as 8-direction paths). Integer
    /// arithmetic, so every target agrees.
    /// </summary>
    public bool HasLineOfSight(GridCell from, GridCell to)
    {
        if (!IsWalkable(from) || !IsWalkable(to))
        {
            return false;
        }

        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        var sx = to.X > from.X ? 1 : -1;
        var sy = to.Y > from.Y ? 1 : -1;
        var x = from.X;
        var y = from.Y;
        var error = dx - dy;
        for (var steps = dx + dy; steps > 0; steps--)
        {
            var twice = error * 2;
            if (twice == 0)
            {
                // The segment passes exactly through a cell corner: both neighbors of that corner must be open.
                if (!IsWalkable(new GridCell(x + sx, y)) || !IsWalkable(new GridCell(x, y + sy)))
                {
                    return false;
                }

                x += sx;
                y += sy;
                error += dx - dy;
                steps--;
            }
            else if (twice > 0)
            {
                x += sx;
                error -= dy;
            }
            else
            {
                y += sy;
                error += dx;
            }

            if (!IsWalkable(new GridCell(x, y)))
            {
                return false;
            }
        }

        return true;
    }
}
