using System.Diagnostics;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Wgpu;

namespace Ukiyo.Hosting;

/// <summary>Desktop bootstrap: SDL3 window with a CAMetalLayer, WgpuRenderer on Metal.</summary>
public static class PlatformBootstrap
{
    public static IPlatformHost Create(string[] args) => new DesktopHost(DesktopOptions.Parse(args));
}

/// <param name="CaptureDirectory">When set, runs the deterministic checkpoint capture and exits.</param>
/// <param name="ExitAfterFrames">Interactive loop exits after this many presented frames (smoke tests).</param>
/// <param name="ResizeTest">Resizes the window between checkpoints to exercise surface reconfiguration.</param>
public sealed record DesktopOptions(string? CaptureDirectory, long[] Checkpoints, long ExitAfterFrames, bool ResizeTest)
{
    public static DesktopOptions Parse(string[] args)
    {
        string? capture = null;
        long[] checkpoints = [0, 60, 180, 600];
        long exitAfter = 0;
        var resize = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture" when i + 1 < args.Length:
                    capture = args[++i];
                    break;
                case "--checkpoints" when i + 1 < args.Length:
                    checkpoints = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Order()];
                    break;
                case "--frames" when i + 1 < args.Length:
                    exitAfter = long.Parse(args[++i]);
                    break;
                case "--resize-test":
                    resize = true;
                    break;
            }
        }

        return new DesktopOptions(capture, checkpoints, exitAfter, resize);
    }
}

/// <summary>
/// Runs entirely on the calling (main) thread: macOS requires window and Metal view work there, and a console
/// app has no synchronization context to bring an await back. Async renderer calls must complete synchronously.
/// </summary>
public sealed unsafe class DesktopHost(DesktopOptions options) : IPlatformHost
{
    public string Target => "desktop";

    public Task<int> RunAsync(GameRuntime runtime)
    {
        if (Sdl3.SDL_Init(Sdl3.InitVideo) == 0)
        {
            throw new InvalidOperationException($"[DESKTOP]: SDL_Init failed: {Sdl3.LastError}");
        }

        var window = Sdl3.SDL_CreateWindow("ukiyo · RotatingCube · wgpu/Metal", 960, 640, Sdl3.WindowMetal | Sdl3.WindowResizable | Sdl3.WindowHighPixelDensity);
        if (window == 0)
        {
            Sdl3.SDL_Quit();
            throw new InvalidOperationException($"[DESKTOP]: SDL_CreateWindow failed: {Sdl3.LastError}");
        }

        var view = Sdl3.SDL_Metal_CreateView(window);
        try
        {
            var layer = Sdl3.SDL_Metal_GetLayer(view);
            using var renderer = new WgpuRenderer(layer);
            var extent = PixelExtent(window);
            Synchronously(runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-desktop", extent)), "renderer initialization");
            var code = options.CaptureDirectory is { } directory
                ? CaptureCheckpoints(runtime, renderer, window, directory)
                : Interactive(runtime, renderer, window);
            Console.WriteLine($"[ukiyo] desktop frames={renderer.FramesPresented} skipped={renderer.SkippedFrames} dropped-ticks={runtime.DroppedTicks}");
            return Task.FromResult(code);
        }
        finally
        {
            Sdl3.SDL_Metal_DestroyView(view);
            Sdl3.SDL_DestroyWindow(window);
            Sdl3.SDL_Quit();
        }
    }

    private int Interactive(GameRuntime runtime, WgpuRenderer renderer, nint window)
    {
        Console.WriteLine("[ukiyo] keys: space/P pause · S step one tick while paused · Esc quit");
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var extent = PixelExtent(window);
        while (true)
        {
            var sdlEvent = default(Sdl3.Event);
            while (Sdl3.SDL_PollEvent(&sdlEvent) != 0)
            {
                switch (sdlEvent.Type)
                {
                    case Sdl3.EventQuit:
                    case Sdl3.EventWindowCloseRequested:
                        return 0;
                    case Sdl3.EventWindowPixelSizeChanged:
                        extent = PixelExtent(window);
                        runtime.Renderer.Resize(extent);
                        break;
                    case Sdl3.EventKeyDown when sdlEvent.Repeat == 0:
                        if (sdlEvent.Key == Sdl3.KeyEscape)
                        {
                            return 0;
                        }

                        if (sdlEvent.Key is Sdl3.KeySpace or Sdl3.KeyP)
                        {
                            runtime.Paused = !runtime.Paused;
                            Console.WriteLine($"[ukiyo] paused={runtime.Paused} tick={runtime.Tick}");
                        }
                        else if (sdlEvent.Key == Sdl3.KeyS && runtime.Paused)
                        {
                            runtime.Step(1);
                            Console.WriteLine($"snapshot {runtime.SnapshotJson()}");
                        }

                        break;
                }
            }

            var now = clock.Elapsed.TotalSeconds;
            runtime.AdvanceTime(now - last);
            last = now;
            runtime.RenderFrame(extent);
            if (options.ExitAfterFrames > 0 && renderer.FramesPresented >= options.ExitAfterFrames)
            {
                Console.WriteLine($"snapshot {runtime.SnapshotJson()}");
                return 0;
            }
        }
    }

    /// <summary>Paused, deterministic: step to each checkpoint, present it, read the frame back from the GPU and save it.</summary>
    private int CaptureCheckpoints(GameRuntime runtime, WgpuRenderer renderer, nint window, string directory)
    {
        Directory.CreateDirectory(directory);
        runtime.Paused = true;
        var sizes = new[] { (960, 640), (640, 480), (1200, 700) };
        for (var i = 0; i < options.Checkpoints.Length; i++)
        {
            if (options.ResizeTest)
            {
                var (w, h) = sizes[i % sizes.Length];
                Sdl3.SDL_SetWindowSize(window, w, h);
                Sdl3.SDL_SyncWindow(window);
            }

            var sdlEvent = default(Sdl3.Event);
            while (Sdl3.SDL_PollEvent(&sdlEvent) != 0)
            {
            }

            var extent = PixelExtent(window);
            runtime.Renderer.Resize(extent);
            var tick = options.Checkpoints[i];
            runtime.StepTo(tick);
            PresentVisibly(runtime, renderer, window, extent);
            var capture = Synchronously(renderer.CaptureAsync(new CaptureRequest(tick), CancellationToken.None), "capture");
            var path = Path.Combine(directory, $"desktop-tick-{tick:D6}.png");
            File.WriteAllBytes(path, capture.Png);
            var snapshot = runtime.SnapshotJson();
            File.WriteAllText(Path.Combine(directory, $"desktop-tick-{tick:D6}.json"), snapshot);
            Console.WriteLine($"snapshot {snapshot}");
            Console.WriteLine($"[ukiyo] captured {path} {capture.Width}x{capture.Height} backend={capture.Backend}");
        }

        return 0;
    }

    /// <summary>Re-presents the same tick until the compositor accepts a frame, so the capture matches a frame actually shown.</summary>
    private static void PresentVisibly(GameRuntime runtime, WgpuRenderer renderer, nint window, RenderExtent extent)
    {
        var before = renderer.FramesPresented;
        for (var attempt = 0; attempt < 240 && renderer.FramesPresented == before; attempt++)
        {
            var sdlEvent = default(Sdl3.Event);
            while (Sdl3.SDL_PollEvent(&sdlEvent) != 0)
            {
            }

            runtime.RenderFrame(extent);
            if (renderer.FramesPresented == before)
            {
                Thread.Sleep(8);
            }
        }

        if (renderer.FramesPresented == before)
        {
            throw new InvalidOperationException("[DESKTOP]: window never became visible; no frame was presented");
        }
    }

    private static RenderExtent PixelExtent(nint window)
    {
        int width, height;
        Sdl3.SDL_GetWindowSizeInPixels(window, &width, &height);
        return new RenderExtent(width, height, 1f);
    }

    private static void Synchronously(ValueTask task, string what)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException($"[DESKTOP]: {what} did not complete on the main thread");
        }

        task.GetAwaiter().GetResult();
    }

    private static T Synchronously<T>(ValueTask<T> task, string what)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException($"[DESKTOP]: {what} did not complete on the main thread");
        }

        return task.GetAwaiter().GetResult();
    }
}
