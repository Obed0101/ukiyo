using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.TexturedQuad;

/// <summary>
/// Textured quads from imported PNGs. The art in <c>art/</c> (a procedural 4-frame strip from the MCP sprite tool and
/// an 8×8 translucent tile written by a script) is imported at dev time into <c>gen/QuadArt.g.cs</c>; this game
/// compiles those pixels in and never decodes an image or opens a file. Every texel covers exactly 8×8 drawing-buffer
/// pixels at 960×640, so captures from every renderer can be compared pixel for pixel. Translucent glass over opaque
/// art checks straight-alpha blending in linear space; tiles drawn edge to edge check that atlas neighbors never bleed.
/// </summary>
public sealed class TexturedQuadGame : IGame
{
    /// <summary>World units visible vertically: 640 px tall / 10 = 64 px per unit, so a 1/8-unit texel is 8 px.</summary>
    public const float ViewHeight = 10f;
    public const float TexelUnits = 1f / 8f;
    public static readonly Vector2 WispHome = new(0f, 0.5f);
    public static readonly Vector2 GlassPosition = new(0.5f, 0f);

    private SpriteSheet _sheet = null!;
    private SpriteAnimation _float = null!;
    private SpriteFrame _glass;
    private long _tick;

    public string Name => "TexturedQuad";

    public SpriteSheet Sheet => _sheet;

    public void Initialize(GameContext context)
    {
        _sheet = QuadArt.Load(context);
        _float = _sheet.Animation("wisp_float");
        _glass = _sheet.Frame("glass");
    }

    public void Update(in TickInfo tick) => _tick = tick.Tick + 1;

    public int WispFrameIndex => (int)(MathF.Floor(_tick * _float.FramesPerSecond / Simulation.TickRate) % _float.Frames.Count);

    /// <summary>The animated wisp bobs one texel up on odd frames: whole pixels only, so captures stay comparable.</summary>
    public Vector2 WispPosition => WispHome + new Vector2(0f, WispFrameIndex % 2 * TexelUnits);

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = new Vector4(0.012f, 0.013f, 0.016f, 1f);
        frame.Camera2D = new Camera2D(Vector2.Zero, ViewHeight);
        for (var i = -4; i <= 4; i++)
        {
            frame.DrawSprite(_glass, new Vector2(i, -2.5f), Vector2.One, layer: 0);
        }

        var wispSize = _float.Frames[0].PixelSize * TexelUnits;
        frame.DrawSprite(_float.At(_tick), WispPosition, wispSize, layer: 1);
        frame.DrawSprite(_sheet.Frame("wisp_float_2"), new Vector2(-3f, 0.5f), wispSize, layer: 1, flipX: true);
        frame.DrawSprite(_sheet.Frame("wisp_float_0"), new Vector2(3f, 0.5f), wispSize, layer: 1, tint: new Vector4(0.55f, 0.8f, 1f, 1f));
        frame.DrawSprite(_glass, GlassPosition, Vector2.One, layer: 2);
        frame.DrawScreenSprite(_glass, new Vector2(16f, 16f), new Vector2(64f, 64f), layer: 10);
    }

    public GameSnapshot Snapshot(long tick) =>
        new(tick, new Dictionary<string, EntityPose> { ["wisp"] = EntityPose.Planar(WispPosition) })
        {
            Values = new Dictionary<string, double> { ["wispFrame"] = WispFrameIndex, ["atlasWidth"] = QuadArt.Width, ["atlasHeight"] = QuadArt.Height },
        };
}
