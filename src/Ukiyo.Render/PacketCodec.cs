using System.Buffers.Binary;
using System.Numerics;

namespace Ukiyo.Rendering;

/// <summary>
/// Binary wire format for resource batches and frame packets (protocol v1, little-endian, 4-byte aligned).
/// Used across the C#↔JS bridge; the JS decoder lives in web/three-adapter/protocol.js and must match
/// docs/protocol.md byte for byte. 64-bit ticks are written as two uint32 (lo, hi) so JS reads them exactly.
/// </summary>
public static class PacketCodec
{
    public const uint ResourceMagic = 0x42524B55; // "UKRB"
    public const uint FrameMagic = 0x50524B55;    // "UKRP"
    public const ushort Version = 1;
    public const int HeaderSize = 16;
    public const int InstanceSize = 96;           // 2 handles (16 bytes each) + 16 floats (64 bytes)
    public const int SpriteSize = 84;             // handle (16) + u32 space + i32 layer + 15 floats (60)
    public const int Camera2DSize = 16;           // center xy, view height, pad

    /// <summary>Frame flag: a 2D section (camera + sprites) follows the instances. Absent, the bytes are exactly v1 G0.</summary>
    public const ushort FlagSprites = 0x1;

    public static byte[] Encode(ResourceBatch batch)
    {
        RenderValidation.Validate(batch);
        var size = HeaderSize;
        foreach (var command in batch.Commands)
        {
            size += 12 + command.Kind switch
            {
                ResourceCommandKind.CreateMesh => 8 + command.Mesh!.Vertices.Length * VertexPositionColor.SizeInBytes + command.Mesh.Indices.Length * 4,
                ResourceCommandKind.CreateMaterial => 20,
                ResourceCommandKind.CreateTexture => 12 + command.Texture!.Rgba.Length,
                _ => 0,
            };
        }

        var writer = new Writer(size);
        writer.U32(ResourceMagic);
        writer.U16(Version);
        writer.U16(0);
        writer.U32((uint)batch.Commands.Count);
        writer.U32((uint)size);
        foreach (var command in batch.Commands)
        {
            writer.U8((byte)command.Kind);
            writer.U8((byte)command.Handle.Kind);
            writer.U16(0);
            writer.U32(command.Handle.Index);
            writer.U32(command.Handle.Generation);
            if (command.Kind == ResourceCommandKind.CreateMesh)
            {
                var mesh = command.Mesh!;
                writer.U32((uint)mesh.Vertices.Length);
                writer.U32((uint)mesh.Indices.Length);
                foreach (var vertex in mesh.Vertices)
                {
                    writer.Vec3(vertex.Position);
                    writer.Vec3(vertex.Color);
                }

                foreach (var index in mesh.Indices)
                {
                    writer.U32(index);
                }
            }
            else if (command.Kind == ResourceCommandKind.CreateMaterial)
            {
                writer.Vec4(command.Material!.BaseColor);
                writer.U32(command.Material.UseVertexColors ? 1u : 0u);
            }
            else if (command.Kind == ResourceCommandKind.CreateTexture)
            {
                var texture = command.Texture!;
                writer.U32((uint)texture.Width);
                writer.U32((uint)texture.Height);
                writer.U32((uint)texture.Filter);
                writer.Bytes(texture.Rgba);
            }
        }

        return writer.Finish();
    }

    public static byte[] Encode(RenderPacket packet)
    {
        RenderValidation.Validate(packet);
        var hasSprites = packet.Sprites.Count > 0;
        var size = HeaderSize + 16 + 16 + 40 + 4 + packet.Instances.Count * InstanceSize;
        if (hasSprites)
        {
            size += Camera2DSize + 4 + packet.Sprites.Count * SpriteSize;
        }

        var writer = new Writer(size);
        writer.U32(FrameMagic);
        writer.U16(Version);
        writer.U16(hasSprites ? FlagSprites : (ushort)0);
        writer.U32(packet.Sequence);
        writer.U32((uint)size);
        writer.U32((uint)(packet.Tick & 0xFFFFFFFF));
        writer.U32((uint)((ulong)packet.Tick >> 32));
        writer.U32((uint)packet.Viewport.Width);
        writer.U32((uint)packet.Viewport.Height);
        writer.Vec4(packet.ClearColor);
        writer.Vec3(packet.Camera.Position);
        writer.Quat(packet.Camera.Rotation);
        writer.F32(packet.Camera.FieldOfViewY);
        writer.F32(packet.Camera.NearPlane);
        writer.F32(packet.Camera.FarPlane);
        writer.U32((uint)packet.Instances.Count);
        foreach (var instance in packet.Instances)
        {
            writer.Handle(instance.Mesh);
            writer.Handle(instance.Material);
            writer.Matrix(instance.World);
        }

        if (hasSprites)
        {
            writer.F32(packet.Camera2D.Center.X);
            writer.F32(packet.Camera2D.Center.Y);
            writer.F32(packet.Camera2D.ViewHeight);
            writer.F32(0);
            writer.U32((uint)packet.Sprites.Count);
            foreach (var sprite in packet.Sprites)
            {
                writer.Handle(sprite.Texture);
                writer.U32((uint)sprite.Space);
                writer.U32(unchecked((uint)sprite.Layer));
                writer.F32(sprite.Position.X);
                writer.F32(sprite.Position.Y);
                writer.F32(sprite.Size.X);
                writer.F32(sprite.Size.Y);
                writer.F32(sprite.Pivot.X);
                writer.F32(sprite.Pivot.Y);
                writer.F32(sprite.Rotation);
                writer.Vec4(sprite.Uv);
                writer.Vec4(sprite.Color);
            }
        }

        return writer.Finish();
    }

    public static ResourceBatch DecodeResources(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        reader.Header(ResourceMagic, allowedFlags: 0);
        var count = reader.U32();
        reader.ExpectLength();
        var commands = new List<ResourceCommand>((int)Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            var kind = (ResourceCommandKind)reader.U8();
            var resourceKind = (ResourceKind)reader.U8();
            reader.U16();
            var handle = new ResourceHandle(resourceKind, reader.U32(), reader.U32());
            switch (kind)
            {
                case ResourceCommandKind.CreateMesh:
                    var vertexCount = reader.Count(RenderValidation.MaxVertices, "vertex");
                    var indexCount = reader.Count(RenderValidation.MaxIndices, "index");
                    var vertices = new VertexPositionColor[vertexCount];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        vertices[v] = new VertexPositionColor(reader.Vec3(), reader.Vec3());
                    }

                    var indices = new uint[indexCount];
                    for (var n = 0; n < indexCount; n++)
                    {
                        indices[n] = reader.U32();
                    }

                    commands.Add(ResourceCommand.CreateMesh(handle, new MeshData(vertices, indices)));
                    break;
                case ResourceCommandKind.CreateMaterial:
                    commands.Add(ResourceCommand.CreateMaterial(handle, new MaterialData(reader.Vec4(), reader.U32() != 0)));
                    break;
                case ResourceCommandKind.CreateTexture:
                    var width = reader.Count(RenderValidation.MaxTextureSize, "texture width");
                    var height = reader.Count(RenderValidation.MaxTextureSize, "texture height");
                    var filter = (TextureFilter)reader.U32();
                    commands.Add(ResourceCommand.CreateTexture(handle, new TextureData(width, height, reader.Bytes(width * height * 4), filter)));
                    break;
                case ResourceCommandKind.Destroy:
                    commands.Add(ResourceCommand.Destroy(handle));
                    break;
                default:
                    throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown resource command {(byte)kind}");
            }
        }

        reader.RequireEnd();
        var batch = new ResourceBatch(commands);
        RenderValidation.Validate(batch);
        return batch;
    }

    public static RenderPacket DecodeFrame(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        var flags = reader.Header(FrameMagic, allowedFlags: FlagSprites);
        var sequence = reader.U32();
        reader.ExpectLength();
        var tick = (long)(reader.U32() | ((ulong)reader.U32() << 32));
        var viewport = new RenderExtent((int)reader.U32(), (int)reader.U32(), 1f);
        var clear = reader.Vec4();
        var camera = new CameraState(reader.Vec3(), reader.Quat(), reader.F32(), reader.F32(), reader.F32());
        var count = reader.Count(RenderValidation.MaxInstances, "instance");
        var instances = new RenderInstance[count];
        for (var i = 0; i < count; i++)
        {
            instances[i] = new RenderInstance(reader.Handle(), reader.Handle(), reader.Matrix());
        }

        var camera2D = Camera2D.Default;
        SpriteInstance[] sprites = [];
        if ((flags & FlagSprites) != 0)
        {
            camera2D = new Camera2D(new Vector2(reader.F32(), reader.F32()), reader.F32());
            reader.F32();
            var spriteCount = reader.Count(RenderValidation.MaxSprites, "sprite");
            sprites = new SpriteInstance[spriteCount];
            for (var i = 0; i < spriteCount; i++)
            {
                var texture = reader.Handle();
                var space = (SpriteSpace)reader.U32();
                var layer = unchecked((int)reader.U32());
                var position = new Vector2(reader.F32(), reader.F32());
                var size = new Vector2(reader.F32(), reader.F32());
                var pivot = new Vector2(reader.F32(), reader.F32());
                var rotation = reader.F32();
                sprites[i] = new SpriteInstance(texture, space, position, size, pivot, rotation, reader.Vec4(), reader.Vec4(), layer);
            }
        }

        reader.RequireEnd();
        var packet = new RenderPacket(sequence, tick, viewport, clear, camera, instances) { Camera2D = camera2D, Sprites = sprites };
        RenderValidation.Validate(packet);
        return packet;
    }

    private sealed class Writer(int size)
    {
        private readonly byte[] _buffer = new byte[size];
        private int _offset;

        public void U8(byte value) => _buffer[_offset++] = value;
        public void U16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_offset), value); _offset += 2; }
        public void U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_offset), value); _offset += 4; }
        public void F32(float value) { BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_offset), value); _offset += 4; }
        public void Vec3(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
        public void Vec4(Vector4 v) { F32(v.X); F32(v.Y); F32(v.Z); F32(v.W); }
        public void Bytes(ReadOnlySpan<byte> data) { data.CopyTo(_buffer.AsSpan(_offset)); _offset += data.Length; }
        public void Quat(Quaternion q) { F32(q.X); F32(q.Y); F32(q.Z); F32(q.W); }
        public void Handle(ResourceHandle handle) { U32((uint)handle.Kind); U32(handle.Index); U32(handle.Generation); U32(0); }

        public void Matrix(Matrix4x4 m)
        {
            F32(m.M11); F32(m.M12); F32(m.M13); F32(m.M14);
            F32(m.M21); F32(m.M22); F32(m.M23); F32(m.M24);
            F32(m.M31); F32(m.M32); F32(m.M33); F32(m.M34);
            F32(m.M41); F32(m.M42); F32(m.M43); F32(m.M44);
        }

        public byte[] Finish()
        {
            if (_offset != _buffer.Length)
            {
                throw new InvalidOperationException($"[CODEC]: wrote {_offset} of {_buffer.Length} bytes");
            }

            return _buffer;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        /// <summary>Checks magic and version; returns the flags, rejecting any bit this decoder does not understand.</summary>
        public ushort Header(uint magic, ushort allowedFlags)
        {
            if (_data.Length < HeaderSize)
            {
                throw new RenderException(RenderErrorCode.TruncatedPayload, $"{_data.Length} bytes is shorter than the {HeaderSize}-byte header");
            }

            if (U32() != magic)
            {
                throw new RenderException(RenderErrorCode.InvalidPacket, "bad magic");
            }

            var version = U16();
            if (version != Version)
            {
                throw new RenderException(RenderErrorCode.UnsupportedVersion, $"protocol v{version}, expected v{Version}");
            }

            var flags = U16();
            if ((flags & ~allowedFlags) != 0)
            {
                throw new RenderException(RenderErrorCode.InvalidPacket, $"unknown flags 0x{flags:X4}");
            }

            return flags;
        }

        public void ExpectLength()
        {
            var declared = U32();
            if (declared != _data.Length)
            {
                throw new RenderException(RenderErrorCode.TruncatedPayload, $"declared {declared} bytes, received {_data.Length}");
            }
        }

        public int Count(int max, string what)
        {
            var value = U32();
            if (value > max)
            {
                throw new RenderException(RenderErrorCode.OutOfRange, $"{what} count {value} exceeds {max}");
            }

            return (int)value;
        }

        public byte U8() { Need(1); return _data[_offset++]; }
        public byte[] Bytes(int count) { Need(count); var v = _data.Slice(_offset, count).ToArray(); _offset += count; return v; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]); _offset += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]); _offset += 4; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_offset..]); _offset += 4; return v; }
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Vector4 Vec4() => new(F32(), F32(), F32(), F32());
        public Quaternion Quat() => new(F32(), F32(), F32(), F32());

        public ResourceHandle Handle()
        {
            var kind = (ResourceKind)U32();
            var handle = new ResourceHandle(kind, U32(), U32());
            U32();
            return handle;
        }

        public Matrix4x4 Matrix() => new(
            F32(), F32(), F32(), F32(),
            F32(), F32(), F32(), F32(),
            F32(), F32(), F32(), F32(),
            F32(), F32(), F32(), F32());

        public void RequireEnd()
        {
            if (_offset != _data.Length)
            {
                throw new RenderException(RenderErrorCode.InvalidPacket, $"{_data.Length - _offset} trailing bytes");
            }
        }

        private void Need(int bytes)
        {
            if (_offset + bytes > _data.Length)
            {
                throw new RenderException(RenderErrorCode.TruncatedPayload, $"need {bytes} bytes at offset {_offset}, have {_data.Length - _offset}");
            }
        }
    }
}
