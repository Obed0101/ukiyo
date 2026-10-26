namespace Ukiyo.Audio;

/// <summary>
/// Software mixer shared by the desktop device and the headless recorder: stereo float output at a fixed rate,
/// linear resampling (source rate × pitch), equal-power pan, and a soft limiter. Pure arithmetic in a fixed order, so
/// the same events give the same samples on the same platform. Not thread-safe; call it from one thread.
/// </summary>
public sealed class AudioMixer
{
    public const int DefaultSampleRate = 48_000;
    public const int DefaultMaxVoices = 32;

    private readonly List<Voice> _voices = [];

    public AudioMixer(int sampleRate = DefaultSampleRate, int maxVoices = DefaultMaxVoices)
    {
        if (sampleRate is < 8_000 or > AudioData.MaxSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "[AUDIO]: mixer rate must be 8000..192000 Hz");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxVoices, 1);
        SampleRate = sampleRate;
        MaxVoices = maxVoices;
    }

    public int SampleRate { get; }

    public int MaxVoices { get; }

    public int ActiveVoices => _voices.Count;

    /// <summary>Master gain applied before the limiter.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Starts a voice. When all voices are busy the oldest one is cut, so new sounds are never dropped.</summary>
    public void Play(AudioData data, float volume = 1f, float pan = 0f, float pitch = 1f)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_voices.Count >= MaxVoices)
        {
            _voices.RemoveAt(0);
        }

        // Equal-power pan: center is −3 dB per side, hard left/right is full level on one side.
        var angle = (Math.Clamp(pan, -1f, 1f) + 1f) * MathF.PI * 0.25f;
        _voices.Add(new Voice(data, data.SampleRate * pitch / SampleRate, volume * MathF.Cos(angle), volume * MathF.Sin(angle)));
    }

    public void StopAll() => _voices.Clear();

    /// <summary>Overwrites <paramref name="stereo"/> (interleaved L, R) with the next frames of the mix.</summary>
    public void Mix(Span<float> stereo)
    {
        if (stereo.Length % 2 != 0)
        {
            throw new ArgumentException("[AUDIO]: stereo buffer needs an even length", nameof(stereo));
        }

        stereo.Clear();
        for (var v = _voices.Count - 1; v >= 0; v--)
        {
            if (!_voices[v].Render(stereo))
            {
                _voices.RemoveAt(v);
            }
        }

        for (var i = 0; i < stereo.Length; i++)
        {
            stereo[i] = Limit(stereo[i] * Volume);
        }
    }

    /// <summary>Transparent below ±0.8, then a smooth knee that never exceeds ±1 (no wrap-around clicks).</summary>
    private static float Limit(float x)
    {
        const float Knee = 0.8f;
        var magnitude = MathF.Abs(x);
        if (magnitude <= Knee)
        {
            return x;
        }

        var over = (magnitude - Knee) / (1f - Knee);
        return MathF.CopySign(Knee + (1f - Knee) * (over / (1f + over)), x);
    }

    private sealed class Voice(AudioData data, double step, float left, float right)
    {
        private double _position;

        /// <summary>Adds this voice into the buffer. Returns false once the sound has finished.</summary>
        public bool Render(Span<float> stereo)
        {
            var frames = data.Frames;
            var samples = data.Samples;
            for (var i = 0; i < stereo.Length; i += 2)
            {
                var index = (int)_position;
                if (index >= frames)
                {
                    return false;
                }

                var next = Math.Min(index + 1, frames - 1);
                var t = (float)(_position - index);
                float l, r;
                if (data.Channels == 1)
                {
                    l = r = samples[index] + (samples[next] - samples[index]) * t;
                }
                else
                {
                    l = samples[index * 2] + (samples[next * 2] - samples[index * 2]) * t;
                    r = samples[index * 2 + 1] + (samples[next * 2 + 1] - samples[index * 2 + 1]) * t;
                }

                stereo[i] += l * left;
                stereo[i + 1] += r * right;
                _position += step;
            }

            return (int)_position < frames;
        }
    }
}

/// <summary>
/// Offline output for headless runs and tests: renders exactly one tick of audio (rate / 60 frames) after every tick,
/// so the recording lines up with snapshots, and keeps simple statistics an agent can check.
/// </summary>
public sealed class RecordingAudioOutput : IAudioOutput
{
    private readonly AudioMixer _mixer;
    private readonly List<float> _samples = [];
    private readonly float[] _tickBuffer;

    public RecordingAudioOutput(int sampleRate = AudioMixer.DefaultSampleRate)
    {
        if (sampleRate % Simulation.TickRate != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "[AUDIO]: recording rate must be a multiple of the tick rate");
        }

        _mixer = new AudioMixer(sampleRate);
        _tickBuffer = new float[sampleRate / Simulation.TickRate * 2];
    }

    public int SampleRate => _mixer.SampleRate;

    public int SoundsPlayed { get; private set; }

    /// <summary>Interleaved stereo samples recorded so far.</summary>
    public IReadOnlyList<float> Samples => _samples;

    public double Seconds => _samples.Count / 2.0 / SampleRate;

    public void Play(in SoundEvent sound, AudioData data)
    {
        _mixer.Play(data, sound.Volume, sound.Pan, sound.Pitch);
        SoundsPlayed++;
    }

    public void EndTick(long tick)
    {
        _mixer.Mix(_tickBuffer);
        _samples.AddRange(_tickBuffer);
    }

    public (float Peak, float Rms) Levels()
    {
        double sum = 0;
        var peak = 0f;
        foreach (var sample in _samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
            sum += sample * (double)sample;
        }

        return (peak, _samples.Count == 0 ? 0f : (float)Math.Sqrt(sum / _samples.Count));
    }

    public byte[] ToWav() => WavCodec.Encode(new AudioData(SampleRate, 2, [.. _samples]));

    public void Dispose()
    {
    }
}
