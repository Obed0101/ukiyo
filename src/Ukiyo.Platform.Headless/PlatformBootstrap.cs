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
public sealed record HeadlessOptions(long[] Checkpoints, string? InputScript, string? CaptureDirectory, RenderExtent Extent, string? AudioPath = null)
{
    public static readonly long[] DefaultCheckpoints = [0, 60, 180, 600];

    public static HeadlessOptions Parse(string[] args)
    {
        var checkpoints = DefaultCheckpoints;
        string? input = null;
        string? capture = null;
        string? audio = null;
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

        return new HeadlessOptions(checkpoints, input, capture, extent, audio);
    }
}

/// <summary>
/// Runs the game with no window, GPU or DOM. Each checkpoint prints one JSON line prefixed with "snapshot "; with
/// --capture it also writes headless-tick-NNNNNN.png and prints "capture {path}". Agents parse exactly these lines.
/// </summary>
public sealed class HeadlessHost(HeadlessOptions options) : IPlatformHost
{
    public string Target => "headless";

    public async Task<int> RunAsync(GameRuntime runtime)
    {
        if (options.InputScript is { } path)
        {
            runtime.Script = InputScript.Parse(await File.ReadAllTextAsync(path));
            Console.WriteLine($"[ukiyo] input script {path} events={runtime.Script.Events.Count}");
        }

        using var recorder = options.AudioPath is null ? null : new RecordingAudioOutput();
        runtime.Audio = recorder;
        var code = options.CaptureDirectory is { } directory
            ? await RunWithCaptures(runtime, directory)
            : await RunNull(runtime);
        if (recorder is not null && options.AudioPath is { } audioPath)
        {
            var wav = Path.GetFullPath(audioPath);
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            await File.WriteAllBytesAsync(wav, recorder.ToWav());
            var (peak, rms) = recorder.Levels();
            Console.WriteLine(FormattableString.Invariant($"audio {wav} seconds={recorder.Seconds:0.###} sounds={recorder.SoundsPlayed} peak={peak:0.####} rms={rms:0.####}"));
        }

        return code;
    }

    private async Task<int> RunNull(GameRuntime runtime)
    {
        using var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-headless", options.Extent));
        runtime.Paused = true;
        foreach (var checkpoint in options.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(options.Extent);
            Console.WriteLine($"snapshot {runtime.SnapshotJson()}");
        }

        Console.WriteLine($"[ukiyo] null frames={renderer.FramesRendered} instances={renderer.InstancesSubmitted} sprites={renderer.SpritesSubmitted} meshes={renderer.LiveMeshes} materials={renderer.LiveMaterials} textures={renderer.LiveTextures}");
        return 0;
    }

    private async Task<int> RunWithCaptures(GameRuntime runtime, string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-headless", options.Extent));
        runtime.Paused = true;
        foreach (var checkpoint in options.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(options.Extent);
            var capture = await renderer.CaptureAsync(new CaptureRequest(runtime.Tick), CancellationToken.None);
            var png = Path.GetFullPath(Path.Combine(directory, $"headless-tick-{checkpoint:D6}.png"));
            await File.WriteAllBytesAsync(png, capture.Png);
            var snapshot = runtime.SnapshotJson();
            await File.WriteAllTextAsync(Path.ChangeExtension(png, ".json"), snapshot);
            Console.WriteLine($"snapshot {snapshot}");
            Console.WriteLine($"capture {png}");
        }

        Console.WriteLine($"[ukiyo] software frames={renderer.FramesRendered} size={options.Extent.Width}x{options.Extent.Height}");
        return 0;
    }
}
