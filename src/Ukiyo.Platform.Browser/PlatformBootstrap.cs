using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Three;

namespace Ukiyo.Hosting;

/// <summary>Browser bootstrap: .NET WebAssembly runs the game; Three.js presents it through the adapter.</summary>
[SupportedOSPlatform("browser")]
public static class PlatformBootstrap
{
    public static IPlatformHost Create(string[] args) => new BrowserHost(BrowserOptions.Parse(args));
}

/// <param name="Checkpoints">When set, runs the deterministic checkpoint capture and publishes evidence to the page.</param>
/// <param name="FixedSize">Keeps one canvas size for every checkpoint instead of cycling sizes (for animations).</param>
public sealed record BrowserOptions(long[]? Checkpoints, bool StartPaused, bool FixedSize = false)
{
    public static BrowserOptions Parse(string[] args)
    {
        long[]? checkpoints = null;
        var paused = false;
        var fixedSize = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--checkpoints" && i + 1 < args.Length)
            {
                checkpoints = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Order()];
            }
            else if (args[i] == "--paused")
            {
                paused = true;
            }
            else if (args[i] == "--fixed-size")
            {
                fixedSize = true;
            }
        }

        return new BrowserOptions(checkpoints, paused, fixedSize);
    }
}

/// <summary>
/// One frame coordinator: the page's requestAnimationFrame calls <see cref="BrowserExports.Frame"/>, which hands
/// elapsed time to the C# fixed clock and then renders. JS never advances the simulation on its own.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserHost(BrowserOptions options) : IPlatformHost
{
    private static GameRuntime? s_runtime;
    private static double s_lastTimestamp = double.NaN;
    private static RenderExtent s_extent;

    public string Target => "web";

    internal static GameRuntime Runtime => s_runtime ?? throw new InvalidOperationException("[BROWSER]: runtime not started");

    public async Task<int> RunAsync(GameRuntime runtime)
    {
        s_extent = CurrentExtent();
        var renderer = new ThreeJsRenderer("ukiyo-canvas");
        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-web", s_extent));
        s_runtime = runtime;
        runtime.Paused = options.StartPaused;
        if (options.Checkpoints is { } checkpoints)
        {
            await PublishEvidence(runtime, renderer, checkpoints, options.FixedSize);
            return 0;
        }

        StartLoop();
        return 0;
    }

    internal static void OnFrame(double timestampMs, int width, int height)
    {
        var runtime = Runtime;
        var extent = new RenderExtent(width, height, 1f);
        if (extent != s_extent && !extent.IsEmpty)
        {
            s_extent = extent;
            runtime.Renderer.Resize(extent);
        }

        var elapsed = double.IsNaN(s_lastTimestamp) ? 0 : (timestampMs - s_lastTimestamp) / 1000.0;
        s_lastTimestamp = timestampMs;
        runtime.AdvanceTime(Math.Max(0, elapsed));
        runtime.RenderFrame(s_extent);
    }

    /// <summary>
    /// Deterministic evidence: paused, step to each checkpoint, render, read back the canvas. Then wait real time
    /// while paused and render again, proving nothing moves without C# advancing the simulation.
    /// </summary>
    private static async Task PublishEvidence(GameRuntime runtime, ThreeJsRenderer renderer, long[] checkpoints, bool fixedSize)
    {
        runtime.Paused = true;
        var json = new StringBuilder();
        json.Append("{\"target\":\"web\",\"captures\":[");
        var sizes = new[] { (960, 640), (640, 480), (1200, 700) };
        for (var i = 0; i < checkpoints.Length; i++)
        {
            var (w, h) = fixedSize ? sizes[0] : sizes[i % sizes.Length];
            SetCanvasSize(w, h);
            s_extent = new RenderExtent(w, h, 1f);
            renderer.Resize(s_extent);
            runtime.StepTo(checkpoints[i]);
            AppendCapture(json, runtime, renderer, i > 0);
        }

        var tickBeforeWait = runtime.Tick;
        await Task.Delay(1500);
        runtime.AdvanceTime(1.5);
        json.Append("],\"pauseCheck\":{\"tickBefore\":").Append(tickBeforeWait).Append(",\"tickAfter\":").Append(runtime.Tick).Append(",\"frames\":[");
        AppendCapture(json, runtime, renderer, false);
        await Task.Delay(500);
        AppendCapture(json, runtime, renderer, true);
        json.Append("]},\"bytesSent\":").Append(renderer.BytesSent).Append('}');
        Publish(json.ToString());
    }

    private static void AppendCapture(StringBuilder json, GameRuntime runtime, ThreeJsRenderer renderer, bool comma)
    {
        runtime.RenderFrame(s_extent);
        var capture = renderer.CaptureAsync(new CaptureRequest(runtime.Tick), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (comma)
        {
            json.Append(',');
        }

        json.Append("{\"tick\":").Append(runtime.Tick)
            .Append(",\"width\":").Append(capture.Width)
            .Append(",\"height\":").Append(capture.Height)
            .Append(",\"snapshot\":").Append(runtime.SnapshotJson())
            .Append(",\"png\":\"").Append(Convert.ToBase64String(capture.Png)).Append("\"}");
    }

    private static RenderExtent CurrentExtent()
    {
        var size = CanvasPixelSize().Split('x');
        return new RenderExtent(int.Parse(size[0]), int.Parse(size[1]), 1f);
    }

    [JSImport("startLoop", "ukiyo-host")]
    private static partial void StartLoop();

    [JSImport("canvasPixelSize", "ukiyo-host")]
    private static partial string CanvasPixelSize();

    [JSImport("setCanvasSize", "ukiyo-host")]
    private static partial void SetCanvasSize(int width, int height);

    [JSImport("publishEvidence", "ukiyo-host")]
    private static partial void Publish(string json);
}

/// <summary>Entry points the page calls. The runtime state lives in C#; these only forward.</summary>
[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    [JSExport]
    public static void Frame(double timestampMs, int width, int height) => BrowserHost.OnFrame(timestampMs, width, height);

    [JSExport]
    public static void SetPaused(bool paused) => BrowserHost.Runtime.Paused = paused;

    [JSExport]
    public static string Step(int ticks)
    {
        BrowserHost.Runtime.Step(ticks);
        return BrowserHost.Runtime.SnapshotJson();
    }

    [JSExport]
    public static string Snapshot() => BrowserHost.Runtime.SnapshotJson();
}
