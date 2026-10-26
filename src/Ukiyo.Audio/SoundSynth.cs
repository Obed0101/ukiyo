using System.Globalization;
using System.Text.Json;

namespace Ukiyo.Audio;

public enum SynthWave : byte
{
    Square = 0,
    Saw = 1,
    Sine = 2,
    Triangle = 3,
    Noise = 4,
}

/// <summary>
/// A retro sound effect described by a few numbers, in the spirit of sfxr: oscillator, pitch slide, vibrato, one
/// arpeggio jump, an attack/sustain/decay envelope and a low-pass filter. Agents can create sounds with no audio files:
/// pick a preset, a seed for variation, and override any parameter. Rendering is deterministic for a given platform
/// (it uses a seeded xorshift for noise and variation).
/// </summary>
public sealed record SoundSynth
{
    public const int SampleRate = 44_100;

    public SynthWave Wave { get; init; } = SynthWave.Square;

    /// <summary>Start frequency in Hz.</summary>
    public float Frequency { get; init; } = 440f;

    /// <summary>Octaves per second (negative falls). −4 is a fast drop.</summary>
    public float Slide { get; init; }

    /// <summary>Lowest frequency the slide can reach; the sound stops there, as in sfxr.</summary>
    public float MinFrequency { get; init; } = 20f;

    public float VibratoDepth { get; init; }

    public float VibratoSpeed { get; init; }

    /// <summary>Frequency multiplier applied once after <see cref="ArpeggioTime"/> seconds (coin pickups use ~1.5).</summary>
    public float Arpeggio { get; init; } = 1f;

    public float ArpeggioTime { get; init; }

    /// <summary>Square duty cycle 0.05..0.95.</summary>
    public float Duty { get; init; } = 0.5f;

    public float Attack { get; init; }

    public float Sustain { get; init; } = 0.1f;

    /// <summary>Fraction of the sustain level reached at the very start (a "punch").</summary>
    public float Punch { get; init; }

    public float Decay { get; init; } = 0.2f;

    /// <summary>Low-pass cutoff in Hz; 0 disables the filter.</summary>
    public float LowPass { get; init; }

    public float Volume { get; init; } = 0.5f;

    public int Seed { get; init; } = 1;

    public static IReadOnlyList<string> PresetNames { get; } = ["blip", "coin", "jump", "hit", "explosion", "powerup", "laser"];

    /// <summary>A preset with seeded variation: the same name and seed always give the same sound.</summary>
    public static SoundSynth Preset(string name, int seed = 1)
    {
        var random = new XorShift(seed);
        float Vary(float value, float amount) => value * (1f + (random.NextFloat() * 2f - 1f) * amount);
        return name switch
        {
            "blip" => new SoundSynth { Wave = SynthWave.Square, Frequency = Vary(880f, 0.3f), Sustain = 0.03f, Decay = Vary(0.06f, 0.3f), Duty = 0.5f, Seed = seed },
            "coin" => new SoundSynth { Wave = SynthWave.Square, Frequency = Vary(988f, 0.15f), Arpeggio = 1.335f, ArpeggioTime = Vary(0.06f, 0.3f), Sustain = 0.05f, Punch = 0.4f, Decay = Vary(0.25f, 0.3f), Duty = 0.25f, Seed = seed },
            "jump" => new SoundSynth { Wave = SynthWave.Square, Frequency = Vary(330f, 0.2f), Slide = Vary(2.2f, 0.3f), Sustain = 0.06f, Decay = Vary(0.16f, 0.3f), Duty = 0.4f, LowPass = 6000f, Seed = seed },
            "hit" => new SoundSynth { Wave = SynthWave.Noise, Frequency = Vary(600f, 0.4f), Slide = Vary(-3f, 0.3f), Sustain = 0.02f, Punch = 0.5f, Decay = Vary(0.18f, 0.3f), LowPass = 4000f, Seed = seed },
            "explosion" => new SoundSynth { Wave = SynthWave.Noise, Frequency = Vary(160f, 0.4f), Slide = Vary(-0.8f, 0.4f), Sustain = 0.15f, Punch = 0.6f, Decay = Vary(0.7f, 0.3f), LowPass = 1800f, Volume = 0.6f, Seed = seed },
            "powerup" => new SoundSynth { Wave = SynthWave.Triangle, Frequency = Vary(262f, 0.2f), Slide = Vary(1.5f, 0.3f), VibratoDepth = 0.05f, VibratoSpeed = 14f, Sustain = 0.25f, Decay = Vary(0.3f, 0.3f), Seed = seed },
            "laser" => new SoundSynth { Wave = SynthWave.Saw, Frequency = Vary(1400f, 0.3f), Slide = Vary(-5f, 0.3f), MinFrequency = 120f, Sustain = 0.05f, Decay = Vary(0.12f, 0.3f), LowPass = 7000f, Seed = seed },
            _ => throw new ArgumentException($"[SYNTH]: unknown preset \"{name}\"; use {string.Join(", ", PresetNames)}", nameof(name)),
        };
    }

    /// <summary>
    /// <c>{"preset":"coin","seed":3,"decay":0.4}</c>, or a full description without a preset. Keys match the property
    /// names in camelCase; "wave" is a <see cref="SynthWave"/> name.
    /// </summary>
    public static SoundSynth FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("[SYNTH]: a sound description is a JSON object");
        }

        var seed = root.TryGetProperty("seed", out var seedElement) ? seedElement.GetInt32() : 1;
        var synth = root.TryGetProperty("preset", out var preset) ? Preset(preset.GetString() ?? "", seed) : new SoundSynth { Seed = seed };
        foreach (var property in root.EnumerateObject())
        {
            synth = property.Name switch
            {
                "preset" or "seed" or "$comment" or "meta" => synth,
                "wave" => synth with { Wave = Enum.TryParse<SynthWave>(property.Value.GetString(), ignoreCase: true, out var wave) ? wave : throw new FormatException($"[SYNTH]: unknown wave {property.Value.GetRawText()}") },
                "frequency" => synth with { Frequency = property.Value.GetSingle() },
                "slide" => synth with { Slide = property.Value.GetSingle() },
                "minFrequency" => synth with { MinFrequency = property.Value.GetSingle() },
                "vibratoDepth" => synth with { VibratoDepth = property.Value.GetSingle() },
                "vibratoSpeed" => synth with { VibratoSpeed = property.Value.GetSingle() },
                "arpeggio" => synth with { Arpeggio = property.Value.GetSingle() },
                "arpeggioTime" => synth with { ArpeggioTime = property.Value.GetSingle() },
                "duty" => synth with { Duty = property.Value.GetSingle() },
                "attack" => synth with { Attack = property.Value.GetSingle() },
                "sustain" => synth with { Sustain = property.Value.GetSingle() },
                "punch" => synth with { Punch = property.Value.GetSingle() },
                "decay" => synth with { Decay = property.Value.GetSingle() },
                "lowPass" => synth with { LowPass = property.Value.GetSingle() },
                "volume" => synth with { Volume = property.Value.GetSingle() },
                _ => throw new FormatException($"[SYNTH]: unknown parameter \"{property.Name}\""),
            };
        }

        return synth;
    }

    public string ToJson() => string.Create(CultureInfo.InvariantCulture,
        $"{{\"wave\":\"{Wave}\",\"frequency\":{Frequency},\"slide\":{Slide},\"minFrequency\":{MinFrequency},\"vibratoDepth\":{VibratoDepth},\"vibratoSpeed\":{VibratoSpeed},\"arpeggio\":{Arpeggio},\"arpeggioTime\":{ArpeggioTime},\"duty\":{Duty},\"attack\":{Attack},\"sustain\":{Sustain},\"punch\":{Punch},\"decay\":{Decay},\"lowPass\":{LowPass},\"volume\":{Volume},\"seed\":{Seed}}}");

    /// <summary>Renders the sound as mono 44.1 kHz PCM.</summary>
    public AudioData Render()
    {
        Validate();
        var attack = (int)(Attack * SampleRate);
        var sustain = (int)(Sustain * SampleRate);
        var decay = (int)(Decay * SampleRate);
        var total = Math.Max(1, attack + sustain + decay);
        var samples = new float[total];
        var noise = new XorShift(Seed ^ 0x5EED);
        var noiseValue = 0f;
        double phase = 0;
        var frequency = (double)Frequency;
        var slidePerSample = Math.Pow(2, Slide / SampleRate);
        var arpeggioAt = ArpeggioTime > 0 ? (int)(ArpeggioTime * SampleRate) : int.MaxValue;
        var filterAlpha = LowPass > 0 ? 1f - MathF.Exp(-2f * MathF.PI * LowPass / SampleRate) : 1f;
        var filtered = 0f;
        var written = total;
        for (var i = 0; i < total; i++)
        {
            if (i == arpeggioAt)
            {
                frequency *= Arpeggio;
            }

            frequency *= slidePerSample;
            if (frequency < MinFrequency)
            {
                written = i;
                break;
            }

            var vibrato = VibratoDepth > 0 ? 1.0 + VibratoDepth * Math.Sin(2 * Math.PI * VibratoSpeed * i / SampleRate) : 1.0;
            var previousPhase = phase;
            phase += frequency * vibrato / SampleRate;
            phase -= Math.Floor(phase);
            if (phase < previousPhase)
            {
                noiseValue = noise.NextFloat() * 2f - 1f; // new noise value once per period, like sfxr
            }

            var p = (float)phase;
            var oscillator = Wave switch
            {
                SynthWave.Square => p < Math.Clamp(Duty, 0.05f, 0.95f) ? 1f : -1f,
                SynthWave.Saw => 1f - 2f * p,
                SynthWave.Sine => MathF.Sin(2f * MathF.PI * p),
                SynthWave.Triangle => 1f - 4f * MathF.Abs(p - 0.5f),
                _ => noiseValue,
            };

            filtered += (oscillator - filtered) * filterAlpha;
            samples[i] = filtered * Envelope(i, attack, sustain, decay) * Volume;
        }

        return new AudioData(SampleRate, 1, written == total ? samples : samples[..Math.Max(1, written)]);
    }

    private float Envelope(int i, int attack, int sustain, int decay)
    {
        if (i < attack)
        {
            return i / (float)attack;
        }

        i -= attack;
        if (i < sustain)
        {
            return 1f + Punch * (1f - i / (float)sustain);
        }

        i -= sustain;
        return decay == 0 ? 0f : MathF.Max(0f, 1f - i / (float)decay);
    }

    private void Validate()
    {
        float[] values = [Frequency, Slide, MinFrequency, VibratoDepth, VibratoSpeed, Arpeggio, ArpeggioTime, Duty, Attack, Sustain, Punch, Decay, LowPass, Volume];
        if (values.Any(v => !float.IsFinite(v)))
        {
            throw new ArgumentOutOfRangeException(nameof(Frequency), "[SYNTH]: every parameter must be finite");
        }

        if (Frequency is < 20f or > 20_000f || Attack < 0 || Sustain < 0 || Decay < 0 || Attack + Sustain + Decay is <= 0f or > 10f || Volume is < 0f or > 1f || Arpeggio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Frequency), $"[SYNTH]: frequency 20..20000 Hz, envelope 0..10 s, volume 0..1, arpeggio > 0 (got {ToJson()})");
        }
    }

    /// <summary>Small deterministic generator (xorshift32); never zero.</summary>
    private struct XorShift(int seed)
    {
        private uint _state = (uint)seed * 2654435761u | 1u;

        public float NextFloat()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state >> 8) / 16777216f;
        }
    }
}
