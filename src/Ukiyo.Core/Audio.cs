namespace Ukiyo;

/// <summary>A sound created through <see cref="GameContext.CreateSound"/>. Id 0 is invalid.</summary>
public readonly record struct SoundHandle(uint Id)
{
    public bool IsValid => Id != 0;
}

/// <summary>
/// PCM audio as 32-bit float samples in −1..1, interleaved when <see cref="Channels"/> is 2. Sounds are data, like
/// textures: the same bytes reach every host, and mixing happens in C# (desktop, headless) or Web Audio (browser).
/// </summary>
public sealed record AudioData(int SampleRate, int Channels, float[] Samples)
{
    public const int MaxSampleRate = 192_000;
    public const int MaxSeconds = 600;

    public int Frames => Samples.Length / Channels;

    public double Seconds => Frames / (double)SampleRate;

    public void Validate()
    {
        if (SampleRate is < 8_000 or > MaxSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "[AUDIO]: sample rate must be 8000..192000 Hz");
        }

        if (Channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "[AUDIO]: only mono and stereo are supported");
        }

        if (Samples.Length == 0 || Samples.Length % Channels != 0 || Samples.Length / Channels > (long)SampleRate * MaxSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(Samples), Samples.Length, $"[AUDIO]: need 1..{MaxSeconds} s of whole frames");
        }

        foreach (var sample in Samples)
        {
            if (!float.IsFinite(sample))
            {
                throw new ArgumentOutOfRangeException(nameof(Samples), sample, "[AUDIO]: samples must be finite");
            }
        }
    }
}

/// <summary>
/// One "play this sound" emitted by the game during <see cref="Tick"/>. <see cref="Pan"/> is −1 (left)..1 (right);
/// <see cref="Pitch"/> is a playback-rate multiplier (2 = one octave up, twice as short).
/// </summary>
public readonly record struct SoundEvent(long Tick, SoundHandle Sound, float Volume, float Pan, float Pitch);

/// <summary>
/// Where sounds go. Hosts implement it (SDL3 device, Web Audio, WAV recorder); the runtime calls it on the simulation
/// thread right after each tick, so a game never talks to an audio device and audio never feeds back into state.
/// </summary>
public interface IAudioOutput : IDisposable
{
    void Play(in SoundEvent sound, AudioData data);

    /// <summary>Called once after every simulated tick (offline outputs render exactly one tick of audio here).</summary>
    void EndTick(long tick);
}

/// <summary>
/// The sounds a tick asks for, read by the game as <c>tick.Audio</c>. Playing is fire-and-forget: the call only records
/// an event, so games stay deterministic and testable (see <see cref="GameRuntime.SoundLog"/>).
/// </summary>
public sealed class SoundQueue
{
    private readonly List<SoundEvent> _events = [];
    private readonly bool _discard;
    private long _tick;

    public SoundQueue()
    {
    }

    private SoundQueue(bool discard)
    {
        _discard = discard;
    }

    /// <summary>Default for ticks created outside a runtime (tests calling Update directly): plays nothing.</summary>
    public static SoundQueue Discard { get; } = new(discard: true);

    public void Play(SoundHandle sound, float volume = 1f, float pan = 0f, float pitch = 1f)
    {
        if (!sound.IsValid)
        {
            throw new ArgumentException("[AUDIO]: invalid sound handle", nameof(sound));
        }

        if (!float.IsFinite(volume) || volume < 0 || !float.IsFinite(pan) || !(pitch > 0) || !float.IsFinite(pitch))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), $"[AUDIO]: volume {volume} pan {pan} pitch {pitch} out of range");
        }

        if (!_discard)
        {
            _events.Add(new SoundEvent(_tick, sound, MathF.Min(volume, 4f), Math.Clamp(pan, -1f, 1f), Math.Clamp(pitch, 0.125f, 8f)));
        }
    }

    internal void Begin(long tick)
    {
        _tick = tick;
        _events.Clear();
    }

    internal IReadOnlyList<SoundEvent> Events => _events;
}
