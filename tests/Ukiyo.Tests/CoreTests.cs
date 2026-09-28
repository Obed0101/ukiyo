using System.Numerics;
using System.Reflection;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.RotatingCube;
using Xunit;

namespace Ukiyo.Tests;

public sealed class CoreTests
{
    private const float Tolerance = 1e-6f;

    internal static async Task<(GameRuntime Runtime, NullRenderer Renderer)> StartCubeAsync()
    {
        var runtime = new GameRuntime(new CubeGame(), new SourceIdentity(new Dictionary<string, string>()));
        var renderer = new NullRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", new RenderExtent(640, 480, 1f)));
        return (runtime, renderer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(600)]
    public async Task Cube_rotation_at_checkpoint_matches_the_rotation_law_without_wall_clock(long tick)
    {
        var (runtime, _) = await StartCubeAsync();
        runtime.StepTo(tick);

        var rotation = runtime.Snapshot().Entities["cube"].Rotation;
        var expected = CubeGame.ExpectedRotation(tick);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, expected)) > 1 - Tolerance, $"tick {tick}: {rotation} vs {expected}");
    }

    [Fact]
    public async Task Stepping_600_ticks_in_one_call_or_one_by_one_reaches_the_same_state()
    {
        var (bulk, _) = await StartCubeAsync();
        var (single, _) = await StartCubeAsync();
        bulk.Step(600);
        for (var i = 0; i < 600; i++)
        {
            single.Step(1);
        }

        Assert.Equal(bulk.Snapshot().Entities["cube"], single.Snapshot().Entities["cube"]);
    }

    [Fact]
    public async Task Paused_runtime_ignores_elapsed_time_but_still_steps_explicitly()
    {
        var (runtime, _) = await StartCubeAsync();
        runtime.Paused = true;
        runtime.AdvanceTime(5.0);
        Assert.Equal(0, runtime.Tick);

        runtime.Step(3);
        Assert.Equal(3, runtime.Tick);
    }

    [Fact]
    public void Fixed_clock_caps_catch_up_and_counts_dropped_ticks_instead_of_hiding_them()
    {
        var clock = new FixedClock { MaxCatchUpTicks = 5 };
        Assert.Equal(5, clock.Consume(1.0));
        Assert.Equal(55, clock.DroppedTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Consume(double.NaN));
    }

    [Fact]
    public void Fixed_clock_produces_60_ticks_per_second_across_uneven_frames()
    {
        var clock = new FixedClock();
        var ticks = 0;
        foreach (var dt in Enumerable.Repeat(1.0 / 144, 144))
        {
            ticks += clock.Consume(dt);
        }

        Assert.InRange(ticks, 59, 60);
    }

    [Fact]
    public async Task Headless_runtime_renders_through_null_renderer_with_one_mesh_and_material()
    {
        var (runtime, renderer) = await StartCubeAsync();
        runtime.StepTo(60);
        var packet = runtime.RenderFrame(new RenderExtent(640, 480, 1f));

        Assert.Equal(60, packet.Tick);
        Assert.Single(packet.Instances);
        Assert.Equal(1, renderer.FramesRendered);
        Assert.Equal(1, renderer.LiveMeshes);
        Assert.Equal(1, renderer.LiveMaterials);
    }

    [Fact]
    public void Null_renderer_assembly_depends_only_on_render_contracts()
    {
        var references = typeof(NullRenderer).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        Assert.Contains("Ukiyo.Render", references);
        Assert.DoesNotContain(references, name => name!.Contains("Wgpu") || name.Contains("Three") || name.Contains("SDL") || name.Contains("JavaScript"));
    }

    [Fact]
    public void Null_renderer_has_no_capture_capability()
    {
        using var renderer = new NullRenderer();
        Assert.False(renderer.Capabilities.SupportsCapture);
        Assert.False(typeof(IRenderCapture).IsAssignableFrom(typeof(NullRenderer)));
    }

    [Fact]
    public void Shared_game_sources_contain_no_platform_branches_or_backend_references()
    {
        var shared = Path.Combine(RepoRoot(), "samples", "RotatingCube", "Shared");
        foreach (var file in Directory.GetFiles(shared, "*.cs"))
        {
            var code = File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToArray();
            Assert.DoesNotContain(code, line => line.TrimStart().StartsWith('#'));
            foreach (var forbidden in new[] { "Wgpu", "THREE", "Three.", "SDL", "JSImport", "Metal" })
            {
                Assert.False(code.Any(line => line.Contains(forbidden, StringComparison.Ordinal)), $"{Path.GetFileName(file)} references {forbidden}");
            }
        }
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("[TEST]: repository root not found");
    }
}
