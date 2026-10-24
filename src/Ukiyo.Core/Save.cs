using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Ukiyo;

/// <summary>What went wrong saving, restoring or rewinding. Message prefix is <c>[SAVE:Code]:</c>.</summary>
public enum SaveErrorCode
{
    /// <summary>The game does not implement <see cref="ISaveable"/>.</summary>
    NotSaveable,

    /// <summary>The bytes are not a ukiyo save (wrong magic).</summary>
    BadMagic,

    /// <summary>Written by an engine with a different save format version.</summary>
    FormatVersion,

    /// <summary>Saved by another game, or by one with and one without a scene registry.</summary>
    Mismatch,

    /// <summary>The game's own <see cref="ISaveable.SaveVersion"/> differs.</summary>
    GameVersion,

    /// <summary>The payload checksum does not match: the file was damaged or edited.</summary>
    Corrupt,

    /// <summary>A read ran past the end of the data.</summary>
    Truncated,

    /// <summary>A block was not fully read, or a section tag did not match: Save and Load disagree.</summary>
    Layout,

    /// <summary>A value that cannot be real (negative count, unknown scene, impossible length).</summary>
    Invalid,

    /// <summary>Time travel was never enabled on this runtime.</summary>
    NoTimeline,

    /// <summary>Rewind target is after the current tick.</summary>
    FutureTick,

    /// <summary>Rewind target is older than the oldest checkpoint still kept.</summary>
    OutOfWindow,
}

/// <summary>A save that cannot be written or applied. Restores validate before they change anything where they can.</summary>
public sealed class SaveException(SaveErrorCode code, string message, Exception? inner = null)
    : Exception($"[SAVE:{code}]: {message}", inner)
{
    public SaveErrorCode Code { get; } = code;
}

/// <summary>
/// Opt-in for games: implement next to <see cref="IGame"/> to be saved and restored at a tick boundary. Save exactly the
/// state <c>Update</c> reads or writes across ticks (positions, velocities, timers, score, UI focus, random generator
/// state) and nothing derived from it; resources made in <c>Initialize</c> are not state. <c>Load</c> runs on a game
/// that was initialized (a fresh process, or the running game being rewound) and must read exactly what <c>Save</c>
/// wrote, in the same order. See docs/save-replay.md.
/// </summary>
public interface ISaveable
{
    /// <summary>Layout version of what <see cref="Save"/> writes. Bump it when the layout changes: older saves then fail with <see cref="SaveErrorCode.GameVersion"/>.</summary>
    int SaveVersion => 1;

    void Save(SaveWriter writer);

    /// <summary>
    /// Replaces the game's state. The scene registry (if any) was restored first, so entities exist again as new
    /// instances built from their saved arguments: look them up again instead of holding on to old references.
    /// </summary>
    void Load(SaveReader reader);
}

/// <summary>
/// Little-endian binary writer for saves. Floats are written as their exact bits (no rounding, −0 stays −0), strings as
/// length-prefixed UTF-8. Iterate collections in a stable order when you write them.
/// </summary>
public sealed class SaveWriter
{
    private readonly MemoryStream _stream = new();

    public int Length => (int)_stream.Length;

    public void Bool(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

    public void Byte(byte value) => _stream.WriteByte(value);

    public void Int32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void Int64(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void UInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void Single(float value) => Int32(BitConverter.SingleToInt32Bits(value));

    public void Double(double value) => Int64(BitConverter.DoubleToInt64Bits(value));

    /// <summary>UTF-8 with a length prefix; null is distinct from empty.</summary>
    public void String(string? value)
    {
        if (value is null)
        {
            Int32(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Int32(bytes.Length);
        _stream.Write(bytes);
    }

    public void Vector2(Vector2 value)
    {
        Single(value.X);
        Single(value.Y);
    }

    public void Vector3(Vector3 value)
    {
        Single(value.X);
        Single(value.Y);
        Single(value.Z);
    }

    public void Vector4(Vector4 value)
    {
        Single(value.X);
        Single(value.Y);
        Single(value.Z);
        Single(value.W);
    }

    public void Quaternion(Quaternion value)
    {
        Single(value.X);
        Single(value.Y);
        Single(value.Z);
        Single(value.W);
    }

    /// <summary>A length-prefixed block of raw bytes.</summary>
    public void Bytes(ReadOnlySpan<byte> value)
    {
        Int32(value.Length);
        _stream.Write(value);
    }

    /// <summary>A named marker; <see cref="SaveReader.Expect"/> checks it, which pins down a Save/Load that drifted apart.</summary>
    public void Tag(string name) => String(name);

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>Reads what <see cref="SaveWriter"/> wrote. Any read past the end throws <see cref="SaveErrorCode.Truncated"/>.</summary>
public sealed class SaveReader(ReadOnlyMemory<byte> data)
{
    private int _position;

    public int Remaining => data.Length - _position;

    public bool Bool() => Take(1)[0] switch
    {
        0 => false,
        1 => true,
        var other => throw new SaveException(SaveErrorCode.Invalid, $"boolean byte {other} at offset {_position - 1}"),
    };

    public byte Byte() => Take(1)[0];

    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public float Single() => BitConverter.Int32BitsToSingle(Int32());

    public double Double() => BitConverter.Int64BitsToDouble(Int64());

    public string? NullableString()
    {
        var length = Int32();
        if (length == -1)
        {
            return null;
        }

        if (length < 0)
        {
            throw new SaveException(SaveErrorCode.Invalid, $"string length {length} at offset {_position - 4}");
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Take(length));
        }
        catch (ArgumentException error)
        {
            throw new SaveException(SaveErrorCode.Invalid, $"string is not valid UTF-8 at offset {_position - length}", error);
        }
    }

    public string String() => NullableString() ?? throw new SaveException(SaveErrorCode.Invalid, $"null string where text was expected at offset {_position - 4}");

    public Vector2 Vector2() => new(Single(), Single());

    public Vector3 Vector3() => new(Single(), Single(), Single());

    public Vector4 Vector4() => new(Single(), Single(), Single(), Single());

    public Quaternion Quaternion() => new(Single(), Single(), Single(), Single());

    public byte[] Bytes()
    {
        var length = Int32();
        if (length < 0)
        {
            throw new SaveException(SaveErrorCode.Invalid, $"block length {length} at offset {_position - 4}");
        }

        return Take(length).ToArray();
    }

    /// <summary>
    /// An element count for a list you are about to read. Each element is at least one byte, so a count larger than the
    /// data left is corruption: it throws instead of allocating.
    /// </summary>
    public int Count()
    {
        var count = Int32();
        if (count < 0 || count > Remaining)
        {
            throw new SaveException(SaveErrorCode.Invalid, $"count {count} with {Remaining} bytes left at offset {_position - 4}");
        }

        return count;
    }

    public void Expect(string tag)
    {
        var actual = NullableString();
        if (actual != tag)
        {
            throw new SaveException(SaveErrorCode.Layout, $"expected section \"{tag}\" but found \"{actual}\": Save and Load disagree");
        }
    }

    internal void RequireEnd(string what)
    {
        if (Remaining != 0)
        {
            throw new SaveException(SaveErrorCode.Layout, $"{what} left {Remaining} unread byte(s): Save and Load disagree");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
        {
            throw new SaveException(SaveErrorCode.Truncated, $"needed {count} byte(s) at offset {_position}, {Remaining} left");
        }

        var span = data.Span.Slice(_position, count);
        _position += count;
        return span;
    }
}

/// <summary>Header facts of a save, read without restoring it.</summary>
public sealed record SaveInfo(string Game, long Tick, int FormatVersion, int GameVersion, int Bytes, bool HasScenes);

/// <summary>
/// The save container: <c>"UKSV"</c>, format version (u16), game name, the game's <see cref="ISaveable.SaveVersion"/>,
/// tick, scenes flag, payload length and FNV-1a 64 of the payload, then the payload (engine state, game state). Only
/// deterministic data goes in: the same state saves to the same bytes.
/// </summary>
internal static class SaveFile
{
    public const ushort FormatVersion = 1;
    private static readonly byte[] Magic = "UKSV"u8.ToArray();

    public static byte[] Write(string game, int gameVersion, long tick, bool hasScenes, byte[] payload)
    {
        var writer = new SaveWriter();
        foreach (var b in Magic)
        {
            writer.Byte(b);
        }

        writer.Byte((byte)(FormatVersion & 0xFF));
        writer.Byte((byte)(FormatVersion >> 8));
        writer.String(game);
        writer.Int32(gameVersion);
        writer.Int64(tick);
        writer.Bool(hasScenes);
        writer.UInt64(Fnv(payload));
        writer.Bytes(payload);
        return writer.ToArray();
    }

    /// <summary>Validates magic, format version and checksum; returns the header and the payload.</summary>
    public static (SaveInfo Info, byte[] Payload) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length + 2 || !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new SaveException(SaveErrorCode.BadMagic, "not a ukiyo save (missing UKSV header)");
        }

        var format = bytes[4] | (bytes[5] << 8);
        if (format != FormatVersion)
        {
            throw new SaveException(SaveErrorCode.FormatVersion, $"save format {format}, this engine reads {FormatVersion}");
        }

        var reader = new SaveReader(bytes[6..].ToArray());
        var game = reader.String();
        var gameVersion = reader.Int32();
        var tick = reader.Int64();
        var hasScenes = reader.Bool();
        var checksum = reader.UInt64();
        var payload = reader.Bytes();
        reader.RequireEnd("save file");
        if (Fnv(payload) != checksum)
        {
            throw new SaveException(SaveErrorCode.Corrupt, "payload checksum mismatch: the save is damaged or was edited");
        }

        if (tick < 0)
        {
            throw new SaveException(SaveErrorCode.Invalid, $"tick {tick}");
        }

        return (new SaveInfo(game, tick, format, gameVersion, bytes.Length, hasScenes), payload);
    }

    private static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 0x100000001b3UL;
        }

        return hash;
    }
}
