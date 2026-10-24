using Ukiyo.AI;

namespace Ukiyo.Samples.Patrol;

/// <summary>The level: a symmetric ring of corridors around an open hall. Rows are written top first, as drawn.</summary>
public static class PatrolMap
{
    public const int Width = 20;
    public const int Height = 12;

    private static readonly string[] RowsTopFirst =
    [
        "####################",
        "#........#.........#",
        "#.######.#.#######.#",
        "#.#......#.......#.#",
        "#.#.####.....###.#.#",
        "#...#..........#...#",
        "#...#..........#...#",
        "#.#.####.....###.#.#",
        "#.#......#.......#.#",
        "#.######.#.#######.#",
        "#........#.........#",
        "####################",
    ];

    /// <summary>Cell (0, 0) is the bottom-left corner; the world is the map at one unit per cell.</summary>
    public static GridMap Create() => GridMap.FromRows([.. RowsTopFirst.Reverse()]);

    public static readonly GridCell PlayerStart = new(9, 5);

    public static readonly GridCell[] SentryRoute = [new(1, 10), new(8, 10), new(8, 8), new(3, 8), new(3, 6), new(1, 6)];

    public static readonly GridCell[] HoundRoute = [new(18, 10), new(10, 10), new(10, 8), new(16, 8), new(16, 6), new(18, 6)];
}
