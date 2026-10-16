using System.Numerics;
using System.Text;
using System.Text.Json;
using Ukiyo.Rendering;
using Ukiyo.Samples.SceneHop;
using Xunit;

namespace Ukiyo.Tests;

public sealed class ScenePatchTests
{
    private static ScenePatch Patch(string id, long tick, params PatchOp[] ops) => new(id, tick, ops);

    private static SpawnOp Dot(string id, float x, double? v = null) =>
        new(id, "dot", new Vector3(x, 0, 0)) { Values = v is { } value ? new Dictionary<string, double> { ["v"] = value } : SceneValues.None };

    private static JsonElement SceneJson(GameRuntime runtime) => JsonDocument.Parse(runtime.SnapshotJson()).RootElement.GetProperty("scene");

    [Fact]
    public async Task A_spawn_patch_for_a_future_tick_applies_at_that_boundary_and_the_snapshot_records_its_id()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.SubmitPatch(Patch("add-a3", 5, Dot("a3", 3, v: 4)));

        runtime.StepTo(4);
        Assert.False(game.Scenes.TryGet("a3", out _));
        Assert.Single(game.Scenes.PendingPatches);

        runtime.StepTo(5);
        Assert.Equal(new Vector3(3, 0, 0), game.Scenes["a3"].Position);
        Assert.Equal(4, game.Scenes["a3"].Value);
        var patches = SceneJson(runtime).GetProperty("patches");
        Assert.Equal("add-a3", patches[0].GetProperty("id").GetString());
        Assert.Equal(5, patches[0].GetProperty("tick").GetInt64());
        Assert.Empty(game.Scenes.PendingPatches);
    }

    [Fact]
    public async Task A_patch_for_the_current_paused_boundary_applies_immediately()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.StepTo(30);
        runtime.SubmitPatch(Patch("now", 30, new DisposeOp("a1")));

        Assert.False(game.Scenes.TryGet("a1", out _));
        Assert.Equal([new PatchRecord("now", 30)], runtime.SceneState!.Applied);
        Assert.Equal(["a1"], game.Disposed);
    }

    [Fact]
    public async Task Retarget_updates_in_place_through_the_prefab_hook_and_keeps_unset_values()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        var before = game.Scenes["a2"];
        runtime.SubmitPatch(Patch("move-a2", 0, new RetargetOp("a2") { Position = new Vector3(-2, 1, 0) }));

        Assert.Same(before, game.Scenes["a2"]);
        Assert.Equal(new Vector3(-2, 1, 0), before.Position);
        Assert.Equal(3, before.Value);
        Assert.Equal(1, game.Scenes.Entities.ToList().FindIndex(e => e.Id == "a2"));
    }

    [Fact]
    public async Task Retarget_without_a_hook_disposes_and_rebuilds_the_entity_from_merged_values()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.SubmitPatch(Patch("ring", 0, new SpawnOp("r", "ring", new Vector3(0, 1, 0)) { Values = new Dictionary<string, double> { ["r"] = 1.5 } }));
        var before = game.Scenes["r"];
        runtime.SubmitPatch(Patch("ring-up", 0, new RetargetOp("r") { Position = new Vector3(0, 2, 0) }));

        var after = game.Scenes["r"];
        Assert.NotSame(before, after);
        Assert.Equal(1.5, after.Value);
        Assert.Equal(new Vector3(0, 2, 0), after.Position);
        Assert.Equal(["r"], game.Disposed);
    }

    [Fact]
    public async Task Ops_run_in_order_so_one_patch_can_replace_an_entity_and_tune_it()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.SubmitPatch(Patch("swap", 0, new DisposeOp("a1"), Dot("a1", -1), new RetargetOp("a1") { Values = new Dictionary<string, double> { ["v"] = 9 } }));

        Assert.Equal(new Vector3(-1, 0, 0), game.Scenes["a1"].Position);
        Assert.Equal(9, game.Scenes["a1"].Value);
        Assert.Equal(["a2", "a1"], game.Scenes.Entities.Select(e => e.Id));
    }

    public static TheoryData<string, ScenePatch, SceneErrorCode> SubmitRejections => new()
    {
        { "past tick", Patch("late", 9, Dot("x", 0)), SceneErrorCode.PastTick },
        { "negative tick", Patch("neg", -1, Dot("x", 0)), SceneErrorCode.PastTick },
        { "unknown prefab", Patch("ghost", 20, new SpawnOp("x", "ghost", Vector3.Zero)), SceneErrorCode.UnknownPrefab },
        { "value above range", Patch("hot", 20, Dot("x", 0, v: 10.5)), SceneErrorCode.OutOfRange },
        { "value not finite", Patch("nan", 20, Dot("x", 0, v: double.NaN)), SceneErrorCode.OutOfRange },
        { "position not finite", Patch("inf", 20, new SpawnOp("x", "dot", new Vector3(float.PositiveInfinity, 0, 0))), SceneErrorCode.OutOfRange },
        { "unknown parameter", Patch("param", 20, new SpawnOp("x", "dot", Vector3.Zero) { Values = new Dictionary<string, double> { ["speed"] = 1 } }), SceneErrorCode.UnknownParameter },
        { "no ops", Patch("empty", 20), SceneErrorCode.Malformed },
        { "retarget changes nothing", Patch("noop", 20, new RetargetOp("a1")), SceneErrorCode.Malformed },
        { "bad patch id", Patch("no spaces", 20, Dot("x", 0)), SceneErrorCode.InvalidId },
        { "bad entity id", Patch("id", 20, Dot("x/y", 0)), SceneErrorCode.InvalidId },
        { "unknown scene", Patch("where", 20, Dot("x", 0)) with { Scene = "gamma" }, SceneErrorCode.UnknownScene },
        { "already submitted", Patch("dup", 20, Dot("x", 0)), SceneErrorCode.DuplicatePatch },
    };

    [Theory]
    [MemberData(nameof(SubmitRejections))]
    public async Task Patches_refused_at_submit_throw_a_typed_error_and_leave_state_unchanged(string scenario, ScenePatch patch, SceneErrorCode expected)
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.StepTo(10);
        if (expected == SceneErrorCode.DuplicatePatch)
        {
            runtime.SubmitPatch(Patch("dup", 15, Dot("y", 0)));
        }

        var before = runtime.SnapshotJson();
        var pending = game.Scenes.PendingPatches.Count;
        var error = Assert.Throws<SceneException>(() => runtime.SubmitPatch(patch));

        Assert.True(expected == error.Code, $"{scenario}: expected {expected}, got {error.Code} ({error.Message})");
        Assert.StartsWith($"[SCENE:{expected}]: ", error.Message);
        Assert.Equal(before, runtime.SnapshotJson());
        Assert.Equal(pending, game.Scenes.PendingPatches.Count);
    }

    public static TheoryData<string, ScenePatch, SceneErrorCode> TickRejections => new()
    {
        { "dispose unknown entity", Patch("d", 5, new DisposeOp("nobody")), SceneErrorCode.UnknownEntity },
        { "retarget unknown entity", Patch("r", 5, new RetargetOp("nobody") { Position = Vector3.Zero }), SceneErrorCode.UnknownEntity },
        { "spawn over a live id", Patch("s", 5, Dot("a1", 0)), SceneErrorCode.DuplicateEntity },
        { "spawn outside the scene bounds", Patch("far", 5, Dot("x", 50)), SceneErrorCode.OutOfRange },
        { "retarget outside the scene bounds", Patch("away", 5, new RetargetOp("a1") { Position = new Vector3(0, -6, 0) }), SceneErrorCode.OutOfRange },
        { "retarget value above range", Patch("hot", 5, new RetargetOp("a1") { Values = new Dictionary<string, double> { ["v"] = 11 } }), SceneErrorCode.OutOfRange },
        { "retarget unknown parameter", Patch("p", 5, new RetargetOp("a1") { Values = new Dictionary<string, double> { ["r"] = 1 } }), SceneErrorCode.UnknownParameter },
        { "wrong scene", Patch("w", 5, Dot("x", 0)) with { Scene = "beta" }, SceneErrorCode.WrongScene },
        { "valid op then invalid op", Patch("half", 5, Dot("x", 0), new DisposeOp("nobody")), SceneErrorCode.UnknownEntity },
    };

    [Theory]
    [MemberData(nameof(TickRejections))]
    public async Task Patches_refused_at_their_tick_are_recorded_with_a_code_and_change_nothing(string scenario, ScenePatch patch, SceneErrorCode expected)
    {
        var (control, _) = await ProbeGame.StartInAlphaAsync();
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.SubmitPatch(patch);
        control.StepTo(5);
        runtime.StepTo(5);

        var rejection = Assert.Single(game.Scenes.RejectedPatches);
        Assert.True(expected == rejection.Code, $"{scenario}: expected {expected}, got {rejection.Code} ({rejection.Message})");
        Assert.Equal((patch.Id, 5L), (rejection.Id, rejection.Tick));
        Assert.Empty(game.Scenes.AppliedPatches);
        Assert.Equal(control.Snapshot().Entities, runtime.Snapshot().Entities);
        Assert.Empty(game.Disposed);
        var recorded = SceneJson(runtime).GetProperty("rejected")[0];
        Assert.Equal(expected.ToString(), recorded.GetProperty("code").GetString());
        Assert.Equal(patch.Id, recorded.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_world_dependent_refusal_at_the_current_boundary_throws_and_is_recorded_like_a_queued_one()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        runtime.StepTo(3);
        var entities = runtime.Snapshot().Entities;

        var error = Assert.Throws<SceneException>(() => runtime.SubmitPatch(Patch("half", 3, Dot("x", 0), new DisposeOp("nobody"))));
        Assert.Equal(SceneErrorCode.UnknownEntity, error.Code);
        Assert.Equal(entities, runtime.Snapshot().Entities);
        Assert.Equal(new PatchRejection("half", 3, SceneErrorCode.UnknownEntity, error.Message), Assert.Single(game.Scenes.RejectedPatches));

        Assert.Equal(SceneErrorCode.DuplicatePatch, Assert.Throws<SceneException>(() => runtime.SubmitPatch(Patch("half", 3, Dot("x", 0)))).Code);
        runtime.SubmitPatch(Patch("half-2", 3, Dot("x", 0)));
        Assert.True(game.Scenes.TryGet("x", out _));
    }

    [Fact]
    public async Task Game_code_cannot_patch_the_tick_it_is_running_but_can_patch_the_next()
    {
        SceneException? refused = null;
        var (runtime, game) = await ProbeGame.StartInAlphaAsync((scenes, tick) =>
        {
            if (tick.Tick != 7)
            {
                return;
            }

            refused = Assert.Throws<SceneException>(() => scenes.Submit(new ScenePatch("same-tick", 7, [new DisposeOp("a1")])));
            scenes.Submit(new ScenePatch("next-tick", 8, [new DisposeOp("a1")]));
            Assert.True(scenes.TryGet("a1", out _));
        });

        runtime.StepTo(8);
        Assert.Equal(SceneErrorCode.PastTick, refused!.Code);
        Assert.False(game.Scenes.TryGet("a1", out _));
        Assert.Equal([new PatchRecord("next-tick", 8)], runtime.SceneState!.Applied);
    }

    [Fact]
    public async Task Patches_due_at_a_scene_change_apply_after_the_load()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync((scenes, tick) =>
        {
            if (tick.Tick == 9)
            {
                scenes.LoadScene("beta");
            }
        });
        runtime.SubmitPatch(Patch("beta-only", 10, Dot("b2", 1)) with { Scene = "beta" });
        runtime.SubmitPatch(Patch("alpha-spawn", 4, Dot("a3", 1)));

        runtime.StepTo(10);
        Assert.Equal(["b1", "b2"], game.Scenes.Entities.Select(e => e.Id));
        Assert.Equal(["alpha-spawn", "beta-only"], runtime.SceneState!.Applied.Select(p => p.Id));
        Assert.Equal(["a3", "a2", "a1"], game.Disposed);
    }

    [Fact]
    public async Task The_same_patch_log_gives_the_same_snapshot_sequence_whether_queued_up_front_or_submitted_live()
    {
        const string log = """
            [
              {"id":"p1","tick":12,"ops":[{"op":"spawn","id":"bat-3","prefab":"drifter","position":[-5,-1],"values":{"speed":3}}]},
              {"id":"p2","tick":40,"ops":[{"op":"retarget","id":"lamp-east","position":[2,1],"values":{"glow":0.25}}]},
              {"id":"p3","tick":75,"scene":"cavern","ops":[{"op":"dispose","id":"bat-2"},{"op":"retarget","id":"bat-1","values":{"speed":6}}]},
              {"id":"p4","tick":80,"ops":[{"op":"dispose","id":"moth"}]}
            ]
            """;
        long[] checkpoints = [0, 12, 30, 40, 59, 60, 75, 80, 119, 120];

        async Task<List<string>> Run(bool upFront)
        {
            var (runtime, _) = await SceneTests.StartSceneHopAsync();
            var patches = ScenePatch.ParseLog(log);
            if (upFront)
            {
                foreach (var patch in patches)
                {
                    runtime.SubmitPatch(patch);
                }
            }

            var snapshots = new List<string>();
            foreach (var checkpoint in checkpoints)
            {
                runtime.StepTo(checkpoint);
                if (!upFront)
                {
                    foreach (var patch in patches.Where(p => p.Tick == checkpoint))
                    {
                        try
                        {
                            runtime.SubmitPatch(patch);
                        }
                        catch (SceneException refused) when (patch.Id == "p4")
                        {
                            Assert.Equal(SceneErrorCode.UnknownEntity, refused.Code);
                        }
                    }
                }

                runtime.RenderFrame(new RenderExtent(640, 360, 1f));
                snapshots.Add(runtime.SnapshotJson());
            }

            return snapshots;
        }

        var queued = await Run(upFront: true);
        Assert.Equal(queued, await Run(upFront: true));
        Assert.Equal(queued, await Run(upFront: false));

        var last = JsonDocument.Parse(queued[^1]).RootElement;
        Assert.Equal(["p1", "p2", "p3"], last.GetProperty("scene").GetProperty("patches").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        var rejected = last.GetProperty("scene").GetProperty("rejected")[0];
        Assert.Equal(("p4", "UnknownEntity"), (rejected.GetProperty("id").GetString(), rejected.GetProperty("code").GetString()));
        var at75 = JsonDocument.Parse(queued[6]).RootElement.GetProperty("entities");
        Assert.False(at75.TryGetProperty("bat-2", out _));
    }

    [Fact]
    public void Patch_json_round_trips_and_a_log_reads_back_in_order()
    {
        const string json = """{"id":"p1","tick":12,"scene":"meadow","ops":[{"op":"spawn","id":"e","prefab":"drifter","position":[1,2],"values":{"speed":3,"size":1}},{"op":"retarget","id":"e","position":[0,0,1]},{"op":"dispose","id":"e"}]}""";
        var patch = ScenePatch.Parse(json);

        Assert.Equal("meadow", patch.Scene);
        Assert.IsType<SpawnOp>(patch.Ops[0]);
        Assert.Equal(new Vector3(0, 0, 1), ((RetargetOp)patch.Ops[1]).Position);
        Assert.IsType<DisposeOp>(patch.Ops[2]);
        Assert.Equal(patch.ToJson(), ScenePatch.Parse(patch.ToJson()).ToJson());
        Assert.Contains("\"values\":{\"size\":1,\"speed\":3}", patch.ToJson());

        var log = ScenePatch.WriteLog([patch, new ScenePatch("p2", 20, [new DisposeOp("x")])]);
        Assert.Equal(["p1", "p2"], ScenePatch.ParseLog(log).Select(p => p.Id));
        Assert.Equal("[]", ScenePatch.WriteLog([]));
    }

    [Theory]
    [InlineData("""{"id":"p","tick":1,"ops":[{"op":"teleport","id":"e"}]}""")]
    [InlineData("""{"id":"p","tick":1.5,"ops":[]}""")]
    [InlineData("""{"id":"p","tick":1,"ops":[{"op":"dispose","id":"e","prefab":"x"}]}""")]
    [InlineData("""{"id":"p","tick":1,"ops":[],"when":"now"}""")]
    [InlineData("""{"id":"p","ops":[]}""")]
    [InlineData("""{"id":"p","tick":1,"ops":[{"op":"spawn","id":"e","prefab":"x"}]}""")]
    [InlineData("""[{"id":"p"}""")]
    public void Malformed_patch_json_is_refused_with_the_malformed_code(string json)
    {
        var error = Assert.Throws<SceneException>(() => ScenePatch.Parse(json));
        Assert.Equal(SceneErrorCode.Malformed, error.Code);
    }

    /// <summary>Patch i retargets a1 (applied) except every fourth, which disposes a missing entity (rejected at its tick).</summary>
    private static ScenePatch LongLogEntry(int i) =>
        i % 4 == 0
            ? Patch($"p{i:D5}", i, new DisposeOp("nobody"))
            : Patch($"p{i:D5}", i, new RetargetOp("a1") { Values = new Dictionary<string, double> { ["v"] = i % 10 } });

    private static async Task<List<string>> RunLongLog(int count, long[] checkpoints, bool upFront)
    {
        var (runtime, _) = await ProbeGame.StartInAlphaAsync();
        var log = Enumerable.Range(1, count).Select(LongLogEntry).ToList();
        if (upFront)
        {
            log.ForEach(runtime.SubmitPatch);
        }

        var snapshots = new List<string>();
        var next = 0;
        foreach (var checkpoint in checkpoints)
        {
            if (upFront)
            {
                runtime.StepTo(checkpoint);
            }
            else
            {
                // Paused at each boundary, submitted for that boundary: applied now or refused now (thrown and recorded).
                for (var tick = runtime.Tick; tick <= checkpoint; tick++)
                {
                    runtime.StepTo(tick);
                    for (; next < log.Count && log[next].Tick == tick; next++)
                    {
                        try
                        {
                            runtime.SubmitPatch(log[next]);
                        }
                        catch (SceneException refused)
                        {
                            Assert.Equal(SceneErrorCode.UnknownEntity, refused.Code);
                        }
                    }
                }
            }

            snapshots.Add(runtime.SnapshotJson());
        }

        return snapshots;
    }

    [Fact]
    public async Task A_ten_thousand_patch_session_keeps_snapshots_under_a_fixed_byte_ceiling()
    {
        // 16 rejected entries of ~136 bytes, 16 applied of ~28, two probes and the counters: about 3 KB whatever the
        // session length. The unbounded format listed all 10 000 entries, about 560 KB for this log.
        const int ceiling = 4096;
        long[] checkpoints = [16, 100, 1_000, 5_000, 10_000];
        var snapshots = await RunLongLog(10_000, checkpoints, upFront: true);

        Assert.All(snapshots, snapshot => Assert.InRange(Encoding.UTF8.GetByteCount(snapshot), 1, ceiling));
        // Once both windows are full (tick 64), 9 900 more patches only lengthen numbers: each of the 32 listed ticks and
        // ids grows from 2-3 to 4-5 digits, plus the tick and both counters. That is 70 bytes here, never one more entry.
        Assert.InRange(snapshots[^1].Length - snapshots[1].Length, 0, 2 * PatchHistory.Window * 2 + 8);

        var scene = JsonDocument.Parse(snapshots[^1]).RootElement.GetProperty("scene");
        Assert.Equal(7_500, scene.GetProperty("appliedCount").GetInt64());
        Assert.Equal(2_500, scene.GetProperty("rejectedCount").GetInt64());
        Assert.Equal(PatchHistory.Window, scene.GetProperty("patches").GetArrayLength());
        Assert.Equal(PatchHistory.Window, scene.GetProperty("rejected").GetArrayLength());
        Assert.Equal("p09999", scene.GetProperty("patches")[PatchHistory.Window - 1].GetProperty("id").GetString());
        Assert.Equal("p10000", scene.GetProperty("rejected")[PatchHistory.Window - 1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_same_long_patch_log_gives_identical_snapshots_twice_and_live_and_the_hashes_replay_from_the_log()
    {
        long[] checkpoints = [0, 15, 16, 17, 64, 333, 1_200];
        var queued = await RunLongLog(1_200, checkpoints, upFront: true);

        Assert.Equal(queued, await RunLongLog(1_200, checkpoints, upFront: true));
        Assert.Equal(queued, await RunLongLog(1_200, checkpoints, upFront: false));

        // The chains are recomputable from the log alone: every entry reached its tick, so the outcome is known.
        var log = Enumerable.Range(1, 1_200).Select(LongLogEntry).ToList();
        var applied = log.Where(p => p.Tick % 4 != 0).Select(p => new PatchRecord(p.Id, p.Tick));
        var rejected = log.Where(p => p.Tick % 4 == 0).Select(p => new PatchRejection(p.Id, p.Tick, SceneErrorCode.UnknownEntity, ""));
        var scene = JsonDocument.Parse(queued[^1]).RootElement.GetProperty("scene");
        Assert.Equal(PatchHistory.Format(PatchHistory.AppliedHash(applied)), scene.GetProperty("appliedHash").GetString());
        Assert.Equal(PatchHistory.Format(PatchHistory.RejectedHash(rejected)), scene.GetProperty("rejectedHash").GetString());

        // A different log (one patch moved by a tick) with the same counts gives a different chain.
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        foreach (var patch in log.Select(p => p.Id == "p00001" ? p with { Tick = 2 } : p))
        {
            runtime.SubmitPatch(patch);
        }

        runtime.StepTo(1_200);
        Assert.Equal(game.Scenes.AppliedCount, scene.GetProperty("appliedCount").GetInt64());
        Assert.NotEqual(scene.GetProperty("appliedHash").GetString(), PatchHistory.Format(game.Scenes.AppliedHash));
    }

    [Fact]
    public async Task The_applied_window_evicts_the_oldest_patch_first_and_keeps_application_order()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        for (var i = 1; i <= 20; i++)
        {
            runtime.SubmitPatch(Patch($"p{i:D2}", i, new RetargetOp("a1") { Values = new Dictionary<string, double> { ["v"] = i % 10 } }));
        }

        runtime.StepTo(16);
        Assert.Equal([.. Enumerable.Range(1, 16).Select(i => $"p{i:D2}")], game.Scenes.AppliedPatches.Select(p => p.Id));

        runtime.StepTo(17);
        Assert.Equal("p02", game.Scenes.AppliedPatches[0].Id);
        Assert.Equal("p17", game.Scenes.AppliedPatches[^1].Id);

        runtime.StepTo(20);
        string[] expected = [.. Enumerable.Range(5, 16).Select(i => $"p{i:D2}")];
        Assert.Equal(expected, game.Scenes.AppliedPatches.Select(p => p.Id));
        Assert.Equal(expected, runtime.SceneState!.Applied.Select(p => p.Id));
        var scene = SceneJson(runtime);
        Assert.Equal(expected, scene.GetProperty("patches").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(20, scene.GetProperty("appliedCount").GetInt64());
        var all = Enumerable.Range(1, 20).Select(i => new PatchRecord($"p{i:D2}", i));
        Assert.Equal(PatchHistory.Format(PatchHistory.AppliedHash(all)), scene.GetProperty("appliedHash").GetString());
    }

    [Fact]
    public async Task Rejected_entries_are_bounded_by_the_same_window_and_counted_in_full()
    {
        var (runtime, game) = await ProbeGame.StartInAlphaAsync();
        for (var i = 1; i <= 40; i++)
        {
            runtime.SubmitPatch(Patch($"r{i:D2}", i, new DisposeOp("nobody")));
        }

        runtime.StepTo(40);
        Assert.Equal([.. Enumerable.Range(25, 16).Select(i => $"r{i:D2}")], game.Scenes.RejectedPatches.Select(r => r.Id));
        Assert.Equal(40, game.Scenes.RejectedCount);
        Assert.Equal(0, game.Scenes.AppliedCount);
        var scene = SceneJson(runtime);
        Assert.Equal(PatchHistory.Window, scene.GetProperty("rejected").GetArrayLength());
        Assert.Equal(40, scene.GetProperty("rejectedCount").GetInt64());
        Assert.Equal(PatchHistory.Format(PatchHistory.EmptyHash), scene.GetProperty("appliedHash").GetString());
        var all = Enumerable.Range(1, 40).Select(i => new PatchRejection($"r{i:D2}", i, SceneErrorCode.UnknownEntity, ""));
        Assert.Equal(PatchHistory.Format(PatchHistory.RejectedHash(all)), scene.GetProperty("rejectedHash").GetString());

        // A refused patch's id stays used after it leaves the window.
        Assert.Equal(SceneErrorCode.DuplicatePatch, Assert.Throws<SceneException>(() => runtime.SubmitPatch(Patch("r01", 50, new DisposeOp("a1")))).Code);
    }

    /// <summary>Plain FNV-1a 64 over bytes, checked against the published vectors; the chain must equal it on the documented encoding.</summary>
    private static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 0x100000001b3UL;
        }

        return hash;
    }

    [Fact]
    public void The_hash_chain_is_fnv1a_64_over_the_documented_byte_encoding()
    {
        Assert.Equal(0xcbf29ce484222325UL, Fnv1a64([]));
        Assert.Equal(0xaf63dc4c8601ec8cUL, Fnv1a64("a"u8));
        Assert.Equal(0x85944171f73967e8UL, Fnv1a64("foobar"u8));

        // applied: id, 0, tick (8 bytes little-endian); rejected: the same, then the code name and 0.
        byte[] applied = [.. "p1"u8, 0, 12, 0, 0, 0, 0, 0, 0, 0, .. "p2"u8, 0, 0x2c, 0x01, 0, 0, 0, 0, 0, 0];
        Assert.Equal(Fnv1a64(applied), PatchHistory.ChainApplied(PatchHistory.ChainApplied(PatchHistory.EmptyHash, "p1", 12), "p2", 300));
        byte[] rejected = [.. "p4"u8, 0, 80, 0, 0, 0, 0, 0, 0, 0, .. "UnknownEntity"u8, 0];
        Assert.Equal(Fnv1a64(rejected), PatchHistory.ChainRejected(PatchHistory.EmptyHash, "p4", 80, SceneErrorCode.UnknownEntity));

        Assert.Equal("cbf29ce484222325", PatchHistory.Format(PatchHistory.EmptyHash));
        Assert.Equal("000000000000000a", PatchHistory.Format(10));
    }

    [Fact]
    public async Task SceneHop_lantern_glow_patch_shows_in_values_and_snapshot()
    {
        var (runtime, game) = await SceneTests.StartSceneHopAsync();
        runtime.SubmitPatch(ScenePatch.Parse("""{"id":"dim","tick":10,"ops":[{"op":"retarget","id":"lamp-west","values":{"glow":0.1}}]}"""));
        runtime.StepTo(10);

        Assert.Equal(0.1f, game.Scenes["lamp-west"].Glow);
        Assert.Equal(0.1f + 0.6f, (float)runtime.Snapshot().Values["glow"], 5);
        Assert.Equal("dim", runtime.Snapshot().Scene!.Applied.Single().Id);
    }
}
