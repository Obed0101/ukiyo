using System.Text.Json;
using Ukiyo.Hosting;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.RotatingCube;
using Ukiyo.Samples.SceneHop;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>The headless host's --patches flag, run in process with its output captured.</summary>
public sealed class HeadlessPatchTests : IDisposable
{
    // Spawns, retargets and disposes; p4 is refused at tick 80 because the cavern (live from 60) has no moth.
    private const string SceneHopLog = """
        [
          {"id":"p1","tick":12,"ops":[{"op":"spawn","id":"bat-3","prefab":"drifter","position":[-5,-1],"values":{"speed":3}}]},
          {"id":"p2","tick":40,"ops":[{"op":"retarget","id":"lamp-east","position":[2,1],"values":{"glow":0.25}}]},
          {"id":"p3","tick":75,"scene":"cavern","ops":[{"op":"dispose","id":"bat-2"},{"op":"retarget","id":"bat-1","values":{"speed":6}}]},
          {"id":"p4","tick":80,"ops":[{"op":"dispose","id":"moth"}]}
        ]
        """;

    private const string InputJson = """[{"tick":5,"key":"Space","down":true},{"tick":9,"key":"Space","down":false}]""";

    private static readonly long[] Checkpoints = [0, 12, 30, 40, 59, 60, 75, 80, 119, 120];

    private static readonly SourceIdentity Source = new(new Dictionary<string, string>());

    private readonly string _directory = Directory.CreateTempSubdirectory("ukiyo-headless-patches-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int Code, string[] Snapshots, string Stdout, string Stderr)> RunHeadless(IGame game, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new HeadlessHost(HeadlessOptions.Parse(args), stdout, stderr);
        var code = await host.RunAsync(new GameRuntime(game, Source));
        var lines = stdout.ToString().Split('\n', StringSplitOptions.TrimEntries);
        return (code, [.. lines.Where(line => line.StartsWith("snapshot ", StringComparison.Ordinal)).Select(line => line["snapshot ".Length..])], stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void The_patches_flag_names_a_log_and_needs_a_value()
    {
        Assert.Equal("log.json", HeadlessOptions.Parse(["--checkpoints", "0,60", "--patches", "log.json"]).PatchLog);
        Assert.Null(HeadlessOptions.Parse(["--checkpoints", "0"]).PatchLog);
        var missing = Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse(["--patches"]));
        Assert.StartsWith("[HEADLESS]:", missing.Message);
    }

    [Fact]
    public async Task A_patch_log_with_input_and_checkpoints_gives_the_same_snapshots_as_the_csharp_api()
    {
        var log = Write("patches.json", SceneHopLog);
        var input = Write("input.json", InputJson);
        var (code, snapshots, stdout, stderr) = await RunHeadless(new SceneHopGame(), "--checkpoints", string.Join(',', Checkpoints), "--input", input, "--patches", log);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains($"patches {Path.GetFullPath(log)} submitted=4", stdout);

        // The same log through GameRuntime.SubmitPatch, queued up front, on the same renderer and extent.
        var runtime = new GameRuntime(new SceneHopGame(), Source) { Script = InputScript.Parse(InputJson) };
        var extent = new RenderExtent(1280, 720, 1f);
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("api", extent));
        foreach (var patch in ScenePatch.ParseLog(SceneHopLog))
        {
            runtime.SubmitPatch(patch);
        }

        var expected = new List<string>();
        foreach (var checkpoint in Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(extent);
            expected.Add(runtime.SnapshotJson());
        }

        Assert.Equal(expected, snapshots);
        var (again, repeated, _, _) = await RunHeadless(new SceneHopGame(), "--checkpoints", string.Join(',', Checkpoints), "--input", input, "--patches", log);
        Assert.Equal(0, again);
        Assert.Equal(snapshots, repeated);

        var scene = JsonDocument.Parse(snapshots[^1]).RootElement.GetProperty("scene");
        Assert.Equal(["p1", "p2", "p3"], scene.GetProperty("patches").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        var rejected = Assert.Single(scene.GetProperty("rejected").EnumerateArray());
        Assert.Equal(("p4", 80, "UnknownEntity"), (rejected.GetProperty("id").GetString(), rejected.GetProperty("tick").GetInt64(), rejected.GetProperty("code").GetString()));
        Assert.True(JsonDocument.Parse(snapshots[1]).RootElement.GetProperty("entities").TryGetProperty("bat-3", out _));
        Assert.False(JsonDocument.Parse(snapshots[6]).RootElement.GetProperty("entities").TryGetProperty("bat-2", out _));
    }

    [Theory]
    [InlineData("""[{"id":"p1","tick":5,"ops":[""", "Malformed")]
    [InlineData("""{"id":"p1","tick":5,"ops":[{"op":"dispose","id":"moth"}]}""", "Malformed")]
    [InlineData("""[{"id":"p1","tick":5,"ops":[{"op":"spawn","id":"x","prefab":"dragon","position":[0,0]}]}]""", "UnknownPrefab")]
    [InlineData("""[{"id":"p1","tick":5,"ops":[{"op":"spawn","id":"x","prefab":"drifter","position":[0,0],"values":{"speed":99}}]}]""", "OutOfRange")]
    [InlineData("""[{"id":"p1","tick":5,"ops":[{"op":"dispose","id":"moth"}]},{"id":"p1","tick":9,"ops":[{"op":"dispose","id":"lamp-west"}]}]""", "DuplicatePatch")]
    [InlineData("""[{"id":"p1","tick":5,"scene":"desert","ops":[{"op":"dispose","id":"moth"}]}]""", "UnknownScene")]
    public async Task An_invalid_patch_log_stops_the_run_before_any_snapshot_with_the_typed_message(string log, string code)
    {
        var (exit, snapshots, stdout, stderr) = await RunHeadless(new SceneHopGame(), "--checkpoints", "0,60", "--patches", Write("bad.json", log));

        Assert.Equal(HeadlessHost.PatchLogRefused, exit);
        Assert.NotEqual(0, exit);
        Assert.Empty(snapshots);
        Assert.DoesNotContain("patches ", stdout);
        Assert.Contains($"[SCENE:{code}]:", stderr);
        Assert.StartsWith("[HEADLESS]: --patches ", stderr);
    }

    [Fact]
    public async Task A_missing_log_or_a_game_without_scenes_is_refused_with_a_nonzero_exit()
    {
        var missing = await RunHeadless(new SceneHopGame(), "--checkpoints", "0", "--patches", Path.Combine(_directory, "nope.json"));
        Assert.Equal(HeadlessHost.PatchLogRefused, missing.Code);
        Assert.Contains("nope.json", missing.Stderr);

        var cube = await RunHeadless(new CubeGame(), "--checkpoints", "0", "--patches", Write("cube.json", """[{"id":"p","tick":1,"ops":[{"op":"dispose","id":"cube"}]}]"""));
        Assert.Equal(HeadlessHost.PatchLogRefused, cube.Code);
        Assert.Empty(cube.Snapshots);
        Assert.Contains("RotatingCube has no SceneRegistry", cube.Stderr);
    }

    [Fact]
    public async Task A_tick_zero_patch_the_world_refuses_is_recorded_and_the_run_goes_on()
    {
        var log = Write("zero.json", """[{"id":"ghost","tick":0,"ops":[{"op":"dispose","id":"nobody"}]},{"id":"dim","tick":0,"ops":[{"op":"retarget","id":"lamp-west","values":{"glow":0.1}}]}]""");
        var (code, snapshots, _, stderr) = await RunHeadless(new SceneHopGame(), "--checkpoints", "0", "--patches", log);

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        var scene = JsonDocument.Parse(Assert.Single(snapshots)).RootElement.GetProperty("scene");
        Assert.Equal("dim", Assert.Single(scene.GetProperty("patches").EnumerateArray()).GetProperty("id").GetString());
        var rejected = Assert.Single(scene.GetProperty("rejected").EnumerateArray());
        Assert.Equal(("ghost", "UnknownEntity"), (rejected.GetProperty("id").GetString(), rejected.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task More_tick_zero_refusals_than_the_window_holds_are_still_recorded_not_a_bad_log()
    {
        var ghosts = Enumerable.Range(1, PatchHistory.Window + 4).Select(i => $$"""{"id":"ghost-{{i}}","tick":0,"ops":[{"op":"dispose","id":"nobody"}]}""");
        var (code, snapshots, stdout, stderr) = await RunHeadless(new SceneHopGame(), "--checkpoints", "0", "--patches", Write("ghosts.json", $"[{string.Join(',', ghosts)}]"));

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains($"submitted={PatchHistory.Window + 4}", stdout);
        var scene = JsonDocument.Parse(Assert.Single(snapshots)).RootElement.GetProperty("scene");
        Assert.Equal(PatchHistory.Window + 4, scene.GetProperty("rejectedCount").GetInt64());
        Assert.Equal(PatchHistory.Window, scene.GetProperty("rejected").GetArrayLength());
        Assert.Equal("ghost-5", scene.GetProperty("rejected")[0].GetProperty("id").GetString());
    }
}
