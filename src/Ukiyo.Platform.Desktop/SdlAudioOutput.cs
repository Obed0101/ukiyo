using Ukiyo.Audio;

namespace Ukiyo.Hosting;

/// <summary>
/// Plays the game's sounds on the default SDL3 playback device. The shared <see cref="AudioMixer"/> runs on the main
/// thread; <see cref="Pump"/> keeps about 50 ms queued on the device stream, which bounds latency without a callback
/// thread. Returns null from <see cref="TryOpen"/> when there is no device or <c>UKIYO_AUDIO=0</c>.
/// </summary>
internal sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const int Rate = AudioMixer.DefaultSampleRate;
    private const int ChunkFrames = Rate / Simulation.TickRate;
    private const int TargetQueuedFrames = ChunkFrames * 3;

    private readonly AudioMixer _mixer = new(Rate);
    private readonly float[] _chunk = new float[ChunkFrames * 2];
    private nint _stream;

    private SdlAudioOutput(nint stream)
    {
        _stream = stream;
    }

    public static SdlAudioOutput? TryOpen()
    {
        if (Environment.GetEnvironmentVariable("UKIYO_AUDIO") is "0" or "off" or "false")
        {
            return null;
        }

        if (Sdl3.SDL_InitSubSystem(Sdl3.InitAudio) == 0)
        {
            Console.Error.WriteLine($"[ukiyo] audio disabled: {Sdl3.LastError}");
            return null;
        }

        var spec = new Sdl3.AudioSpec { Format = Sdl3.AudioF32LE, Channels = 2, Freq = Rate };
        var stream = Sdl3.SDL_OpenAudioDeviceStream(Sdl3.AudioDeviceDefaultPlayback, &spec, 0, 0);
        if (stream == 0)
        {
            Console.Error.WriteLine($"[ukiyo] audio disabled: {Sdl3.LastError}");
            return null;
        }

        Sdl3.SDL_ResumeAudioStreamDevice(stream);
        Console.WriteLine($"[ukiyo] audio {Rate} Hz stereo, mixer voices={AudioMixer.DefaultMaxVoices}");
        return new SdlAudioOutput(stream);
    }

    public void Play(in SoundEvent sound, AudioData data) => _mixer.Play(data, sound.Volume, sound.Pan, sound.Pitch);

    public void EndTick(long tick)
    {
    }

    /// <summary>Call once per presented frame: tops the device queue up to the target with freshly mixed audio.</summary>
    public void Pump()
    {
        if (_stream == 0)
        {
            return;
        }

        var queuedFrames = Sdl3.SDL_GetAudioStreamQueued(_stream) / (sizeof(float) * 2);
        while (queuedFrames < TargetQueuedFrames)
        {
            _mixer.Mix(_chunk);
            fixed (float* data = _chunk)
            {
                Sdl3.SDL_PutAudioStreamData(_stream, data, _chunk.Length * sizeof(float));
            }

            queuedFrames += ChunkFrames;
        }
    }

    public void Dispose()
    {
        if (_stream != 0)
        {
            Sdl3.SDL_DestroyAudioStream(_stream);
            _stream = 0;
        }
    }
}
