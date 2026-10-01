using System.Diagnostics;
using System.Numerics;
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
/// <param name="InputScript">Input script fed tick by tick (same JSON as headless --input), for cross-target parity.</param>
public sealed record DesktopOptions(string? CaptureDirectory, long[] Checkpoints, long ExitAfterFrames, bool ResizeTest, string? InputScript = null)
{
    public static DesktopOptions Parse(string[] args)
    {
        string? capture = null;
        string? input = null;
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
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
            }
        }

        return new DesktopOptions(capture, checkpoints, exitAfter, resize, input);
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
        if (options.InputScript is { } path)
        {
            runtime.Script = InputScript.Parse(File.ReadAllText(path));
            Console.WriteLine($"[ukiyo] input script {path} events={runtime.Script.Events.Count}");
        }

        if (Sdl3.SDL_Init(Sdl3.InitVideo) == 0)
        {
            throw new InvalidOperationException($"[DESKTOP]: SDL_Init failed: {Sdl3.LastError}");
        }

        var window = Sdl3.SDL_CreateWindow($"ukiyo · {runtime.Game.Name} · wgpu/Metal", 960, 640, Sdl3.WindowMetal | Sdl3.WindowResizable | Sdl3.WindowHighPixelDensity);
        if (window == 0)
        {
            Sdl3.SDL_Quit();
            throw new InvalidOperationException($"[DESKTOP]: SDL_CreateWindow failed: {Sdl3.LastError}");
        }

        if (options.CaptureDirectory is not null)
        {
            // A capture run started from a terminal opens behind it, and macOS presents nothing to an occluded window.
            Sdl3.SDL_RaiseWindow(window);
            Sdl3.SDL_SyncWindow(window);
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

    /// <summary>
    /// Every key and mouse event goes to the game through <see cref="GameRuntime.Input"/>, except the host's own
    /// function keys: F5 pause/resume, F6 step one tick while paused. Closing the window (or Cmd+Q) quits.
    /// </summary>
    private int Interactive(GameRuntime runtime, WgpuRenderer renderer, nint window)
    {
        Console.WriteLine("[ukiyo] host keys: F5 pause · F6 step one tick while paused · close the window to quit");
        using var dev = DevHooks.Start(runtime, Target);
        using var audio = SdlAudioOutput.TryOpen();
        runtime.Audio = audio;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var extent = PixelExtent(window);
        while (true)
        {
            var density = Sdl3.SDL_GetWindowPixelDensity(window);
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
                    case Sdl3.EventWindowFocusLost:
                        runtime.Input.ReleaseAll();
                        break;
                    case Sdl3.EventKeyDown when sdlEvent.Key == Sdl3.KeyF5:
                        if (sdlEvent.Repeat == 0)
                        {
                            runtime.Paused = !runtime.Paused;
                            Console.WriteLine($"[ukiyo] paused={runtime.Paused} tick={runtime.Tick}");
                        }

                        break;
                    case Sdl3.EventKeyDown when sdlEvent.Key == Sdl3.KeyF6:
                        if (runtime.Paused)
                        {
                            runtime.Step(1);
                            Console.WriteLine($"snapshot {runtime.SnapshotJson()}");
                        }

                        break;
                    case Sdl3.EventKeyDown when sdlEvent.Repeat == 0:
                    case Sdl3.EventKeyUp:
                        if (MapKey(sdlEvent.Key) is var key and not Key.None)
                        {
                            runtime.Input.Push(sdlEvent.Type == Sdl3.EventKeyDown ? InputEvent.KeyDown(key) : InputEvent.KeyUp(key));
                        }

                        break;
                    case Sdl3.EventMouseMotion:
                        runtime.Input.Push(InputEvent.PointerMove(MousePixel(sdlEvent, density)));
                        break;
                    case Sdl3.EventMouseButtonDown:
                    case Sdl3.EventMouseButtonUp:
                        if (MapButton(sdlEvent.Button) is { } button)
                        {
                            var position = MousePixel(sdlEvent, density);
                            runtime.Input.Push(sdlEvent.Type == Sdl3.EventMouseButtonDown ? InputEvent.PointerDown(button, position) : InputEvent.PointerUp(button, position));
                        }

                        break;
                }
            }

            dev?.Pump();
            var now = clock.Elapsed.TotalSeconds;
            runtime.AdvanceTime(now - last);
            last = now;
            runtime.RenderFrame(extent);
            audio?.Pump();
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

    private static Vector2 MousePixel(in Sdl3.Event sdlEvent, float density) =>
        new(sdlEvent.MouseX * (density > 0 ? density : 1f), sdlEvent.MouseY * (density > 0 ? density : 1f));

    private static PointerButton? MapButton(byte button) => button switch
    {
        Sdl3.ButtonLeft => PointerButton.Left,
        Sdl3.ButtonRight => PointerButton.Right,
        Sdl3.ButtonMiddle => PointerButton.Middle,
        _ => null,
    };

    private static Key MapKey(uint keycode)
    {
        if (keycode is >= 'a' and <= 'z')
        {
            return (Key)((int)Key.A + (int)(keycode - 'a'));
        }

        if (keycode is >= '0' and <= '9')
        {
            return (Key)((int)Key.D0 + (int)(keycode - '0'));
        }

        if (keycode is >= Sdl3.KeyF1 and <= Sdl3.KeyF12)
        {
            return (Key)((int)Key.F1 + (int)(keycode - Sdl3.KeyF1));
        }

        return keycode switch
        {
            Sdl3.KeySpace => Key.Space,
            Sdl3.KeyReturn => Key.Enter,
            Sdl3.KeyEscape => Key.Escape,
            Sdl3.KeyTab => Key.Tab,
            Sdl3.KeyBackspace => Key.Backspace,
            Sdl3.KeyLeft => Key.Left,
            Sdl3.KeyRight => Key.Right,
            Sdl3.KeyUp => Key.Up,
            Sdl3.KeyDown => Key.Down,
            Sdl3.KeyLeftShift => Key.LeftShift,
            Sdl3.KeyRightShift => Key.RightShift,
            Sdl3.KeyLeftCtrl => Key.LeftControl,
            Sdl3.KeyRightCtrl => Key.RightControl,
            Sdl3.KeyLeftAlt => Key.LeftAlt,
            Sdl3.KeyRightAlt => Key.RightAlt,
            _ => Key.None,
        };
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
