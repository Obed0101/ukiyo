using System.Numerics;
using Ukiyo.Physics;
using Xunit;

namespace Ukiyo.Tests;

public sealed class PhysicsTests
{
    private const float Dt = Simulation.TickSecondsF;

    private static PhysicsWorld2D GroundWorld(out Body2D ground)
    {
        var world = new PhysicsWorld2D();
        ground = world.AddBox("ground", BodyType.Static, new Vector2(0, -0.5f), new Vector2(20, 1));
        return world;
    }

    [Fact]
    public void Falling_box_comes_to_rest_on_the_ground_and_reports_grounded()
    {
        var world = GroundWorld(out _);
        var box = world.AddBox("box", BodyType.Dynamic, new Vector2(0, 3), new Vector2(1, 1));
        for (var i = 0; i < 240; i++)
        {
            world.Step(Dt);
        }

        Assert.InRange(box.Position.Y, 0.45f, 0.52f);
        Assert.InRange(MathF.Abs(box.Velocity.Y), 0f, 0.05f);
        Assert.True(box.IsGrounded);
        Assert.True(Vector2.Distance(Vector2.UnitY, box.GroundNormal) < 1e-4f, $"ground normal {box.GroundNormal}");
    }

    [Fact]
    public void Ball_bounces_with_restitution_and_loses_height()
    {
        var world = GroundWorld(out var ground);
        var ball = world.AddCircle("ball", BodyType.Dynamic, new Vector2(0, 4), 0.5f);
        ball.Restitution = 0.6f;
        ground.Restitution = 0.6f;
        var bounced = false;
        var peakAfterBounce = 0f;
        for (var i = 0; i < 180; i++)
        {
            world.Step(Dt);
            bounced |= ball.Velocity.Y > 1f;
            if (bounced)
            {
                peakAfterBounce = MathF.Max(peakAfterBounce, ball.Position.Y);
            }
        }

        Assert.True(bounced);
        Assert.InRange(peakAfterBounce, 1f, 3.9f);
    }

    [Fact]
    public void Same_inputs_give_bit_identical_worlds()
    {
        static PhysicsWorld2D Build()
        {
            var world = GroundWorld(out _);
            for (var i = 0; i < 12; i++)
            {
                var body = i % 2 == 0
                    ? world.AddBox($"b{i}", BodyType.Dynamic, new Vector2(i * 0.37f - 2, 1 + i * 0.8f), new Vector2(0.6f, 0.4f), i * 0.3f)
                    : world.AddCircle($"c{i}", BodyType.Dynamic, new Vector2(i * 0.41f - 2, 1 + i * 0.8f), 0.3f);
                body.Velocity = new Vector2(MathF.Sin(i), 0);
            }

            return world;
        }

        var a = Build();
        var b = Build();
        for (var i = 0; i < 300; i++)
        {
            a.Step(Dt);
            b.Step(Dt);
        }

        Assert.Equal(a.Bodies.Select(x => (x.Position, x.Angle, x.Velocity)), b.Bodies.Select(x => (x.Position, x.Angle, x.Velocity)));
    }

    [Fact]
    public void Sensor_reports_enter_once_and_exit_once_without_blocking()
    {
        var world = new PhysicsWorld2D { Gravity = Vector2.Zero };
        var sensor = world.AddBox("zone", BodyType.Static, Vector2.Zero, new Vector2(1, 1));
        sensor.IsSensor = true;
        var mover = world.AddCircle("mover", BodyType.Dynamic, new Vector2(-3, 0), 0.25f);
        mover.Velocity = new Vector2(6, 0);
        var events = new List<TriggerEvent>();
        for (var i = 0; i < 90; i++)
        {
            world.Step(Dt);
            events.AddRange(world.TriggerEvents);
        }

        Assert.Equal(new[] { true, false }, events.Select(e => e.Entered).ToArray());
        Assert.All(events, e => Assert.Same(sensor, e.Sensor));
        Assert.True(mover.Position.X > 3);
    }

    [Fact]
    public void Layer_masks_filter_collisions_both_ways()
    {
        var world = GroundWorld(out var ground);
        ground.Layer = 0b10;
        var ghost = world.AddBox("ghost", BodyType.Dynamic, new Vector2(0, 1), new Vector2(1, 1));
        ghost.Mask = 0b01;
        for (var i = 0; i < 120; i++)
        {
            world.Step(Dt);
        }

        Assert.True(ghost.Position.Y < -2);
    }

    [Fact]
    public void Raycast_hits_the_nearest_shape_with_its_surface_normal()
    {
        var world = GroundWorld(out var ground);
        var result = world.Raycast(new Vector2(0, 5), -Vector2.UnitY, 20f);

        Assert.True(result.HasValue);
        var hit = result.GetValueOrDefault();
        Assert.Same(ground, hit.Body);
        Assert.Equal(5f, hit.Distance, 4);
        Assert.True(Vector2.Distance(Vector2.UnitY, hit.Normal) < 1e-4f, $"hit normal {hit.Normal}");
        Assert.Null(world.Raycast(new Vector2(0, 5), Vector2.UnitY, 20f));
    }

    [Fact]
    public void Overlap_queries_return_bodies_in_creation_order()
    {
        var world = GroundWorld(out var ground);
        var crate = world.AddBox("crate", BodyType.Static, new Vector2(2, 0.5f), new Vector2(1, 1));

        Assert.Equal(new[] { ground, crate }, world.OverlapBox(new Vector2(2, 0), new Vector2(0.5f, 0.5f)).ToArray());
        Assert.Equal(new[] { crate }, world.OverlapPoint(new Vector2(2, 0.8f)).ToArray());
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-2.3f)]
    [InlineData(3.14159f)]
    [InlineData(100.25f)]
    public void Deterministic_sine_and_cosine_match_the_platform_within_float_precision(float angle)
    {
        var (sin, cos) = DetMath.SinCos(angle);
        Assert.Equal(MathF.Sin(angle), sin, 5);
        Assert.Equal(MathF.Cos(angle), cos, 5);
    }

    [Fact]
    public void Invalid_shapes_and_steps_fail_with_typed_errors()
    {
        var world = new PhysicsWorld2D();
        Assert.Throws<ArgumentOutOfRangeException>(() => world.AddCircle("bad", BodyType.Dynamic, Vector2.Zero, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Step(float.NaN));
    }
}
