using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>Keys every host maps. Names are stable: input scripts, replays and the dev bridge use them as strings.</summary>
public enum Key : byte
{
    None = 0,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    Space, Enter, Escape, Tab, Backspace,
    Left, Right, Up, Down,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

public enum PointerButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

public enum InputEventKind : byte
{
    KeyDown = 1,
    KeyUp = 2,
    PointerMove = 3,
    PointerDown = 4,
    PointerUp = 5,
}

/// <summary>One raw event from a host, a script or the dev bridge. Pointer positions are drawing-buffer pixels (top-left origin).</summary>
public readonly record struct InputEvent(InputEventKind Kind, Key Key, PointerButton Button, Vector2 Position)
{
    public static InputEvent KeyDown(Key key) => new(InputEventKind.KeyDown, key, default, default);
    public static InputEvent KeyUp(Key key) => new(InputEventKind.KeyUp, key, default, default);
    public static InputEvent PointerMove(Vector2 position) => new(InputEventKind.PointerMove, Key.None, default, position);
    public static InputEvent PointerDown(PointerButton button, Vector2 position) => new(InputEventKind.PointerDown, Key.None, button, position);
    public static InputEvent PointerUp(PointerButton button, Vector2 position) => new(InputEventKind.PointerUp, Key.None, button, position);
}

/// <summary>
/// Input as one tick sees it. Immutable: the runtime latches host events once per tick, so a game reads the same
/// state no matter how many frames or which target produced it. Pressed/released are edges since the previous tick.
/// </summary>
public sealed class InputState
{
    private const int KeySlots = 128;
    private readonly bool[] _down;
    private readonly bool[] _pressed;
    private readonly bool[] _released;
    private readonly byte _buttonsDown;
    private readonly byte _buttonsPressed;
    private readonly byte _buttonsReleased;

    internal InputState(bool[] down, bool[] pressed, bool[] released, byte buttonsDown, byte buttonsPressed, byte buttonsReleased, Vector2 pointer, RenderExtent viewport)
    {
        _down = down;
        _pressed = pressed;
        _released = released;
        _buttonsDown = buttonsDown;
        _buttonsPressed = buttonsPressed;
        _buttonsReleased = buttonsReleased;
        Pointer = pointer;
        Viewport = viewport;
    }

    public static InputState Empty { get; } = new(new bool[KeySlots], new bool[KeySlots], new bool[KeySlots], 0, 0, 0, Vector2.Zero, default);

    /// <summary>Pointer in drawing-buffer pixels, origin top-left.</summary>
    public Vector2 Pointer { get; }

    /// <summary>Viewport of the last presented frame; UI uses it to map the pointer into its virtual canvas.</summary>
    public RenderExtent Viewport { get; }

    public bool IsDown(Key key) => _down[(int)key];

    public bool WasPressed(Key key) => _pressed[(int)key];

    public bool WasReleased(Key key) => _released[(int)key];

    public bool IsDown(PointerButton button) => (_buttonsDown & (1 << (int)button)) != 0;

    public bool WasPressed(PointerButton button) => (_buttonsPressed & (1 << (int)button)) != 0;

    public bool WasReleased(PointerButton button) => (_buttonsReleased & (1 << (int)button)) != 0;

    /// <summary>-1, 0 or +1 from two keys; both held cancel out.</summary>
    public float Axis(Key negative, Key positive) => (IsDown(positive) ? 1f : 0f) - (IsDown(negative) ? 1f : 0f);

    /// <summary>Axis that accepts either of two key pairs (arrows and WASD, for example).</summary>
    public float Axis(Key negative, Key positive, Key altNegative, Key altPositive) =>
        Math.Clamp(Axis(negative, positive) + Axis(altNegative, altPositive), -1f, 1f);

    internal static bool[] NewKeys() => new bool[KeySlots];
}

/// <summary>
/// Host side of input. Hosts push raw events whenever they arrive; <see cref="GameRuntime"/> latches them into an
/// <see cref="InputState"/> right before each tick and, when recording, stores them with that tick for replay.
/// </summary>
public sealed class InputCollector
{
    private readonly List<InputEvent> _queue = [];
    private readonly bool[] _held = InputState.NewKeys();
    private readonly List<ScriptedInput> _recorded = [];
    private byte _buttonsHeld;
    private Vector2 _pointer;

    public bool Recording { get; set; }

    public IReadOnlyList<ScriptedInput> Recorded => _recorded;

    public void Push(InputEvent inputEvent)
    {
        if (!Enum.IsDefined(inputEvent.Kind) || (int)inputEvent.Key >= 128)
        {
            throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent, "[INPUT]: unknown event kind or key");
        }

        if (!float.IsFinite(inputEvent.Position.X) || !float.IsFinite(inputEvent.Position.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent, "[INPUT]: pointer position must be finite");
        }

        _queue.Add(inputEvent);
    }

    /// <summary>Applies queued events in arrival order and returns the state the tick <paramref name="tick"/> will see.</summary>
    public InputState Latch(long tick, RenderExtent viewport)
    {
        var pressed = InputState.NewKeys();
        var released = InputState.NewKeys();
        byte buttonsPressed = 0, buttonsReleased = 0;
        foreach (var e in _queue)
        {
            if (Recording)
            {
                _recorded.Add(new ScriptedInput(tick, e));
            }

            switch (e.Kind)
            {
                case InputEventKind.KeyDown when e.Key != Key.None:
                    if (!_held[(int)e.Key])
                    {
                        pressed[(int)e.Key] = true;
                    }

                    _held[(int)e.Key] = true;
                    break;
                case InputEventKind.KeyUp when e.Key != Key.None:
                    if (_held[(int)e.Key])
                    {
                        released[(int)e.Key] = true;
                    }

                    _held[(int)e.Key] = false;
                    break;
                case InputEventKind.PointerMove:
                    _pointer = e.Position;
                    break;
                case InputEventKind.PointerDown:
                    _pointer = e.Position;
                    if ((_buttonsHeld & Bit(e.Button)) == 0)
                    {
                        buttonsPressed |= Bit(e.Button);
                    }

                    _buttonsHeld |= Bit(e.Button);
                    break;
                case InputEventKind.PointerUp:
                    _pointer = e.Position;
                    if ((_buttonsHeld & Bit(e.Button)) != 0)
                    {
                        buttonsReleased |= Bit(e.Button);
                    }

                    _buttonsHeld &= (byte)~Bit(e.Button);
                    break;
            }
        }

        _queue.Clear();
        return new InputState((bool[])_held.Clone(), pressed, released, _buttonsHeld, buttonsPressed, buttonsReleased, _pointer, viewport);
    }

    /// <summary>Releases everything that is held (window lost focus, agent took over).</summary>
    public void ReleaseAll()
    {
        for (var i = 0; i < _held.Length; i++)
        {
            if (_held[i])
            {
                _queue.Add(InputEvent.KeyUp((Key)i));
            }
        }

        foreach (var button in new[] { PointerButton.Left, PointerButton.Right, PointerButton.Middle })
        {
            if ((_buttonsHeld & Bit(button)) != 0)
            {
                _queue.Add(InputEvent.PointerUp(button, _pointer));
            }
        }
    }

    private static byte Bit(PointerButton button) => (byte)(1 << (int)button);
}

public readonly record struct ScriptedInput(long Tick, InputEvent Event);

/// <summary>
/// Input scheduled by tick: agents drive headless runs with it and replays are stored in it. JSON form, one object per event:
/// <c>{"tick":10,"key":"Space","down":true}</c>, <c>{"tick":12,"pointer":[320,180]}</c>,
/// <c>{"tick":13,"button":"Left","down":true,"pointer":[320,180]}</c>.
/// </summary>
public sealed class InputScript
{
    private readonly ScriptedInput[] _events;
    private int _next;

    public InputScript(IEnumerable<ScriptedInput> events)
    {
        _events = [.. events.OrderBy(e => e.Tick)];
    }

    public IReadOnlyList<ScriptedInput> Events => _events;

    /// <summary>Pushes every event scheduled for <paramref name="tick"/> (and any earlier one not yet delivered).</summary>
    public void Feed(long tick, InputCollector collector)
    {
        while (_next < _events.Length && _events[_next].Tick <= tick)
        {
            collector.Push(_events[_next].Event);
            _next++;
        }
    }

    public static InputScript Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("[INPUT]: an input script is a JSON array of events");
        }

        var events = new List<ScriptedInput>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            events.Add(ParseEvent(item));
        }

        return new InputScript(events);
    }

    public string ToJson()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < _events.Length; i++)
        {
            var (tick, e) = _events[i];
            json.Append(i == 0 ? "\n  " : ",\n  ").Append("{\"tick\":").Append(tick);
            switch (e.Kind)
            {
                case InputEventKind.KeyDown or InputEventKind.KeyUp:
                    json.Append(",\"key\":\"").Append(e.Key).Append("\",\"down\":").Append(e.Kind == InputEventKind.KeyDown ? "true" : "false");
                    break;
                case InputEventKind.PointerMove:
                    AppendPointer(json, e.Position);
                    break;
                default:
                    json.Append(",\"button\":\"").Append(e.Button).Append("\",\"down\":").Append(e.Kind == InputEventKind.PointerDown ? "true" : "false");
                    AppendPointer(json, e.Position);
                    break;
            }

            json.Append('}');
        }

        return json.Append(_events.Length == 0 ? "]" : "\n]").ToString();
    }

    internal static ScriptedInput ParseEvent(JsonElement item)
    {
        if (!item.TryGetProperty("tick", out var tickElement) || !tickElement.TryGetInt64(out var tick) || tick < 0)
        {
            throw new FormatException($"[INPUT]: event without a non-negative \"tick\": {item.GetRawText()}");
        }

        return new ScriptedInput(tick, ParseAction(item));
    }

    /// <summary>The event part of a script entry (also used by the dev bridge, which has no tick).</summary>
    public static InputEvent ParseAction(JsonElement item)
    {
        var down = item.TryGetProperty("down", out var downElement) && downElement.ValueKind == JsonValueKind.True;
        var position = Vector2.Zero;
        if (item.TryGetProperty("pointer", out var pointer))
        {
            if (pointer.ValueKind != JsonValueKind.Array || pointer.GetArrayLength() != 2)
            {
                throw new FormatException($"[INPUT]: \"pointer\" must be [x, y]: {item.GetRawText()}");
            }

            position = new Vector2(pointer[0].GetSingle(), pointer[1].GetSingle());
        }

        if (item.TryGetProperty("key", out var keyElement))
        {
            var key = KeyNames.Parse(keyElement.GetString());
            return down ? InputEvent.KeyDown(key) : InputEvent.KeyUp(key);
        }

        if (item.TryGetProperty("button", out var buttonElement))
        {
            if (!Enum.TryParse<PointerButton>(buttonElement.GetString(), ignoreCase: true, out var button))
            {
                throw new FormatException($"[INPUT]: unknown pointer button {buttonElement.GetRawText()}");
            }

            return down ? InputEvent.PointerDown(button, position) : InputEvent.PointerUp(button, position);
        }

        if (item.TryGetProperty("pointer", out _))
        {
            return InputEvent.PointerMove(position);
        }

        throw new FormatException($"[INPUT]: event needs \"key\", \"button\" or \"pointer\": {item.GetRawText()}");
    }

    private static void AppendPointer(StringBuilder json, Vector2 position) =>
        json.Append(",\"pointer\":[")
            .Append(position.X.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(position.Y.ToString(CultureInfo.InvariantCulture)).Append(']');
}

/// <summary>Key names across hosts: <see cref="Key"/> names, plus the browser's <c>KeyboardEvent.code</c> values.</summary>
public static class KeyNames
{
    public static Key Parse(string? name)
    {
        if (!string.IsNullOrEmpty(name) && Enum.TryParse<Key>(name, ignoreCase: true, out var key) && key != Key.None && Enum.IsDefined(key))
        {
            return key;
        }

        var web = FromWebCode(name);
        return web != Key.None ? web : throw new FormatException($"[INPUT]: unknown key \"{name}\"");
    }

    /// <summary>Maps <c>KeyboardEvent.code</c> ("KeyA", "Digit1", "ArrowLeft", "ShiftLeft"...). Unknown codes map to <see cref="Key.None"/>.</summary>
    public static Key FromWebCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Key.None;
        }

        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
        {
            return (Key)((int)Key.A + (code[3] - 'A'));
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
        {
            return (Key)((int)Key.D0 + (code[5] - '0'));
        }

        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 12)
        {
            return (Key)((int)Key.F1 + f - 1);
        }

        return code switch
        {
            "Space" => Key.Space,
            "Enter" or "NumpadEnter" => Key.Enter,
            "Escape" => Key.Escape,
            "Tab" => Key.Tab,
            "Backspace" => Key.Backspace,
            "ArrowLeft" => Key.Left,
            "ArrowRight" => Key.Right,
            "ArrowUp" => Key.Up,
            "ArrowDown" => Key.Down,
            "ShiftLeft" => Key.LeftShift,
            "ShiftRight" => Key.RightShift,
            "ControlLeft" => Key.LeftControl,
            "ControlRight" => Key.RightControl,
            "AltLeft" => Key.LeftAlt,
            "AltRight" => Key.RightAlt,
            _ => Key.None,
        };
    }
}
