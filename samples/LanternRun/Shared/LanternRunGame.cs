using System.Numerics;
using Ukiyo.Audio;
using Ukiyo.Physics;
using Ukiyo.Rendering;
using Ukiyo.UI;

namespace Ukiyo.Samples.LanternRun;

public enum RunState
{
    Title = 0,
    Playing = 1,
    Paused = 2,
    Won = 3,
    GameOver = 4,
}

/// <summary>
/// A small platformer that exercises the 2D stack end to end: pixel art from code, world sprites, deterministic
/// physics with sensors, input, and game UI (title, HUD, pause menu). Everything happens in Update from the tick's
/// input, so a headless input script reproduces any run exactly.
/// Controls: arrows/A-D move, Space/Up/W jump, Escape pauses. Collect every ember, then reach the shrine.
/// </summary>
public sealed class LanternRunGame : IGame
{
    public const float RunSpeed = 6f;
    public const float JumpSpeed = 12.5f;
    public const float FallLimit = -12f;
    public const int StartLives = 3;
    public static readonly Vector2 Spawn = new(1.5f, 2f);

    // Level as (center x, center y, width, height) of static stone blocks, in tiles (1 tile = 1 meter).
    private static readonly Vector4[] Blocks =
    [
        new(6f, 0f, 14f, 1f),
        new(17f, 2f, 4f, 1f),
        new(24f, 4f, 4f, 1f),
        new(30f, 1f, 6f, 1f),
        new(-1.5f, 3f, 1f, 7f),
        new(33.5f, 3f, 1f, 5f),
    ];

    private static readonly Vector2[] EmberSpots = [new(5f, 1.5f), new(9f, 2.5f), new(17f, 3.5f), new(24f, 5.5f), new(28.5f, 2.5f)];
    private static readonly Vector2 ShrinePosition = new(31.5f, 2f);

    private readonly List<Body2D> _embers = [];
    private PhysicsWorld2D _world = new();
    private GameUi _ui = null!;
    private SpriteSheet _sheet = null!;
    private Body2D _player = null!;
    private Body2D _shrine = null!;
    private RunState _state = RunState.Title;
    private long _tick;
    private int _score;
    private int _lives = StartLives;
    private bool _facingLeft;
    private long _stateSince;
    private Sounds _sounds;
    private SoundQueue _audio = SoundQueue.Discard;

    /// <summary>Every sound is synthesized from a preset and a seed: no audio files ship with the game.</summary>
    private readonly record struct Sounds(SoundHandle Jump, SoundHandle Ember, SoundHandle Fall, SoundHandle Win, SoundHandle Menu);

    public string Name => "LanternRun";

    public RunState State => _state;

    public int Score => _score;

    public int Lives => _lives;

    public Body2D Player => _player;

    public void Initialize(GameContext context)
    {
        var atlas = PixelArt.Strip(LanternArt.Frames, LanternArt.Palette);
        var texture = context.CreateTexture(atlas);
        _sheet = SpriteSheet.FromGrid(texture, atlas.Width, atlas.Height, LanternArt.Cell, LanternArt.Cell);
        _ui = new GameUi(context, 320, 180);
        _sounds = new Sounds(
            context.CreateSound(SoundSynth.Preset("jump", seed: 2).Render()),
            context.CreateSound(SoundSynth.Preset("coin", seed: 7).Render()),
            context.CreateSound(SoundSynth.Preset("hit", seed: 3).Render()),
            context.CreateSound(SoundSynth.Preset("powerup", seed: 5).Render()),
            context.CreateSound(SoundSynth.Preset("blip", seed: 1).Render()));
        BuildWorld();
    }

    public void Update(in TickInfo tick)
    {
        _tick = tick.Tick;
        _audio = tick.Audio;
        var input = tick.Input;
        _ui.Begin(tick);
        switch (_state)
        {
            case RunState.Title:
                TitleMenu();
                break;
            case RunState.Playing:
                if (input.WasPressed(Key.Escape))
                {
                    Enter(RunState.Paused);
                    _ui.Focus("resume");
                }
                else
                {
                    Play(input);
                }

                Hud();
                break;
            case RunState.Paused:
                Hud();
                PauseMenu(input);
                break;
            case RunState.Won:
            case RunState.GameOver:
                Hud();
                EndMenu();
                break;
        }

        _ui.End();
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = new Vector4(0.010f, 0.012f, 0.022f, 1f);
        var cameraX = Math.Clamp(_player.Position.X, 6f, 28f);
        frame.Camera2D = new Camera2D(new Vector2(cameraX, 3.5f), 12f);

        foreach (var block in Blocks)
        {
            for (var x = 0; x < (int)block.Z; x++)
            {
                for (var y = 0; y < (int)block.W; y++)
                {
                    var center = new Vector2(block.X - block.Z * 0.5f + x + 0.5f, block.Y - block.W * 0.5f + y + 0.5f);
                    frame.DrawSprite(_sheet.Frame($"frame_{LanternArt.TileFrame}"), center, Vector2.One, layer: 0);
                }
            }
        }

        frame.DrawSprite(_sheet.Frame($"frame_{LanternArt.ShrineFrame}"), ShrinePosition, new Vector2(1.5f), layer: 1, tint: AllEmbersCollected ? Vector4.One : new Vector4(0.35f, 0.35f, 0.4f, 1f));
        var emberFrame = _sheet.Frame($"frame_{LanternArt.EmberFrame0 + (int)(_tick / 12 % 2)}");
        foreach (var ember in _embers)
        {
            if (ember.Enabled)
            {
                frame.DrawSprite(emberFrame, ember.Position, new Vector2(0.75f), layer: 1);
            }
        }

        var walking = _player.IsGrounded && MathF.Abs(_player.Velocity.X) > 0.1f;
        var playerFrame = _sheet.Frame($"frame_{LanternArt.PlayerFrame0 + (walking ? (int)(_tick / 8 % 2) : 0)}");
        frame.DrawSprite(playerFrame, _player.Position, new Vector2(1f), layer: 2, flipX: _facingLeft);

        _ui.Draw(frame);
    }

    public GameSnapshot Snapshot(long tick)
    {
        var entities = new Dictionary<string, EntityPose> { ["player"] = _player.Pose };
        foreach (var ember in _embers)
        {
            entities[ember.Name] = ember.Pose with { Scale = ember.Enabled ? Vector3.One : Vector3.Zero };
        }

        return new GameSnapshot(tick, entities)
        {
            Values = new Dictionary<string, double>
            {
                ["state"] = (int)_state,
                ["score"] = _score,
                ["lives"] = _lives,
                ["grounded"] = _player.IsGrounded ? 1 : 0,
                ["embersLeft"] = _embers.Count(e => e.Enabled),
            },
        };
    }

    private bool AllEmbersCollected => _embers.TrueForAll(e => !e.Enabled);

    private void BuildWorld()
    {
        _world = new PhysicsWorld2D { Gravity = new Vector2(0, -32f) };
        _embers.Clear();
        for (var i = 0; i < Blocks.Length; i++)
        {
            var b = Blocks[i];
            var block = _world.AddBox($"block{i}", BodyType.Static, new Vector2(b.X, b.Y), new Vector2(b.Z, b.W));
            block.Friction = 0.8f;
        }

        _player = _world.AddBox("player", BodyType.Dynamic, Spawn, new Vector2(0.75f, 0.9f));
        _player.FixedRotation = true;
        _player.Friction = 0f;
        for (var i = 0; i < EmberSpots.Length; i++)
        {
            var ember = _world.AddCircle($"ember{i}", BodyType.Static, EmberSpots[i], 0.35f);
            ember.IsSensor = true;
            _embers.Add(ember);
        }

        _shrine = _world.AddBox("shrine", BodyType.Static, ShrinePosition, new Vector2(1.2f, 1.5f));
        _shrine.IsSensor = true;
        _score = 0;
        _lives = StartLives;
    }

    private void Play(InputState input)
    {
        var move = input.Axis(Key.Left, Key.Right, Key.A, Key.D);
        if (move != 0)
        {
            _facingLeft = move < 0;
        }

        var velocity = _player.Velocity with { X = move * RunSpeed };
        var jump = input.WasPressed(Key.Space) || input.WasPressed(Key.Up) || input.WasPressed(Key.W);
        if (jump && _player.IsGrounded)
        {
            velocity.Y = JumpSpeed;
            _audio.Play(_sounds.Jump, 0.7f, Pan(_player.Position.X));
        }

        _player.Velocity = velocity;
        _world.Step(Simulation.TickSecondsF);

        foreach (var trigger in _world.TriggerEvents)
        {
            if (!trigger.Entered || trigger.Other != _player)
            {
                continue;
            }

            if (_embers.Contains(trigger.Sensor) && trigger.Sensor.Enabled)
            {
                trigger.Sensor.Enabled = false;
                _score += 100;
                // Each ember rings a little higher than the last.
                _audio.Play(_sounds.Ember, 0.8f, Pan(trigger.Sensor.Position.X), 1f + 0.06f * _embers.Count(e => !e.Enabled));
            }
            else if (trigger.Sensor == _shrine && AllEmbersCollected)
            {
                _score += 500;
                _audio.Play(_sounds.Win);
                Enter(RunState.Won);
                _ui.Focus("again");
            }
        }

        if (_player.Position.Y < FallLimit)
        {
            _lives--;
            _audio.Play(_sounds.Fall, 0.8f);
            _player.Position = Spawn;
            _player.Velocity = Vector2.Zero;
            if (_lives <= 0)
            {
                Enter(RunState.GameOver);
                _ui.Focus("again");
            }
        }
    }

    private void TitleMenu()
    {
        var canvas = _ui.Canvas;
        _ui.Panel(UiRect.Anchored(canvas, UiAnchor.Center, new Vector2(180, 110)));
        _ui.Label(new Vector2(canvas.X / 2, 48), "LANTERN RUN", _ui.Theme.Accent, 2f, TextAlign.Center);
        _ui.Label(new Vector2(canvas.X / 2, 74), "collect every ember", _ui.Theme.MutedText, 1f, TextAlign.Center);
        if (_ui.Button("start", new UiRect(canvas.X / 2 - 50, 96, 100, 18), "START"))
        {
            Enter(RunState.Playing);
        }

        if (_tick == 0 || _ui.Focused is null)
        {
            _ui.Focus("start");
        }
    }

    private void PauseMenu(InputState input)
    {
        var canvas = _ui.Canvas;
        _ui.Panel(UiRect.Anchored(canvas, UiAnchor.Center, new Vector2(140, 90)));
        _ui.Label(new Vector2(canvas.X / 2, 56), "PAUSED", _ui.Theme.Text, 2f, TextAlign.Center);
        var resume = _ui.Button("resume", new UiRect(canvas.X / 2 - 45, 82, 90, 16), "RESUME");
        var restart = _ui.Button("restart", new UiRect(canvas.X / 2 - 45, 102, 90, 16), "RESTART");
        if (resume || (input.WasPressed(Key.Escape) && _tick > _stateSince))
        {
            Enter(RunState.Playing);
        }
        else if (restart)
        {
            Restart();
        }
    }

    private void EndMenu()
    {
        var canvas = _ui.Canvas;
        var won = _state == RunState.Won;
        _ui.Panel(UiRect.Anchored(canvas, UiAnchor.Center, new Vector2(170, 90)));
        _ui.Label(new Vector2(canvas.X / 2, 56), won ? "SHRINE LIT" : "THE LIGHT WENT OUT", won ? _ui.Theme.Accent : _ui.Theme.Text, won ? 2f : 1f, TextAlign.Center);
        _ui.Label(new Vector2(canvas.X / 2, 80), $"score {_score}", _ui.Theme.MutedText, 1f, TextAlign.Center);
        if (_ui.Button("again", new UiRect(canvas.X / 2 - 45, 100, 90, 16), "PLAY AGAIN"))
        {
            Restart();
        }
    }

    private void Hud()
    {
        _ui.Label(new Vector2(8, 8), $"SCORE {_score:D5}", _ui.Theme.Text);
        _ui.Bar(new UiRect(_ui.Canvas.X - 68, 8, 60, 8), _lives / (float)StartLives);
        _ui.Label(new Vector2(_ui.Canvas.X - 72, 8), "LIFE", _ui.Theme.MutedText, 1f, TextAlign.Right);
    }

    private void Restart()
    {
        BuildWorld();
        Enter(RunState.Playing);
    }

    private void Enter(RunState state)
    {
        if (state is RunState.Playing or RunState.Paused)
        {
            _audio.Play(_sounds.Menu, 0.5f);
        }

        _state = state;
        _stateSince = _tick;
    }

    /// <summary>Stereo position from the player's place on screen: −1 at the left edge of the view, 1 at the right.</summary>
    private float Pan(float worldX)
    {
        var cameraX = Math.Clamp(_player.Position.X, 6f, 28f);
        return Math.Clamp((worldX - cameraX) / 10f, -1f, 1f);
    }
}
