---
name: ukiyo-physics
description: Use for 2D collision and movement — rigid bodies, boxes and circles, gravity, platformer controllers, grounded checks, triggers/sensors, raycasts, overlap queries and collision layers (Ukiyo.Physics).
---

# ukiyo physics (2D)

`Ukiyo.Physics.PhysicsWorld2D` is a deterministic 2D rigid-body world: sort-and-sweep broad phase, SAT boxes and
circles with contact clipping, sequential impulses with friction and restitution, positional correction, sensors.
Same inputs and step count give bit-identical results (bodies are processed in creation order; trig uses `DetMath`).
Reference the project from each host csproj: `src/Ukiyo.Physics/Ukiyo.Physics.csproj`.

## World and bodies

```csharp
using Ukiyo.Physics;

var world = new PhysicsWorld2D { Gravity = new Vector2(0, -30f) };   // default (0, -9.81); platformers feel better heavier
var ground = world.AddBox("ground", BodyType.Static, new Vector2(0, -0.5f), new Vector2(40, 1));   // center, full size
var player = world.AddBox("player", BodyType.Dynamic, new Vector2(0, 2), new Vector2(0.75f, 0.9f));
var coin = world.AddCircle("coin", BodyType.Static, new Vector2(4, 1.5f), 0.35f);
coin.IsSensor = true;

// in Update, once per tick
world.Step(Simulation.TickSecondsF);
```

Body types: `Static` (never moves), `Kinematic` (moves by its velocity, ignores forces), `Dynamic`.
Body properties: `Position`, `Angle`, `Velocity`, `AngularVelocity`, `Friction` (combined √(a·b)), `Restitution`
(combined max), `LinearDamping`, `AngularDamping`, `GravityScale`, `Density`, `FixedRotation`, `IsSensor`, `Layer`,
`Mask`, `Enabled`, `UserData`, and `ApplyForce`, `ApplyTorque`, `ApplyImpulse`. `body.Pose` gives an `EntityPose` for snapshots;
`world.Poses()` gives all of them.

## Platformer controller

```csharp
_player.FixedRotation = true;
_player.Friction = 0;                                    // walls do not grab the player
var v = _player.Velocity;
v.X = input.Axis(Key.Left, Key.Right, Key.A, Key.D) * RunSpeed;
if (input.WasPressed(Key.Space) && _player.IsGrounded) v.Y = JumpSpeed;
_player.Velocity = v;
world.Step(Simulation.TickSecondsF);
```

`IsGrounded` / `GroundNormal` are set during `Step` when the body rests on something below it (normal pointing up
within ~45°). Read them after the step that you care about.

## Events and queries

```csharp
foreach (var e in world.TriggerEvents)          // this step only: (Sensor, Other, Entered)
    if (e.Entered && e.Other == _player && e.Sensor == coin) { coin.Enabled = false; _score += 100; }
foreach (var c in world.Contacts) { /* BodyA, BodyB, Normal, Point, Penetration */ }

RaycastHit? hit = world.Raycast(origin, direction, maxDistance, mask: uint.MaxValue, includeSensors: false);
List<Body2D> under = world.OverlapPoint(point);
List<Body2D> inBox = world.OverlapBox(center, size);
```

Layers: two bodies collide only if `(a.Layer & b.Mask) != 0 && (b.Layer & a.Mask) != 0`. Disable a body
(`Enabled = false`) instead of removing it when you might bring it back (collectibles, respawns); `Remove` is final.

## Rules

- Step exactly once per `Update` with `Simulation.TickSecondsF`. Never with wall-clock time.
- Units are meters: a 16-px sprite is usually 1 unit. Very small (< 0.05) or very fast bodies can tunnel; keep sizes
  sensible and speeds under ~one body size per tick.
- Put physics state you assert on (position, grounded, score) in `Snapshot` and test it (see ukiyo-testing):
  `tests/Ukiyo.Tests/PhysicsTests.cs` shows rest, bounce, determinism, sensors, layers, raycasts.
