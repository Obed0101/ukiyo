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
    private bool _started;
    private bool _replaying;
    private TimeTravel? _timeline;

    public IGame Game { get; } = game;

    /// <summary>Hosts, the dev bridge and tests push input here; it is latched once per tick.</summary>
    public InputCollector Input { get; } = new();

    /// <summary>Everything from outside that reached this runtime since tick 0 (input, patches, viewport): see <see cref="ReplayLog"/>.</summary>
    public ReplayLog History { get; } = new();

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
        _started = true;
        CaptureIfDue();
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
            if (!_replaying)
            {
                Script?.Feed(Tick, Input);
                Input.History ??= History;
                History.NoteViewport(Tick, Viewport);
            }

            var input = Input.Latch(Tick, Viewport);
            _sounds.Begin(Tick);
            scenes?.BeginUpdate(Tick);
            Game.Update(new TickInfo(Tick, Simulation.TickSeconds) { Input = input, Audio = _sounds });
            if (!_replaying)
            {
                foreach (var sound in _sounds.Events)
                {
                    SoundLog?.Add(sound);
                    Audio?.Play(sound, _context.Sound(sound.Sound));
                }

                Audio?.EndTick(Tick);
            }

            Tick++;

            // Tick boundary: scene loads requested during the tick and patches due at the new tick apply here, so a
            // snapshot at tick N already shows them and Update(N) runs on the changed world.
            scenes?.AtBoundary(Tick);
            if (!_replaying)
            {
                CaptureIfDue();
            }
        }
    }

    /// <summary>Whether the game implements <see cref="ISaveable"/>, so it can be saved, restored and rewound.</summary>
    public bool CanSave => Game is ISaveable;

    /// <summary>
    /// The whole game state at the current tick boundary as bytes (docs/save-replay.md): tick, input state, scene
    /// registry and patch history, and what the game writes in <see cref="ISaveable.Save"/>. The same state gives the
    /// same bytes. Throws <see cref="SaveErrorCode.NotSaveable"/> for a game that does not opt in.
    /// </summary>
    public byte[] Save()
    {
        var saveable = Game as ISaveable ?? throw new SaveException(SaveErrorCode.NotSaveable, $"{Game.Name} does not implement ISaveable");
        var payload = new SaveWriter();
        payload.Tag("engine");
        Input.Save(payload);
        _context.Scenes?.SaveState(payload);
        var game = new SaveWriter();
        saveable.Save(game);
        payload.Tag("game");
        payload.Bytes(game.ToArray());
        return SaveFile.Write(Game.Name, saveable.SaveVersion, Tick, _context.Scenes is not null, payload.ToArray());
    }

    /// <summary>Reads the header of a save (game, tick, versions) after checking its magic, format and checksum, without restoring it.</summary>
    public static SaveInfo Inspect(ReadOnlySpan<byte> save) => SaveFile.Read(save).Info;

    /// <summary>
    /// Replaces the game state with a save made by <see cref="Save"/> of the same game and save version. The game must be
    /// started (<see cref="StartAsync"/>) so its resources and scene registry exist. The header, checksum and engine
    /// sections are validated before anything changes; a typed <see cref="SaveException"/> reports a mismatch. If the
    /// game's own <see cref="ISaveable.Load"/> throws midway the game is left half-loaded: restore again or discard it.
    /// An attached <see cref="Script"/> continues from the restored tick.
    /// </summary>
    public void Restore(ReadOnlySpan<byte> save)
    {
        var saveable = Game as ISaveable ?? throw new SaveException(SaveErrorCode.NotSaveable, $"{Game.Name} does not implement ISaveable");
        if (!_started)
        {
            throw new InvalidOperationException("[RUNTIME]: not started; call StartAsync before Restore");
        }

        var (info, payload) = SaveFile.Read(save);
        if (info.Game != Game.Name)
        {
            throw new SaveException(SaveErrorCode.Mismatch, $"saved by {info.Game}, this is {Game.Name}");
        }

        if (info.GameVersion != saveable.SaveVersion)
        {
            throw new SaveException(SaveErrorCode.GameVersion, $"{Game.Name} save version {info.GameVersion}, this build reads {saveable.SaveVersion}");
        }

        if (info.HasScenes != (_context.Scenes is not null))
        {
            throw new SaveException(SaveErrorCode.Mismatch, $"the save {(info.HasScenes ? "has" : "has no")} scene registry but {Game.Name} {(_context.Scenes is not null ? "has one" : "has none")}");
        }

        var reader = new SaveReader(payload);
        reader.Expect("engine");
        Input.Restore(reader);
        _context.Scenes?.RestoreState(reader, info.Tick);
        reader.Expect("game");
        var game = new SaveReader(reader.Bytes());
        reader.RequireEnd("engine state");
        Tick = info.Tick;
        saveable.Load(game);
        game.RequireEnd($"{Game.Name}.Load");
        Script?.SeekTo(Tick);
        _clock.Reset();
    }

    /// <summary>
    /// Keeps a ring of checkpoints (<see cref="TimeTravelOptions"/>) and a journal of outside input and patches so that
    /// <see cref="Rewind"/> can return to any recent tick. The dev bridge calls this for saveable games. A checkpoint
    /// of the current tick is taken immediately when the game already started, otherwise when it does.
    /// </summary>
    public void EnableTimeTravel(TimeTravelOptions? options = null)
    {
        if (!CanSave)
        {
            throw new SaveException(SaveErrorCode.NotSaveable, $"{Game.Name} does not implement ISaveable");
        }

        options ??= new TimeTravelOptions();
        options.Validate();
        _timeline = new TimeTravel(options);
        Input.Journal = _timeline.Inputs;
        if (_started)
        {
            _timeline.Add(Tick, Save());
        }
    }

    /// <summary>The ticks <see cref="Rewind"/> can reach right now, or null when time travel is not enabled or nothing was captured yet.</summary>
    public TimeTravelWindow? RewindWindow => _timeline?.Window(Tick);

    /// <summary>
    /// Returns the game to <paramref name="tick"/> (at most the current tick): restores the newest checkpoint at or before
    /// it and steps forward, feeding the input and patches that came from outside at their original ticks, so the state
    /// equals what an uninterrupted run showed at that tick. Sounds do not replay. The future is abandoned: checkpoints,
    /// journal entries and recorded input from <paramref name="tick"/> on are dropped, and the runtime continues from
    /// here with whatever input arrives next. If the replay fails the previous state is restored and the error rethrown.
    /// </summary>
    public RewindResult Rewind(long tick)
    {
        var timeline = _timeline ?? throw new SaveException(SaveErrorCode.NoTimeline, $"time travel is not enabled for {Game.Name} (it must implement ISaveable and run under the dev bridge, or call EnableTimeTravel)");
        if (tick < 0 || tick > Tick)
        {
            throw new SaveException(SaveErrorCode.FutureTick, $"cannot rewind to tick {tick}: the game is at {Tick} (rewind goes back; use step to go forward)");
        }

        var (checkpointTick, checkpoint) = timeline.Nearest(tick)
            ?? throw new SaveException(SaveErrorCode.OutOfWindow, $"tick {tick} is older than the oldest checkpoint ({timeline.OldestTick}); the window is {timeline.OldestTick}..{Tick}");
        var inputs = timeline.Inputs.Where(entry => entry.Tick >= checkpointTick && entry.Tick < tick).ToList();
        var patches = timeline.Patches.Where(entry => entry.SubmitTick >= checkpointTick && entry.SubmitTick < tick).ToList();
        var before = Save();
        var journal = Input.Journal;
        var history = Input.History;
        _replaying = true;
        Input.Journal = null;
        Input.History = null;
        try
        {
            Restore(checkpoint);
            var nextInput = 0;
            var nextPatch = 0;
            while (Tick < tick)
            {
                for (; nextPatch < patches.Count && patches[nextPatch].SubmitTick == Tick; nextPatch++)
                {
                    try
                    {
                        _context.Scenes!.Submit(patches[nextPatch].Patch);
                    }
                    catch (SceneException)
                    {
                        // Refused the same way the first time: the refusal is part of the state it left behind.
                    }
                }

                for (; nextInput < inputs.Count && inputs[nextInput].Tick == Tick; nextInput++)
                {
                    Input.Push(inputs[nextInput].Event);
                }

                Step(1);
            }
        }
        catch
        {
            Restore(before);
            throw;
        }
        finally
        {
            _replaying = false;
            Input.Journal = journal;
            Input.History = history;
        }

        timeline.TruncateFrom(tick);
        Input.TruncateRecorded(tick);
        History.TruncateFrom(tick);
        Script?.SeekTo(tick);
        _clock.Reset();
        return new RewindResult(Tick, checkpointTick, tick - checkpointTick, timeline.Window(Tick)!);
    }

    private void CaptureIfDue()
    {
        if (_timeline is { } timeline && _started && Tick % timeline.Options.IntervalTicks == 0 && !timeline.Has(Tick))
        {
            timeline.Add(Tick, Save());
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
    public void SubmitPatch(ScenePatch patch)
    {
        var scenes = _context.Scenes ?? throw new InvalidOperationException($"[RUNTIME]: {Game.Name} has no SceneRegistry, so it cannot take patches");
        // Refused submissions are kept too: one refused at the current boundary changes the snapshot's rejected list.
        // A patch that cannot be written as JSON (NaN, infinity) is refused as malformed before it touches any state.
        if (!_replaying)
        {
            try
            {
                History.AddPatch(Tick, patch.ToJson());
            }
            catch (ArgumentException)
            {
            }
        }

        if (_timeline is not { } timeline || _replaying)
        {
            scenes.Submit(patch);
            return;
        }

        // A patch from outside the simulation is journaled with the boundary it arrived at, so a rewind can submit it
        // again at the same point. A refusal the world recorded counts too; one thrown before anything was recorded does not.
        var rejectedBefore = scenes.State().RejectedCount;
        var recorded = false;
        try
        {
            scenes.Submit(patch);
            recorded = true;
        }
        catch (SceneException)
        {
            recorded = scenes.State().RejectedCount > rejectedBefore;
            throw;
        }
        finally
        {
            if (recorded)
            {
                timeline.Patches.Add(new JournaledPatch(Tick, patch));
            }
        }
    }

    /// <summary>The replay log as JSON, for a fresh process to <see cref="RestoreFrom"/> to the current tick.</summary>
    public string ExportHistoryJson() => History.ToJson(Tick, Input.Pending);

    /// <summary>
    /// Replays <paramref name="log"/> from tick 0 on this fresh runtime until it is at boundary <paramref name="tick"/>:
    /// same code and same log give the same state as the process the log came from. Events that were queued but not
    /// yet latched there are queued again. Refuses a runtime that already ran and a log that no longer holds the whole
    /// history. If the code changed, the replay still runs and the state may differ: compare snapshots to see.
    /// </summary>
    public void RestoreFrom(ReplayLog log, long tick)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Truncated)
        {
            throw new InvalidOperationException($"[REPLAY]: the history of this session outgrew {ReplayLog.MaxEntries} entries and is incomplete; it cannot be replayed");
        }

        if (Tick != 0 || History.Count != 0)
        {
            throw new InvalidOperationException($"[REPLAY]: only a fresh runtime can be restored (tick {Tick})");
        }

        var window = Viewport;
        int inputAt = 0, patchAt = 0, viewportAt = 0;
        for (var t = 0L; t <= tick; t++)
        {
            while (viewportAt < log.Viewports.Count && log.Viewports[viewportAt].Tick <= t)
            {
                Viewport = log.Viewports[viewportAt++].Extent;
            }

            while (patchAt < log.Patches.Count && log.Patches[patchAt].AtTick <= t)
            {
                try
                {
                    SubmitPatch(ScenePatch.Parse(log.Patches[patchAt++].PatchJson));
                }
                catch (SceneException)
                {
                    // Refused the same way the original session was; the refusal is part of the replayed history.
                }
            }

            while (inputAt < log.Input.Count && log.Input[inputAt].Tick <= t)
            {
                Input.Push(log.Input[inputAt++].Event);
            }

            if (t < tick)
            {
                Step(1);
            }
        }

        Viewport = window;
    }

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

/// <summary>Command-line flags that only the headless host honors. Any other host refuses them instead of ignoring them.</summary>
public static class HostFlags
{
    private static readonly string[] HeadlessOnly = ["--patches"];

    /// <summary>The error for the first headless-only flag in <paramref name="args"/>, or null when the host can take them all.</summary>
    public static string? Unsupported(string target, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var flag = args.FirstOrDefault(arg => HeadlessOnly.Contains(arg, StringComparer.Ordinal));
        return flag is null
            ? null
            : $"[{target.ToUpperInvariant()}]: {flag} is headless-only; the {target} host would ignore it. Replay patches with the headless host (ukiyo_run {{\"patches\":[...]}}) or submit them to a running game with ukiyo_live patch";
    }
}

/// <summary>A host that cannot start: prints why on stderr and exits with code 2, so a bad command line is never silently ignored.</summary>
public sealed class StartupFailure(string target, string message) : IPlatformHost
{
    public string Target => target;

    public Task<int> RunAsync(GameRuntime runtime)
    {
        Console.Error.WriteLine(message);
        return Task.FromResult(2);
    }
}
