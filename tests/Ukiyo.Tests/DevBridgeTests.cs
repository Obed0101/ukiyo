using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ukiyo.DevBridge;
using Xunit;

namespace Ukiyo.Tests;

public sealed class DevBridgeTests
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

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
