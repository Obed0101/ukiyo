using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;

namespace Ukiyo.Hosting;

/// <summary>Headless bootstrap: NullRenderer, explicit tick advance, JSON snapshots on stdout.</summary>
public static class PlatformBootstrap
{
    public static IPlatformHost Create(string[] args) => new HeadlessHost(HeadlessOptions.Parse(args));
}

public sealed record HeadlessOptions(long[] Checkpoints)
{
    public static readonly long[] DefaultCheckpoints = [0, 60, 180, 600];

    public static HeadlessOptions Parse(string[] args)
    {
        var checkpoints = DefaultCheckpoints;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--checkpoints" && i + 1 < args.Length)
            {
                checkpoints = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Order()];
            }
        }

        return new HeadlessOptions(checkpoints);
    }
}

/// <summary>Runs the game with no window, GPU or DOM. Each checkpoint prints one JSON line prefixed with "snapshot ".</summary>
public sealed class HeadlessHost(HeadlessOptions options) : IPlatformHost
{
    public string Target => "headless";

    public async Task<int> RunAsync(GameRuntime runtime)
    {
        using var renderer = new NullRenderer();
        var extent = new RenderExtent(1280, 720, 1f);
        await runtime.StartAsync(renderer, new RenderConfiguration("ukiyo-headless", extent));
        runtime.Paused = true;
        foreach (var checkpoint in options.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(extent);
            Console.WriteLine($"snapshot {runtime.SnapshotJson()}");
        }

        Console.WriteLine($"[ukiyo] null frames={renderer.FramesRendered} instances={renderer.InstancesSubmitted} meshes={renderer.LiveMeshes} materials={renderer.LiveMaterials}");
        return 0;
    }
}
