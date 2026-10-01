using System.Numerics;
using System.Text;
using Ukiyo.Rendering;

namespace Ukiyo.UI;

/// <summary>Rectangle in UI canvas pixels (origin top-left, +Y down).</summary>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public Vector2 Position => new(X, Y);

    public Vector2 Size => new(Width, Height);

    public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);

    public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < X + Width && point.Y < Y + Height;

    public UiRect Inset(float amount) => new(X + amount, Y + amount, Width - amount * 2, Height - amount * 2);

    /// <summary>A rectangle of <paramref name="size"/> placed against an edge or corner of the canvas.</summary>
    public static UiRect Anchored(Vector2 canvas, UiAnchor anchor, Vector2 size, float margin = 0f)
    {
        var x = anchor switch
        {
            UiAnchor.TopLeft or UiAnchor.Left or UiAnchor.BottomLeft => margin,
            UiAnchor.TopRight or UiAnchor.Right or UiAnchor.BottomRight => canvas.X - size.X - margin,
            _ => (canvas.X - size.X) * 0.5f,
        };
        var y = anchor switch
        {
            UiAnchor.TopLeft or UiAnchor.Top or UiAnchor.TopRight => margin,
            UiAnchor.BottomLeft or UiAnchor.Bottom or UiAnchor.BottomRight => canvas.Y - size.Y - margin,
            _ => (canvas.Y - size.Y) * 0.5f,
        };
        return new UiRect(x, y, size.X, size.Y);
    }
}

public enum UiAnchor : byte
{
    Center,
    TopLeft,
    Top,
    TopRight,
    Left,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

public enum TextAlign : byte
{
    Left,
    Center,
    Right,
}

/// <summary>What an agent or test can see about an interactive element in the last UI pass.</summary>
public readonly record struct UiElementInfo(string Id, string Kind, UiRect Rect, bool Hovered, bool Focused, string Label);

/// <summary>
/// Immediate-mode game UI on a virtual pixel canvas (for example 320×180), scaled to the window and letterboxed.
/// Declare widgets in <c>Update</c> between <see cref="Begin"/> and <see cref="End"/> — they read that tick's input, so
/// clicks are deterministic and replayable — then call <see cref="Draw"/> from <c>Extract</c>.
/// Keyboard: Up/Down/Tab move focus, Enter/Space activate. Mouse: hover focuses, press and release inside clicks.
/// </summary>
public sealed class GameUi
{
    private readonly List<DrawItem> _items = [];
    private readonly List<UiElementInfo> _elements = [];
    private readonly List<string> _focusOrder = [];
    private InputState _input = InputState.Empty;
    private Vector2 _pointer;
    private bool _pointerInside;
    private bool _pointerMoved;
    private Vector2 _lastPointer;
    private string? _focused;
    private string? _pressed;
    private int _focusMove;
    private bool _activate;
    private bool _inPass;

    public GameUi(GameContext context, int canvasWidth, int canvasHeight, UiTheme? theme = null, bool pixelPerfect = true)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), $"[UI]: canvas {canvasWidth}x{canvasHeight} must be positive");
        }

        Font = PixelFont.Create(context);
        Canvas = new Vector2(canvasWidth, canvasHeight);
        Theme = theme ?? UiTheme.Default;
        PixelPerfect = pixelPerfect;
    }

    public PixelFont Font { get; }

    public Vector2 Canvas { get; }

    public UiTheme Theme { get; set; }

    /// <summary>Scale only by whole numbers so pixel art stays crisp (extra space becomes letterbox).</summary>
    public bool PixelPerfect { get; }

    /// <summary>Pointer in canvas pixels for the current pass.</summary>
    public Vector2 Pointer => _pointer;

    /// <summary>Id of the focused interactive element, if any.</summary>
    public string? Focused => _focused;

    /// <summary>Interactive elements declared in the last pass, in order.</summary>
    public IReadOnlyList<UiElementInfo> Elements => _elements;

    public void Begin(in TickInfo tick)
    {
        if (_inPass)
        {
            throw new InvalidOperationException("[UI]: Begin called twice without End");
        }

        _inPass = true;
        _items.Clear();
        _elements.Clear();
        _focusOrder.Clear();
        _input = tick.Input;
        var (scale, offset) = Fit(_input.Viewport);
        _pointer = scale > 0 ? (_input.Pointer - offset) / scale : Vector2.Zero;
        _pointerInside = _pointer.X >= 0 && _pointer.Y >= 0 && _pointer.X < Canvas.X && _pointer.Y < Canvas.Y;
        _pointerMoved = _pointer != _lastPointer;
        _lastPointer = _pointer;
        _focusMove = (_input.WasPressed(Key.Down) || _input.WasPressed(Key.Tab) ? 1 : 0) - (_input.WasPressed(Key.Up) ? 1 : 0);
        _activate = _input.WasPressed(Key.Enter) || _input.WasPressed(Key.Space);
    }

    public void End()
    {
        RequirePass();
        _inPass = false;
        if (!_input.IsDown(PointerButton.Left))
        {
            _pressed = null;
        }

        if (_focusOrder.Count == 0 || _focusMove == 0)
        {
            // A focus requested for an element that is not declared yet stays pending until it appears.
            return;
        }

        var index = _focused is null ? -1 : _focusOrder.IndexOf(_focused);
        _focused = index < 0 ? _focusOrder[0] : _focusOrder[(index + _focusMove + _focusOrder.Count) % _focusOrder.Count];
    }

    /// <summary>Moves keyboard focus to an element (for example the default button of a menu that just opened).</summary>
    public void Focus(string? id) => _focused = id;

    public void Panel(UiRect rect, Vector4? color = null, bool border = true)
    {
        RequirePass();
        Fill(rect, color ?? Theme.Panel);
        if (border)
        {
            Outline(rect, Theme.Border, Theme.BorderWidth);
        }
    }

    public void Rect(UiRect rect, Vector4 color) => Fill(rect, color);

    public void Label(Vector2 position, string text, Vector4? color = null, float? scale = null, TextAlign align = TextAlign.Left)
    {
        RequirePass();
        var s = scale ?? Theme.TextScale;
        var size = PixelFont.Measure(text) * s;
        // Glyphs start on whole canvas pixels: centering odd-width text would otherwise land on half pixels, which
        // the CPU renderer samples as jagged glyphs and multisampled GPUs smear into gray edges.
        var x0 = MathF.Floor(align switch
        {
            TextAlign.Center => position.X - size.X * 0.5f,
            TextAlign.Right => position.X - size.X,
            _ => position.X,
        });
        var tint = color ?? Theme.Text;
        var x = x0;
        var y = MathF.Floor(position.Y);
        foreach (var c in text)
        {
            if (c == '\n')
            {
                x = x0;
                y += PixelFont.LineHeight * s;
                continue;
            }

            if (c != ' ')
            {
                _items.Add(new DrawItem(Font.Glyph(c), new UiRect(x, y, PixelFont.GlyphWidth * s, PixelFont.GlyphHeight * s), tint));
            }

            x += PixelFont.Advance * s;
        }
    }

    /// <summary>Text centered inside <paramref name="rect"/>.</summary>
    public void Label(UiRect rect, string text, Vector4? color = null, float? scale = null)
    {
        var s = scale ?? Theme.TextScale;
        var size = PixelFont.Measure(text) * s;
        Label(new Vector2(MathF.Round(rect.Center.X), MathF.Round(rect.Center.Y - size.Y * 0.5f)), text, color, s, TextAlign.Center);
    }

    /// <summary>True on the tick the button is activated (mouse release inside, or Enter/Space while focused).</summary>
    public bool Button(string id, UiRect rect, string label)
    {
        RequirePass();
        _focusOrder.Add(id);
        var hovered = _pointerInside && rect.Contains(_pointer);
        // A stationary pointer must not steal focus back from the keyboard, so only movement or a press focuses.
        if (hovered && (_pointerMoved || _input.WasPressed(PointerButton.Left)))
        {
            _focused = id;
        }

        if (hovered && _input.WasPressed(PointerButton.Left))
        {
            _pressed = id;
        }

        var clicked = hovered && _pressed == id && _input.WasReleased(PointerButton.Left);
        var focused = _focused == id;
        var activated = clicked || (focused && _activate);
        var held = _pressed == id && _input.IsDown(PointerButton.Left);

        Fill(rect, held ? Theme.ButtonPressed : hovered ? Theme.ButtonHover : Theme.Button);
        Outline(rect, focused ? Theme.Accent : Theme.Border, focused ? Theme.FocusWidth : Theme.BorderWidth);
        Label(rect, label, Theme.Text);
        _elements.Add(new UiElementInfo(id, "button", rect, hovered, focused, label));
        return activated;
    }

    /// <summary>Filled bar for health, stamina or progress. <paramref name="value"/> is clamped to 0..1.</summary>
    public void Bar(UiRect rect, float value, Vector4? fill = null, string? label = null)
    {
        RequirePass();
        var v = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
        Fill(rect, Theme.BarTrack);
        if (v > 0)
        {
            Fill(new UiRect(rect.X, rect.Y, MathF.Round(rect.Width * v), rect.Height), fill ?? Theme.Accent);
        }

        Outline(rect, Theme.Border, Theme.BorderWidth);
        if (label is not null)
        {
            Label(rect, label, Theme.Text);
        }
    }

    public void Image(UiRect rect, SpriteFrame sprite, Vector4? tint = null)
    {
        RequirePass();
        _items.Add(new DrawItem(sprite, rect, tint ?? Vector4.One));
    }

    /// <summary>Emits the last pass as screen-space sprites scaled to the frame's viewport. Does not change UI state.</summary>
    public void Draw(FrameBuilder frame)
    {
        var (scale, offset) = Fit(frame.Viewport);
        if (scale <= 0)
        {
            return;
        }

        foreach (var item in _items)
        {
            var topLeft = offset + item.Rect.Position * scale;
            frame.DrawScreenSprite(item.Sprite, topLeft, item.Rect.Size * scale, Theme.Layer, item.Color);
        }
    }

    /// <summary>Plain-text description of the interactive elements, for agents reading a snapshot or a log.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var e in _elements)
        {
            text.Append(e.Kind).Append(" \"").Append(e.Id).Append("\" [").Append(e.Label).Append("] at ")
                .Append(e.Rect.X).Append(',').Append(e.Rect.Y).Append(' ').Append(e.Rect.Width).Append('x').Append(e.Rect.Height)
                .Append(e.Focused ? " focused" : "").Append(e.Hovered ? " hovered" : "").Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Drawing-buffer pixel at the center of a canvas point (what a host or script should click).</summary>
    public Vector2 CanvasToScreen(Vector2 canvasPoint, RenderExtent viewport)
    {
        var (scale, offset) = Fit(viewport);
        return offset + canvasPoint * scale;
    }

    private (float Scale, Vector2 Offset) Fit(RenderExtent viewport)
    {
        if (viewport.IsEmpty)
        {
            return (0, Vector2.Zero);
        }

        var scale = MathF.Min(viewport.Width / Canvas.X, viewport.Height / Canvas.Y);
        if (PixelPerfect)
        {
            scale = MathF.Max(1f, MathF.Floor(scale));
        }

        var offset = new Vector2(MathF.Floor((viewport.Width - Canvas.X * scale) * 0.5f), MathF.Floor((viewport.Height - Canvas.Y * scale) * 0.5f));
        return (scale, offset);
    }

    private void Fill(UiRect rect, Vector4 color)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            _items.Add(new DrawItem(Font.White, rect, color));
        }
    }

    private void Outline(UiRect rect, Vector4 color, float width)
    {
        if (width <= 0)
        {
            return;
        }

        Fill(new UiRect(rect.X, rect.Y, rect.Width, width), color);
        Fill(new UiRect(rect.X, rect.Y + rect.Height - width, rect.Width, width), color);
        Fill(new UiRect(rect.X, rect.Y + width, width, rect.Height - width * 2), color);
        Fill(new UiRect(rect.X + rect.Width - width, rect.Y + width, width, rect.Height - width * 2), color);
    }

    private void RequirePass()
    {
        if (!_inPass)
        {
            throw new InvalidOperationException("[UI]: widgets must be declared between Begin and End (in Update)");
        }
    }

    private readonly record struct DrawItem(SpriteFrame Sprite, UiRect Rect, Vector4 Color);
}
