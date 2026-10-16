using Ukiyo.Audio;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Rendering.Software;

namespace Ukiyo.Hosting;

/// <summary>Headless bootstrap: NullRenderer (or the CPU renderer when capturing), explicit ticks, JSON snapshots on stdout.</summary>
public static class PlatformBootstrap
{
    public static IPlatformHost Create(string[] args) => new HeadlessHost(HeadlessOptions.Parse(args));
}

/// <param name="Checkpoints">Ticks to stop at; each prints one snapshot line.</param>
/// <param name="InputScript">JSON input script (see <see cref="Ukiyo.InputScript"/>) fed before each tick.</param>
/// <param name="CaptureDirectory">When set, renders with the CPU renderer and writes a PNG per checkpoint.</param>
/// <param name="Extent">Drawing-buffer size used for frames, UI layout and captures.</param>
/// <param name="AudioPath">When set, mixes every sound the run plays and writes it as a WAV (one tick of audio per tick).</param>
/// <param name="PatchLog">When set, a patch log (docs/scenes-patches.md) submitted at boundary 0, before the first tick.</param>
/// <param name="EdgeMaskDirectory">
/// Debug, for pixel-parity gates; needs <paramref name="CaptureDirectory"/>. Writes the CPU frame's geometric edge mask
/// (<see cref="SurfaceMap"/>) per checkpoint. The captures themselves are the same bytes with or without it.
/// </param>
public sealed record HeadlessOptions(long[] Checkpoints, string? InputScript, string? CaptureDirectory, RenderExtent Extent, string? AudioPath = null, string? PatchLog = null, string? EdgeMaskDirectory = null)
{
    public static readonly long[] DefaultCheckpoints = [0, 60, 180, 600];

    public static HeadlessOptions Parse(string[] args)
    {
        var checkpoints = DefaultCheckpoints;
        string? input = null;
        string? capture = null;
        string? audio = null;
        string? patches = null;
        string? edgeMasks = null;
        var extent = new RenderExtent(1280, 720, 1f);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--checkpoints" when i + 1 < args.Length:
                    checkpoints = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Order()];
                    break;
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--capture" when i + 1 < args.Length:
                    capture = args[++i];
                    break;
                case "--audio" when i + 1 < args.Length:
                    audio = args[++i];
                    break;
                case "--patches" when i + 1 < args.Length:
                    patches = args[++i];
                    break;
                case "--patches":
                    throw new ArgumentException("[HEADLESS]: --patches expects a patch log path (a JSON array of patches)");
                case "--edge-masks" when i + 1 < args.Length:
                    edgeMasks = args[++i];
                    break;
                case "--edge-masks":
                    throw new ArgumentException("[HEADLESS]: --edge-masks expects a directory");
                case "--size" when i + 1 < args.Length:
                    var size = args[++i].Split('x');
                    if (size.Length != 2 || !int.TryParse(size[0], out var w) || !int.TryParse(size[1], out var h))
                    {
                        throw new ArgumentException($"[HEADLESS]: --size expects WIDTHxHEIGHT, got \"{args[i]}\"");
                    }

                    extent = new RenderExtent(w, h, 1f);
                    break;
            }
        }

        if (extent.IsEmpty)
        {
            throw new ArgumentException($"[HEADLESS]: --size must be positive, got {extent.Width}x{extent.Height}");
        }

        if (edgeMasks is not null && capture is null)
        {
            throw new ArgumentException("[HEADLESS]: --edge-masks needs --capture: the masks describe the CPU frames it writes");
        }

        return new HeadlessOptions(checkpoints, input, capture, extent, audio, patches, edgeMasks);
    }
}

/// <summary>
/// Runs the game with no window, GPU or DOM. Each checkpoint prints one JSON line prefixed with "snapshot "; with
/// --capture it also writes headless-tick-NNNNNN.png and prints "capture {path}" (and with --edge-masks writes
/// edges-tick-NNNNNN.png and prints "edges {path}"); with --patches it prints "patches {path} submitted=N" once the
/// log is accepted. Agents parse exactly these lines. Output goes to
/// <paramref name="output"/> (stdout by default) and refusals to <paramref name="errors"/> (stderr by default).
/// </summary>
public sealed class HeadlessHost(HeadlessOptions options, TextWriter? output = null, TextWriter? errors = null) : IPlatformHost
{
    /// <summary>Exit code when the patch log cannot be submitted (the run stops before its first tick).</summary>
    public const int PatchLogRefused = 2;

    private readonly TextWriter _out = output ?? Console.Out;
    private readonly TextWriter _errors = errors ?? Console.Error;

    public string Target => "headless";

    public async Task<int> RunAsync(GameRuntime runtime)
    {
        if (options.InputScript is { } path)
        {
            runtime.Script = InputScript.Parse(await File.ReadAllTextAsync(path));
            _out.WriteLine($"[ukiyo] input script {path} events={runtime.Script.Events.Count}");
        }

        IReadOnlyList<ScenePatch>? patches = null;
        if (options.PatchLog is { } logPath)
        {
            try
            {
                patches = ScenePatch.ParseLog(await File.ReadAllTextAsync(logPath));
            }
            catch (Exception refused) when (refused is SceneException or IOException or UnauthorizedAccessException)
            {
                return RefusePatchLog(logPath, refused.Message);
            }
        }

        using var recorder = options.AudioPath is null ? null : new RecordingAudioOutput();
        runtime.Audio = recorder;
        var code = options.CaptureDirectory is { } directory
            ? await RunWithCaptures(runtime, directory, patches)
            : await RunNull(runtime, patches);
        if (code == 0 && recorder is not null && options.AudioPath is { } audioPath)
        {
            var wav = Path.GetFullPath(audioPath);
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            await File.WriteAllBytesAsync(wav, recorder.ToWav());
            var (peak, rms) = recorder.Levels();
            _out.WriteLine(FormattableString.Invariant($"audio {wav} seconds={recorder.Seconds:0.###} sounds={recorder.SoundsPlayed} peak={peak:0.####} rms={rms:0.####}"));
        }

        return code;
    }

    private async Task<int> RunNull(GameRuntime runtime, IReadOnlyList<ScenePatch>? patches)
    {
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-headless", options.Extent));
        runtime.Paused = true;
        if (patches is not null && !SubmitPatches(runtime, patches))
        {
            return PatchLogRefused;
        }

        foreach (var checkpoint in options.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(options.Extent);
            _out.WriteLine($"snapshot {runtime.SnapshotJson()}");
        }

        _out.WriteLine($"[ukiyo] null frames={renderer.FramesRendered} instances={renderer.InstancesSubmitted} sprites={renderer.SpritesSubmitted} meshes={renderer.LiveMeshes} materials={renderer.LiveMaterials} textures={renderer.LiveTextures}");
        return 0;
    }

    private async Task<int> RunWithCaptures(GameRuntime runtime, string directory, IReadOnlyList<ScenePatch>? patches)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SoftwareRenderer();
        if (options.EdgeMaskDirectory is { } edgeDirectory)
        {
            Directory.CreateDirectory(edgeDirectory);
            renderer.Surfaces = new SurfaceMap();
        }

        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-headless", options.Extent));
        runtime.Paused = true;
        if (patches is not null && !SubmitPatches(runtime, patches))
        {
            return PatchLogRefused;
        }

        foreach (var checkpoint in options.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(options.Extent);
            var capture = await renderer.CaptureAsync(new CaptureRequest(runtime.Tick), CancellationToken.None);
            var png = Path.GetFullPath(Path.Combine(directory, $"headless-tick-{checkpoint:D6}.png"));
            await File.WriteAllBytesAsync(png, capture.Png);
            var snapshot = runtime.SnapshotJson();
            await File.WriteAllTextAsync(Path.ChangeExtension(png, ".json"), snapshot);
            _out.WriteLine($"snapshot {snapshot}");
            _out.WriteLine($"capture {png}");
            if (renderer.Surfaces is { } surfaces)
            {
                var edges = Path.GetFullPath(Path.Combine(options.EdgeMaskDirectory!, $"edges-tick-{checkpoint:D6}.png"));
                await File.WriteAllBytesAsync(edges, surfaces.EncodePng(runtime.Tick));
                _out.WriteLine($"edges {edges}");
            }
        }

        _out.WriteLine($"[ukiyo] software frames={renderer.FramesRendered} size={options.Extent.Width}x{options.Extent.Height}");
        return 0;
    }

    /// <summary>
    /// Submits the whole log at boundary 0 in file order, exactly as <see cref="GameRuntime.SubmitPatch"/> would from
    /// code, so the run's snapshots match a replay through the C# API. A submit-time refusal stops the run. A patch for
    /// tick 0 that the world refuses is recorded in the snapshot's <c>rejected</c> list, like any patch refused at its
    /// tick, and the run goes on.
    /// </summary>
    private bool SubmitPatches(GameRuntime runtime, IReadOnlyList<ScenePatch> patches)
    {
        var path = options.PatchLog!;
        if (!runtime.HasScenes)
        {
            RefusePatchLog(path, $"{runtime.Game.Name} has no SceneRegistry, so it cannot take patches");
            return false;
        }

        foreach (var patch in patches)
        {
            var rejectedBefore = runtime.SceneState!.Rejected.Count;
            try
            {
                runtime.SubmitPatch(patch);
            }
            catch (SceneException) when (runtime.SceneState!.Rejected.Count > rejectedBefore)
            {
                // Refused by the world at tick 0: already in the snapshot's rejected list, not a bad log.
            }
            catch (SceneException refused)
            {
                RefusePatchLog(path, refused.Message);
                return false;
            }
        }

        _out.WriteLine($"patches {Path.GetFullPath(path)} submitted={patches.Count}");
        return true;
    }

    private int RefusePatchLog(string path, string message)
    {
        _errors.WriteLine($"[HEADLESS]: --patches {path}: {message}");
        return PatchLogRefused;
    }
}
