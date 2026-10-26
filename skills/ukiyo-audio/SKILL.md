---
name: ukiyo-audio
description: Use for sound in a ukiyo game — sound effects synthesized from presets (no audio files), WAV assets, playing sounds from Update with volume/pan/pitch, and proving sound happens with sound logs and headless WAV recordings.
---

# ukiyo audio

Sounds are data and playing is an event. The game says "play this" during `Update`; the runtime records a
`SoundEvent(Tick, Sound, Volume, Pan, Pitch)` and hands it to the host's output: SDL3 device on desktop, Web Audio in
the browser, a WAV recorder headless. Audio never feeds back into game state, so determinism is untouched. Reference
`src/Ukiyo.Audio/Ukiyo.Audio.csproj` from each host csproj.

## Making sounds

```csharp
using Ukiyo.Audio;

// Initialize
_jump  = context.CreateSound(SoundSynth.Preset("jump", seed: 2).Render());    // synthesized, no files
_coin  = context.LoadSound("sounds/coin");          // Shared/assets/sounds/coin.wav, or coin.json (synth description)
_music = context.LoadSound(context.Assets.Read("music/theme.wav"));
_zap   = context.CreateSound((SoundSynth.Preset("laser") with { Decay = 0.3f, LowPass = 3000f }).Render());

// Update
tick.Audio.Play(_jump);                                   // volume 1, centered, normal pitch
tick.Audio.Play(_coin, volume: 0.8f, pan: -0.5f, pitch: 1.2f);
```

- Presets: `blip`, `coin`, `jump`, `hit`, `explosion`, `powerup`, `laser`. The same preset and seed always give the same
  sound; change the seed for variations. Every parameter (`Wave`, `Frequency`, `Slide`, `Arpeggio`, `Attack`, `Sustain`,
  `Punch`, `Decay`, `LowPass`, `Volume`, …) can be overridden with `with`.
- `ukiyo_sound_generate {"game":"MyGame","name":"jump","preset":"jump","seed":4,"overrides":{"decay":0.3}}` writes
  `Shared/assets/sounds/jump.json`, which `context.LoadSound("sounds/jump")` renders at load.
- WAV: PCM 8/16/24/32-bit or float 32-bit, mono or stereo, up to 10 minutes per sound.
- Pan −1..1, pitch 0.125..8 (rate multiplier; also shortens the sound), volume 0..4. Up to 32 voices; the oldest is cut.
- If the game stores a `SoundQueue`, take it from `tick.Audio` every tick; outside the runtime it is `SoundQueue.Discard`.

Hosts: desktop keeps ~50 ms queued (`UKIYO_AUDIO=0` disables it); browsers only start audio after the first key or
click on the page, and sounds before that are skipped.

## Proving it

- Tests: `runtime.SoundLog = [];` then step and assert on events (tick, pitch, count). Example in
  `tests/Ukiyo.Tests/AudioTests.cs`.
- Headless: `--audio out.wav` mixes the whole run (one tick of audio per tick, aligned with snapshots) and prints
  `audio <path> seconds=… sounds=… peak=… rms=…`. Through MCP: `ukiyo_run {"game":…,"audio":true,"input":[…]}`.
  Peak 0 means nothing was heard.
- Sound is not image evidence: say "the run played 3 sounds, peak 0.6" — not "it sounds good". Listening is a human check.
