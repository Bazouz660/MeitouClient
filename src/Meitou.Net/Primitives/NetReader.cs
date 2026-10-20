using System.Buffers.Binary;
using System.Text;

namespace Meitou.Net;

/// <summary>
/// The packet parser that mirrors <see cref="NetWriter"/> over a span, with no allocation but the strings it returns. It is used on bytes from the
/// network, so every failure throws <see cref="NetFormatException"/> and nothing else (docs/net.md, "Reader rules"): an overrun, an overlong varint
/// (more than 5 bytes for 32 bits, 10 for 64), a bool that is not 0 or 1, invalid UTF-8, a count over its limit, an undefined enum value, a
/// non-finite float where one is required.
/// </summary>
public ref struct NetReader
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    readonly ReadOnlySpan<byte> data;
    int position;

    public NetReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        position = 0;
    }

    /// <summary>Bytes not yet read.</summary>
    public readonly int Remaining => data.Length - position;

    ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new NetFormatException($"Read of {count} bytes overruns the packet ({Remaining} left).");
        var slice = data.Slice(position, count);
        position += count;
        return slice;
    }

    public byte ReadByte()
    {
        if (position >= data.Length) throw new NetFormatException("Read overruns the packet.");
        return data[position++];
    }

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new NetFormatException($"A bool byte is {b}, not 0 or 1."),
    };

    /// <summary>LEB128 of up to 64 bits (10 bytes, the last holding one bit).</summary>
    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = ReadByte();
            if (shift == 63 && b > 1) throw new NetFormatException("A varint is longer than 64 bits.");
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new NetFormatException("A varint is longer than 10 bytes.");
    }

    /// <summary>LEB128 of up to 32 bits (5 bytes, the last holding four bits).</summary>
    public uint ReadVarUInt32()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = ReadByte();
            if (shift == 28 && b > 0x0F) throw new NetFormatException("A varint is longer than 32 bits.");
            result |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new NetFormatException("A varint is longer than 5 bytes.");
    }

    public long ReadVarInt()
    {
        ulong u = ReadVarUInt();
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    /// <summary>A zigzag varint that must fit 32 bits.</summary>
    public int ReadVarInt32()
    {
        uint u = ReadVarUInt32();
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>The float's raw bits: NaN payloads and -0 come back as written.</summary>
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>A float that must be finite: every value a client sends the host goes through this.</summary>
    public float ReadFinite()
    {
        float v = ReadSingle();
        if (!float.IsFinite(v)) throw new NetFormatException("A float is NaN or infinite.");
        return v;
    }

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public Half ReadHalf() => BinaryPrimitives.ReadHalfLittleEndian(Take(2));

    /// <summary>A varint byte count (at most <paramref name="maxBytes"/>), then strictly validated UTF-8.</summary>
    public string ReadUtf8(int maxBytes)
    {
        uint count = ReadVarUInt32();
        if (count > (uint)maxBytes) throw new NetFormatException($"A string of {count} bytes exceeds its limit of {maxBytes}.");
        if (count == 0) return "";
        try
        {
            return StrictUtf8.GetString(Take((int)count));
        }
        catch (ArgumentException)
        {
            throw new NetFormatException("A string is not valid UTF-8.");
        }
    }

    /// <summary>The next <paramref name="count"/> bytes, borrowed from the packet.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    public Guid ReadGuid() => new(Take(16), true);

    /// <summary>A varint element count that must not exceed <paramref name="max"/>.</summary>
    public int ReadCount(int max)
    {
        uint count = ReadVarUInt32();
        if (count > (uint)max) throw new NetFormatException($"A count of {count} exceeds its limit of {max}.");
        return (int)count;
    }

    /// <summary>
    /// Reads an enum written by <see cref="NetWriter.WriteEnum{T}"/> and checks it: a defined member, or for a <see cref="FlagsAttribute"/> enum
    /// bits that some member uses.
    /// </summary>
    public T ReadEnum<T>() where T : struct, Enum
    {
        ulong bits = NetEnum<T>.Wire switch
        {
            EnumWire.Byte => ReadByte(),
            EnumWire.SByte => (ulong)(long)(sbyte)ReadByte(),
            EnumWire.Unsigned => ReadVarUInt(),
            _ => (ulong)ReadVarInt(),
        };
        if (!NetEnum<T>.IsValid(bits)) throw new NetFormatException($"{typeof(T).Name} has no value {(long)bits}.");
        return NetEnum<T>.FromBits(bits);
    }

    /// <summary>Throws when bytes remain: a message is exactly its layout.</summary>
    public readonly void ExpectEnd()
    {
        if (Remaining != 0) throw new NetFormatException($"{Remaining} trailing bytes after the message.");
    }
}
