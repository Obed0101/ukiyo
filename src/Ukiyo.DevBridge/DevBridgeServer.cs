using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ukiyo.DevBridge;

/// <summary>
/// Lets an agent (through the ukiyo MCP server) or the Studio drive a running development build: status, pause,
/// resume, step, snapshot, capture, inject input and record replays. Newline-delimited JSON over TCP on 127.0.0.1.
/// Every request carries the token from the discovery file <c>~/.ukiyo/live/&lt;pid&gt;.json</c> (mode 0600), so other
/// local processes cannot drive the game. Requests run on the host thread inside <see cref="Pump"/>, never concurrently
/// with a tick or a frame.
/// <para>Request: <c>{"token":"…","id":1,"cmd":"step","ticks":10}</c>. Response: <c>{"id":1,"ok":true,"result":{…}}</c>.</para>
/// <para>Disable with <c>UKIYO_DEV_BRIDGE=0</c>; pick a port with <c>UKIYO_DEV_BRIDGE_PORT</c> (default: any free port).</para>
/// </summary>
public sealed class DevBridgeServer : IDevSession
{
    private const int MaxRequestsPerPump = 32;
    private const int MaxLineBytes = 1 << 20;

    private readonly GameRuntime _runtime;
    private readonly string _target;
    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly string _discoveryFile;
    private readonly ConcurrentQueue<PendingRequest> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    private DevBridgeServer(GameRuntime runtime, string target, int port)
    {
        _runtime = runtime;
        _target = target;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _discoveryFile = WriteDiscoveryFile();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>Factory registered with <see cref="DevHooks"/>. Returns null when disabled by environment.</summary>
    public static IDevSession? TryStart(GameRuntime runtime, string target)
    {
        if (Environment.GetEnvironmentVariable("UKIYO_DEV_BRIDGE") is "0" or "false" or "off")
        {
            return null;
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("UKIYO_DEV_BRIDGE_PORT"), out var p) ? p : 0;
        try
        {
            var server = new DevBridgeServer(runtime, target, port);
            Console.WriteLine($"[ukiyo] dev bridge on 127.0.0.1:{server.Port} (discovery {server._discoveryFile})");
            return server;
        }
        catch (SocketException error)
        {
            Console.Error.WriteLine($"[ukiyo] dev bridge disabled: {error.Message}");
            return null;
        }
    }

    public void Pump()
    {
        for (var i = 0; i < MaxRequestsPerPump && _queue.TryDequeue(out var request); i++)
        {
            string response;
            try
            {
                response = Execute(request.Id, request.Document.RootElement);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                response = Error(request.Id, $"{error.GetType().Name}: {error.Message}");
            }
            finally
            {
                request.Document.Dispose();
            }

            request.Completion.TrySetResult(response);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        while (_queue.TryDequeue(out var pending))
        {
            pending.Document.Dispose();
            pending.Completion.TrySetResult(Error(pending.Id, "game exited"));
        }

        try
        {
            File.Delete(_discoveryFile);
        }
        catch (IOException)
        {
        }

        _stop.Dispose();
    }

    private string Execute(long id, JsonElement request)
    {
        var command = request.TryGetProperty("cmd", out var cmd) ? cmd.GetString() : null;
        switch (command)
        {
            case "status":
                return Ok(id, json => WriteStatus(json));
            case "pause":
                _runtime.Paused = true;
                return Ok(id, json => WriteStatus(json));
            case "resume":
                _runtime.Paused = false;
                return Ok(id, json => WriteStatus(json));
            case "step":
            {
                var ticks = request.TryGetProperty("ticks", out var t) ? t.GetInt32() : 1;
                if (ticks is < 0 or > 36_000)
                {
                    return Error(id, "ticks must be within 0..36000 (10 minutes)");
                }

                _runtime.Paused = true;
                _runtime.Step(ticks);
                return Ok(id, json => json.WriteRawValue(_runtime.SnapshotJson()));
            }

            case "snapshot":
                return Ok(id, json => json.WriteRawValue(_runtime.SnapshotJson()));
            case "capture":
            {
                var capture = _runtime.CaptureAsync().AsTask().GetAwaiter().GetResult();
                if (capture is null)
                {
                    return Error(id, $"{_runtime.Renderer.Capabilities.RendererName} cannot capture");
                }

                return Ok(id, json =>
                {
                    json.WriteStartObject();
                    json.WriteNumber("tick", _runtime.Tick);
                    json.WriteNumber("width", capture.Width);
                    json.WriteNumber("height", capture.Height);
                    json.WriteString("backend", capture.Backend);
                    json.WriteString("png", Convert.ToBase64String(capture.Png));
                    json.WriteEndObject();
                });
            }

            case "input":
            {
                if (!request.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                {
                    return Error(id, "input needs \"events\": [{\"key\":\"Space\",\"down\":true}, {\"pointer\":[x,y]}, …]");
                }

                var count = 0;
                foreach (var item in events.EnumerateArray())
                {
                    _runtime.Input.Push(InputScript.ParseAction(item));
                    count++;
                }

                return Ok(id, json =>
                {
                    json.WriteStartObject();
                    json.WriteNumber("queued", count);
                    json.WriteNumber("tick", _runtime.Tick);
                    json.WriteEndObject();
                });
            }

            case "record":
            {
                var on = request.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
                var recorded = _runtime.Input.Recorded.ToArray();
                _runtime.Input.Recording = on;
                return Ok(id, json =>
                {
                    json.WriteStartObject();
                    json.WriteBoolean("recording", on);
                    json.WriteNumber("events", recorded.Length);
                    json.WritePropertyName("script");
                    // Responses are newline-delimited: the script's pretty-printing newlines must not reach the wire.
                    json.WriteRawValue(new InputScript(recorded).ToJson().ReplaceLineEndings(string.Empty));
                    json.WriteEndObject();
                });
            }

            default:
                return Error(id, $"unknown cmd \"{command}\"; use status, pause, resume, step, snapshot, capture, input, record");
        }
    }

    private void WriteStatus(Utf8JsonWriter json)
    {
        var caps = _runtime.Renderer.Capabilities;
        json.WriteStartObject();
        json.WriteString("game", _runtime.Game.Name);
        json.WriteString("target", _target);
        json.WriteNumber("tick", _runtime.Tick);
        json.WriteBoolean("paused", _runtime.Paused);
        json.WriteNumber("droppedTicks", _runtime.DroppedTicks);
        json.WriteString("renderer", caps.RendererName);
        json.WriteString("backend", caps.Backend);
        json.WriteBoolean("capture", caps.SupportsCapture);
        json.WriteNumber("width", _runtime.Viewport.Width);
        json.WriteNumber("height", _runtime.Viewport.Height);
        json.WriteEndObject();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            while (!_stop.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    return;
                }

                if (line is null)
                {
                    return;
                }

                if (line.Length > MaxLineBytes)
                {
                    await writer.WriteLineAsync(Error(0, "request too large"));
                    return;
                }

                await writer.WriteLineAsync(await Enqueue(line));
            }
        }
    }

    private Task<string> Enqueue(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException error)
        {
            return Task.FromResult(Error(0, $"invalid JSON: {error.Message}"));
        }

        var root = document.RootElement;
        var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var value) ? value : 0;
        var token = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token ?? ""), Encoding.UTF8.GetBytes(_token)))
        {
            document.Dispose();
            return Task.FromResult(Error(id, "bad or missing token (read it from the discovery file)"));
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new PendingRequest(id, document, completion));
        return completion.Task;
    }

    private string WriteDiscoveryFile()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ukiyo", "live");
        var path = Path.Combine(directory, $"{Environment.ProcessId}.json");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteNumber("port", Port);
            json.WriteString("token", _token);
            json.WriteString("game", _runtime.Game.Name);
            json.WriteString("target", _target);
            json.WriteString("startedAt", DateTimeOffset.UtcNow.ToString("O"));
            json.WriteStartObject("source");
            foreach (var (file, hash) in _runtime.Source.Files)
            {
                json.WriteString(file, hash);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        // The file holds the token: create it owner-only from the first byte instead of tightening it afterwards.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var file = new FileStream(path, options))
        {
            file.Write(stream.ToArray());
        }

        if (!OperatingSystem.IsWindows())
        {
            // A stale file from a recycled pid keeps its old mode on FileMode.Create; enforce it.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static string Ok(long id, Action<Utf8JsonWriter> writeResult) => Respond(id, true, json =>
    {
        json.WritePropertyName("result");
        writeResult(json);
    });

    private static string Error(long id, string message) => Respond(id, false, json => json.WriteString("error", message));

    private static string Respond(long id, bool ok, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("id", id);
            json.WriteBoolean("ok", ok);
            body(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record PendingRequest(long Id, JsonDocument Document, TaskCompletionSource<string> Completion);
}
