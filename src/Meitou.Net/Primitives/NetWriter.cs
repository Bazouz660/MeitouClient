using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Meitou.Net;

/// <summary>
/// The byte-aligned packet builder every codec writes with (docs/net.md, "Primitives"): little-endian fixed-width values, LEB128 varints,
/// zigzag signed varints, raw float bits and length-prefixed UTF-8. It rents its buffer from the array pool and is reusable
/// (<see cref="Reset"/>); <see cref="Dispose"/> returns the buffer. One writer is used by one thread.
/// </summary>
public sealed class NetWriter : IDisposable
{
    /// <summary>Largest packet a writer builds; a runaway loop fails here rather than exhausting memory.</summary>
    const int MaxCapacity = 1 << 24;

    static readonly UTF8Encoding Utf8 = new(false, false);

    byte[]? buffer;
    int length;

    public NetWriter(int capacity = 256) => buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(capacity, 16, MaxCapacity));

    /// <summary>Bytes written so far.</summary>
    public int Length => length;

    /// <summary>The bytes written so far; valid until the next write, <see cref="Reset"/> or <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Written => new(Storage, 0, length);

    byte[] Storage => buffer ?? throw new ObjectDisposedException(nameof(NetWriter));

    /// <summary>Forgets everything written, keeping the buffer.</summary>
    public void Reset() => length = 0;

    /// <summary>Drops everything after the first <paramref name="newLength"/> bytes (a rollback point from <see cref="Length"/>).</summary>
    public void Truncate(int newLength)
    {
        if ((uint)newLength > (uint)length) throw new ArgumentOutOfRangeException(nameof(newLength), newLength, "Truncate can only shorten the written bytes.");
        length = newLength;
    }

    public void Dispose()
    {
        if (buffer is { } b) ArrayPool<byte>.Shared.Return(b);
        buffer = null;
        length = 0;
    }

    Span<byte> Reserve(int count)
    {
        var b = Storage;
        if (length + count > b.Length)
        {
            long needed = (long)length + count;
            if (needed > MaxCapacity) throw new InvalidOperationException($"A packet would exceed {MaxCapacity} bytes.");
            var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(MaxCapacity, Math.Max(needed, b.Length * 2L)));
            System.Buffer.BlockCopy(b, 0, bigger, 0, length);
            ArrayPool<byte>.Shared.Return(b);
            buffer = b = bigger;
        }
        var span = b.AsSpan(length, count);
        length += count;
        return span;
    }

    public void WriteByte(byte v) => Reserve(1)[0] = v;

    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    /// <summary>LEB128: seven bits per byte, low group first, the high bit set while more follow (1 to 10 bytes).</summary>
    public void WriteVarUInt(ulong v)
    {
        while (v >= 0x80)
        {
            WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        WriteByte((byte)v);
    }

    /// <summary>Zigzag, so small magnitudes of either sign stay short: 0, -1, 1, -2, ... map to 0, 1, 2, 3, ...</summary>
    public void WriteVarInt(long v) => WriteVarUInt((ulong)((v << 1) ^ (v >> 63)));

    public void WriteUInt16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), v);

    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);

    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);

    /// <summary>The float's raw bits, so a NaN payload and -0 survive.</summary>
    public void WriteSingle(float v) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), v);

    public void WriteDouble(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), v);

    public void WriteHalf(Half v) => BinaryPrimitives.WriteHalfLittleEndian(Reserve(2), v);

    /// <summary>A varint byte count, then the UTF-8 bytes. A lone surrogate is written as U+FFFD.</summary>
    public void WriteUtf8(string v)
    {
        ArgumentNullException.ThrowIfNull(v);
        int bytes = Utf8.GetByteCount(v);
        WriteVarUInt((uint)bytes);
        Utf8.GetBytes(v, Reserve(bytes));
    }

    /// <summary>The bytes as they are, without a length: the caller writes the count.</summary>
    public void WriteBytes(ReadOnlySpan<byte> v) => v.CopyTo(Reserve(v.Length));

    /// <summary>The 16 bytes of <see cref="Guid.TryWriteBytes(Span{byte})"/> (the RFC layout of the standard string form).</summary>
    public void WriteGuid(Guid v) => v.TryWriteBytes(Reserve(16), true, out _);

    /// <summary>
    /// An enum by its underlying type: one raw byte for <see cref="byte"/> and <see cref="sbyte"/>, a varint for <see cref="ushort"/>,
    /// <see cref="uint"/> and <see cref="ulong"/>, a zigzag varint for the signed wider types; <see cref="NetReader.ReadEnum{T}"/> reads it back.
    /// </summary>
    public void WriteEnum<T>(T v) where T : struct, Enum
    {
        ulong bits = NetEnum<T>.ToBits(v);
        switch (NetEnum<T>.Wire)
        {
            case EnumWire.Byte or EnumWire.SByte: WriteByte((byte)bits); break;
            case EnumWire.Unsigned: WriteVarUInt(bits); break;
            default: WriteVarInt((long)bits); break;
        }
    }
}
