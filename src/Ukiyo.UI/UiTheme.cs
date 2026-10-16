using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.UI;

/// <summary>
/// Every UI color and metric in one place. Colors are linear RGBA (the engine's working space); <see cref="Srgb"/>
/// converts hex values picked in a design tool. The default follows the ukiyo mark: ink, paper and seal vermilion.
/// </summary>
public sealed record UiTheme
{
    public static UiTheme Default { get; } = new();

    public Vector4 Panel { get; init; } = Srgb(0x111214, 0.92f);

    public Vector4 Border { get; init; } = Srgb(0x3A3D44);

    public Vector4 Text { get; init; } = Srgb(0xF2F0EA);

    public Vector4 MutedText { get; init; } = Srgb(0x9A9DA5);

    public Vector4 Button { get; init; } = Srgb(0x1E2024);

    public Vector4 ButtonHover { get; init; } = Srgb(0x2A2D33);

    public Vector4 ButtonPressed { get; init; } = Srgb(0x0C0D0F);

    /// <summary>Focus ring and primary accents: seal vermilion #D8432E.</summary>
    public Vector4 Accent { get; init; } = Srgb(0xD8432E);

    public Vector4 BarTrack { get; init; } = Srgb(0x26282D);

    /// <summary>Integer multiple of the 5×8 font in canvas pixels.</summary>
    public float TextScale { get; init; } = 1f;

    public float BorderWidth { get; init; } = 1f;

    public float FocusWidth { get; init; } = 1f;

    /// <summary>Screen-sprite layer of the UI; game HUD art below it, cursors above it.</summary>
    public int Layer { get; init; } = 1000;

    /// <summary>Linear RGBA of a 0xRRGGBB color: the sRGB decode table, so UI tints in packets are the same bits everywhere.</summary>
    public static Vector4 Srgb(int rgb, float alpha = 1f) => new(
        ColorSpace.SrgbToLinear((byte)(rgb >> 16)),
        ColorSpace.SrgbToLinear((byte)(rgb >> 8)),
        ColorSpace.SrgbToLinear((byte)rgb),
        alpha);
}
