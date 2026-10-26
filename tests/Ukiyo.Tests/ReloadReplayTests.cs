using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.LanternRun;
using Ukiyo.Samples.SceneHop;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// Controlled reload (Studio, ukiyo_live reload): a fresh runtime of the same code, given the replay log of a running
/// one, reaches the same snapshot and the same pixels at the same tick.
/// </summary>
public sealed class ReloadReplayTests
{
    private static readonly SourceIdentity Source = new(new Dictionary<string, string>());
    private static readonly RenderExtent Extent = new(640, 360, 1f);

    private static async Task<GameRuntime> StartAsync(IGame game, IRenderer renderer, RenderExtent? extent = null)
    {
        var runtime = new GameRuntime(game, Source);
        await runtime.StartAsync(renderer, new RenderConfiguration("reload", extent ?? Extent));
        return runtime;
    }

    private static void Key(GameRuntime runtime, string key, bool down) =>
        runtime.Input.Push(down ? InputEvent.KeyDown(KeyNames.Parse(key)) : InputEvent.KeyUp(KeyNames.Parse(key)));

    /// <summary>Plays LanternRun the way a person does: Enter on the title, then runs right, pointing the mouse around.</summary>
    private static void Play(GameRuntime runtime)
    {
        runtime.Step(61);
        Key(runtime, "Enter", true);
        runtime.Step(1);
        Key(runtime, "Enter", false);
        Key(runtime, "Right", true);
        runtime.Input.Push(InputEvent.PointerMove(new(10, 10)));
        runtime.Input.Push(InputEvent.PointerMove(new(300.5f, 200.25f)));
        runtime.Step(90);
        Key(runtime, "Right", false);
        runtime.Input.Push(InputEvent.PointerDown(PointerButton.Left, new(120, 80)));
        runtime.Step(7);
        runtime.Input.ReleaseAll();
        runtime.Step(33);
    }

    [Fact]
    public async Task A_fresh_LanternRun_replaying_the_log_restores_the_same_snapshot_sound_and_pixels()
    {
        var oldGame = new LanternRunGame();
        using var oldRenderer = new SoftwareRenderer();
        var old = await StartAsync(oldGame, oldRenderer);
        Play(old);
        Assert.Equal(RunState.Playing, oldGame.State);
        Assert.True(oldGame.Player.Position.X > LanternRunGame.Spawn.X + 5, "the played session must have moved the player");

        var (log, tick) = ReplayLog.Parse(old.ExportHistoryJson());
        Assert.Equal(old.Tick, tick);
        Assert.False(log.Truncated);

        var game = new LanternRunGame();
        using var renderer = new SoftwareRenderer();
        var restored = await StartAsync(game, renderer);
        restored.RestoreFrom(log, tick);

        Assert.Equal(old.Tick, restored.Tick);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());
        var oldPng = (await old.CaptureAsync())!.Png;
        var newPng = (await restored.CaptureAsync())!.Png;
        Assert.Equal(oldPng, newPng);

        // Both go on from the restored tick identically (the replayed history is a real history, not a snapshot copy).
        Key(old, "Left", true);
        Key(restored, "Left", true);
        old.Step(40);
        restored.Step(40);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());
    }

    [Fact]
    public async Task A_restored_runtime_keeps_a_complete_log_so_it_can_be_reloaded_again()
    {
        using var firstRenderer = new SoftwareRenderer();
        var first = await StartAsync(new LanternRunGame(), firstRenderer);
        Play(first);
        var (log, tick) = ReplayLog.Parse(first.ExportHistoryJson());

        using var secondRenderer = new SoftwareRenderer();
        var second = await StartAsync(new LanternRunGame(), secondRenderer);
        second.RestoreFrom(log, tick);
        second.Step(25);

        using var thirdRenderer = new SoftwareRenderer();
        var third = await StartAsync(new LanternRunGame(), thirdRenderer);
        var (again, againTick) = ReplayLog.Parse(second.ExportHistoryJson());
        third.RestoreFrom(again, againTick);
        Assert.Equal(second.SnapshotJson(), third.SnapshotJson());
    }

    [Fact]
    public async Task Input_queued_but_not_yet_latched_survives_the_reload()
    {
        var oldGame = new LanternRunGame();
        using var oldRenderer = new SoftwareRenderer();
        var old = await StartAsync(oldGame, oldRenderer);
        old.Step(61);
        Key(old, "Enter", true);

        var (log, tick) = ReplayLog.Parse(old.ExportHistoryJson());
        using var renderer = new SoftwareRenderer();
        var restored = await StartAsync(new LanternRunGame(), renderer);
        restored.RestoreFrom(log, tick);
        Assert.Single(restored.Input.Pending);

        old.Step(5);
        restored.Step(5);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());
    }

    [Fact]
    public async Task SceneHop_patches_including_a_refused_one_replay_to_the_same_snapshot()
    {
        var old = await StartAsync(new SceneHopGame(), new NullRenderer(), new RenderExtent(1280, 720, 1f));
        foreach (var patch in ScenePatch.ParseLog("""
            [
              {"id":"p1","tick":12,"ops":[{"op":"spawn","id":"bat-3","prefab":"drifter","position":[-5,-1],"values":{"speed":3}}]},
              {"id":"p2","tick":40,"ops":[{"op":"retarget","id":"lamp-east","position":[2,1],"values":{"glow":0.25}}]}
            ]
            """))
        {
            old.SubmitPatch(patch);
        }

        old.Step(40);
        // Applied at the boundary we are on; a second one for a tick that already passed is refused and must not matter.
        old.SubmitPatch(ScenePatch.Parse("""{"id":"live","tick":40,"ops":[{"op":"retarget","id":"lamp-east","values":{"glow":0.5}}]}"""));
        Assert.ThrowsAny<SceneException>(() => old.SubmitPatch(ScenePatch.Parse("""{"id":"late","tick":3,"ops":[{"op":"dispose","id":"moth"}]}""")));
        old.Step(35);
        old.SubmitPatch(ScenePatch.Parse("""{"id":"queued","tick":90,"ops":[{"op":"dispose","id":"bat-2"}]}"""));

        var (log, tick) = ReplayLog.Parse(old.ExportHistoryJson());
        var restored = await StartAsync(new SceneHopGame(), new NullRenderer(), new RenderExtent(1280, 720, 1f));
        restored.RestoreFrom(log, tick);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());

        // The patch that was only queued at the time of the reload still fires at its tick in both.
        old.Step(20);
        restored.Step(20);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());
    }

    [Fact]
    public async Task A_different_window_size_is_replayed_as_it_was_and_the_new_one_is_kept()
    {
        var old = await StartAsync(new LanternRunGame(), new NullRenderer(), new RenderExtent(800, 600, 1f));
        old.Step(61);
        old.RenderFrame(new RenderExtent(1000, 700, 1f));
        old.Input.Push(InputEvent.PointerMove(new(500, 350)));
        old.Step(3);

        var (log, tick) = ReplayLog.Parse(old.ExportHistoryJson());
        Assert.Equal(2, log.Viewports.Count);
        var restored = await StartAsync(new LanternRunGame(), new NullRenderer(), new RenderExtent(640, 360, 1f));
        restored.RestoreFrom(log, tick);
        Assert.Equal(new RenderExtent(640, 360, 1f), restored.Viewport);
        Assert.Equal(old.SnapshotJson(), restored.SnapshotJson());
    }

    [Fact]
    public async Task A_truncated_or_used_runtime_refuses_to_restore()
    {
        var old = await StartAsync(new LanternRunGame(), new NullRenderer());
        old.Step(10);
        var (log, tick) = ReplayLog.Parse(old.ExportHistoryJson());

        var used = await StartAsync(new LanternRunGame(), new NullRenderer());
        used.Step(1);
        Assert.Contains("fresh runtime", Assert.Throws<InvalidOperationException>(() => used.RestoreFrom(log, tick)).Message);

        var truncated = ReplayLog.Parse(old.ExportHistoryJson().Replace("\"truncated\":false", "\"truncated\":true", StringComparison.Ordinal)).Log;
        var fresh = await StartAsync(new LanternRunGame(), new NullRenderer());
        Assert.Contains("incomplete", Assert.Throws<InvalidOperationException>(() => fresh.RestoreFrom(truncated, tick)).Message);
        Assert.Throws<FormatException>(() => ReplayLog.Parse("""{"version":2,"tick":0}"""));
    }
}
