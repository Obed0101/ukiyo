using System.Buffers.Binary;

namespace Ukiyo.Audio;

/// <summary>
/// RIFF/WAVE reader and writer for game sounds: PCM 8/16/24/32-bit and IEEE float 32-bit, mono or stereo (WAVE_FORMAT_
/// EXTENSIBLE included). Writes 16-bit PCM. Anything else is a typed error.
/// </summary>
public static class WavCodec
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static AudioData Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("[WAV]: not a RIFF/WAVE file");
        }

        ushort format = 0, channels = 0, bits = 0;
        var rate = 0;
        ReadOnlySpan<byte> data = default;
        var haveFormat = false;
        var haveData = false;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);
            if (size < 0 || offset + 8 + size > wav.Length)
            {
                // Some encoders write a data size past the end; take what is there.
                size = wav.Length - offset - 8;
            }

            var body = wav.Slice(offset + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    throw new InvalidDataException("[WAV]: fmt chunk too short");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == FormatExtensible && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // first two bytes of the sub-format GUID
                }

                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = body;
                haveData = true;
            }

            offset += 8 + size + (size & 1); // chunks are word-aligned
        }

        if (!haveFormat || !haveData)
        {
            throw new InvalidDataException("[WAV]: missing fmt or data chunk");
        }

        if (channels is not (1 or 2))
        {
            throw new InvalidDataException($"[WAV]: {channels} channels; only mono and stereo are supported");
        }

        var bytesPerSample = bits / 8;
        var supported = (format == FormatPcm && bits is 8 or 16 or 24 or 32) || (format == FormatFloat && bits == 32);
        if (!supported)
        {
            throw new InvalidDataException($"[WAV]: format {format} with {bits} bits is not supported (use PCM 16-bit or float 32-bit)");
        }

        var count = data.Length / bytesPerSample / channels * channels;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var s = data.Slice(i * bytesPerSample, bytesPerSample);
            samples[i] = (format, bits) switch
            {
                (FormatFloat, _) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (_, 8) => (s[0] - 128) / 128f,
                (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (_, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }

        var audio = new AudioData(rate, channels, samples);
        audio.Validate();
        return audio;
    }

    /// <summary>16-bit PCM WAV. Samples are clamped to −1..1.</summary>
    public static byte[] Encode(AudioData audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var dataBytes = audio.Samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)audio.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], audio.SampleRate * audio.Channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(audio.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        for (var i = 0; i < audio.Samples.Length; i++)
        {
            var value = (short)MathF.Round(Math.Clamp(audio.Samples[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], value);
        }

        return wav;
    }
}

/// <summary>Loading sounds from a game's embedded assets.</summary>
public static class AudioContextExtensions
{
    /// <summary>Decodes a WAV (for example <c>Assets.Read("sounds/jump.wav")</c>) and registers it.</summary>
    public static SoundHandle LoadSound(this GameContext context, ReadOnlySpan<byte> wav)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CreateSound(WavCodec.Decode(wav));
    }

    /// <summary>
    /// Loads <c>{name}.wav</c> when it exists, otherwise synthesizes <c>{name}.json</c> (written by
    /// <c>ukiyo_sound_generate</c>): <c>{"preset":"coin","seed":3}</c> plus optional parameter overrides.
    /// </summary>
    public static SoundHandle LoadSound(this GameContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Assets.Exists($"{name}.wav"))
        {
            return context.LoadSound(context.Assets.Read($"{name}.wav"));
        }

        return context.CreateSound(SoundSynth.FromJson(context.Assets.ReadText($"{name}.json")).Render());
    }
}
