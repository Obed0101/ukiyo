using System.Numerics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.LanternRun;
using Ukiyo.UI;
using Xunit;

namespace Ukiyo.Tests;

public sealed class UiAndSampleTests
{
    private static readonly RenderExtent Viewport = new(640, 360, 1f);

    [Fact]
    public void Button_clicks_once_on_release_inside_and_maps_window_pixels_to_the_canvas()
    {
        var context = new GameContext();
        var ui = new GameUi(context, 320, 180);
        var input = new InputCollector();
        var button = new UiRect(100, 50, 60, 20);
        var center = ui.CanvasToScreen(button.Center, Viewport);
        Assert.Equal(new Vector2(260, 120), center); // scale 2, no letterbox

        bool Pass(long tick)
        {
            ui.Begin(new TickInfo(tick, Simulation.TickSeconds) { Input = input.Latch(tick, Viewport) });
            var clicked = ui.Button("ok", button, "OK");
            ui.End();
            return clicked;
        }

        input.Push(InputEvent.PointerMove(center));
        Assert.False(Pass(0));
        input.Push(InputEvent.PointerDown(PointerButton.Left, center));
        Assert.False(Pass(1));
        input.Push(InputEvent.PointerUp(PointerButton.Left, center));
        Assert.True(Pass(2));
        Assert.False(Pass(3));
        Assert.Equal("ok", ui.Focused);
    }

    [Fact]
    public void Keyboard_moves_focus_and_enter_activates()
    {
        var ui = new GameUi(new GameContext(), 320, 180);
        var input = new InputCollector();
        string? activated = null;

        void Pass(long tick)
        {
            ui.Begin(new TickInfo(tick, Simulation.TickSeconds) { Input = input.Latch(tick, Viewport) });
            if (ui.Button("a", new UiRect(10, 10, 50, 12), "A"))
            {
                activated = "a";
            }

            if (ui.Button("b", new UiRect(10, 30, 50, 12), "B"))
            {
                activated = "b";
            }

            ui.End();
        }

        input.Push(InputEvent.KeyDown(Key.Down));
        Pass(0);
        Assert.Equal("a", ui.Focused);
        input.Push(InputEvent.KeyUp(Key.Down));
        input.Push(InputEvent.KeyDown(Key.Down));
        Pass(1);
        Assert.Equal("b", ui.Focused);
        input.Push(InputEvent.KeyDown(Key.Enter));
        Pass(2);
        Assert.Equal("b", activated);
    }

    [Fact]
    public void Font_atlas_has_ink_for_letters_and_none_for_space()
    {
        var atlas = PixelFont.BuildAtlas();
        Assert.Equal((PixelFont.AtlasWidth, PixelFont.AtlasHeight), (atlas.Width, atlas.Height));
        Assert.True(InkInCell(atlas, 'A') > 10);
        Assert.Equal(0, InkInCell(atlas, ' '));
        Assert.Equal(new Vector2(29, 8), PixelFont.Measure("HELLO"));
    }

    [Fact]
    public async Task LanternRun_starts_from_the_title_with_enter_and_runs_right_deterministically()
    {
        const string script = """
            [
              {"tick": 1, "key": "Enter", "down": true},
              {"tick": 2, "key": "Enter", "down": false},
              {"tick": 30, "key": "Right", "down": true},
              {"tick": 120, "key": "Right", "down": false}
            ]
            """;

        async Task<(GameRuntime Runtime, NullRenderer Renderer, LanternRunGame Game)> Run()
        {
            var game = new LanternRunGame();
            var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>())) { Script = InputScript.Parse(script) };
            var renderer = new NullRenderer();
            await runtime.StartAsync(renderer, new RenderConfiguration("test", Viewport));
            runtime.StepTo(180);
            runtime.RenderFrame(Viewport);
            return (runtime, renderer, game);
        }

        var (first, renderer, game) = await Run();
        var (second, _, _) = await Run();

        Assert.Equal(RunState.Playing, game.State);
        Assert.True(game.Player.Position.X > LanternRunGame.Spawn.X + 5, $"player at {game.Player.Position}");
        Assert.True(game.Player.IsGrounded);
        Assert.True(game.Score >= 100, $"score {game.Score}: the first ember sits on the running line");
        Assert.True(renderer.SpritesSubmitted > 0);
        Assert.Equal(first.SnapshotJson(), second.SnapshotJson());
    }

    private static int InkInCell(TextureData atlas, char c)
    {
        var index = c - 32;
        var originX = index % 16 * (PixelFont.GlyphWidth + 1);
        var originY = index / 16 * (PixelFont.GlyphHeight + 1);
        var ink = 0;
        for (var y = 0; y < PixelFont.GlyphHeight; y++)
        {
            for (var x = 0; x < PixelFont.GlyphWidth; x++)
            {
                ink += atlas.Rgba[((originY + y) * atlas.Width + originX + x) * 4 + 3] > 0 ? 1 : 0;
            }
        }

        return ink;
    }
}
