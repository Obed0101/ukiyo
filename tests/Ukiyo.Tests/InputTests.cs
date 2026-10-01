using System.Numerics;
using Ukiyo.Rendering;
using Xunit;

namespace Ukiyo.Tests;

public sealed class InputTests
{
    private static readonly RenderExtent Viewport = new(640, 360, 1f);

    [Fact]
    public void Press_is_an_edge_for_one_tick_while_down_holds_until_release()
    {
        var input = new InputCollector();
        input.Push(InputEvent.KeyDown(Key.Space));
        var first = input.Latch(0, Viewport);
        var second = input.Latch(1, Viewport);
        input.Push(InputEvent.KeyUp(Key.Space));
        var third = input.Latch(2, Viewport);

        Assert.True(first.WasPressed(Key.Space) && first.IsDown(Key.Space));
        Assert.True(!second.WasPressed(Key.Space) && second.IsDown(Key.Space));
        Assert.True(third.WasReleased(Key.Space) && !third.IsDown(Key.Space));
    }

    [Fact]
    public void Tap_within_one_frame_is_still_seen_as_pressed_and_released()
    {
        var input = new InputCollector();
        input.Push(InputEvent.KeyDown(Key.Enter));
        input.Push(InputEvent.KeyUp(Key.Enter));
        var state = input.Latch(0, Viewport);

        Assert.True(state.WasPressed(Key.Enter));
        Assert.True(state.WasReleased(Key.Enter));
        Assert.False(state.IsDown(Key.Enter));
    }

    [Fact]
    public void Pointer_buttons_track_position_and_edges()
    {
        var input = new InputCollector();
        input.Push(InputEvent.PointerDown(PointerButton.Left, new Vector2(12, 34)));
        var down = input.Latch(0, Viewport);
        input.Push(InputEvent.PointerUp(PointerButton.Left, new Vector2(13, 35)));
        var up = input.Latch(1, Viewport);

        Assert.True(down.WasPressed(PointerButton.Left) && down.IsDown(PointerButton.Left));
        Assert.Equal(new Vector2(12, 34), down.Pointer);
        Assert.True(up.WasReleased(PointerButton.Left) && !up.IsDown(PointerButton.Left));
        Assert.Equal(Viewport, up.Viewport);
    }

    [Fact]
    public void Axis_combines_two_key_pairs_and_cancels_opposites()
    {
        var input = new InputCollector();
        input.Push(InputEvent.KeyDown(Key.Right));
        input.Push(InputEvent.KeyDown(Key.A));
        var state = input.Latch(0, Viewport);

        Assert.Equal(1f, state.Axis(Key.Left, Key.Right));
        Assert.Equal(0f, state.Axis(Key.Left, Key.Right, Key.A, Key.D));
    }

    [Fact]
    public void Script_parses_feeds_by_tick_and_serializes_back()
    {
        const string json = """
            [
              {"tick": 2, "key": "Space", "down": true},
              {"tick": 0, "pointer": [10, 20]},
              {"tick": 3, "button": "Left", "down": true, "pointer": [5.5, 6]},
              {"tick": 4, "key": "ArrowLeft", "down": false}
            ]
            """;
        var script = InputScript.Parse(json);
        Assert.Equal(new long[] { 0, 2, 3, 4 }, script.Events.Select(e => e.Tick).ToArray());
        Assert.Equal(Key.Left, script.Events[3].Event.Key);

        var again = InputScript.Parse(script.ToJson());
        Assert.Equal(script.Events, again.Events);
    }

    [Fact]
    public void Runtime_feeds_the_script_and_records_what_each_tick_saw()
    {
        var game = new InputProbeGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()))
        {
            Script = InputScript.Parse("""[{"tick":3,"key":"D","down":true},{"tick":5,"key":"D","down":false}]"""),
        };
        runtime.Input.Recording = true;
        runtime.Step(8);

        Assert.Equal(new long[] { 3, 4 }, game.DownTicks);
        Assert.Equal(2, runtime.Input.Recorded.Count);
        Assert.Equal(3, runtime.Input.Recorded[0].Tick);
    }

    [Fact]
    public void Web_key_codes_map_to_engine_keys()
    {
        Assert.Equal(Key.A, KeyNames.FromWebCode("KeyA"));
        Assert.Equal(Key.D7, KeyNames.FromWebCode("Digit7"));
        Assert.Equal(Key.F11, KeyNames.FromWebCode("F11"));
        Assert.Equal(Key.Up, KeyNames.FromWebCode("ArrowUp"));
        Assert.Equal(Key.None, KeyNames.FromWebCode("MediaPlayPause"));
        Assert.Throws<FormatException>(() => KeyNames.Parse("NotAKey"));
    }

    private sealed class InputProbeGame : IGame
    {
        public List<long> DownTicks { get; } = [];

        public string Name => "InputProbe";

        public void Initialize(GameContext context)
        {
        }

        public void Update(in TickInfo tick)
        {
            if (tick.Input.IsDown(Key.D))
            {
                DownTicks.Add(tick.Tick);
            }
        }

        public void Extract(FrameBuilder frame)
        {
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }
}
