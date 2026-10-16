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
        }
        finally
        {
            session.Dispose();
        }

        Assert.False(File.Exists(discovery));
    }
}
