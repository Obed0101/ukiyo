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

    /// <summary>
    /// Initialize the renderer with the game's <see cref="IGame.Profile"/> (hosts do not choose it), then the game,
    /// then upload the game's resources. A renderer that comes up on a lower profile fails with UnsupportedFeature.
    /// </summary>
    public async ValueTask StartAsync(IRenderer renderer, RenderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _renderer = renderer;
        Viewport = configuration.InitialExtent;
        await renderer.InitializeAsync(configuration with { Profile = Game.Profile }, cancellationToken);
        if (!renderer.Capabilities.Profile.Includes(Game.Profile))
        {
            throw new RenderException(RenderErrorCode.UnsupportedFeature, $"{renderer.Capabilities.RendererName} runs {renderer.Capabilities.Profile}; {Game.Name} needs {Game.Profile}");
        }

        Game.Initialize(_context);
        _context.Scenes?.AtBoundary(Tick);
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
        var scenes = _context.Scenes;
        for (var i = 0; i < ticks; i++)
        {
            Script?.Feed(Tick, Input);
            var input = Input.Latch(Tick, Viewport);
            _sounds.Begin(Tick);
            scenes?.BeginUpdate(Tick);
            Game.Update(new TickInfo(Tick, Simulation.TickSeconds) { Input = input, Audio = _sounds });
            foreach (var sound in _sounds.Events)
            {
                SoundLog?.Add(sound);
                Audio?.Play(sound, _context.Sound(sound.Sound));
            }

            Audio?.EndTick(Tick);
            Tick++;

            // Tick boundary: scene loads requested during the tick and patches due at the new tick apply here, so a
            // snapshot at tick N already shows them and Update(N) runs on the changed world.
            scenes?.AtBoundary(Tick);
        }
    }

    /// <summary>Whether the game created a <see cref="SceneRegistry{TEntity}"/> (and so accepts patches).</summary>
    public bool HasScenes => _context.Scenes is not null;

    /// <summary>Active scene and patch history, or null for a game without scenes.</summary>
    public SceneState? SceneState => _context.Scenes?.State();

    /// <summary>
    /// Submits a patch to the game's scene registry: applied at the boundary before tick <c>patch.Tick</c> (now, when
    /// paused exactly there). The one entry point for tests, the dev bridge, agent tools and reload.
    /// </summary>
    public void SubmitPatch(ScenePatch patch) =>
        (_context.Scenes ?? throw new InvalidOperationException($"[RUNTIME]: {Game.Name} has no SceneRegistry, so it cannot take patches")).Submit(patch);

    public void StepTo(long tick)
    {
        if (tick < Tick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"[RUNTIME]: cannot step back from {Tick}");
        }

        Step((int)(tick - Tick));
    }

    /// <summary>
    /// The packets of the last <see cref="RenderFrame"/>: one for a single-camera frame, one per pass otherwise.
    /// </summary>
    public IReadOnlyList<RenderPacket> LastFrame { get; private set; } = [];

    /// <summary>Passes of the last frame (1 for a single-camera frame).</summary>
    public int PassCount => LastFrame.Count is 0 ? 1 : LastFrame[0].Pass?.Count ?? 1;

    /// <summary>The pass tools preview as the game view (inspector, dev bridge). 0 for a single-camera frame.</summary>
    public int PrimaryPass => LastFrame.Count is 0 ? 0 : LastFrame[0].Pass?.PrimaryId ?? 0;

    /// <summary>
    /// Extracts the frame and renders each of its packets in pass order. Returns the primary pass's packet (the only one
    /// for a single-camera game); <see cref="LastFrame"/> has all of them.
    /// </summary>
    public RenderPacket RenderFrame(RenderExtent viewport)
    {
        FlushResources();
        Viewport = viewport;
        var builder = new FrameBuilder { DrawingBuffer = viewport };
        Game.Extract(builder);
        var packets = builder.Build(ref _sequence, Tick, viewport);
        foreach (var packet in packets)
        {
            if (packet.Sprites.Count > 0 && !Renderer.Capabilities.SupportsSprites)
            {
                throw new RenderException(RenderErrorCode.UnsupportedFeature, $"{Renderer.Capabilities.RendererName} does not draw sprites; {packet.Sprites.Count} were submitted");
            }

            if (packet.Pass is { } pass && !Renderer.Capabilities.SupportsPasses)
            {
                throw new RenderException(RenderErrorCode.UnsupportedFeature, $"{Renderer.Capabilities.RendererName} does not draw multi-camera frames; {Game.Name} built {pass.Count} pass(es)");
            }

            RenderValidation.RequireProfile(Renderer.Capabilities.Profile, packet);
        }

        foreach (var packet in packets)
        {
            Renderer.Render(packet);
        }

        LastFrame = packets;
        return packets[PrimaryPass];
    }

    /// <summary>Renders the current tick again and reads the whole frame back, when the renderer can. Returns null otherwise.</summary>
    public ValueTask<CaptureResult?> CaptureAsync(CancellationToken cancellationToken = default) => CaptureAsync(pass: null, cancellationToken);

    /// <summary>
    /// Renders the current tick again and reads back pass <paramref name="pass"/> alone at its rect size (null: the whole
    /// frame). Returns null when the renderer cannot capture.
    /// </summary>
    public async ValueTask<CaptureResult?> CaptureAsync(int? pass, CancellationToken cancellationToken = default)
    {
        if (Renderer is not IRenderCapture capture)
        {
            return null;
        }

        RenderFrame(Viewport);
        return await capture.CaptureAsync(new CaptureRequest(Tick) { Pass = pass }, cancellationToken);
    }

    public GameSnapshot Snapshot()
    {
        var snapshot = Game.Snapshot(Tick);
        return _context.Scenes is { } scenes ? snapshot with { Scene = scenes.State() } : snapshot;
    }

    public string SnapshotJson() => SnapshotWriter.Write(Snapshot(), Source, Renderer.Capabilities);

    private void FlushResources()
    {
        var batch = _context.TakePending();
        if (batch is not null)
        {
            RenderValidation.RequireProfile(Renderer.Capabilities.Profile, batch);
            Renderer.ApplyResources(batch);
        }
    }
}

/// <summary>
/// Stable JSON for snapshots: parity tooling compares these across targets. Keys are sorted ordinally and zeros are
/// written as +0 (<see cref="DetMath.PositiveZero(float)"/>), so a sign-of-zero difference between CPUs never reaches the text.
/// </summary>
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
                    json.WriteNumber(name, DetMath.PositiveZero(value));
                }

                json.WriteEndObject();
            }

            if (snapshot.Scene is { } scene)
            {
                WriteScene(json, scene);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Only games with scenes get this key, so snapshots of every other game stay byte-identical.</summary>
    private static void WriteScene(Utf8JsonWriter json, SceneState scene)
    {
        json.WriteStartObject("scene");
        if (scene.Active is null)
        {
            json.WriteNull("active");
        }
        else
        {
            json.WriteString("active", scene.Active);
        }

        json.WriteNumber("loadedAt", scene.LoadedAt);
        // Bounded history (docs/scenes-patches.md): counts and hash chains cover the whole session, the lists only the
        // last PatchHistory.Window entries, so snapshot size does not grow with the number of patches.
        json.WriteNumber("appliedCount", scene.AppliedCount);
        json.WriteString("appliedHash", PatchHistory.Format(scene.AppliedHash));
        json.WriteStartArray("patches");
        foreach (var patch in scene.Applied)
        {
            json.WriteStartObject();
            json.WriteString("id", patch.Id);
            json.WriteNumber("tick", patch.Tick);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteNumber("rejectedCount", scene.RejectedCount);
        json.WriteString("rejectedHash", PatchHistory.Format(scene.RejectedHash));
        json.WriteStartArray("rejected");
        foreach (var rejection in scene.Rejected)
        {
            json.WriteStartObject();
            json.WriteString("id", rejection.Id);
            json.WriteNumber("tick", rejection.Tick);
            json.WriteString("code", rejection.Code.ToString());
            json.WriteString("message", rejection.Message);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter json, string name, params float[] values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteNumberValue(DetMath.PositiveZero(value));
        }

        json.WriteEndArray();
    }
}
