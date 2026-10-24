---
name: ukiyo-animation
description: Use for gameplay cameras, tweens, easing, keyframe tracks and tick-based sequences in a ukiyo game (Ukiyo.Animation) — follow, orbit, rail and fixed cameras, seeded shake, blends between cameras, cutscene-style timelines that appear in the snapshot and can be canceled.
---

# ukiyo animation

Everything advances one step per tick and is a pure function of the tick, so a run is identical on arm64 and x64
(all math through `DetMath`, no libm, +0 zeros). Reference `src/Ukiyo.Animation/Ukiyo.Animation.csproj` from each host
csproj (after the platform reference). Working example: `samples/CameraRig`; tests: `tests/Ukiyo.Tests/AnimationTests.cs`.

## Cameras

```csharp
using Ukiyo.Animation;

_follow = new FollowCamera(new FollowCameraSettings { Offset = new(0, 3.5f, 7), Damping = 0.12f, Deadzone = new(0.5f, 0, 0.5f) }, start);
_orbit  = new OrbitCamera { Center = Vector3.Zero, Distance = 14, Pitch = 0.5f, YawPerTick = 0.02f };
_rail   = new RailCamera(new RailCameraSettings { Path = [a, b, c], LookAt = Vector3.Zero, DurationTicks = 120 });
_director = new CameraDirector(_follow) { Shake = new CameraShake(seed: 7) };

// Update, once per tick, in this order
_follow.Advance(playerPosition);  _orbit.Advance();  _rail.Advance();  _director.Advance();
// Extract
frame.Camera = _director.State;           // CameraPose -> FrameBuilder.LookAt
```

- Cameras implement `ICameraSource` (`CameraPose Pose`: position, target, fov, near, far). `FixedCamera` holds one pose.
- `director.BlendTo(camera, ticks, ease)` blends pose to pose over N ticks; `CutTo` switches at once; 0 ticks cuts.
  Interrupting a blend freezes the on-screen pose as the new start, so nothing pops.
- `director.Shake.Trigger(amplitude, durationTicks, aimAmplitude, decay)`: seeded smooth noise, same seed same shake.
  A weaker trigger during a stronger shake is ignored; zero duration is a no-op.
- A camera exactly above or below its target is degenerate for LookAt: `ToState()` throws `[CAMERA]`; keep pitch off the poles
  (`OrbitCamera` clamps to +-1.5 rad).

## Tweens, easing, tracks

```csharp
var t = Tween.Float(0, 1, durationTicks: 30, Ease.CubicInOut);   // also Vector, Color, Rotation (nlerp, shortest arc)
var v = t.ValueAt(elapsedTicks);                                 // clamps; zero duration jumps to the end value
var track = Track.Float([new Keyframe<float>(0, 0), new Keyframe<float>(60, 5, Ease.QuadOut), new Keyframe<float>(120, 0)], loop: true);
var x = track.Evaluate(tick);
```

Ease set: Linear, SmoothStep, Quad/Cubic/Quart/Sine In/Out/InOut, ExpoIn/Out, BackOut, ElasticOut, BounceOut. Endpoints are
exact 0 and 1. Keyframe ticks must strictly increase.

## Sequences

```csharp
_show = Sequence.Create()
    .Wait(50)
    .Call(() => _director.BlendTo(_orbit, 40))
    .Parallel(b => b.Play(Tween.Float(1, 2, 12, Ease.BackOut), v => _pulse = v), b => b.Wait(45))
    .WaitUntil(() => _door.Open)
    .Build();
// Update: _show.Advance();   // Cancel(), Restart(), Status, IsRunning, ElapsedTicks
// Snapshot: _show.WriteSnapshot("show", values);
```

Steps with a duration use one tick per advance; instant steps (`Call`) chain in the same advance, so `Wait(3).Call(f)` fires f
on the third `Advance`. `Parallel` completes when every branch does. `Cancel()` is safe from inside a callback;
`Restart()` is not (throws). The state is in the snapshot (`show.status` 1 running, 2 completed, 3 canceled, `show.step`,
`show.cur.*`), so replays and golden snapshots cover it. Callbacks must only touch game state; keep them deterministic.

## Prove it

`ukiyo_run` snapshots at ticks, `ukiyo_capture` frames (look at them), `dotnet test --filter AnimationTests`.
Related: ukiyo-core, ukiyo-rendering (FrameBuilder, cameras), ukiyo-testing.
