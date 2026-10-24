using System.Numerics;
using Ukiyo.AI;
using Ukiyo.Rendering;

namespace Ukiyo.Samples.Patrol;

/// <summary>
/// Two guards patrol a ring of corridors around a hall and chase a player who walks (arrows or WASD). The sentry is a
/// behavior tree, the hound a state machine; both route with A* on the same grid and move with steering on the fixed
/// timestep. What each one is thinking is in the snapshot: <c>sentry.bt.*</c> (active node path), <c>sentry.bb.*</c>
/// (blackboard), <c>hound.state.*</c> (current state). A guard that touches the player ends the round: everyone restarts and <c>caught</c> counts up.
/// </summary>
public sealed class PatrolGame : IGame
{
    public const float CatchDistance = 0.6f;
    public const float TileSize = 1f;

    private static readonly Vector4 Floor = new(0.018f, 0.022f, 0.032f, 1f);
    private static readonly Vector4 Wall = new(0.10f, 0.11f, 0.15f, 1f);
    private static readonly Vector4 RouteDot = new(0.9f, 0.9f, 0.9f, 0.5f);
    private static readonly Vector4 PlayerColor = new(0.1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 Calm = new(0.05f, 0.25f, 0.8f, 1f);
    private static readonly Vector4 Hunting = new(0.9f, 0.1f, 0.05f, 1f);

    private readonly GridMap _map = PatrolMap.Create();
    private GridPathfinder _pathfinder = null!;
    private Npc[] _npcs = [];
    private Player _player = null!;
    private SpriteFrame _white;
    private int _caught;

    public string Name => "Patrol";

    public int Caught => _caught;

    public IReadOnlyList<Npc> Npcs => _npcs;

    public Player Player => _player;

    public void Initialize(GameContext context)
    {
        _white = SpriteFrame.Whole(context.CreateTexture(new TextureData(1, 1, [255, 255, 255, 255])), 1, 1);
        _pathfinder = new GridPathfinder(_map);
        StartRound();
    }

    /// <summary>Player at the start, guards back at their routes with fresh brains.</summary>
    private void StartRound()
    {
        _player = new Player(_map, PatrolMap.PlayerStart);
        _npcs = [new Sentry(_map, _pathfinder, PatrolMap.SentryRoute), new Hound(_map, _pathfinder, PatrolMap.HoundRoute)];
    }

    public void Update(in TickInfo tick)
    {
        var dt = Simulation.TickSecondsF;
        var direction = new Vector2(tick.Input.Axis(Key.Left, Key.Right, Key.A, Key.D), tick.Input.Axis(Key.Down, Key.Up, Key.S, Key.W));
        if (direction != Vector2.Zero)
        {
            direction = DetMath.Normalize(direction);
        }

        _player.Step(direction, dt);
        foreach (var npc in _npcs)
        {
            var visible = Perception.Sees(_map, npc.Agent.Position, _player.Position);
            npc.Think(new Senses(tick.Tick, _player.Position, visible));
            npc.Follower.Step(dt);
            if (DetMath.Length(npc.Agent.Position - _player.Position) < CatchDistance)
            {
                _caught++;
                StartRound();
                return;
            }
        }
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = Floor;
        frame.Camera2D = new Camera2D(new Vector2(PatrolMap.Width * 0.5f, PatrolMap.Height * 0.5f), PatrolMap.Height + 0.5f);
        for (var y = 0; y < _map.Height; y++)
        {
            for (var x = 0; x < _map.Width; x++)
            {
                var cell = new GridCell(x, y);
                frame.DrawSprite(_white, _map.CellCenter(cell), new Vector2(TileSize), layer: 0, tint: _map.IsWalkable(cell) ? Floor : Wall);
            }
        }

        foreach (var npc in _npcs)
        {
            foreach (var point in npc.Follower.Route)
            {
                frame.DrawSprite(_white, point, new Vector2(0.14f), layer: 1, tint: RouteDot);
            }

            frame.DrawSprite(_white, npc.Agent.Position, new Vector2(0.62f), layer: 2, tint: npc.Alerted ? Hunting : Calm);
        }

        frame.DrawSprite(_white, _player.Position, new Vector2(Player.HalfSize * 2f), layer: 3, tint: PlayerColor);
    }

    public GameSnapshot Snapshot(long tick)
    {
        var entities = new Dictionary<string, EntityPose> { ["player"] = EntityPose.Planar(_player.Position) };
        var values = new Dictionary<string, double> { ["caught"] = _caught };
        foreach (var npc in _npcs)
        {
            entities[npc.Name] = EntityPose.Planar(npc.Agent.Position);
            values[$"{npc.Name}.alerted"] = npc.Alerted ? 1 : 0;
            values[$"{npc.Name}.waypoint"] = npc.Follower.NextWaypoint;
            npc.WriteSnapshot(values);
        }

        return new GameSnapshot(tick, entities) { Values = values };
    }
}
