using System.Reflection;
using System.Text;
using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>A host for one target (desktop window, browser page, headless runner). Lives in the host assembly.</summary>
public interface IPlatformHost
{
    string Target { get; }

    Task<int> RunAsync(GameRuntime runtime);
}

public static class GameApplication
{
    public static Task<int> RunAsync(IGame game, IPlatformHost platform)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(platform);
        var runtime = new GameRuntime(game, SourceIdentity.FromEntryAssembly());
        Console.WriteLine($"[ukiyo] target={platform.Target} game={game.Name} {runtime.Source}");
        return platform.RunAsync(runtime);
    }
}

/// <summary>SHA-256 of the shared source files this build compiled (recorded by Directory.Build.targets).</summary>
public sealed record SourceIdentity(IReadOnlyDictionary<string, string> Files)
{
    public static SourceIdentity FromEntryAssembly()
    {
        var assembly = Assembly.GetEntryAssembly();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in assembly?.GetCustomAttributes<AssemblyMetadataAttribute>() ?? [])
        {
            if (attribute.Key.StartsWith("ukiyo.source:", StringComparison.Ordinal) && attribute.Value is not null)
            {
                files[attribute.Key["ukiyo.source:".Length..]] = attribute.Value;
            }
        }

        return new SourceIdentity(files);
    }

    public override string ToString() => string.Join(' ', Files.Select(file => $"{file.Key}={file.Value[..12]}"));
}

/// <summary>
/// Fixed-step accumulator. Caps catch-up per frame and counts the ticks it had to drop instead of hiding them.
/// </summary>
public sealed class FixedClock
{
    private double _accumulator;

    public int MaxCatchUpTicks { get; init; } = 5;

    public long DroppedTicks { get; private set; }

    public int Consume(double elapsedSeconds)
    {
        if (!(elapsedSeconds >= 0) || double.IsInfinity(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), elapsedSeconds, "[CLOCK]: elapsed time must be finite and non-negative");
        }

        _accumulator += elapsedSeconds;
        var due = (long)(_accumulator / Simulation.TickSeconds);
        _accumulator -= due * Simulation.TickSeconds;
        if (due > MaxCatchUpTicks)
        {
            DroppedTicks += due - MaxCatchUpTicks;
            return MaxCatchUpTicks;
        }

        return (int)due;
    }

    public void Reset() => _accumulator = 0;
}

/// <summary>Owns tick progression, resource flushing and frame extraction for one game on one renderer.</summary>
public sealed class GameRuntime(IGame game, SourceIdentity source)
{
    private readonly GameContext _context = new(GameAssets.FromAssembly(game.GetType().Assembly));
    private readonly FixedClock _clock = new();
    private readonly SoundQueue _sounds = new();
    private IRenderer? _renderer;
    private uint _sequence;

    public IGame Game { get; } = game;

    /// <summary>Hosts, the dev bridge and tests push input here; it is latched once per tick.</summary>
    public InputCollector Input { get; } = new();

    /// <summary>Scheduled input fed before each tick (headless agent runs and replays).</summary>
    public InputScript? Script { get; set; }

    /// <summary>Where the game's sounds go (SDL3 device, Web Audio, WAV recorder). Null plays nothing.</summary>
    public IAudioOutput? Audio { get; set; }

    /// <summary>When set, every sound event is appended here: tests and agents assert on sounds without a device.</summary>
    public List<SoundEvent>? SoundLog { get; set; }

    /// <summary>Drawing-buffer size of the last frame (or the initial extent before the first one).</summary>
    public RenderExtent Viewport { get; private set; }

    public SourceIdentity Source { get; } = source;

    public long Tick { get; private set; }

    public bool Paused { get; set; }

    public long DroppedTicks => _clock.DroppedTicks;

    public IRenderer Renderer => _renderer ?? throw new InvalidOperationException("[RUNTIME]: not started");

    /// <summary>Initialize the renderer, then the game, then upload the game's resources.</summary>
    public async ValueTask StartAsync(IRenderer renderer, RenderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _renderer = renderer;
        Viewport = configuration.InitialExtent;
        await renderer.InitializeAsync(configuration, cancellationToken);
        Game.Initialize(_context);
        FlushResources();
        var caps = renderer.Capabilities;
        Console.WriteLine($"[ukiyo] renderer={caps.RendererName} backend={caps.Backend} device=\"{caps.Device}\" profile={caps.Profile} capture={caps.SupportsCapture}");
    }

    /// <summary>Advance by wall-clock time through the fixed clock. Does nothing while paused.</summary>
    public int AdvanceTime(double elapsedSeconds)
    {
        var ticks = _clock.Consume(elapsedSeconds);
        if (Paused)
        {
            return 0;
        }

        Step(ticks);
        return ticks;
    }

    /// <summary>Advance an exact number of ticks, regardless of pause. Used by tests, agents and the step button.</summary>
    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var i = 0; i < ticks; i++)
        {
            Script?.Feed(Tick, Input);
            var input = Input.Latch(Tick, Viewport);
            _sounds.Begin(Tick);
            Game.Update(new TickInfo(Tick, Simulation.TickSeconds) { Input = input, Audio = _sounds });
            foreach (var sound in _sounds.Events)
            {
                SoundLog?.Add(sound);
                Audio?.Play(sound, _context.Sound(sound.Sound));
            }

            Audio?.EndTick(Tick);
            Tick++;
        }
    }

    public void StepTo(long tick)
    {
        if (tick < Tick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"[RUNTIME]: cannot step back from {Tick}");
        }

        Step((int)(tick - Tick));
    }

    public RenderPacket RenderFrame(RenderExtent viewport)
    {
        FlushResources();
        Viewport = viewport;
        var builder = new FrameBuilder { Viewport = viewport };
        Game.Extract(builder);
        var packet = builder.Build(++_sequence, Tick, viewport);
        if (packet.Sprites.Count > 0 && !Renderer.Capabilities.SupportsSprites)
        {
            throw new RenderException(RenderErrorCode.UnsupportedFeature, $"{Renderer.Capabilities.RendererName} does not draw sprites; {packet.Sprites.Count} were submitted");
        }

        Renderer.Render(packet);
        return packet;
    }

    /// <summary>Renders the current tick again and reads it back, when the renderer can. Returns null otherwise.</summary>
    public async ValueTask<CaptureResult?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (Renderer is not IRenderCapture capture)
        {
            return null;
        }

        RenderFrame(Viewport);
        return await capture.CaptureAsync(new CaptureRequest(Tick), cancellationToken);
    }

    public GameSnapshot Snapshot() => Game.Snapshot(Tick);

    public string SnapshotJson() => SnapshotWriter.Write(Snapshot(), Source, Renderer.Capabilities);

    private void FlushResources()
    {
        var batch = _context.TakePending();
        if (batch is not null)
        {
            Renderer.ApplyResources(batch);
        }
    }
}

/// <summary>Stable JSON for snapshots: parity tooling compares these across targets.</summary>
public static class SnapshotWriter
{
    public static string Write(GameSnapshot snapshot, SourceIdentity source, RenderCapabilities capabilities)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("tick", snapshot.Tick);
            json.WriteString("renderer", capabilities.RendererName);
            json.WriteString("backend", capabilities.Backend);
            json.WriteStartObject("source");
            foreach (var file in source.Files)
            {
                json.WriteString(file.Key, file.Value);
            }

            json.WriteEndObject();
            json.WriteStartObject("entities");
            foreach (var (name, pose) in snapshot.Entities.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(name);
                WriteArray(json, "position", pose.Position.X, pose.Position.Y, pose.Position.Z);
                WriteArray(json, "rotation", pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
                WriteArray(json, "scale", pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            if (snapshot.Values.Count > 0)
            {
                json.WriteStartObject("values");
                foreach (var (name, value) in snapshot.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    json.WriteNumber(name, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteArray(Utf8JsonWriter json, string name, params float[] values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteNumberValue(value);
        }

        json.WriteEndArray();
    }
}
