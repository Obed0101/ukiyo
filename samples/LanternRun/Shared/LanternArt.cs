namespace Ukiyo.Samples.LanternRun;

/// <summary>
/// All of LanternRun's art, as text. One 8×8 cell per frame in a single strip:
/// 0–1 lantern walking, 2–3 ember spinning, 4 stone tile, 5 goal shrine.
/// </summary>
public static class LanternArt
{
    public const int Cell = 8;
    public const int PlayerFrame0 = 0;
    public const int EmberFrame0 = 2;
    public const int TileFrame = 4;
    public const int ShrineFrame = 5;

    public static readonly Dictionary<char, uint> Palette = new()
    {
        ['k'] = PixelArt.Rgb(0x111214), // ink
        ['w'] = PixelArt.Rgb(0xF2F0EA), // paper
        ['r'] = PixelArt.Rgb(0xD8432E), // seal vermilion
        ['o'] = PixelArt.Rgb(0xF08A3C), // lantern glow
        ['y'] = PixelArt.Rgb(0xE8B04A), // ember gold
        ['g'] = PixelArt.Rgb(0x3A3D44), // stone
        ['d'] = PixelArt.Rgb(0x26282D), // dark stone
    };

    public static readonly string[][] Frames =
    [
        [
            "..kkkk..",
            ".krrrrk.",
            "kroowork",
            "krwkkwrk",
            "krrrrrrk",
            "krooookk",
            ".krrrrk.",
            "..k..k..",
        ],
        [
            "..kkkk..",
            ".krrrrk.",
            "kroowork",
            "krwkkwrk",
            "krrrrrrk",
            "krooookk",
            ".krrrrk.",
            ".k....k.",
        ],
        [
            "..kkkk..",
            ".kyyyyk.",
            "kyywyyyk",
            "kyywyyyk",
            "kyyyyyyk",
            "kyyyyyyk",
            ".kyyyyk.",
            "..kkkk..",
        ],
        [
            "...kk...",
            "..kyyk..",
            "..kwyk..",
            "..kwyk..",
            "..kyyk..",
            "..kyyk..",
            "..kyyk..",
            "...kk...",
        ],
        [
            "gggggggg",
            "gdddgddd",
            "gdddgddd",
            "gggggggg",
            "ddgdddgd",
            "ddgdddgd",
            "gggggggg",
            "dddgdddg",
        ],
        [
            "rrrrrrrr",
            ".rrrrrr.",
            "..k..k..",
            "rrrrrrrr",
            "..k..k..",
            "..k..k..",
            "..k..k..",
            ".kk..kk.",
        ],
    ];
}
