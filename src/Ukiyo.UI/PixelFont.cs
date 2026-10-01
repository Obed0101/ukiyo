using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.UI;

/// <summary>
/// Built-in 5×8 pixel font for printable ASCII, drawn by hand on a grid (row 6 is the baseline, row 7 holds descenders).
/// The atlas is generated in code, so every game has text with no font file, and the same cell also carries a solid
/// white block that UI panels stretch. Unknown characters render as '?'.
/// </summary>
public sealed class PixelFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 8;
    public const int Advance = 6;
    public const int LineHeight = 10;
    private const int CellWidth = GlyphWidth + 1;
    private const int CellHeight = GlyphHeight + 1;
    private const int Columns = 16;
    private const int Rows = 6;
    private const char SolidCell = (char)127;

    private readonly SpriteFrame[] _frames = new SpriteFrame[96];

    private PixelFont(ResourceHandle texture)
    {
        Texture = texture;
        for (var i = 0; i < _frames.Length; i++)
        {
            var x = i % Columns * CellWidth;
            var y = i / Columns * CellHeight;
            _frames[i] = SpriteFrame.FromPixels(texture, AtlasWidth, AtlasHeight, x, y, GlyphWidth, GlyphHeight);
        }

        // Sample the middle of the solid cell only, so stretched panels never pick up a neighbour's edge.
        var solid = _frames[SolidCell - 32];
        var cx = (solid.Uv.X + solid.Uv.Z) * 0.5f;
        var cy = (solid.Uv.Y + solid.Uv.W) * 0.5f;
        White = new SpriteFrame(texture, new Vector4(cx, cy, cx, cy), Vector2.One);
    }

    public static int AtlasWidth => Columns * CellWidth;

    public static int AtlasHeight => Rows * CellHeight;

    public ResourceHandle Texture { get; }

    /// <summary>A single white texel for solid rectangles (panels, bars, borders). Tint it.</summary>
    public SpriteFrame White { get; }

    public static PixelFont Create(GameContext context) => new(context.CreateTexture(BuildAtlas()));

    public SpriteFrame Glyph(char c) => c is >= ' ' and <= '~' ? _frames[c - 32] : _frames['?' - 32];

    /// <summary>Width and height in font pixels (multiply by the scale you draw with). Newlines start new lines.</summary>
    public static Vector2 Measure(string text)
    {
        var width = 0;
        var line = 0;
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
                line = 0;
                continue;
            }

            line++;
            width = Math.Max(width, line);
        }

        return new Vector2(width == 0 ? 0 : width * Advance - 1, lines * LineHeight - (LineHeight - GlyphHeight));
    }

    public static TextureData BuildAtlas()
    {
        var rgba = new byte[AtlasWidth * AtlasHeight * 4];
        for (var i = 0; i < 96; i++)
        {
            var originX = i % Columns * CellWidth;
            var originY = i / Columns * CellHeight;
            var c = (char)(i + 32);
            var rows = c == SolidCell ? SolidRows : Glyphs.TryGetValue(c, out var pattern) ? pattern.Split('/') : Glyphs['?'].Split('/');
            for (var y = 0; y < Math.Min(rows.Length, GlyphHeight); y++)
            {
                for (var x = 0; x < Math.Min(rows[y].Length, GlyphWidth); x++)
                {
                    if (rows[y][x] != '#')
                    {
                        continue;
                    }

                    var p = ((originY + y) * AtlasWidth + originX + x) * 4;
                    rgba[p] = rgba[p + 1] = rgba[p + 2] = rgba[p + 3] = 255;
                }
            }
        }

        return new TextureData(AtlasWidth, AtlasHeight, rgba, TextureFilter.Nearest);
    }

    private static readonly string[] SolidRows = ["#####", "#####", "#####", "#####", "#####", "#####", "#####", "#####"];

    // Rows top to bottom, '#' = ink. Missing trailing rows are empty.
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        [' '] = "",
        ['!'] = "..#../..#../..#../..#../..#../...../..#..",
        ['"'] = ".#.#./.#.#.",
        ['#'] = ".#.#./.#.#./#####/.#.#./#####/.#.#./.#.#.",
        ['$'] = "..#../.####/#.#../.###./..#.#/####./..#..",
        ['%'] = "##.../##..#/...#./..#../.#.../#..##/...##",
        ['&'] = ".##../#..#./#.#../.#.../#.#.#/#..#./.##.#",
        ['\''] = "..#../..#..",
        ['('] = "...#./..#../.#.../.#.../.#.../..#../...#.",
        [')'] = ".#.../..#../...#./...#./...#./..#../.#...",
        ['*'] = "...../..#../#.#.#/.###./#.#.#/..#..",
        ['+'] = "...../..#../..#../#####/..#../..#..",
        [','] = "...../...../...../...../...../..#../..#../.#...",
        ['-'] = "...../...../...../#####",
        ['.'] = "...../...../...../...../...../...../..#..",
        ['/'] = "...../....#/...#./..#../.#.../#....",
        ['0'] = ".###./#...#/#..##/#.#.#/##..#/#...#/.###.",
        ['1'] = "..#../.##../..#../..#../..#../..#../.###.",
        ['2'] = ".###./#...#/....#/...#./..#../.#.../#####",
        ['3'] = "#####/...#./..#../...#./....#/#...#/.###.",
        ['4'] = "...#./..##./.#.#./#..#./#####/...#./...#.",
        ['5'] = "#####/#..../####./....#/....#/#...#/.###.",
        ['6'] = "..##./.#.../#..../####./#...#/#...#/.###.",
        ['7'] = "#####/....#/...#./..#../.#.../.#.../.#...",
        ['8'] = ".###./#...#/#...#/.###./#...#/#...#/.###.",
        ['9'] = ".###./#...#/#...#/.####/....#/...#./.##..",
        [':'] = "...../..#../..#../...../..#../..#..",
        [';'] = "...../..#../..#../...../..#../..#../.#...",
        ['<'] = "...#./..#../.#.../#..../.#.../..#../...#.",
        ['='] = "...../...../#####/...../#####",
        ['>'] = ".#.../..#../...#./....#/...#./..#../.#...",
        ['?'] = ".###./#...#/....#/...#./..#../...../..#..",
        ['@'] = ".###./#...#/#.###/#.#.#/#.###/#..../.###.",
        ['A'] = ".###./#...#/#...#/#####/#...#/#...#/#...#",
        ['B'] = "####./#...#/#...#/####./#...#/#...#/####.",
        ['C'] = ".###./#...#/#..../#..../#..../#...#/.###.",
        ['D'] = "####./#...#/#...#/#...#/#...#/#...#/####.",
        ['E'] = "#####/#..../#..../####./#..../#..../#####",
        ['F'] = "#####/#..../#..../####./#..../#..../#....",
        ['G'] = ".###./#...#/#..../#.###/#...#/#...#/.####",
        ['H'] = "#...#/#...#/#...#/#####/#...#/#...#/#...#",
        ['I'] = ".###./..#../..#../..#../..#../..#../.###.",
        ['J'] = "..###/...#./...#./...#./...#./#..#./.##..",
        ['K'] = "#...#/#..#./#.#../##.../#.#../#..#./#...#",
        ['L'] = "#..../#..../#..../#..../#..../#..../#####",
        ['M'] = "#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#",
        ['N'] = "#...#/#...#/##..#/#.#.#/#..##/#...#/#...#",
        ['O'] = ".###./#...#/#...#/#...#/#...#/#...#/.###.",
        ['P'] = "####./#...#/#...#/####./#..../#..../#....",
        ['Q'] = ".###./#...#/#...#/#...#/#.#.#/#..#./.##.#",
        ['R'] = "####./#...#/#...#/####./#.#../#..#./#...#",
        ['S'] = ".####/#..../#..../.###./....#/....#/####.",
        ['T'] = "#####/..#../..#../..#../..#../..#../..#..",
        ['U'] = "#...#/#...#/#...#/#...#/#...#/#...#/.###.",
        ['V'] = "#...#/#...#/#...#/#...#/#...#/.#.#./..#..",
        ['W'] = "#...#/#...#/#...#/#.#.#/#.#.#/#.#.#/.#.#.",
        ['X'] = "#...#/#...#/.#.#./..#../.#.#./#...#/#...#",
        ['Y'] = "#...#/#...#/.#.#./..#../..#../..#../..#..",
        ['Z'] = "#####/....#/...#./..#../.#.../#..../#####",
        ['['] = ".###./.#.../.#.../.#.../.#.../.#.../.###.",
        ['\\'] = "...../#..../.#.../..#../...#./....#",
        [']'] = ".###./...#./...#./...#./...#./...#./.###.",
        ['^'] = "..#../.#.#./#...#",
        ['_'] = "...../...../...../...../...../...../...../#####",
        ['`'] = ".#.../..#..",
        ['a'] = "...../...../.###./....#/.####/#...#/.####",
        ['b'] = "#..../#..../#.##./##..#/#...#/#...#/####.",
        ['c'] = "...../...../.###./#..../#..../#...#/.###.",
        ['d'] = "....#/....#/.##.#/#..##/#...#/#...#/.####",
        ['e'] = "...../...../.###./#...#/#####/#..../.###.",
        ['f'] = "..##./.#..#/.#.../###../.#.../.#.../.#...",
        ['g'] = "...../...../.####/#...#/#...#/.####/....#/.###.",
        ['h'] = "#..../#..../#.##./##..#/#...#/#...#/#...#",
        ['i'] = "..#../...../.##../..#../..#../..#../.###.",
        ['j'] = "...#./...../..##./...#./...#./...#./#..#./.##..",
        ['k'] = "#..../#..../#..#./#.#../##.../#.#../#..#.",
        ['l'] = ".##../..#../..#../..#../..#../..#../.###.",
        ['m'] = "...../...../##.#./#.#.#/#.#.#/#...#/#...#",
        ['n'] = "...../...../#.##./##..#/#...#/#...#/#...#",
        ['o'] = "...../...../.###./#...#/#...#/#...#/.###.",
        ['p'] = "...../...../####./#...#/#...#/####./#..../#....",
        ['q'] = "...../...../.####/#...#/#...#/.####/....#/....#",
        ['r'] = "...../...../#.##./##..#/#..../#..../#....",
        ['s'] = "...../...../.###./#..../.###./....#/####.",
        ['t'] = ".#.../.#.../###../.#.../.#.../.#..#/..##.",
        ['u'] = "...../...../#...#/#...#/#...#/#..##/.##.#",
        ['v'] = "...../...../#...#/#...#/#...#/.#.#./..#..",
        ['w'] = "...../...../#...#/#...#/#.#.#/#.#.#/.#.#.",
        ['x'] = "...../...../#...#/.#.#./..#../.#.#./#...#",
        ['y'] = "...../...../#...#/#...#/#...#/.####/....#/.###.",
        ['z'] = "...../...../#####/...#./..#../.#.../#####",
        ['{'] = "...#./..#../..#../.#.../..#../..#../...#.",
        ['|'] = "..#../..#../..#../..#../..#../..#../..#..",
        ['}'] = ".#.../..#../..#../...#./..#../..#../.#...",
        ['~'] = "...../...../.#.../#.#.#/...#.",
    };
}
