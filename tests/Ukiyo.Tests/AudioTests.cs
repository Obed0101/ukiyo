using Ukiyo.Audio;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.LanternRun;
using Xunit;

namespace Ukiyo.Tests;

public sealed class AudioTests
{
    private static readonly RenderExtent Viewport = new(640, 360, 1f);

    [Fact]
    public void Same_preset_and_seed_render_identical_samples_and_seeds_vary_the_sound()
    {
        var a = SoundSynth.Preset("coin", seed: 4).Render();
        var b = SoundSynth.Preset("coin", seed: 4).Render();
        var c = SoundSynth.Preset("coin", seed: 5).Render();

        Assert.Equal(a.Samples, b.Samples);
        Assert.NotEqual(a.Samples, c.Samples);
        Assert.Equal(SoundSynth.SampleRate, a.SampleRate);
        Assert.InRange(a.Seconds, 0.1, 1.0);
        Assert.All(a.Samples, s => Assert.InRange(s, -1f, 1f));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_preset_renders_audible_finite_audio(string preset)
    {
        var sound = SoundSynth.Preset(preset).Render();
        Assert.True(sound.Samples.Max(MathF.Abs) > 0.05f, $"{preset} is silent");
        sound.Validate();
    }

    public static TheoryData<string> Presets()
    {
        var data = new TheoryData<string>();
        foreach (var name in SoundSynth.PresetNames)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Json_description_applies_preset_then_overrides()
    {
        var synth = SoundSynth.FromJson("""{"preset":"laser","seed":9,"decay":0.5,"wave":"sine"}""");
        Assert.Equal(SynthWave.Sine, synth.Wave);
        Assert.Equal(0.5f, synth.Decay);
        Assert.Equal(SoundSynth.Preset("laser", 9).Frequency, synth.Frequency);
        Assert.Throws<FormatException>(() => SoundSynth.FromJson("""{"preset":"coin","loudness":2}"""));
        Assert.Throws<ArgumentException>(() => SoundSynth.Preset("trumpet"));
    }

    [Fact]
    public void Wav_round_trip_keeps_rate_channels_and_samples_within_16_bit_precision()
    {
        var original = new AudioData(22_050, 2, [0f, 0.5f, -0.5f, 1f, -1f, 0.25f]);
        var decoded = WavCodec.Decode(WavCodec.Encode(original));

        Assert.Equal((22_050, 2), (decoded.SampleRate, decoded.Channels));
        Assert.Equal(original.Samples.Length, decoded.Samples.Length);
        for (var i = 0; i < original.Samples.Length; i++)
        {
            Assert.InRange(decoded.Samples[i] - original.Samples[i], -1e-4f, 1e-4f);
        }

        Assert.Throws<InvalidDataException>(() => WavCodec.Decode("RIFF0000WAVEjunk"u8));
    }

    [Fact]
    public void Mixer_pans_hard_left_and_never_exceeds_full_scale()
    {
        var mixer = new AudioMixer(48_000);
        var loud = new AudioData(48_000, 1, Enumerable.Repeat(1f, 480).ToArray());
        mixer.Play(loud, volume: 1f, pan: -1f);
        mixer.Play(loud, volume: 1f, pan: -1f);
        mixer.Play(loud, volume: 1f, pan: -1f);
        var buffer = new float[200];
        mixer.Mix(buffer);

        Assert.All(buffer.Where((_, i) => i % 2 == 0), s => Assert.InRange(s, 0.8f, 1f));
        Assert.All(buffer.Where((_, i) => i % 2 == 1), s => Assert.InRange(s, -1e-6f, 1e-6f));
    }

    [Fact]
    public void Mixer_releases_voices_when_sounds_end_and_steals_the_oldest_when_full()
    {
        var mixer = new AudioMixer(48_000, maxVoices: 2);
        var shortSound = new AudioData(48_000, 1, new float[10]);
        mixer.Play(shortSound);
        mixer.Play(shortSound);
        mixer.Play(shortSound);
        Assert.Equal(2, mixer.ActiveVoices);
        mixer.Mix(new float[64]);
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public async Task LanternRun_plays_the_menu_blip_and_an_ember_chime_that_land_in_the_recording()
    {
        var game = new LanternRunGame();
        var recorder = new RecordingAudioOutput();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()))
        {
            Script = InputScript.Parse("""[{"tick":1,"key":"Enter","down":true},{"tick":2,"key":"Enter","down":false},{"tick":30,"key":"Right","down":true}]"""),
            SoundLog = [],
            Audio = recorder,
        };
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", Viewport));
        runtime.StepTo(120);

        Assert.Equal(1, runtime.SoundLog![0].Tick);
        Assert.Contains(runtime.SoundLog, s => s.Pitch > 1f); // ember chime rises per ember collected
        Assert.Equal(runtime.SoundLog.Count, recorder.SoundsPlayed);
        Assert.Equal(120 * 48_000 / 60 * 2, recorder.Samples.Count);
        Assert.True(recorder.Levels().Peak > 0.05f);
    }

    [Fact]
    public void Invalid_sounds_and_plays_fail_with_typed_errors()
    {
        var context = new GameContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => context.CreateSound(new AudioData(44_100, 3, new float[6])));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.CreateSound(new AudioData(44_100, 1, [float.NaN])));
        var queue = new SoundQueue();
        Assert.Throws<ArgumentException>(() => queue.Play(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Play(new SoundHandle(1), pitch: 0f));
    }
}
