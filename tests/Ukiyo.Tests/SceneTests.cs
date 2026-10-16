using System.Numerics;
using System.Text.Json;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.LanternRun;
using Ukiyo.Samples.SceneHop;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>Test-only entity: a name, a position and a value, plus which prefab call produced it.</summary>
public sealed class Probe(string id, Vector3 position, double value)
{
    public string Id { get; } = id;

    public Vector3 Position { get; set; } = position;

    public double Value { get; set; } = value;
}

/// <summary>A minimal scene game whose setup and per-tick behavior each test supplies.</summary>
public sealed class ProbeGame(Action<SceneRegistry<Probe>> setup, Action<SceneRegistry<Probe>, TickInfo>? update = null) : IGame
{
    public const string TwoScenes = """
        [{ "name": "alpha", "bounds": { "min": [-5, -5], "max": [5, 5] },
           "entities": [{ "id": "a1", "prefab": "dot", "position": [1, 0] }, { "id": "a2", "prefab": "dot", "position": [2, 0], "values": { "v": 3 } }] },
         { "name": "beta", "entities": [{ "id": "b1", "prefab": "dot", "position": [0, 9], "values": { "v": 7 } }] }]
        """;

    public SceneRegistry<Probe> Scenes { get; private set; } = null!;

    public List<string> Disposed { get; } = [];

    public string Name => "Probe";

    /// <summary>"dot" has a parameter v in [0, 10] and a dispose log; "ring" has no retarget hook, so retarget rebuilds.</summary>
    public void Initialize(GameContext context)
    {
        Scenes = new SceneRegistry<Probe>(context);
        Scenes.Register(new Prefab<Probe>("dot", args => new Probe(args.Id, args.Position, args["v"]))
        {
            Parameters = [new PrefabParameter("v", 0, 10, 1)],
            Dispose = probe => Disposed.Add(probe.Id),
            Retarget = (probe, args) => (probe.Position, probe.Value) = (args.Position, args["v"]),
        });
        Scenes.Register(new Prefab<Probe>("ring", args => new Probe(args.Id, args.Position, args["r"]))
        {
            Parameters = [new PrefabParameter("r", 0.5, 2, 1)],
            Dispose = probe => Disposed.Add(probe.Id),
        });
        setup(Scenes);
    }

    public void Update(in TickInfo tick) => update?.Invoke(Scenes, tick);

    public void Extract(FrameBuilder frame)
    {
    }

    public GameSnapshot Snapshot(long tick) =>
        new(tick, Scenes.Entities.ToDictionary(e => e.Id, e => EntityPose.Planar(new Vector2(e.Instance.Position.X, e.Instance.Position.Y), 0f, (float)e.Instance.Value)));

    public static void RegisterTwoScenes(SceneRegistry<Probe> scenes)
    {
        using var document = JsonDocument.Parse(TwoScenes);
        foreach (var scene in document.RootElement.EnumerateArray())
        {
            scenes.RegisterJson(scene.GetRawText());
        }
    }

    public static async Task<(GameRuntime Runtime, ProbeGame Game)> StartAsync(Action<SceneRegistry<Probe>> setup, Action<SceneRegistry<Probe>, TickInfo>? update = null)
    {
        var game = new ProbeGame(setup, update);
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", new RenderExtent(320, 180, 1f)));
        return (runtime, game);
    }

    public static Task<(GameRuntime Runtime, ProbeGame Game)> StartInAlphaAsync(Action<SceneRegistry<Probe>, TickInfo>? update = null) =>
        StartAsync(scenes =>
        {
            RegisterTwoScenes(scenes);
            scenes.LoadScene("alpha");
        }, update);
}

public sealed class SceneTests
{
    internal static async Task<(GameRuntime Runtime, SceneHopGame Game)> StartSceneHopAsync()
    {
        var game = new SceneHopGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", new RenderExtent(640, 360, 1f)));
        return (runtime, game);
    }

    private static SceneException Refused(string sceneJson)
    {
        var context = new GameContext();
        var scenes = new SceneRegistry<Probe>(context);
        scenes.Register(new Prefab<Probe>("dot", args => new Probe(args.Id, args.Position, args["v"])) { Parameters = [new PrefabParameter("v", 0, 10, 1)] });
        return Assert.Throws<SceneException>(() => scenes.RegisterJson(sceneJson));
    }

    [Fact]
    public async Task The_scene_requested_in_initialize_is_live_at_tick_zero_and_recorded_in_the_snapshot()
    {
        var (runtime, game) = await StartSceneHopAsync();

        Assert.Equal("meadow", game.Scenes.ActiveScene);
        Assert.Equal(["lamp-west", "lamp-east", "moth"], game.Scenes.Entities.Select(e => e.Id));
        var json = JsonDocument.Parse(runtime.SnapshotJson()).RootElement;
        Assert.Equal("meadow", json.GetProperty("scene").GetProperty("active").GetString());
        Assert.Equal(0, json.GetProperty("scene").GetProperty("loadedAt").GetInt64());
        Assert.Equal(0, json.GetProperty("scene").GetProperty("patches").GetArrayLength());
    }

    [Fact]
    public async Task A_load_requested_during_a_tick_waits_for_the_next_boundary()
    {
        var seenDuringUpdate = new List<string?>();
        var (runtime, game) = await ProbeGame.StartInAlphaAsync((scenes, tick) =>
        {
            if (tick.Tick == 10)
            {
                scenes.LoadScene("beta");
            }

            seenDuringUpdate.Add(scenes.ActiveScene + ":" + string.Join(',', scenes.Entities.Select(e => e.Id)));
        });

        runtime.StepTo(11);
        Assert.Equal("alpha:a1,a2", seenDuringUpdate[10]);
        Assert.Equal("beta", game.Scenes.ActiveScene);
        Assert.Equal(11, game.Scenes.LoadedAt);
        Assert.Equal(["a2", "a1"], game.Disposed);

        runtime.Step(1);
        Assert.Equal("beta:b1", seenDuringUpdate[11]);
    }

    [Fact]
    public async Task SceneHop_runs_60_ticks_in_meadow_then_60_in_cavern_with_its_own_entities()
    {
        var (runtime, game) = await StartSceneHopAsync();

        runtime.StepTo(59);
        Assert.Equal("meadow", game.Scenes.ActiveScene);
        Assert.Equal(-6f + 3f * 59 / 60, game.Scenes["moth"].Position.X, 4);

        runtime.StepTo(60);
        Assert.Equal("cavern", game.Scenes.ActiveScene);
        Assert.Equal(["torch", "bat-1", "bat-2", "drip"], game.Scenes.Entities.Select(e => e.Id));
        Assert.False(game.Scenes.TryGet("moth", out _));
        Assert.Equal(new Vector2(5f, 1.5f), game.Scenes["bat-1"].Position);

        runtime.StepTo(119);
        Assert.Equal("cavern", game.Scenes.ActiveScene);
        Assert.Equal(5f - 4f * 59 / 60, game.Scenes["bat-1"].Position.X, 4);
        Assert.Equal(59, runtime.Snapshot().Values["sceneTicks"]);

        runtime.StepTo(120);
        Assert.Equal("meadow", game.Scenes.ActiveScene);
        Assert.Equal(new Vector2(-6f, 2f), game.Scenes["moth"].Position);
    }

    [Fact]
    public async Task SceneHop_snapshots_for_both_scenes_are_identical_across_runs()
    {
        long[] checkpoints = [0, 30, 59, 60, 90, 119, 120];
        async Task<string[]> Run()
        {
            var (runtime, _) = await StartSceneHopAsync();
            var snapshots = new List<string>();
            foreach (var checkpoint in checkpoints)
            {
                runtime.StepTo(checkpoint);
                runtime.RenderFrame(new RenderExtent(640, 360, 1f));
                snapshots.Add(runtime.SnapshotJson());
            }

            return [.. snapshots];
        }

        var first = await Run();
        Assert.Equal(first, await Run());
        Assert.Contains("\"active\":\"meadow\"", first[2]);
        Assert.Contains("\"active\":\"cavern\"", first[3]);
    }

    [Theory]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"ghost","position":[0,0]}]}""", SceneErrorCode.UnknownPrefab)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","position":[0,0],"values":{"v":11}}]}""", SceneErrorCode.OutOfRange)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","position":[0,0],"values":{"speed":1}}]}""", SceneErrorCode.UnknownParameter)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","position":[0,0]},{"id":"e","prefab":"dot","position":[1,0]}]}""", SceneErrorCode.DuplicateEntity)]
    [InlineData("""{"name":"s","bounds":{"min":[-1,-1],"max":[1,1]},"entities":[{"id":"e","prefab":"dot","position":[2,0]}]}""", SceneErrorCode.OutOfRange)]
    [InlineData("""{"name":"s","bounds":{"min":[1,1],"max":[-1,-1]},"entities":[]}""", SceneErrorCode.OutOfRange)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","positon":[0,0]}]}""", SceneErrorCode.Malformed)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","position":[0]}]}""", SceneErrorCode.Malformed)]
    [InlineData("""{"name":"s","entities":[{"id":"e","prefab":"dot","position":[0,0],"values":{"v":"high"}}]}""", SceneErrorCode.Malformed)]
    [InlineData("""{"name":"s","entities":{}}""", SceneErrorCode.Malformed)]
    [InlineData("""{"name":"s","entities":[""", SceneErrorCode.Malformed)]
    [InlineData("""{"name":"has space","entities":[]}""", SceneErrorCode.InvalidId)]
    [InlineData("""{"name":"s","entities":[{"id":"","prefab":"dot","position":[0,0]}]}""", SceneErrorCode.InvalidId)]
    public void Invalid_scene_content_fails_at_registration_with_a_typed_code(string json, SceneErrorCode expected)
    {
        var error = Refused(json);
        Assert.Equal(expected, error.Code);
        Assert.StartsWith($"[SCENE:{expected}]: ", error.Message);
    }

    [Fact]
    public async Task A_game_with_invalid_scene_content_fails_loudly_when_it_starts()
    {
        var error = await Assert.ThrowsAsync<SceneException>(() => ProbeGame.StartAsync(scenes =>
            scenes.RegisterJson("""{"name":"broken","entities":[{"id":"e","prefab":"dot","position":[0,0],"values":{"v":-1}}]}""")));
        Assert.Equal(SceneErrorCode.OutOfRange, error.Code);
        Assert.Contains("v=-1", error.Message);
    }

    [Fact]
    public async Task Loading_an_unknown_scene_or_registering_twice_is_refused()
    {
        var (_, game) = await ProbeGame.StartInAlphaAsync();
        Assert.Equal(SceneErrorCode.UnknownScene, Assert.Throws<SceneException>(() => game.Scenes.LoadScene("gamma")).Code);
        Assert.Equal(SceneErrorCode.DuplicateScene, Assert.Throws<SceneException>(() => game.Scenes.RegisterJson("""{"name":"alpha","entities":[]}""")).Code);
        Assert.Equal(SceneErrorCode.DuplicatePrefab, Assert.Throws<SceneException>(() => game.Scenes.Register(new Prefab<Probe>("dot", args => new Probe(args.Id, args.Position, 0)))).Code);
        Assert.Equal("alpha", game.Scenes.ActiveScene);
    }

    [Fact]
    public void Prefab_parameter_defaults_outside_their_range_are_refused()
    {
        var scenes = new SceneRegistry<Probe>(new GameContext());
        var error = Assert.Throws<SceneException>(() => scenes.Register(new Prefab<Probe>("bad", args => new Probe(args.Id, args.Position, 0)) { Parameters = [new PrefabParameter("v", 0, 1, 2)] }));
        Assert.Equal(SceneErrorCode.OutOfRange, error.Code);
    }

    [Fact]
    public void A_game_gets_one_scene_registry()
    {
        var context = new GameContext();
        _ = new SceneRegistry<Probe>(context);
        Assert.Throws<InvalidOperationException>(() => new SceneRegistry<Probe>(context));
    }

    [Fact]
    public async Task Reading_an_undeclared_parameter_in_a_prefab_function_throws_at_load()
    {
        var error = await Assert.ThrowsAsync<SceneException>(() => ProbeGame.StartAsync(scenes =>
        {
            scenes.Register(new Prefab<Probe>("typo", args => new Probe(args.Id, args.Position, args["vv"])) { Parameters = [new PrefabParameter("v", 0, 1, 0)] });
            scenes.RegisterJson("""{"name":"s","entities":[{"id":"e","prefab":"typo","position":[0,0]}]}""");
            scenes.LoadScene("s");
        }));
        Assert.Equal(SceneErrorCode.UnknownParameter, error.Code);
    }

    [Fact]
    public async Task Games_without_a_registry_keep_their_snapshot_json_unchanged()
    {
        var game = new LanternRunGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", new RenderExtent(640, 360, 1f)));
        runtime.StepTo(10);

        Assert.False(runtime.HasScenes);
        Assert.Null(runtime.SceneState);
        Assert.DoesNotContain("\"scene\"", runtime.SnapshotJson());
        Assert.Throws<InvalidOperationException>(() => runtime.SubmitPatch(new ScenePatch("p", 20, [new DisposeOp("player")])));
    }
}
