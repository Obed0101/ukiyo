using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ukiyo.DevBridge;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.LanternRun;
using Xunit;

namespace Ukiyo.Tests;

public sealed class DevBridgeTests
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task Bridge_recording_replays_LanternRun_state_sound_and_pixels_headless(int idleTicks)
    {
        var extent = new RenderExtent(640, 360, 1f);
        var source = new SourceIdentity(new Dictionary<string, string>());
        var game = new LanternRunGame();
        var live = new GameRuntime(game, source) { SoundLog = [] };
        using var liveRenderer = new SoftwareRenderer();
        await live.StartAsync(liveRenderer, new RenderConfiguration("record", extent));
        using var session = DevBridgeServer.TryStart(live, "test")
            ?? throw new InvalidOperationException("dev bridge disabled by UKIYO_DEV_BRIDGE");
        var server = (DevBridgeServer)session;
        var discovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ukiyo", "live", $"{Environment.ProcessId}.json");
        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(discovery));
        var token = file.RootElement.GetProperty("token").GetString()!;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var reader = new StreamReader(client.GetStream());
        await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\n" };
        long id = 0;

        async Task<JsonElement> Ask(string command, Dictionary<string, object>? args = null)
        {
            var request = args ?? new Dictionary<string, object>();
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
            using var answer = JsonDocument.Parse(await line ?? "{}");
            Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
            return answer.RootElement.GetProperty("result").Clone();
        }

        Task<JsonElement> Step(int ticks) => Ask("step", new() { ["ticks"] = ticks });
        Task<JsonElement> KeyEvent(string key, bool down) => Ask("input", new()
        {
            ["events"] = new[] { new { key, down } },
        });

        // Let the immediate-mode title establish keyboard focus before pressing Enter.
        // A delayed recording retains absolute ticks; prior ticks receive no input.
        await Step(idleTicks + 1);
        await Ask("record", new() { ["on"] = true });
        await KeyEvent("Enter", true);
        await Step(1);
        await KeyEvent("Enter", false);
        await KeyEvent("Right", true);
        await Step(90);
        await KeyEvent("Right", false);
        await Step(30);
        var recorded = await Ask("record", new() { ["on"] = false });
        Assert.False(recorded.GetProperty("recording").GetBoolean());
        Assert.Equal(4, recorded.GetProperty("events").GetInt32());
        var script = InputScript.Parse(recorded.GetProperty("script").GetRawText());
        Assert.Equal(new long[] { idleTicks + 1, idleTicks + 2, idleTicks + 2, idleTicks + 92 }, script.Events.Select(e => e.Tick));
        Assert.Equal(RunState.Playing, game.State);
        Assert.True(game.Player.Position.X > LanternRunGame.Spawn.X + 5);
        Assert.True(game.Score >= 100);
        Assert.NotEmpty(live.SoundLog!);

        var replay = new GameRuntime(new LanternRunGame(), source) { Script = script, SoundLog = [] };
        using var replayRenderer = new SoftwareRenderer();
        await replay.StartAsync(replayRenderer, new RenderConfiguration("replay", extent));
        replay.StepTo(live.Tick);
        Assert.Equal(live.SnapshotJson(), replay.SnapshotJson());
        Assert.Equal(live.SoundLog!.ToArray(), replay.SoundLog!.ToArray());
        var liveCapture = await live.CaptureAsync();
        var replayCapture = await replay.CaptureAsync();
        Assert.NotNull(liveCapture);
        Assert.NotNull(replayCapture);
        Assert.Equal(CaptureStatus.Captured, liveCapture.Status);
        Assert.Equal(CaptureStatus.Captured, replayCapture.Status);
        Assert.Equal(liveCapture.Png, replayCapture.Png);
    }

    [Fact]
    public async Task Bridge_needs_the_discovery_token_then_answers_status_step_and_errors()
    {
        var (runtime, _) = await CoreTests.StartCubeAsync();
        var session = DevBridgeServer.TryStart(runtime, "test") ?? throw new InvalidOperationException("dev bridge disabled by UKIYO_DEV_BRIDGE");
        var server = (DevBridgeServer)session;
        var discovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ukiyo", "live", $"{Environment.ProcessId}.json");
        try
        {
            string token;
            using (var file = JsonDocument.Parse(await File.ReadAllTextAsync(discovery)))
            {
                token = file.RootElement.GetProperty("token").GetString()!;
                Assert.Equal(server.Port, file.RootElement.GetProperty("port").GetInt32());
                Assert.Equal("RotatingCube", file.RootElement.GetProperty("game").GetString());
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(discovery));
            }

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            using var reader = new StreamReader(client.GetStream());
            await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\n" };

            // The host thread pumps the bridge once per frame; the test plays that role while it waits.
            async Task<JsonElement> Ask(Dictionary<string, object> request)
            {
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

            var refused = await Ask(new Dictionary<string, object> { ["id"] = 1, ["cmd"] = "status", ["token"] = "wrong" });
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Contains("token", refused.GetProperty("error").GetString());

            var status = await Ask(new Dictionary<string, object> { ["id"] = 2, ["cmd"] = "status", ["token"] = token });
            Assert.True(status.GetProperty("ok").GetBoolean());
            Assert.Equal(2, status.GetProperty("id").GetInt64());
            Assert.Equal(0, status.GetProperty("result").GetProperty("tick").GetInt64());
            Assert.Equal("NullRenderer", status.GetProperty("result").GetProperty("renderer").GetString());

            var stepped = await Ask(new Dictionary<string, object> { ["id"] = 3, ["cmd"] = "step", ["ticks"] = 10, ["token"] = token });
            Assert.Equal(10, stepped.GetProperty("result").GetProperty("tick").GetInt64());
            Assert.Equal(10, runtime.Tick);
            Assert.True(runtime.Paused);

            var tooFar = await Ask(new Dictionary<string, object> { ["id"] = 4, ["cmd"] = "step", ["ticks"] = 999_999, ["token"] = token });
            Assert.False(tooFar.GetProperty("ok").GetBoolean());
            Assert.Equal(10, runtime.Tick);

            var unknown = await Ask(new Dictionary<string, object> { ["id"] = 5, ["cmd"] = "explode", ["token"] = token });
            Assert.Contains("use status, pause, resume, step", unknown.GetProperty("error").GetString());

            var noScenes = await Ask(new Dictionary<string, object> { ["id"] = 6, ["cmd"] = "patch", ["token"] = token, ["patch"] = Json("""{"id":"p","tick":20,"ops":[{"op":"dispose","id":"cube"}]}""") });
            Assert.False(noScenes.GetProperty("ok").GetBoolean());
            Assert.Equal("[RUNTIME]: RotatingCube has no SceneRegistry, so it cannot take patches", noScenes.GetProperty("error").GetString());
        }
        finally
        {
            session.Dispose();
        }

        Assert.False(File.Exists(discovery));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Bridge_patch_queues_or_applies_with_the_submit_rules_and_needs_the_token()
    {
        var (runtime, game) = await SceneTests.StartSceneHopAsync();
        var session = DevBridgeServer.TryStart(runtime, "test") ?? throw new InvalidOperationException("dev bridge disabled by UKIYO_DEV_BRIDGE");
        var server = (DevBridgeServer)session;
        var discovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ukiyo", "live", $"{Environment.ProcessId}.json");
        try
        {
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

            async Task<JsonElement> Ask(string command, Dictionary<string, object>? args = null, string? withToken = null)
            {
                var request = args ?? new Dictionary<string, object>();
                request["id"] = ++id;
                request["cmd"] = command;
                request["token"] = withToken ?? token;
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

            Task<JsonElement> Patch(string json, string? withToken = null) => Ask("patch", new() { ["patch"] = Json(json) }, withToken);
            static string Refusal(JsonElement answer)
            {
                Assert.False(answer.GetProperty("ok").GetBoolean());
                return answer.GetProperty("error").GetString()!;
            }

            // Without the discovery token nothing is submitted, and the id stays free.
            const string spawn = """{"id":"p1","tick":12,"ops":[{"op":"spawn","id":"bat-3","prefab":"drifter","position":[-5,-1],"values":{"speed":3}}]}""";
            Assert.Contains("token", Refusal(await Patch(spawn, withToken: "wrong")));
            Assert.Empty(game.Scenes.PendingPatches);

            var queued = await Patch(spawn);
            Assert.True(queued.GetProperty("ok").GetBoolean());
            var result = queued.GetProperty("result");
            Assert.Equal(("p1", 12, "queued", 0), (result.GetProperty("id").GetString(), result.GetProperty("tick").GetInt64(), result.GetProperty("status").GetString(), result.GetProperty("currentTick").GetInt64()));
            Assert.Equal("p1", Assert.Single(game.Scenes.PendingPatches).Id);

            var stepped = (await Ask("step", new() { ["ticks"] = 12 })).GetProperty("result");
            Assert.True(stepped.GetProperty("entities").TryGetProperty("bat-3", out _));
            Assert.Equal("p1", stepped.GetProperty("scene").GetProperty("patches")[0].GetProperty("id").GetString());

            // Paused exactly at boundary 12: a patch for 12 applies now, as GameRuntime.SubmitPatch does.
            var now = (await Patch("""{"id":"dim","tick":12,"ops":[{"op":"retarget","id":"lamp-east","values":{"glow":0.25}}]}""")).GetProperty("result");
            Assert.Equal("applied", now.GetProperty("status").GetString());
            Assert.Equal(0.25f, game.Scenes["lamp-east"].Glow);

            // Submit-time refusals: the typed message, nothing recorded.
            Assert.StartsWith("[SCENE:UnknownPrefab]:", Refusal(await Patch("""{"id":"p2","tick":30,"ops":[{"op":"spawn","id":"x","prefab":"dragon","position":[0,0]}]}""")));
            Assert.StartsWith("[SCENE:PastTick]:", Refusal(await Patch("""{"id":"p3","tick":3,"ops":[{"op":"dispose","id":"moth"}]}""")));
            Assert.StartsWith("[SCENE:DuplicatePatch]:", Refusal(await Patch(spawn)));
            Assert.StartsWith("[SCENE:Malformed]:", Refusal(await Patch("""{"id":"p4","tick":30,"ops":[]}""")));
            Assert.StartsWith("patch needs \"patch\"", Refusal(await Ask("patch")));
            Assert.Empty(game.Scenes.PendingPatches);

            // The world refuses at the current boundary: thrown back and recorded, like a queued patch refused at its tick.
            Assert.StartsWith("[SCENE:UnknownEntity]:", Refusal(await Patch("""{"id":"ghost","tick":12,"ops":[{"op":"dispose","id":"nobody"}]}""")));

            // Queued now, refused at tick 80 because the cavern (live from 60) has no moth.
            Assert.Equal("queued", (await Patch("""{"id":"p5","tick":80,"ops":[{"op":"dispose","id":"moth"}]}""")).GetProperty("result").GetProperty("status").GetString());
            var at80 = (await Ask("step", new() { ["ticks"] = 68 })).GetProperty("result").GetProperty("scene");
            Assert.Equal(["p1", "dim"], at80.GetProperty("patches").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
            Assert.Equal([("ghost", 12L, "UnknownEntity"), ("p5", 80L, "UnknownEntity")], at80.GetProperty("rejected").EnumerateArray().Select(r => (r.GetProperty("id").GetString(), r.GetProperty("tick").GetInt64(), r.GetProperty("code").GetString())));
        }
        finally
        {
            session.Dispose();
        }

        Assert.False(File.Exists(discovery));
    }

    [Fact]
    public async Task Bridge_capture_takes_a_pass_id_or_primary_for_multi_camera_games()
    {
        var extent = new RenderExtent(320, 200, 1f);
        var runtime = new GameRuntime(new Ukiyo.Samples.Minimap.MinimapGame(), new SourceIdentity(new Dictionary<string, string>()));
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent));
        runtime.RenderFrame(extent);
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
        async Task<JsonElement> Capture(object? pass)
        {
            var request = new Dictionary<string, object> { ["id"] = ++id, ["cmd"] = "capture", ["token"] = token };
            if (pass is not null)
            {
                request["pass"] = pass;
            }

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

        var map = Ukiyo.Samples.Minimap.MinimapGame.MinimapRect(extent);
        var whole = (await Capture(null)).GetProperty("result");
        Assert.Equal((320, 200), (whole.GetProperty("width").GetInt32(), whole.GetProperty("height").GetInt32()));
        Assert.False(whole.TryGetProperty("pass", out _));

        var primary = (await Capture("primary")).GetProperty("result");
        Assert.Equal((0, 2, 0), (primary.GetProperty("pass").GetInt32(), primary.GetProperty("passes").GetInt32(), primary.GetProperty("primaryPass").GetInt32()));
        Assert.Equal(320, primary.GetProperty("width").GetInt32());

        var minimap = (await Capture(1)).GetProperty("result");
        Assert.Equal((map.Width, map.Height), (minimap.GetProperty("width").GetInt32(), minimap.GetProperty("height").GetInt32()));

        var missing = await Capture(2);
        Assert.False(missing.GetProperty("ok").GetBoolean());
        Assert.Contains("OutOfRange", missing.GetProperty("error").GetString());
        Assert.False((await Capture("main")).GetProperty("ok").GetBoolean());
    }
}
