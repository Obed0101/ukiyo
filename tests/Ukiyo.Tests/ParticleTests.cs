using System.Numerics;
using Ukiyo.Particles;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Xunit;

namespace Ukiyo.Tests;

public sealed class ParticleTests
{
    private static readonly SourceIdentity NoSource = new(new Dictionary<string, string>());

    private static EmitterConfig Config(string name = "e", int max = 100, float rate = 0f, int life = 60, ulong seed = 1) => new()
    {
        Name = name,
        Seed = seed,
        RatePerTick = rate,
        LifetimeMinTicks = life,
        LifetimeMaxTicks = life,
        SpeedMin = 1f,
        SpeedMax = 3f,
        MaxParticles = max,
    };

    private static (ParticleSystem System, ParticleEmitter Emitter) One(EmitterConfig config, int maxLive = 1000)
    {
        var system = new ParticleSystem(maxLive);
        return (system, system.Add(config));
    }

    private static void Steps(ParticleSystem system, int count)
    {
        for (var i = 0; i < count; i++)
        {
            system.Step();
        }
    }

    [Fact]
    public void Random_stream_matches_the_published_SplitMix64_vector()
    {
        var rng = new ParticleRng(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, rng.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.NextUInt64());
    }

    [Fact]
    public void Same_seed_and_calls_give_the_same_state_every_run_and_a_different_seed_does_not()
    {
        ulong Run(ulong seed)
        {
            var (system, _) = One(Config(rate: 2.5f, seed: seed) with { Shape = ParticleShape.Circle(1f), Gravity = new Vector2(0, -9.8f), Drag = 0.01f });
            Steps(system, 300);
            return system.StateHash();
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    public async Task Sparks_snapshots_and_frames_are_byte_identical_across_runs()
    {
        async Task<(string Snapshot, byte[] Rgba)> Run()
        {
            var runtime = new GameRuntime(new Samples.Sparks.SparksGame(), NoSource);
            var renderer = new SoftwareRenderer();
            var extent = new RenderExtent(320, 180, 1f);
            await runtime.StartAsync(renderer, new RenderConfiguration("test", extent));
            runtime.StepTo(150);
            runtime.RenderFrame(extent);
            var capture = (await runtime.CaptureAsync())!;
            return (runtime.SnapshotJson(), PngDecoder.Decode(capture.Png).Rgba);
        }

        var a = await Run();
        var b = await Run();
        Assert.Equal(a.Snapshot, b.Snapshot);
        Assert.Equal(a.Rgba, b.Rgba);
        Assert.Contains("\"particles.live\"", a.Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"particles.dropper.dropped\"", a.Snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_new_keeps_the_live_particles_and_counts_the_refused_ones()
    {
        var (system, emitter) = One(Config(max: 10) with { Overflow = OverflowPolicy.DropNew });
        emitter.Burst(6);
        system.Step();
        var firstSix = emitter.Positions();
        emitter.Burst(19);
        system.Step();

        Assert.Equal(10, emitter.Live);
        Assert.Equal(10, emitter.Spawned);
        Assert.Equal(15, emitter.Dropped);
        Assert.Equal(0, emitter.Evicted);
        Assert.Equal(1, emitter.OverflowTicks);
        // The six that were alive moved on one tick but kept their slots (order is spawn order).
        Assert.Equal(6, firstSix.Length);
        Assert.All(Enumerable.Range(0, 6), i => Assert.NotEqual(firstSix[i], emitter.Positions()[i]));
    }

    [Fact]
    public void Replace_oldest_evicts_the_oldest_and_keeps_the_newest()
    {
        var (system, emitter) = One(Config(max: 10, rate: 4f, life: 1000) with { Overflow = OverflowPolicy.ReplaceOldest });
        Steps(system, 5);

        Assert.Equal(10, emitter.Live);
        Assert.Equal(20, emitter.Spawned);
        Assert.Equal(10, emitter.Evicted);
        Assert.Equal(0, emitter.Dropped);
        Assert.Equal(3, emitter.OverflowTicks);
    }

    [Fact]
    public void Replace_oldest_still_drops_the_excess_of_a_request_larger_than_the_whole_budget()
    {
        var (system, emitter) = One(Config(max: 10) with { Overflow = OverflowPolicy.ReplaceOldest });
        emitter.Burst(25);
        system.Step();

        Assert.Equal(10, emitter.Live);
        Assert.Equal(15, emitter.Dropped);
        Assert.Equal(0, emitter.Evicted);
    }

    [Fact]
    public void The_system_budget_caps_all_emitters_and_earlier_emitters_claim_first()
    {
        var system = new ParticleSystem(150);
        var first = system.Add(Config("first", max: 100));
        var second = system.Add(Config("second", max: 100));
        first.Burst(100);
        second.Burst(100);
        system.Step();

        Assert.Equal((100, 50), (first.Live, second.Live));
        Assert.Equal(150, system.Live);
        Assert.Equal(50, system.Dropped);
        Assert.Equal(1, system.OverflowTicks);
    }

    [Fact]
    public void Replace_oldest_never_pushes_the_system_over_its_budget()
    {
        var system = new ParticleSystem(40);
        var steady = system.Add(Config("steady", max: 30, rate: 1f, life: 1000));
        var greedy = system.Add(Config("greedy", max: 30, rate: 3f, life: 1000) with { Overflow = OverflowPolicy.ReplaceOldest });
        for (var i = 0; i < 100; i++)
        {
            system.Step();
            Assert.True(system.Live <= 40, $"live {system.Live} at step {i}");
        }

        // Both want more than fits: the system stays exactly full and the replacer keeps its whole own budget.
        Assert.Equal(40, system.Live);
        Assert.Equal(30, greedy.Live);
        Assert.Equal(10, steady.Live);
    }

    [Fact]
    public void A_dying_particle_does_not_block_a_later_emitter_in_the_same_tick()
    {
        var system = new ParticleSystem(10);
        var late = system.Add(Config("late", max: 10, life: 5));
        var early = system.Add(Config("early", max: 10, life: 1000));
        late.Burst(10);
        system.Step();
        Steps(system, 5);
        Assert.Equal(0, late.Live);
        early.Burst(10);
        system.Step();
        Assert.Equal(10, early.Live);
    }

    [Fact]
    public void Zero_rate_spawns_nothing_and_leaves_the_state_untouched()
    {
        var (system, emitter) = One(Config(rate: 0f));
        var before = system.StateHash();
        Steps(system, 500);

        Assert.Equal(0, emitter.Spawned);
        Assert.Equal(0, emitter.Live);
        Assert.Equal(before, system.StateHash());
        Assert.Equal(0, emitter.OverflowTicks);
    }

    [Fact]
    public void A_fractional_rate_accumulates_and_a_stopped_emitter_emits_only_manual_bursts()
    {
        var (system, emitter) = One(Config(rate: 0.25f, life: 1000));
        Steps(system, 40);
        Assert.Equal(10, emitter.Spawned);

        emitter.Emitting = false;
        Steps(system, 40);
        Assert.Equal(10, emitter.Spawned);
        emitter.Burst(3);
        system.Step();
        Assert.Equal(13, emitter.Spawned);
    }

    [Fact]
    public void Scheduled_bursts_and_duration_are_relative_to_the_emitters_own_ticks()
    {
        var (system, emitter) = One(Config(rate: 1f, life: 1000) with { Bursts = [new Burst(0, 5), new Burst(10, 7), new Burst(50, 100)], DurationTicks = 20 });
        Steps(system, 100);

        // 20 ticks of rate + bursts at 0 and 10; the burst at 50 is past the duration.
        Assert.Equal(20 + 5 + 7, emitter.Spawned);
    }

    [Fact]
    public void Lifetime_of_one_tick_lives_for_exactly_one_step_and_the_longest_lifetime_survives()
    {
        var (system, emitter) = One(Config(life: 1));
        emitter.Burst(4);
        system.Step();
        Assert.Equal(4, emitter.Live);
        system.Step();
        Assert.Equal(0, emitter.Live);
        Assert.Equal(4, emitter.Expired);

        var (longSystem, longEmitter) = One(Config(life: EmitterConfig.MaxLifetimeTicks));
        longEmitter.Burst(2);
        Steps(longSystem, 2000);
        Assert.Equal(2, longEmitter.Live);
    }

    [Fact]
    public void Invalid_configurations_fail_with_typed_tagged_errors()
    {
        var system = new ParticleSystem();
        Assert.Contains("[PARTICLES]", Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(life: 0))).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(life: EmitterConfig.MaxLifetimeTicks + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config() with { LifetimeMinTicks = 10, LifetimeMaxTicks = 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(rate: -1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(rate: float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(rate: float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(max: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config(max: EmitterConfig.MaxParticlesPerEmitter + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config() with { Drag = 1.5f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config() with { SpeedMin = 5f, SpeedMax = 1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Add(Config() with { Bursts = [new Burst(-1, 1)] }));
        Assert.Throws<ArgumentException>(() => system.Add(Config() with { Name = " " }));
        system.Add(Config("dup"));
        Assert.Throws<ArgumentException>(() => system.Add(Config("dup")));
        Assert.Throws<KeyNotFoundException>(() => system["missing"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSystem(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSystem(RenderValidation.MaxSprites + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => system["dup"].Burst(-1));
    }

    [Fact]
    public void A_huge_burst_is_clamped_by_the_budget_without_allocating_or_overflowing()
    {
        var (system, emitter) = One(Config(max: 50));
        emitter.Burst(int.MaxValue);
        emitter.Burst(int.MaxValue);
        system.Step();

        Assert.Equal(50, emitter.Live);
        Assert.Equal(2L * int.MaxValue - 50, emitter.Dropped);
    }

    [Fact]
    public void Curves_hold_before_and_after_their_stops_and_interpolate_between()
    {
        var curve = new Curve(new CurveStop(0.2f, 1f), new CurveStop(0.6f, 3f));
        Assert.Equal(1f, curve.Evaluate(0f));
        Assert.Equal(2f, curve.Evaluate(0.4f), 5);
        Assert.Equal(3f, curve.Evaluate(1f));

        var color = ColorCurve.Linear(new Vector4(0, 0, 0, 1), new Vector4(1, 1, 1, 0));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), color.Evaluate(0.5f));

        Assert.Throws<ArgumentException>(() => new Curve());
        Assert.Throws<ArgumentException>(() => new Curve(new CurveStop(0.5f, 1), new CurveStop(0.5f, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Curve(new CurveStop(1.5f, 1)));
        Assert.Throws<ArgumentException>(() => new Curve(new CurveStop(0f, float.NaN)));
        Assert.Throws<ArgumentException>(() => new ColorCurve(new ColorStop(0f, new Vector4(float.PositiveInfinity, 0, 0, 0))));
    }

    [Fact]
    public void Shapes_spawn_inside_their_bounds_and_aim_where_they_say()
    {
        var origin = new Vector2(10f, -4f);
        List<Vector2> Spawn(ParticleShape shape, float speed = 0f)
        {
            var (system, emitter) = One(Config(max: 2000, life: 100) with { Shape = shape, SpeedMin = speed, SpeedMax = speed }, maxLive: 5000);
            emitter.Position = origin;
            emitter.Burst(2000);
            system.Step();
            return [.. emitter.Positions()];
        }

        Assert.All(Spawn(ParticleShape.Point()), p => Assert.Equal(origin, p));
        Assert.All(Spawn(ParticleShape.Circle(2f)), p => Assert.True(Vector2.Distance(p, origin) <= 2.0001f));
        Assert.All(Spawn(ParticleShape.Box(new Vector2(3f, 0.5f))), p => Assert.True(MathF.Abs(p.X - origin.X) <= 3.0001f && MathF.Abs(p.Y - origin.Y) <= 0.5001f));

        var circle = Spawn(ParticleShape.Circle(2f));
        Assert.True(circle.Count(p => Vector2.Distance(p, origin) > 1f) > 1000, "a disc has most of its area outside half its radius");

        // Cone straight up with a 0.3 rad half angle: one tick of travel at speed 60 moves 1 unit along the direction.
        var (system, emitter) = One(Config(max: 2000, life: 100) with { Shape = ParticleShape.Cone(MathF.PI / 2f, 0.3f), SpeedMin = 60f, SpeedMax = 60f }, maxLive: 5000);
        emitter.Burst(2000);
        system.Step();
        system.Step();
        var moved = emitter.Positions();
        Assert.All(moved, p =>
        {
            Assert.True(Vector2.Distance(p, Vector2.Zero) is > 0.99f and < 1.01f);
            Assert.True(MathF.Abs(MathF.Atan2(p.Y, p.X) - MathF.PI / 2f) <= 0.3001f);
        });
        Assert.True(moved.Any(p => p.X < -0.1f) && moved.Any(p => p.X > 0.1f), "the cone spreads both ways");
    }

    [Fact]
    public void Gravity_and_drag_act_once_per_tick_on_velocity()
    {
        var (system, emitter) = One(Config(life: 100) with { Shape = ParticleShape.Cone(0f, 0f), SpeedMin = 0f, SpeedMax = 0f, Gravity = new Vector2(0f, -60f), Drag = 0.5f });
        emitter.Burst(1);
        system.Step();
        system.Step();

        // v = (0 + (-60/60)) * 0.5 = -0.5 per second; position = v / 60.
        var position = emitter.Positions()[0];
        Assert.Equal(0f, position.X);
        Assert.Equal(-0.5f / 60f, position.Y, 6);
    }

    [Fact]
    public void Snapshot_values_carry_counts_per_emitter_and_the_budget_hits()
    {
        var system = new ParticleSystem(100);
        var a = system.Add(Config("a", max: 5));
        system.Add(Config("b", max: 5, rate: 1f));
        a.Burst(9);
        system.Step();
        var values = new Dictionary<string, double>();
        system.AddSnapshotValues(values);

        Assert.Equal(6, values["particles.live"]);
        Assert.Equal(100, values["particles.maxLive"]);
        Assert.Equal(4, values["particles.dropped"]);
        Assert.Equal(1, values["particles.overflowTicks"]);
        Assert.Equal(5, values["particles.a.live"]);
        Assert.Equal(4, values["particles.a.dropped"]);
        Assert.Equal(1, values["particles.b.live"]);
        Assert.Equal(system.StateHash() & ((1UL << 52) - 1), (ulong)values["particles.hash"]);
    }

    private sealed class OneSquareGame : IGame
    {
        private readonly ParticleSystem _particles = new(16);
        private SpriteFrame _sprite;

        public string Name => "OneSquare";

        public void Initialize(GameContext context)
        {
            _sprite = ParticleSystem.CreateSprite(context);
            _particles.Add(new EmitterConfig
            {
                Name = "square",
                Bursts = [new Burst(0, 1)],
                LifetimeMinTicks = 100,
                LifetimeMaxTicks = 100,
                SizeOverLife = Curve.Constant(4f),
                ColorOverLife = ColorCurve.Constant(new Vector4(1f, 0f, 0f, 1f)),
                MaxParticles = 4,
            });
        }

        public void Update(in TickInfo tick) => _particles.Step();

        public void Extract(FrameBuilder frame)
        {
            frame.ClearColor = new Vector4(0f, 0f, 1f, 1f);
            frame.Camera2D = new Camera2D(Vector2.Zero, 10f);
            _particles.Draw(frame, _sprite);
        }

        public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose>());
    }

    [Fact]
    public async Task A_particle_is_drawn_as_a_tinted_sprite_where_the_emitter_is()
    {
        var runtime = new GameRuntime(new OneSquareGame(), NoSource);
        var extent = new RenderExtent(100, 100, 1f);
        await runtime.StartAsync(new SoftwareRenderer(), new RenderConfiguration("test", extent));
        runtime.StepTo(3);
        runtime.RenderFrame(extent);
        var image = PngDecoder.Decode((await runtime.CaptureAsync())!.Png);

        (byte R, byte G, byte B) At(int x, int y) => (image.Rgba[(y * 100 + x) * 4], image.Rgba[(y * 100 + x) * 4 + 1], image.Rgba[(y * 100 + x) * 4 + 2]);

        // A 4 x 4 unit square on a 10-unit-high view of 100 px: 40 px wide, centered, so 30..70.
        Assert.Equal((255, 0, 0), At(50, 50));
        Assert.Equal((255, 0, 0), At(32, 32));
        Assert.Equal((255, 0, 0), At(67, 67));
        Assert.Equal((0, 0, 255), At(10, 10));
        Assert.Equal((0, 0, 255), At(90, 50));
    }

    [Fact]
    public async Task Sparks_draws_its_fountain_and_ring_and_the_overflow_counters_move()
    {
        var runtime = new GameRuntime(new Samples.Sparks.SparksGame(), NoSource);
        var extent = new RenderExtent(320, 180, 1f);
        await runtime.StartAsync(new SoftwareRenderer(), new RenderConfiguration("test", extent));
        runtime.StepTo(30);
        runtime.RenderFrame(extent);
        var image = PngDecoder.Decode((await runtime.CaptureAsync())!.Png);

        // Pixel (x, y) of the world point (wx, wy): 18 px per unit, y flipped, origin at the center.
        (byte R, byte G, byte B) At(float wx, float wy) =>
            (image.Rgba[((int)(90 - wy * 18) * 320 + (int)(160 + wx * 18)) * 4], image.Rgba[((int)(90 - wy * 18) * 320 + (int)(160 + wx * 18)) * 4 + 1], image.Rgba[((int)(90 - wy * 18) * 320 + (int)(160 + wx * 18)) * 4 + 2]);

        var background = At(8f, -2f);
        // Linear (0.008, 0.01, 0.02) encodes to sRGB (22, 25, 39).
        Assert.Equal((22, 25, 39), background);

        var orange = 0;
        var cyan = 0;
        for (var y = 0; y < 180; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                var p = (y * 320 + x) * 4;
                if (image.Rgba[p] > 200 && image.Rgba[p + 1] is > 60 and < 230 && image.Rgba[p + 2] < 120)
                {
                    orange++;
                }

                if (image.Rgba[p] < 120 && image.Rgba[p + 1] > 150 && image.Rgba[p + 2] > 200)
                {
                    cyan++;
                }
            }
        }

        Assert.True(orange > 20, $"fountain pixels: {orange}");
        Assert.True(cyan > 20, $"ring pixels: {cyan}");

        var json = runtime.SnapshotJson();
        Assert.Contains("\"particles.dropper.dropped\"", json, StringComparison.Ordinal);
        Assert.Contains("\"particles.replacer.evicted\"", json, StringComparison.Ordinal);
    }
}
