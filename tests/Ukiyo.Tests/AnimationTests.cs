using System.Globalization;
using System.Numerics;
using System.Text;
using Ukiyo.Animation;
using Ukiyo.Hosting;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>Easing, tweens, tracks, sequences and camera rigs (Ukiyo.Animation): exact tick semantics and bit-stable output.</summary>
public sealed class AnimationTests
{
    private static readonly RenderExtent Extent = new(640, 360, 1f);

    public static TheoryData<Ease> AllEases() => [.. Enum.GetValues<Ease>()];

    [Theory]
    [MemberData(nameof(AllEases))]
    public void Every_easing_maps_zero_to_zero_and_one_to_one_exactly(Ease ease)
    {
        Assert.Equal(0f, Easing.Evaluate(ease, 0f));
        Assert.Equal(1f, Easing.Evaluate(ease, 1f));
        Assert.Equal(0f, Easing.Evaluate(ease, -3f));
        Assert.Equal(1f, Easing.Evaluate(ease, 7f));
        Assert.False(float.IsNegative(Easing.Evaluate(ease, 0f)));
    }

    [Theory]
    [MemberData(nameof(AllEases))]
    public void Easing_stays_finite_and_bounded_between_the_ends(Ease ease)
    {
        for (var i = 1; i < 100; i++)
        {
            var value = Easing.Evaluate(ease, i / 100f);
            Assert.True(float.IsFinite(value) && value > -0.5f && value < 1.5f, $"{ease}({i / 100f}) = {value}");
        }
    }

    [Fact]
    public void Easing_known_values_and_overshoot()
    {
        Assert.Equal(0.25f, Easing.Evaluate(Ease.QuadIn, 0.5f));
        Assert.Equal(0.5f, Easing.Evaluate(Ease.SmoothStep, 0.5f));
        Assert.Equal(0.5f, Easing.Evaluate(Ease.CubicInOut, 0.5f));
        Assert.Equal(0.5f, Easing.Evaluate(Ease.SineInOut, 0.5f), 5);
        Assert.True(Easing.Evaluate(Ease.BackOut, 0.7f) > 1f, "back-out overshoots before settling");
        Assert.True(Easing.Evaluate(Ease.ElasticOut, 0.4f) > 1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => Easing.Evaluate((Ease)200, 0.5f));
    }

    [Fact]
    public void Tween_by_ticks_hits_the_end_exactly_and_a_zero_duration_jumps()
    {
        var tween = Tween.Float(10f, 20f, 4);
        Assert.Equal([10f, 12.5f, 15f, 17.5f, 20f], Enumerable.Range(0, 5).Select(tween.ValueAt));
        Assert.Equal(20f, tween.ValueAt(99));
        Assert.Equal(10f, tween.ValueAt(-5));

        var jump = Tween.Vector(Vector3.Zero, new Vector3(1, 2, 3), 0);
        Assert.Equal(new Vector3(1, 2, 3), jump.ValueAt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tween.Float(0, 1, -1));
    }

    [Fact]
    public void Tweens_cover_vectors_colors_and_rotations()
    {
        Assert.Equal(new Vector3(1, 2, 3), Tween.Vector(Vector3.Zero, new Vector3(2, 4, 6), 2).ValueAt(1));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Tween.Color(Vector4.Zero, Vector4.One, 2).ValueAt(1));

        var turn = DetMath.AxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var half = Tween.Rotation(Quaternion.Identity, turn, 2).ValueAt(1);
        var expected = DetMath.AxisAngle(Vector3.UnitY, MathF.PI / 4f);
        Assert.Equal(expected.Y, half.Y, 3);
        Assert.Equal(1f, MathF.Sqrt(half.X * half.X + half.Y * half.Y + half.Z * half.Z + half.W * half.W), 5);

        var negated = new Quaternion(-turn.X, -turn.Y, -turn.Z, -turn.W);
        var shortest = Tween.Rotation(Quaternion.Identity, negated, 2).ValueAt(1);
        Assert.Equal(expected.Y, shortest.Y, 3);
        Assert.Equal(turn, Tween.Rotation(Quaternion.Identity, turn, 5).ValueAt(5));
    }

    [Fact]
    public void Track_holds_ends_eases_between_keys_and_can_loop()
    {
        var track = Track.Float([new Keyframe<float>(10, 0f), new Keyframe<float>(20, 100f, Ease.QuadIn), new Keyframe<float>(30, 0f)]);
        Assert.Equal(0f, track.Evaluate(0));
        Assert.Equal(0f, track.Evaluate(10));
        Assert.Equal(50f, track.Evaluate(15));
        Assert.Equal(100f, track.Evaluate(20));
        Assert.Equal(75f, track.Evaluate(25));
        Assert.Equal(0f, track.Evaluate(1000));

        var loop = Track.Float([new Keyframe<float>(0, 0f), new Keyframe<float>(10, 10f)], loop: true);
        Assert.Equal(3f, loop.Evaluate(13));
        Assert.Equal(0f, loop.Evaluate(20));

        var single = Track.Float([new Keyframe<float>(5, 7f)], loop: true);
        Assert.Equal(7f, single.Evaluate(500));
    }

    [Fact]
    public void Track_rejects_empty_and_unordered_keys()
    {
        Assert.Throws<ArgumentException>(() => Track.Float([]));
        var error = Assert.Throws<ArgumentException>(() => Track.Float([new Keyframe<float>(5, 0f), new Keyframe<float>(5, 1f)]));
        Assert.Contains("[ANIM]", error.Message);
        Assert.Throws<ArgumentException>(() => Track.Float([new Keyframe<float>(5, 0f), new Keyframe<float>(2, 1f)]));
    }

    [Fact]
    public void Sequence_wait_then_call_runs_on_the_last_waited_advance()
    {
        var calls = new List<long>();
        var tick = 0L;
        var sequence = Sequence.Create().Wait(3).Call(() => calls.Add(tick)).Build();
        for (tick = 1; tick <= 6; tick++)
        {
            sequence.Advance();
        }

        Assert.Equal([3L], calls);
        Assert.Equal(SequenceStatus.Completed, sequence.Status);
        Assert.Equal(3, sequence.ElapsedTicks);
    }

    [Fact]
    public void Sequence_steps_without_duration_chain_in_one_advance_and_timed_steps_do_not_share_a_tick()
    {
        var log = new List<string>();
        long tick = 0;
        var sequence = Sequence.Create()
            .Call(() => log.Add($"a@{tick}"))
            .Wait(0)
            .Wait(2)
            .Call(() => log.Add($"b@{tick}"))
            .Wait(2)
            .Call(() => log.Add($"c@{tick}"))
            .Build();
        for (tick = 1; tick <= 8; tick++)
        {
            sequence.Advance();
        }

        Assert.Equal(["a@1", "b@2", "c@4"], log);
    }

    [Fact]
    public void Sequence_tween_applies_each_tick_and_ends_on_the_exact_value()
    {
        var applied = new List<float>();
        var sequence = Sequence.Create().Play(Tween.Float(0f, 1f, 4, Ease.Linear), applied.Add).Play(Tween.Float(1f, 9f, 0), applied.Add).Build();
        for (var i = 0; i < 6; i++)
        {
            sequence.Advance();
        }

        Assert.Equal([0.25f, 0.5f, 0.75f, 1f, 9f], applied);
        Assert.Equal(4, sequence.ElapsedTicks);
    }

    [Fact]
    public void Sequence_with_zero_length_everything_completes_on_the_first_advance_and_empty_sequences_too()
    {
        var count = 0;
        var sequence = Sequence.Create().Wait(0).Play(Tween.Float(0, 1, 0), _ => count++).Call(() => count++).Build();
        sequence.Advance();
        Assert.Equal(2, count);
        Assert.Equal(SequenceStatus.Completed, sequence.Status);

        var empty = Sequence.Create().Build();
        Assert.Equal(SequenceStatus.Running, empty.Status);
        empty.Advance();
        Assert.Equal(SequenceStatus.Completed, empty.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => Sequence.Create().Wait(-1));
    }

    [Fact]
    public void Sequence_parallel_waits_for_its_longest_branch_and_the_next_step_starts_the_tick_after()
    {
        var log = new List<string>();
        long tick = 0;
        var sequence = Sequence.Create()
            .Parallel(
                a => a.Wait(2).Call(() => log.Add($"short@{tick}")),
                b => b.Wait(5).Call(() => log.Add($"long@{tick}")))
            .Call(() => log.Add($"after@{tick}"))
            .Wait(1)
            .Call(() => log.Add($"end@{tick}"))
            .Build();
        for (tick = 1; tick <= 8; tick++)
        {
            sequence.Advance();
        }

        Assert.Equal(["short@2", "long@5", "after@5", "end@6"], log);
    }

    [Fact]
    public void Sequence_parallel_with_no_branches_or_only_instant_branches_takes_no_time()
    {
        var log = new List<string>();
        var sequence = Sequence.Create().Parallel().Parallel(a => a.Call(() => log.Add("x")), b => b.Call(() => log.Add("y"))).Call(() => log.Add("z")).Build();
        sequence.Advance();
        Assert.Equal(["x", "y", "z"], log);
        Assert.Equal(SequenceStatus.Completed, sequence.Status);
    }

    [Fact]
    public void Sequence_wait_until_checks_every_advance_and_spends_no_tick_when_it_passes()
    {
        var open = false;
        var log = new List<long>();
        long tick = 0;
        var sequence = Sequence.Create().WaitUntil(() => open).Call(() => log.Add(tick)).Build();
        for (tick = 1; tick <= 6; tick++)
        {
            open = tick >= 4;
            sequence.Advance();
        }

        Assert.Equal([4L], log);
    }

    [Fact]
    public void Sequence_cancel_stops_callbacks_tweens_and_parallel_branches()
    {
        var applied = new List<float>();
        var late = 0;
        var sequence = Sequence.Create()
            .Parallel(a => a.Play(Tween.Float(0f, 1f, 10), applied.Add), b => b.Wait(10).Call(() => late++))
            .Call(() => late++)
            .Build();
        sequence.Advance();
        sequence.Advance();
        sequence.Cancel();
        for (var i = 0; i < 20; i++)
        {
            sequence.Advance();
        }

        Assert.Equal(2, applied.Count);
        Assert.Equal(0, late);
        Assert.Equal(SequenceStatus.Canceled, sequence.Status);
        sequence.Cancel();
        Assert.Equal(SequenceStatus.Canceled, sequence.Status);
    }

    [Fact]
    public void Sequence_can_cancel_itself_from_a_callback_and_nothing_after_it_runs()
    {
        Sequence? sequence = null;
        var after = 0;
        sequence = Sequence.Create().Call(() => sequence!.Cancel()).Call(() => after++).Wait(3).Call(() => after++).Build();
        for (var i = 0; i < 10; i++)
        {
            sequence.Advance();
        }

        Assert.Equal(0, after);
        Assert.Equal(SequenceStatus.Canceled, sequence.Status);
    }

    [Fact]
    public void Sequence_restart_replays_from_the_start_and_is_refused_inside_a_callback()
    {
        var runs = 0;
        var sequence = Sequence.Create().Wait(2).Call(() => runs++).Build();
        for (var i = 0; i < 3; i++)
        {
            sequence.Advance();
        }

        sequence.Restart();
        Assert.Equal(SequenceStatus.Running, sequence.Status);
        Assert.Equal(0, sequence.ElapsedTicks);
        for (var i = 0; i < 3; i++)
        {
            sequence.Advance();
        }

        Assert.Equal(2, runs);

        Sequence? inner = null;
        inner = Sequence.Create().Call(() => inner!.Restart()).Build();
        Assert.Throws<InvalidOperationException>(() => inner.Advance());
    }

    [Fact]
    public void Sequence_snapshot_numbers_follow_the_cursor()
    {
        var sequence = Sequence.Create().Wait(5).Parallel(a => a.Wait(3), b => b.Wait(1)).Build();
        var values = new Dictionary<string, double>();
        for (var i = 0; i < 6; i++)
        {
            sequence.Advance();
        }

        sequence.WriteSnapshot("s", values);
        Assert.Equal(1, values["s.status"]);
        Assert.Equal(6, values["s.elapsed"]);
        Assert.Equal(1, values["s.step"]);
        Assert.Equal(1, values["s.cur.b0.status"]);
        Assert.Equal(1, values["s.cur.b0.elapsed"]);
        Assert.Equal(2, values["s.cur.b1.status"]);

        for (var i = 0; i < 4; i++)
        {
            sequence.Advance();
        }

        values.Clear();
        sequence.WriteSnapshot("s", values);
        Assert.Equal(2, values["s.status"]);
        Assert.Equal(2, values["s.step"]);
    }

    [Fact]
    public void Follow_camera_ignores_motion_inside_the_deadzone_and_trails_the_part_outside()
    {
        var camera = new FollowCamera(new FollowCameraSettings { Damping = 1f, Deadzone = new Vector3(1f, 0f, 1f), Offset = new Vector3(0, 2, 5) }, Vector3.Zero);
        camera.Advance(new Vector3(0.9f, 0f, -0.5f));
        Assert.Equal(Vector3.Zero, camera.Focus);

        camera.Advance(new Vector3(3f, 0f, 0f));
        Assert.Equal(new Vector3(2f, 0f, 0f), camera.Focus);
        Assert.Equal(new Vector3(2f, 2f, 5f), camera.Pose.Position);

        var damped = new FollowCamera(new FollowCameraSettings { Damping = 0.5f }, Vector3.Zero);
        damped.Advance(new Vector3(8f, 0f, 0f));
        Assert.Equal(4f, damped.Focus.X);
        damped.Advance(new Vector3(8f, 0f, 0f));
        Assert.Equal(6f, damped.Focus.X);
        damped.Snap(new Vector3(-3f, 0f, 0f));
        Assert.Equal(-3f, damped.Focus.X);
    }

    [Fact]
    public void Follow_camera_validates_its_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FollowCamera(new FollowCameraSettings { Damping = 0f }, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FollowCamera(new FollowCameraSettings { Damping = 1.5f }, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FollowCamera(new FollowCameraSettings { Deadzone = new Vector3(-1, 0, 0) }, Vector3.Zero));
    }

    [Fact]
    public void Orbit_camera_keeps_its_distance_wraps_yaw_and_clamps_pitch()
    {
        var orbit = new OrbitCamera { Center = new Vector3(1, 2, 3), Distance = 10f, Pitch = 0f, Yaw = 0f };
        Assert.Equal(new Vector3(1, 2, 13), orbit.Pose.Position);
        Assert.Equal(new Vector3(1, 2, 3), orbit.Pose.Target);

        orbit.Yaw = MathF.PI / 2f;
        orbit.Pitch = 0.6f;
        Assert.Equal(10f, DetMath.Length(orbit.Pose.Position - orbit.Pose.Target), 4);
        Assert.True(orbit.Pose.Position.X > 1f && orbit.Pose.Position.Y > 2f);

        orbit.Yaw = 7f;
        Assert.InRange(orbit.Yaw, -MathF.PI, MathF.PI);
        orbit.Pitch = 99f;
        Assert.Equal(OrbitCamera.MaxPitch, orbit.Pitch);
        orbit.YawPerTick = 0.5f;
        var before = orbit.Yaw;
        orbit.Advance();
        Assert.NotEqual(before, orbit.Yaw);
        Assert.Throws<ArgumentOutOfRangeException>(() => orbit.Distance = 0f);
        Assert.NotNull(orbit.Pose.ToState());
    }

    [Fact]
    public void Rail_camera_travels_the_path_at_constant_speed_and_stops_at_the_end()
    {
        var rail = new RailCamera(new RailCameraSettings
        {
            Path = [Vector3.Zero, new Vector3(10, 0, 0), new Vector3(10, 0, 10)],
            LookAt = new Vector3(0, 0, -5),
            DurationTicks = 4,
            Ease = Ease.Linear,
        });
        Assert.Equal(Vector3.Zero, rail.Pose.Position);
        rail.Advance();
        Assert.Equal(new Vector3(5, 0, 0), rail.Pose.Position);
        rail.Advance();
        Assert.Equal(new Vector3(10, 0, 0), rail.Pose.Position);
        rail.Advance();
        rail.Advance();
        rail.Advance();
        Assert.Equal(new Vector3(10, 0, 10), rail.Pose.Position);
        Assert.True(rail.IsFinished);
        Assert.Equal(4, rail.Ticks);
        rail.Restart();
        Assert.Equal(Vector3.Zero, rail.Pose.Position);
    }

    [Fact]
    public void Rail_camera_edges_zero_duration_target_rail_and_bad_paths()
    {
        var instant = new RailCamera(new RailCameraSettings { Path = [Vector3.Zero, Vector3.One], DurationTicks = 0 });
        Assert.Equal(Vector3.One, instant.Pose.Position);

        var aimed = new RailCamera(new RailCameraSettings { Path = [Vector3.Zero, new Vector3(4, 0, 0)], TargetPath = [new Vector3(0, 0, -3), new Vector3(0, 8, -3)], DurationTicks = 2, Ease = Ease.Linear });
        aimed.Advance();
        Assert.Equal(new Vector3(2, 0, 0), aimed.Pose.Position);
        Assert.Equal(new Vector3(0, 4, -3), aimed.Pose.Target);

        var still = new RailCamera(new RailCameraSettings { Path = [Vector3.UnitX, Vector3.UnitX], DurationTicks = 3 });
        still.Advance();
        Assert.Equal(Vector3.UnitX, still.Pose.Position);

        Assert.Throws<ArgumentException>(() => new RailCamera(new RailCameraSettings { Path = [Vector3.Zero] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RailCamera(new RailCameraSettings { Path = [Vector3.Zero, Vector3.One], DurationTicks = -1 }));
    }

    private static List<Vector3> ShakeRun(uint seed, int ticks, Action<CameraShake>? onStart = null)
    {
        var shake = new CameraShake(seed);
        shake.Trigger(1f, 30, aimAmplitude: 0.5f);
        onStart?.Invoke(shake);
        var offsets = new List<Vector3>();
        for (var i = 0; i < ticks; i++)
        {
            shake.Advance();
            offsets.Add(shake.Offset);
        }

        return offsets;
    }

    [Fact]
    public void Shake_is_the_same_for_the_same_seed_differs_for_another_and_dies_out()
    {
        var a = ShakeRun(5, 40);
        Assert.Equal(a, ShakeRun(5, 40));
        Assert.NotEqual(a, ShakeRun(6, 40));
        Assert.All(a.Take(29), offset => Assert.True(DetMath.Length(offset) <= 1.8f));
        Assert.Contains(a, offset => offset != Vector3.Zero);
        Assert.All(a.Skip(29), offset => Assert.Equal(Vector3.Zero, offset));
        Assert.True(DetMath.Length(a[2]) > DetMath.Length(a[28]) || DetMath.Length(a[1]) > DetMath.Length(a[28]));
    }

    [Fact]
    public void Shake_edges_zero_duration_weaker_and_stronger_triggers_and_cancel()
    {
        var shake = new CameraShake(1);
        shake.Trigger(1f, 0);
        shake.Advance();
        Assert.False(shake.IsActive);
        Assert.Equal(Vector3.Zero, shake.Offset);

        shake.Trigger(1f, 20);
        shake.Advance();
        var strong = shake.Offset;
        shake.Trigger(0.01f, 100);
        shake.Advance();
        Assert.Equal(2, shake.ElapsedTicks);

        shake.Trigger(5f, 10);
        Assert.Equal(0, shake.ElapsedTicks);
        shake.Advance();
        Assert.True(shake.IsActive);
        Assert.NotEqual(strong, shake.Offset);

        shake.Cancel();
        shake.Advance();
        Assert.False(shake.IsActive);
        Assert.Equal(Vector3.Zero, shake.AimOffset);

        Assert.Throws<ArgumentOutOfRangeException>(() => shake.Trigger(-1f, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => shake.Trigger(1f, -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraShake(1, 0));
    }

    [Fact]
    public void Shake_moves_the_position_and_the_target_together_plus_an_aim_jitter()
    {
        var shake = new CameraShake(3);
        shake.Trigger(1f, 30, aimAmplitude: 0.5f);
        shake.Advance();
        var pose = new CameraPose(new Vector3(0, 1, 5), Vector3.Zero);
        var shaken = shake.Apply(pose);
        Assert.Equal(pose.Position + shake.Offset, shaken.Position);
        Assert.Equal(shake.Offset + shake.AimOffset, shaken.Target);
    }

    [Fact]
    public void Director_blends_between_cameras_exactly_from_one_end_to_the_other()
    {
        var a = new FixedCamera(new CameraPose(new Vector3(0, 2, 10), Vector3.Zero, 1f));
        var b = new FixedCamera(new CameraPose(new Vector3(10, 2, 0), new Vector3(1, 0, 0), 2f));
        var director = new CameraDirector(a);
        Assert.Equal(a.Pose, director.Pose);

        director.BlendTo(b, 10, Ease.Linear);
        Assert.True(director.IsBlending);
        Assert.Equal(a.Pose, director.Pose);
        for (var i = 0; i < 5; i++)
        {
            director.Advance();
        }

        Assert.Equal(new Vector3(5, 2, 5), director.Pose.Position);
        Assert.Equal(1.5f, director.Pose.FieldOfViewY);
        for (var i = 0; i < 5; i++)
        {
            director.Advance();
        }

        Assert.False(director.IsBlending);
        Assert.Equal(b.Pose, director.Pose);
        director.Advance();
        Assert.Equal(b.Pose, director.Pose);
        Assert.Same(b, director.Active);
    }

    [Fact]
    public void Director_zero_tick_blend_cuts_and_asking_for_the_current_target_changes_nothing()
    {
        var a = new FixedCamera(new CameraPose(new Vector3(0, 1, 5), Vector3.Zero));
        var b = new FixedCamera(new CameraPose(new Vector3(5, 1, 0), Vector3.Zero));
        var director = new CameraDirector(a);
        director.BlendTo(b, 0);
        Assert.Equal(b.Pose, director.Pose);
        Assert.False(director.IsBlending);

        director.BlendTo(a, 20);
        director.Advance();
        director.Advance();
        var elapsed = director.BlendElapsed;
        director.BlendTo(a, 20);
        Assert.Equal(elapsed, director.BlendElapsed);
        Assert.Throws<ArgumentOutOfRangeException>(() => director.BlendTo(b, -1));

        director.CutTo(b);
        Assert.False(director.IsBlending);
        Assert.Equal(b.Pose, director.Pose);
    }

    [Fact]
    public void Director_interrupted_blend_continues_from_the_pose_on_screen_without_a_jump()
    {
        var a = new FixedCamera(new CameraPose(new Vector3(0, 1, 10), Vector3.Zero));
        var b = new FixedCamera(new CameraPose(new Vector3(10, 1, 0), Vector3.Zero));
        var c = new FixedCamera(new CameraPose(new Vector3(-10, 5, 0), Vector3.Zero));
        var director = new CameraDirector(a);
        director.BlendTo(b, 20);
        for (var i = 0; i < 7; i++)
        {
            director.Advance();
        }

        var onScreen = director.Pose;
        director.BlendTo(c, 20);
        Assert.Equal(onScreen, director.Pose);
        director.Advance();
        Assert.NotEqual(onScreen, director.Pose);
        for (var i = 0; i < 19; i++)
        {
            director.Advance();
        }

        Assert.Equal(c.Pose, director.Pose);
        Assert.False(director.IsBlending);
    }

    [Fact]
    public void Director_state_is_what_FrameBuilder_LookAt_makes_and_a_vertical_camera_is_a_typed_error()
    {
        var pose = new CameraPose(new Vector3(1, 2, 3), new Vector3(0, 1, 0), 1f, 0.2f, 50f);
        var director = new CameraDirector(new FixedCamera(pose));
        Assert.Equal(FrameBuilder.LookAt(pose.Position, pose.Target, 1f, 0.2f, 50f), director.State);

        var error = Assert.Throws<InvalidOperationException>(() => new CameraPose(new Vector3(0, 9, 0), Vector3.Zero).ToState());
        Assert.Contains("[CAMERA]", error.Message);
        Assert.Throws<InvalidOperationException>(() => new CameraPose(Vector3.One, Vector3.One).ToState());
    }

    [Fact]
    public void Director_shake_is_added_to_the_pose_and_advanced_with_it()
    {
        var director = new CameraDirector(new FixedCamera(new CameraPose(new Vector3(0, 1, 5), Vector3.Zero))) { Shake = new CameraShake(9) };
        director.Shake!.Trigger(1f, 20);
        director.Advance();
        Assert.NotEqual(Vector3.Zero, director.Shake.Offset);
        Assert.Equal(new Vector3(0, 1, 5) + director.Shake.Offset, director.Pose.Position);
    }

    private static async Task<(GameRuntime Runtime, Samples.CameraRig.CameraRigGame Game)> RunRigAsync(long tick)
    {
        var game = new Samples.CameraRig.CameraRigGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", Extent));
        runtime.StepTo(tick);
        runtime.RenderFrame(Extent);
        return (runtime, game);
    }

    [Fact]
    public async Task CameraRig_sample_runs_the_show_deterministically_and_every_camera_is_valid()
    {
        var (a, _) = await RunRigAsync(0);
        var (b, _) = await RunRigAsync(0);
        for (var tick = 1L; tick <= 800; tick++)
        {
            a.StepTo(tick);
            b.StepTo(tick);
            a.RenderFrame(Extent);
            b.RenderFrame(Extent);
            var json = a.SnapshotJson();
            Assert.Equal(json, b.SnapshotJson());
            Assert.DoesNotContain("NaN", json);
            Assert.DoesNotContain("-0,", json);
            Assert.Single(a.LastFrame);
        }
    }

    [Fact]
    public async Task CameraRig_sample_hands_over_between_cameras_on_schedule()
    {
        var (runtime, _) = await RunRigAsync(60);
        Assert.Contains("\"camera.blending\":1", runtime.SnapshotJson());
        runtime.StepTo(120);
        Assert.Contains("\"camera.blending\":0", runtime.SnapshotJson());
        runtime.StepTo(140);
        Assert.Contains("\"camera.shake.active\":1", runtime.SnapshotJson());
    }

    private static string F(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static string F(Vector3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

    /// <summary>Raw float bits of every module output at fixed inputs: any platform-dependent rounding shows up as a diff.</summary>
    private static string GoldenText()
    {
        var text = new StringBuilder("# Ukiyo.Animation float bits; regenerate only for an intended change: UKIYO_UPDATE_GOLDEN=1 dotnet test tests/Ukiyo.Tests --filter AnimationTests\n");
        foreach (var ease in Enum.GetValues<Ease>())
        {
            text.Append(CultureInfo.InvariantCulture, $"ease {ease}");
            for (var i = 0; i <= 20; i++)
            {
                text.Append(' ').Append(F(Easing.Evaluate(ease, i / 20f)));
            }

            text.Append('\n');
        }

        var turn = DetMath.AxisAngle(DetMath.Normalize(new Vector3(0.3f, 1f, 0.2f)), 2.1f);
        var rotation = Tween.Rotation(Quaternion.Identity, turn, 9, Ease.SineInOut);
        for (var i = 0; i <= 9; i++)
        {
            var q = rotation.ValueAt(i);
            text.Append(CultureInfo.InvariantCulture, $"nlerp {i} {F(q.X)} {F(q.Y)} {F(q.Z)} {F(q.W)}\n");
        }

        var shake = new CameraShake(1234, 2);
        shake.Trigger(0.8f, 40, aimAmplitude: 0.3f);
        for (var i = 1; i <= 42; i++)
        {
            shake.Advance();
            text.Append(CultureInfo.InvariantCulture, $"shake {i} {F(shake.Offset)} {F(shake.AimOffset)}\n");
        }

        var follow = new FollowCamera(new FollowCameraSettings { Damping = 0.2f, Deadzone = new Vector3(0.3f, 0f, 0.3f) }, Vector3.Zero);
        var orbit = new OrbitCamera { Center = new Vector3(0, 0.5f, 0), Distance = 12f, Pitch = 0.45f, YawPerTick = 0.037f };
        var rail = new RailCamera(new RailCameraSettings { Path = [new Vector3(-8, 3, 9), new Vector3(0, 6, 10), new Vector3(9, 2, -4)], LookAt = Vector3.Zero, DurationTicks = 30, Ease = Ease.CubicInOut });
        var director = new CameraDirector(follow) { Shake = new CameraShake(77) };
        for (var i = 1; i <= 90; i++)
        {
            var (sin, cos) = DetMath.SinCos(0.07f * i);
            follow.Advance(new Vector3(6f * cos, 0.5f, 4f * sin));
            orbit.Advance();
            rail.Advance();
            if (i == 20)
            {
                director.BlendTo(orbit, 25);
                director.Shake!.Trigger(0.5f, 30);
            }

            if (i == 35)
            {
                director.BlendTo(rail, 25, Ease.QuadInOut);
            }

            if (i == 70)
            {
                director.BlendTo(follow, 10);
            }

            director.Advance();
            var pose = director.Pose;
            var state = director.State;
            text.Append(CultureInfo.InvariantCulture, $"cam {i} {F(pose.Position)} {F(pose.Target)} {F(pose.FieldOfViewY)} | {F(state.Position)} {F(state.Rotation.X)} {F(state.Rotation.Y)} {F(state.Rotation.Z)} {F(state.Rotation.W)}\n");
        }

        return text.ToString();
    }

    [Fact]
    public void Animation_output_matches_the_committed_golden_bit_for_bit()
    {
        var actual = GoldenText();
        var path = Path.Combine(CoreTests.RepoRoot(), "tests", "Ukiyo.Tests", "golden", "animation.txt");
        if (Environment.GetEnvironmentVariable("UKIYO_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        var expected = File.Exists(path) ? File.ReadAllText(path) : "";
        Assert.True(expected == actual, $"animation output differs from {Path.GetRelativePath(CoreTests.RepoRoot(), path)}; regenerate only for an intended change");
    }
}
