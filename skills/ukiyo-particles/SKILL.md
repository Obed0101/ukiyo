---
name: ukiyo-particles
description: Use for sparks, smoke, bursts and other VFX — deterministic particle emitters (burst, rate, point/circle/cone/box shapes), life curves, gravity and drag, hard per-emitter and global budgets with a stated overflow policy, and the counters agents read in snapshots (Ukiyo.Particles).
---

# ukiyo particles (2D)

`Ukiyo.Particles.ParticleSystem` simulates emitters on ticks only and draws every live particle as a tinted world
sprite of one white texel. That is the existing sprite path, so every renderer (wgpu, three.js, CPU) draws it the same
and there is no protocol feature involved. Reference `src/Ukiyo.Particles/Ukiyo.Particles.csproj` from each host csproj
(after the platform reference). Sample: `samples/Sparks`.

```csharp
// Initialize
_sprite = ParticleSystem.CreateSprite(context);              // 1x1 white texture, once
var fountain = _particles.Add(new EmitterConfig
{
    Name = "fountain", Seed = 11,                              // seed makes it reproducible; names are unique
    Shape = ParticleShape.Cone(MathF.PI / 2f, 0.28f),          // Point | Circle(r) | Cone(dir, halfAngle) | Box(half, dir, halfAngle)
    RatePerTick = 3f,                                          // fractions accumulate; 0 emits nothing
    Bursts = [new Burst(Tick: 0, Count: 120)],                 // on the emitter's own tick; Burst(n) on the emitter works any time
    LifetimeMinTicks = 70, LifetimeMaxTicks = 100,             // 1..1,048,576 ticks
    SpeedMin = 6.5f, SpeedMax = 8.5f,                          // units per second
    Gravity = new Vector2(0, -9.8f), Drag = 0.004f,            // units/s², fraction of velocity lost per tick
    SizeOverLife = Curve.Linear(0.16f, 0.05f),                 // world units; t = age / lifetime
    ColorOverLife = new ColorCurve(new ColorStop(0, orange), new ColorStop(1, clearRed)),   // linear RGBA, straight alpha
    MaxParticles = 512, Overflow = OverflowPolicy.DropNew,     // hard budget and what happens beyond it
    Layer = 1,
});
fountain.Position = new Vector2(-4.5f, -4f);                   // move emitters from Update, never Extract

// Update (once per tick):  _particles.Step();
// Extract:                 _particles.Draw(frame, _sprite);
// Snapshot:                _particles.AddSnapshotValues(values);   // "particles.*" keys
```

## Rules the module keeps

- Ticks only: `Step()` is the only clock. Randomness is a private SplitMix64 per emitter (`ParticleRng`, integer-only,
  seed 0 is valid); never `System.Random`. Trig is `DetMath.SinCos`; velocity/position math is element-wise `+ - *`.
  Same configs + same calls ⇒ same particles, `StateHash()` and snapshot bytes on every target
  (`SnapshotGoldenTests` pins Sparks on both CI machines).
- A tick is: age/move/expire every emitter, then spawn in the order emitters were added. A particle spawned in tick N
  is drawn at its spawn position in N, first moves in N+1, and lives exactly `lifetime` steps.
- Budgets: an emitter holds at most `MaxParticles` (storage allocated once, up to 65,536); the system at most
  `new ParticleSystem(maxLive)` (default 16,384, max 65,536 because every live particle is one sprite and a frame
  holds `RenderValidation.MaxSprites`; leave room for your other sprites). Earlier emitters have first claim on the
  system budget.
- Overflow policy, per emitter:
  - `DropNew`: refuse the excess; live particles keep their place. Counted in `Dropped`.
  - `ReplaceOldest`: evict this emitter's oldest live particles for the new ones (`Evicted`); a single request larger
    than the whole budget still drops its excess (`Dropped`). It never takes room from other emitters, so under a
    full system budget it only swaps its own.
  - Either way `OverflowTicks` counts ticks in which something did not fit, and the burst is clamped before any loop
    runs (`Burst(int.MaxValue)` is safe).
- Bad configs throw `ArgumentException`/`ArgumentOutOfRangeException` tagged `[PARTICLES]` when added (lifetime
  outside 1..2^20, negative/NaN/huge rate, drag outside 0..1, duplicate name, bad curve stops, budgets out of range).

## What agents see

`AddSnapshotValues` puts these in `Snapshot.Values` (so `ukiyo_run` shows them):
`particles.live|spawned|dropped|evicted|overflowTicks|maxLive|hash` and per emitter `particles.<name>.live|spawned|dropped|evicted|overflowTicks`.
`hash` is the low 52 bits of `StateHash()`: equal across two runs means equal particles, positions and random state.
A non-zero `dropped`/`evicted` means a budget was hit; raise the budget or lower the rate on purpose, do not ignore it.

## Proving it

- Determinism: two runtimes, same steps, `Assert.Equal(a.SnapshotJson(), b.SnapshotJson())` and equal CPU captures
  (`tests/Ukiyo.Tests/ParticleTests.cs`).
- Budgets and edge cases: burst larger than the budget, zero rate, one-tick and maximum lifetimes, system budget shared
  by several emitters (same file).
- Pixels: capture with `bun tools/mcp/src/cli.ts capture Sparks --checkpoints 0,60,120 --out DIR`, look at the PNGs
  and assert a few well-chosen pixels (a 1x1 unit square drawn by one particle lands where the camera math says).
- New sample or game: add the Particles reference to each host csproj and, for a sample, a `SnapshotGoldenTests` case.
