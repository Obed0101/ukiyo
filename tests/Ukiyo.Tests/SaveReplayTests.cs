using System.Net;
using System.Numerics;
using System.Net.Sockets;
using System.Text.Json;
using Ukiyo.DevBridge;
using Ukiyo.Hosting;
using Ukiyo.Physics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.LanternRun;
using Ukiyo.Samples.SceneHop;
using Xunit;
using MyCube = Ukiyo.Games.MyGame.CubeGame;

namespace Ukiyo.Tests;

/// <summary>Save/restore, replays from a checkpoint and live time travel (docs/save-replay.md).</summary>
public sealed class SaveReplayTests : IDisposable
{
    private const string LanternScript = """
        [
          {"tick": 1, "key": "Enter", "down": true},
          {"tick": 2, "key": "Enter", "down": false},
          {"tick": 30, "key": "Right", "down": true},
          {"tick": 70, "key": "Space", "down": true},
          {"tick": 74, "key": "Space", "down": false},
          {"tick": 120, "key": "Right", "down": false}
        ]
        """;

    private const string SceneHopLog = """
        [
          {"id":"p1","tick":12,"ops":[{"op":"spawn","id":"bat-3","prefab":"drifter","position":[-5,-1],"values":{"speed":3}}]},
          {"id":"p2","tick":40,"ops":[{"op":"retarget","id":"lamp-east","position":[2,1],"values":{"glow":0.25}}]},
          {"id":"p3","tick":75,"scene":"cavern","ops":[{"op":"dispose","id":"bat-2"},{"op":"retarget","id":"bat-1","values":{"speed":6}}]}
        ]
        """;

    private static readonly RenderExtent Extent = new(320, 180, 1f);
    private static readonly SourceIdentity Source = new(new Dictionary<string, string>());
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    private readonly string _directory = Directory.CreateTempSubdirectory("ukiyo-save-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<GameRuntime> Start(IGame game, string? script = null, string? patches = null)
    {
        var runtime = new GameRuntime(game, Source);
        if (script is not null)
        {
            runtime.Script = InputScript.Parse(script);
        }

        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", Extent));
        runtime.Paused = true;
        if (patches is not null)
        {
            foreach (var patch in ScenePatch.ParseLog(patches))
            {
                runtime.SubmitPatch(patch);
            }
        }

        return runtime;
    }

    public static TheoryData<string> Samples => ["LanternRun", "SceneHop", "Cube"];

    private static (IGame Game, string? Script, string? Patches) Sample(string name) => name switch
    {
        "LanternRun" => (new LanternRunGame(), LanternScript, null),
        "SceneHop" => (new SceneHopGame(), null, SceneHopLog),
        _ => (new MyCube(), null, null),
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Restoring_a_checkpoint_and_replaying_forward_equals_an_uninterrupted_run(string name)
    {
        var (game, script, patches) = Sample(name);
        var straight = await Start(game, script, patches);
        straight.StepTo(100);
        var checkpoint = straight.Save();
        straight.StepTo(200);

        var (resumedGame, _, _) = Sample(name);
        var resumed = await Start(resumedGame, script);
        resumed.Restore(checkpoint);
        Assert.Equal(100, resumed.Tick);
        resumed.StepTo(200);

        Assert.Equal(straight.SnapshotJson(), resumed.SnapshotJson());
        Assert.Equal(straight.Save(), resumed.Save());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Saving_the_same_state_twice_gives_the_same_bytes_and_a_restore_round_trips_them(string name)
    {
        var (game, script, patches) = Sample(name);
        var runtime = await Start(game, script, patches);
        runtime.StepTo(90);
        var bytes = runtime.Save();
        Assert.Equal(bytes, runtime.Save());

        var info = GameRuntime.Inspect(bytes);
        Assert.Equal(game.Name, info.Game);
        Assert.Equal(90, info.Tick);

        runtime.StepTo(150);
        runtime.Restore(bytes);
        Assert.Equal(90, runtime.Tick);
        Assert.Equal(bytes, runtime.Save());
    }

    [Fact]
    public async Task A_save_taken_mid_patch_history_carries_the_pending_and_applied_patches()
    {
        var runtime = await Start(new SceneHopGame(), patches: SceneHopLog);
        runtime.StepTo(30);
        var bytes = runtime.Save();

        var resumedGame = new SceneHopGame();
        var resumed = await Start(resumedGame);
        resumed.Restore(bytes);
        Assert.Equal(["p1"], resumed.SceneState!.Applied.Select(p => p.Id));
        Assert.Equal(["p2", "p3"], resumedGame.Scenes.PendingPatches.Select(p => p.Id));
        var duplicate = Assert.Throws<SceneException>(() => resumed.SubmitPatch(ScenePatch.ParseLog(SceneHopLog)[0]));
        Assert.Equal(SceneErrorCode.DuplicatePatch, duplicate.Code);
    }

    [Fact]
    public async Task Typed_errors_name_what_is_wrong_with_a_save()
    {
        var runtime = await Start(new LanternRunGame(), LanternScript);
        runtime.StepTo(40);
        var good = runtime.Save();

        Assert.Equal(SaveErrorCode.BadMagic, Assert.Throws<SaveException>(() => runtime.Restore("not a save at all"u8)).Code);
        Assert.Equal(SaveErrorCode.Truncated, Assert.Throws<SaveException>(() => runtime.Restore(good.AsSpan(0, 7))).Code);

        var damaged = (byte[])good.Clone();
        damaged[^3] ^= 0x5A;
        var corrupt = Assert.Throws<SaveException>(() => runtime.Restore(damaged));
        Assert.Equal(SaveErrorCode.Corrupt, corrupt.Code);
        Assert.StartsWith("[SAVE:Corrupt]:", corrupt.Message);

        var otherGame = await Start(new SceneHopGame());
        Assert.Equal(SaveErrorCode.Mismatch, Assert.Throws<SaveException>(() => otherGame.Restore(good)).Code);

        var versioned = await Start(new VersionedGame(1));
        versioned.StepTo(3);
        var v1 = versioned.Save();
        var upgraded = await Start(new VersionedGame(2));
        Assert.Equal(SaveErrorCode.GameVersion, Assert.Throws<SaveException>(() => upgraded.Restore(v1)).Code);

        var plain = await Start(new PlainGame());
        Assert.False(plain.CanSave);
        Assert.Equal(SaveErrorCode.NotSaveable, Assert.Throws<SaveException>(() => plain.Save()).Code);
        Assert.Equal(SaveErrorCode.NoTimeline, Assert.Throws<SaveException>(() => plain.Rewind(0)).Code);
    }

    [Fact]
    public void A_physics_save_for_a_different_set_of_bodies_is_a_layout_error_that_changes_nothing()
    {
        static PhysicsWorld2D World(int bodies)
        {
            var world = new PhysicsWorld2D();
            for (var i = 0; i < bodies; i++)
            {
                world.AddBox($"b{i}", BodyType.Dynamic, new Vector2(i, 3), Vector2.One);
            }

            return world;
        }

        var big = World(2);
        big.Step(Simulation.TickSecondsF);
        var writer = new SaveWriter();
        big.Save(writer);

        var small = World(1);
        var before = small.Bodies[0].Position;
        var refused = Assert.Throws<SaveException>(() => small.Load(new SaveReader(writer.ToArray())));
        Assert.Equal(SaveErrorCode.Layout, refused.Code);
        Assert.Equal(before, small.Bodies[0].Position);

        var twin = World(2);
        twin.Load(new SaveReader(writer.ToArray()));
        Assert.Equal(big.Bodies[1].Position, twin.Bodies[1].Position);
        Assert.Equal(big.Steps, twin.Steps);
    }

    [Fact]
    public async Task Rewind_returns_to_the_recorded_state_and_the_same_input_leads_to_the_same_future()
    {
        var runtime = await Start(new LanternRunGame());
        runtime.EnableTimeTravel(new TimeTravelOptions(IntervalTicks: 60));
        var events = new Dictionary<long, InputEvent[]>
        {
            [1] = [InputEvent.KeyDown(Key.Enter)],
            [2] = [InputEvent.KeyUp(Key.Enter)],
            [30] = [InputEvent.KeyDown(Key.Right)],
            [70] = [InputEvent.KeyDown(Key.Space)],
            [74] = [InputEvent.KeyUp(Key.Space)],
            [120] = [InputEvent.KeyUp(Key.Right)],
        };
        var snapshots = new Dictionary<long, string>();

        void Run(long from, long to)
        {
            for (var t = from; t < to; t++)
            {
                snapshots[t] = runtime.SnapshotJson();
                foreach (var e in events.GetValueOrDefault(t, []))
                {
                    runtime.Input.Push(e);
                }

                runtime.Step(1);
            }

            snapshots[to] = runtime.SnapshotJson();
        }

        Run(0, 200);
        var result = runtime.Rewind(133);
        Assert.Equal((133L, 120L, 13L), (result.Tick, result.Checkpoint, result.Replayed));
        Assert.Equal(133, runtime.Tick);
        Assert.Equal(snapshots[133], runtime.SnapshotJson());

        Run(133, 200);
        Assert.Equal(snapshots[200], runtime.SnapshotJson());
    }

    [Fact]
    public async Task Rewind_through_a_scripted_run_keeps_following_the_script()
    {
        var runtime = await Start(new LanternRunGame(), LanternScript);
        runtime.EnableTimeTravel(new TimeTravelOptions(IntervalTicks: 30));
        runtime.StepTo(160);
        var at160 = runtime.SnapshotJson();
        runtime.Rewind(75);
        runtime.StepTo(160);
        Assert.Equal(at160, runtime.SnapshotJson());
    }

    [Fact]
    public async Task Rewind_replays_patches_submitted_from_outside_at_their_original_boundary()
    {
        var runtime = await Start(new SceneHopGame());
        runtime.EnableTimeTravel(new TimeTravelOptions(IntervalTicks: 20));
        var log = ScenePatch.ParseLog(SceneHopLog);
        var snapshots = new Dictionary<long, string>();

        void Run(long from, long to)
        {
            for (var t = from; t < to; t++)
            {
                snapshots[t] = runtime.SnapshotJson();
                foreach (var patch in log.Where(p => p.Tick - 5 == t))
                {
                    runtime.SubmitPatch(patch);
                }

                runtime.Step(1);
            }
        }

        Run(0, 110);
        var at110 = runtime.SnapshotJson();

        // p1 (submitted at 7) is inside the replay; p2 (35) too. p3 (70) belongs to the abandoned future.
        runtime.Rewind(50);
        Assert.Equal(snapshots[50], runtime.SnapshotJson());
        Assert.Equal(["p1", "p2"], runtime.SceneState!.Applied.Select(p => p.Id));

        Run(50, 110);
        Assert.Equal(at110, runtime.SnapshotJson());
    }

    [Fact]
    public async Task The_checkpoint_ring_is_bounded_and_rewind_reports_what_it_cannot_reach()
    {
        var runtime = await Start(new MyCube());
        var unarmed = Assert.Throws<SaveException>(() => runtime.Rewind(0));
        Assert.Equal(SaveErrorCode.NoTimeline, unarmed.Code);

        runtime.EnableTimeTravel(new TimeTravelOptions(IntervalTicks: 10, MaxCheckpoints: 4));
        runtime.StepTo(100);
        var window = runtime.RewindWindow!;
        Assert.Equal((70L, 100L, 4), (window.OldestTick, window.NewestTick, window.Checkpoints));
        Assert.True(window.Bytes > 0);

        Assert.Equal(SaveErrorCode.OutOfWindow, Assert.Throws<SaveException>(() => runtime.Rewind(69)).Code);
        Assert.Equal(SaveErrorCode.FutureTick, Assert.Throws<SaveException>(() => runtime.Rewind(101)).Code);
        Assert.Equal(70, runtime.Rewind(70).Tick);
        Assert.Equal(0, runtime.Rewind(70).Replayed);

        var tight = await Start(new MyCube());
        tight.EnableTimeTravel(new TimeTravelOptions(IntervalTicks: 1, MaxBytes: 1));
        tight.StepTo(20);
        Assert.Equal(1, tight.RewindWindow!.Checkpoints);
        Assert.Equal(20, tight.RewindWindow.OldestTick);
    }

    [Fact]
    public async Task Headless_save_then_load_continues_to_the_same_snapshots_as_a_straight_run()
    {
        var script = Path.Combine(_directory, "input.json");
        await File.WriteAllTextAsync(script, LanternScript);
        var save = Path.Combine(_directory, "run.uksv");

        var straight = await RunHeadless(new LanternRunGame(), "--checkpoints", "100,200", "--input", script);
        var first = await RunHeadless(new LanternRunGame(), "--checkpoints", "100", "--input", script, "--save", save);
        Assert.Equal(0, first.Code);
        Assert.Contains($"save {Path.GetFullPath(save)} tick=100 bytes=", first.Stdout);

        var resumed = await RunHeadless(new LanternRunGame(), "--checkpoints", "0,100,200", "--input", script, "--load", save);
        Assert.Equal(0, resumed.Code);
        Assert.Contains("tick=100", resumed.Stdout);
        Assert.Equal(2, resumed.Snapshots.Length);
        Assert.Equal(straight.Snapshots, resumed.Snapshots);

        await File.WriteAllBytesAsync(save, [1, 2, 3]);
        var refused = await RunHeadless(new LanternRunGame(), "--checkpoints", "100", "--load", save);
        Assert.Equal(HeadlessHost.SaveRefused, refused.Code);
        Assert.Contains("[SAVE:", refused.Stderr);
    }

    [Fact]
    public async Task Headless_load_skips_patches_the_save_already_holds()
    {
        var log = Path.Combine(_directory, "patches.json");
        await File.WriteAllTextAsync(log, SceneHopLog);
        var save = Path.Combine(_directory, "hop.uksv");

        var straight = await RunHeadless(new SceneHopGame(), "--checkpoints", "100", "--patches", log);
        await RunHeadless(new SceneHopGame(), "--checkpoints", "50", "--patches", log, "--save", save);
        var resumed = await RunHeadless(new SceneHopGame(), "--checkpoints", "100", "--patches", log, "--load", save);

        Assert.Equal(0, resumed.Code);
        Assert.Contains("submitted=0 skipped=3", resumed.Stdout);
        Assert.Equal(straight.Snapshots, resumed.Snapshots);
    }

    [Fact]
    public void The_save_flags_need_a_path()
    {
        Assert.Equal("a.uksv", HeadlessOptions.Parse(["--load", "a.uksv"]).LoadPath);
        Assert.Equal("b.uksv", HeadlessOptions.Parse(["--save", "b.uksv"]).SavePath);
        Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse(["--load"]));
        Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse(["--save"]));
    }

    [Fact]
    public async Task The_bridge_rewinds_a_saveable_game_and_reports_typed_refusals()
    {
        var runtime = new GameRuntime(new LanternRunGame(), Source);
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", Extent));
        using var session = DevBridgeServer.TryStart(runtime, "test") ?? throw new InvalidOperationException("dev bridge disabled by UKIYO_DEV_BRIDGE");
        var server = (DevBridgeServer)session;
        var discovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ukiyo", "live", $"{Environment.ProcessId}.json");
        string token;
        using (var file = JsonDocument.Parse(await File.ReadAllTextAsync(discovery)))
        {
            token = file.RootElement.GetProperty("token").GetString()!;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var reader = new StreamReader(client.GetStream());
        await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\n" };
        long id = 0;

        async Task<JsonElement> Ask(string command, Dictionary<string, object>? args = null)
        {
            var request = args ?? [];
            request["id"] = ++id;
            request["cmd"] = command;
            request["token"] = token;
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            var line = reader.ReadLineAsync();
            var deadline = DateTime.UtcNow + AnswerTimeout;
            while (!line.IsCompleted && DateTime.UtcNow < deadline)
            {
                session.Pump();
                await Task.Delay(5);
            }

            Assert.True(line.IsCompleted, "no answer from the bridge");
            return JsonDocument.Parse(await line ?? "{}").RootElement.Clone();
        }

        await Ask("step", new() { ["ticks"] = 130 });
        var snapshotAt70 = await ReplayReference(70);
        var rewound = await Ask("rewind", new() { ["tick"] = 70 });
        Assert.True(rewound.GetProperty("ok").GetBoolean());
        var result = rewound.GetProperty("result");
        Assert.Equal((70L, 60L, 10L), (result.GetProperty("tick").GetInt64(), result.GetProperty("checkpoint").GetInt64(), result.GetProperty("replayed").GetInt64()));
        Assert.Equal(0, result.GetProperty("window").GetProperty("oldest").GetInt64());
        Assert.Equal(70, runtime.Tick);
        Assert.Equal(snapshotAt70, runtime.SnapshotJson());

        var future = await Ask("rewind", new() { ["tick"] = 5000 });
        Assert.False(future.GetProperty("ok").GetBoolean());
        Assert.StartsWith("[SAVE:FutureTick]:", future.GetProperty("error").GetString());

        var missing = await Ask("rewind");
        Assert.False(missing.GetProperty("ok").GetBoolean());
        Assert.Contains("tick", missing.GetProperty("error").GetString());
    }

    private static async Task<string> ReplayReference(long tick)
    {
        var reference = await Start(new LanternRunGame());
        reference.StepTo(tick);
        return reference.SnapshotJson();
    }

    private static async Task<(int Code, string[] Snapshots, string Stdout, string Stderr)> RunHeadless(IGame game, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await new HeadlessHost(HeadlessOptions.Parse(args), stdout, stderr).RunAsync(new GameRuntime(game, Source));
        var lines = stdout.ToString().Split('\n', StringSplitOptions.TrimEntries);
        return (code, [.. lines.Where(line => line.StartsWith("snapshot ", StringComparison.Ordinal)).Select(line => line["snapshot ".Length..])], stdout.ToString(), stderr.ToString());
    }

    private sealed class PlainGame : IGame
    {
        public string Name => "Plain";

        public void Initialize(GameContext context)
        {
        }

        public void Update(in TickInfo tick)
        {
        }

        public void Extract(FrameBuilder frame)
        {
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    private sealed class VersionedGame(int version) : IGame, ISaveable
    {
        private long _ticks;

        public string Name => "Versioned";

        public int SaveVersion => version;

        public void Initialize(GameContext context)
        {
        }

        public void Update(in TickInfo tick) => _ticks = tick.Tick;

        public void Extract(FrameBuilder frame)
        {
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());

        public void Save(SaveWriter writer) => writer.Int64(_ticks);

        public void Load(SaveReader reader) => _ticks = reader.Int64();
    }
}
