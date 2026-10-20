using Meitou.Net;

namespace Meitou.Tests.Net;

/// <summary>The wire primitives: varints, raw floats and string tables (docs/net.md, "Primitives" and "String tables").</summary>
public class PrimitivesTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(4294967295UL, 5)]
    [InlineData(4294967296UL, 5)]
    [InlineData(34359738368UL, 6)]
    [InlineData(9223372036854775807UL, 9)]
    [InlineData(18446744073709551615UL, 10)]
    public void Varints_round_trip_at_every_length_edge(ulong value, int length)
    {
        using var w = new NetWriter();
        w.WriteVarUInt(value);
        Assert.Equal(length, w.Length);

        var r = new NetReader(w.Written);
        Assert.Equal(value, r.ReadVarUInt());
        r.ExpectEnd();

        if (value <= uint.MaxValue)
        {
            var r32 = new NetReader(w.Written);
            Assert.Equal((uint)value, r32.ReadVarUInt32());
            r32.ExpectEnd();
        }
    }

    [Fact]
    public void Varints_are_little_endian_groups_of_seven_bits()
    {
        using var w = new NetWriter();
        w.WriteVarUInt(300);
        w.WriteVarUInt(128);
        Assert.Equal(new byte[] { 0xAC, 0x02, 0x80, 0x01 }, w.Written.ToArray());
    }

    public static TheoryData<long, int> SignedEdges => new()
    {
        { 0, 1 },
        { -1, 1 },
        { 1, 1 },
        { 63, 1 },
        { -64, 1 },
        { 64, 2 },
        { -65, 2 },
        { int.MaxValue, 5 },
        { int.MinValue, 5 },
        { (long)int.MaxValue + 1, 5 },
        { long.MaxValue, 10 },
        { long.MinValue, 10 },
    };

    [Theory]
    [MemberData(nameof(SignedEdges))]
    public void Zigzag_varints_round_trip_at_every_length_edge(long value, int length)
    {
        using var w = new NetWriter();
        w.WriteVarInt(value);
        Assert.Equal(length, w.Length);

        var r = new NetReader(w.Written);
        Assert.Equal(value, r.ReadVarInt());
        r.ExpectEnd();

        if (value is >= int.MinValue and <= int.MaxValue)
        {
            var r32 = new NetReader(w.Written);
            Assert.Equal((int)value, r32.ReadVarInt32());
            r32.ExpectEnd();
        }
    }

    [Fact]
    public void Zigzag_orders_small_magnitudes_first()
    {
        using var w = new NetWriter();
        foreach (long v in new long[] { 0, -1, 1, -2, 2 }) w.WriteVarInt(v);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, w.Written.ToArray());
    }

    [Fact]
    public void Floats_keep_their_raw_bits_including_nan_payloads_and_negative_zero()
    {
        uint[] singles = [0x7FC12345, 0xFFC00001, 0x80000000, 0x00000000, 0x7F800000, 0xFF800000, 0x00000001, 0x7F7FFFFF];
        ulong[] doubles = [0x7FF8000000001234, 0xFFF0000000000001, 0x8000000000000000, 0x0000000000000000, 0x7FF0000000000000, 0x0000000000000001];
        ushort[] halves = [0x7E01, 0xFE00, 0x8000, 0x0000, 0x7C00, 0xFC00, 0x0001];

        using var w = new NetWriter();
        foreach (uint bits in singles) w.WriteSingle(BitConverter.UInt32BitsToSingle(bits));
        foreach (ulong bits in doubles) w.WriteDouble(BitConverter.UInt64BitsToDouble(bits));
        foreach (ushort bits in halves) w.WriteHalf(BitConverter.UInt16BitsToHalf(bits));
        Assert.Equal(singles.Length * 4 + doubles.Length * 8 + halves.Length * 2, w.Length);

        var r = new NetReader(w.Written);
        foreach (uint bits in singles) Assert.Equal(bits, BitConverter.SingleToUInt32Bits(r.ReadSingle()));
        foreach (ulong bits in doubles) Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(r.ReadDouble()));
        foreach (ushort bits in halves) Assert.Equal(bits, BitConverter.HalfToUInt16Bits(r.ReadHalf()));
        r.ExpectEnd();
    }

    [Fact]
    public void Finite_reads_return_negative_zero_and_extremes_unchanged()
    {
        using var w = new NetWriter();
        w.WriteSingle(-0f);
        w.WriteSingle(float.MaxValue);
        w.WriteSingle(float.Epsilon);

        var r = new NetReader(w.Written);
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(r.ReadFinite()));
        Assert.Equal(float.MaxValue, r.ReadFinite());
        Assert.Equal(float.Epsilon, r.ReadFinite());
        r.ExpectEnd();
    }

    [Fact]
    public void String_table_defines_then_references_and_writes_literals_and_nulls()
    {
        var table = new StringTableWriter(confirmOnSend: true);
        using var w = new NetWriter();
        table.Write(w, "a");
        table.Write(w, "a");
        table.Write(w, null);
        table.Write(w, "b");
        table.Write(w, "lit", intern: false);
        table.Write(w, "lit", intern: false);
        table.Write(w, "b");
        table.Write(w, "ü");

        // define 0, reference 0, null, define 1, literal, literal, reference 1, define 2
        byte[] expected =
        [
            0x03, 0x01, (byte)'a', 0x02, 0x00, 0x05, 0x01, (byte)'b',
            0x01, 0x03, (byte)'l', (byte)'i', (byte)'t', 0x01, 0x03, (byte)'l', (byte)'i', (byte)'t',
            0x04, 0x07, 0x02, 0xC3, 0xBC,
        ];
        Assert.Equal(expected, w.Written.ToArray());
        Assert.Equal(3, table.Count);

        var reader = new StringTableReader();
        var r = new NetReader(w.Written);
        var read = new string?[8];
        for (int i = 0; i < read.Length; i++) read[i] = reader.Read(ref r, 64);
        string?[] expectedStrings = ["a", "a", null, "b", "lit", "lit", "b", "ü"];
        for (int i = 0; i < read.Length; i++) Assert.Equal(expectedStrings[i], read[i]);
        r.ExpectEnd();
    }

    [Fact]
    public void String_table_sends_literals_once_it_is_full()
    {
        var table = new StringTableWriter(confirmOnSend: true);
        using var w = new NetWriter();
        for (int i = 0; i < NetLimits.MaxStringTable; i++) table.Write(w, "s" + i);
        Assert.Equal(NetLimits.MaxStringTable, table.Count);

        int before = w.Length;
        table.Write(w, "overflow");
        table.Write(w, "s0");
        Assert.Equal(NetLimits.MaxStringTable, table.Count);
        Assert.Equal(1, w.Written[before]);

        var reader = new StringTableReader();
        var r = new NetReader(w.Written);
        for (int i = 0; i < NetLimits.MaxStringTable; i++) Assert.Equal("s" + i, reader.Read(ref r, 64));
        Assert.Equal("overflow", reader.Read(ref r, 64));
        Assert.Equal("s0", reader.Read(ref r, 64));
        r.ExpectEnd();
    }

    [Fact]
    public void Rollback_withdraws_definitions_and_frees_the_indices()
    {
        var table = new StringTableWriter(confirmOnSend: true);
        using var w = new NetWriter();
        table.BeginPart();
        table.Write(w, "keep");

        int length = w.Length, mark = table.Mark();
        table.Write(w, "dropped");
        table.Write(w, "dropped");
        table.Write(w, "keep");
        Assert.Equal(2, table.Count);
        table.Rollback(mark);
        w.Truncate(length);
        Assert.Equal(1, table.Count);
        Assert.Equal([0], table.PartDefinitions);

        // The freed index is given out again, and a reader that never saw the dropped text agrees.
        table.Write(w, "later");
        table.Write(w, "dropped");
        Assert.Equal(3, table.Count);

        var reader = new StringTableReader();
        var r = new NetReader(w.Written);
        Assert.Equal("keep", reader.Read(ref r, 64));
        Assert.Equal("later", reader.Read(ref r, 64));
        Assert.Equal("dropped", reader.Read(ref r, 64));
        r.ExpectEnd();
    }

    [Fact]
    public void Unconfirmed_strings_are_defined_again_in_every_part_until_confirmed()
    {
        var table = new StringTableWriter(confirmOnSend: false);

        using var part1 = new NetWriter();
        table.BeginPart();
        table.Write(part1, "x");
        table.Write(part1, "x");
        var definitions1 = table.PartDefinitions.ToArray();
        Assert.Equal([0], definitions1);

        using var part2 = new NetWriter();
        table.BeginPart();
        table.Write(part2, "x");
        table.Write(part2, "y");
        Assert.Equal([0, 1], table.PartDefinitions);

        // Part 1 is acked; part 2 is still in flight, so "y" stays unconfirmed.
        table.Confirm(definitions1);
        using var part3 = new NetWriter();
        table.BeginPart();
        table.Write(part3, "x");
        table.Write(part3, "y");
        Assert.Equal([1], table.PartDefinitions);

        // Redefinitions are idempotent, so parts arriving in any combination decode.
        var reader = new StringTableReader();
        var r2 = new NetReader(part2.Written);
        Assert.Equal("x", reader.Read(ref r2, 64));
        Assert.Equal("y", reader.Read(ref r2, 64));
        var r1 = new NetReader(part1.Written);
        Assert.Equal("x", reader.Read(ref r1, 64));
        Assert.Equal("x", reader.Read(ref r1, 64));
        var r3b = new NetReader(part3.Written);
        Assert.Equal("x", reader.Read(ref r3b, 64));
        Assert.Equal("y", reader.Read(ref r3b, 64));
    }

    [Fact]
    public void Rollback_of_a_redefinition_keeps_the_string_unconfirmed()
    {
        var table = new StringTableWriter(confirmOnSend: false);
        using var w = new NetWriter();
        table.BeginPart();
        table.Write(w, "x");
        table.BeginPart();

        int length = w.Length, mark = table.Mark();
        table.Write(w, "x");
        Assert.Equal([0], table.PartDefinitions);
        table.Rollback(mark);
        w.Truncate(length);
        Assert.Empty(table.PartDefinitions);
        Assert.Equal(1, table.Count);

        table.Write(w, "x");
        Assert.Equal([0], table.PartDefinitions);
        Assert.Equal(0x03, w.Written[length]);
    }

    public enum Small : byte { A = 0, B = 1 }

    public enum Wide { Low = -1, High = 5 }

    [Flags]
    public enum Bits : byte { One = 1, Four = 4 }

    [Fact]
    public void A_truncated_varint_is_refused()
    {
        byte[] open = [0x80, 0x80];
        Assert.Throws<NetFormatException>(() => new NetReader(open).ReadVarUInt());
        Assert.Throws<NetFormatException>(() => new NetReader(open).ReadVarUInt32());
        Assert.Throws<NetFormatException>(() => new NetReader(open).ReadVarInt());
        Assert.Throws<NetFormatException>(() => new NetReader([]).ReadVarUInt());
    }

    [Fact]
    public void An_over_long_varint_is_refused()
    {
        // Six bytes cannot be 32 bits; the fifth byte of a 32-bit varint holds four bits only.
        byte[] six = [0x80, 0x80, 0x80, 0x80, 0x80, 0x00];
        Assert.Throws<NetFormatException>(() => new NetReader(six).ReadVarUInt32());
        byte[] fifth = [0xFF, 0xFF, 0xFF, 0xFF, 0x1F];
        Assert.Throws<NetFormatException>(() => new NetReader(fifth).ReadVarUInt32());
        Assert.Throws<NetFormatException>(() => new NetReader(fifth).ReadVarInt32());
        // Eleven bytes cannot be 64 bits; the tenth holds one bit only.
        byte[] eleven = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00];
        Assert.Throws<NetFormatException>(() => new NetReader(eleven).ReadVarUInt());
        byte[] tenth = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02];
        Assert.Throws<NetFormatException>(() => new NetReader(tenth).ReadVarUInt());
        Assert.Throws<NetFormatException>(() => new NetReader(tenth).ReadVarInt());
    }

    [Fact]
    public void A_count_over_its_maximum_is_refused_and_one_at_it_is_read()
    {
        using var w = new NetWriter();
        w.WriteVarUInt(5);
        var bytes = w.Written.ToArray();
        Assert.Throws<NetFormatException>(() => new NetReader(bytes).ReadCount(4));
        var r = new NetReader(bytes);
        Assert.Equal(5, r.ReadCount(5));
        r.ExpectEnd();
    }

    [Fact]
    public void A_string_over_its_byte_limit_or_past_the_packet_is_refused()
    {
        using var w = new NetWriter();
        w.WriteUtf8("hello");
        var bytes = w.Written.ToArray();
        Assert.Throws<NetFormatException>(() => new NetReader(bytes).ReadUtf8(4));
        var r = new NetReader(bytes);
        Assert.Equal("hello", r.ReadUtf8(5));
        // The declared length runs past the end of the packet.
        Assert.Throws<NetFormatException>(() => new NetReader(bytes[..^1]).ReadUtf8(5));
        // The limit counts bytes, not characters.
        using var wide = new NetWriter();
        wide.WriteUtf8("üü");
        var wideBytes = wide.Written.ToArray();
        Assert.Throws<NetFormatException>(() => new NetReader(wideBytes).ReadUtf8(3));
    }

    [Fact]
    public void A_string_that_is_not_valid_utf8_is_refused()
    {
        Assert.Throws<NetFormatException>(() => new NetReader([1, 0xFF]).ReadUtf8(8));
        Assert.Throws<NetFormatException>(() => new NetReader([2, 0xC3, 0x28]).ReadUtf8(8));
        // A lone surrogate encoded as UTF-8 (ED A0 80) is invalid too.
        Assert.Throws<NetFormatException>(() => new NetReader([3, 0xED, 0xA0, 0x80]).ReadUtf8(8));
    }

    [Fact]
    public void An_enum_value_that_is_not_defined_is_refused()
    {
        Assert.Throws<NetFormatException>(() => new NetReader([2]).ReadEnum<Small>());
        using var w = new NetWriter();
        w.WriteEnum(Wide.Low);
        w.WriteEnum(Wide.High);
        w.WriteVarInt(3);
        var bytes = w.Written.ToArray();
        var r = new NetReader(bytes);
        Assert.Equal(Wide.Low, r.ReadEnum<Wide>());
        Assert.Equal(Wide.High, r.ReadEnum<Wide>());
        Assert.Throws<NetFormatException>(() => new NetReader(bytes[^1..]).ReadEnum<Wide>());
        // A flags enum takes any combination of its bits and refuses a bit no member uses.
        var flags = new NetReader([5]);
        Assert.Equal(Bits.One | Bits.Four, flags.ReadEnum<Bits>());
        Assert.Throws<NetFormatException>(() => new NetReader([2]).ReadEnum<Bits>());
    }

    [Fact]
    public void Trailing_bytes_after_a_message_are_refused()
    {
        byte[] bytes = [1, 2];
        Assert.Throws<NetFormatException>(() => new NetReader(bytes).ExpectEnd());
        Assert.Throws<NetFormatException>(() => new NetReader(bytes[1..]).ExpectEnd());
        var r = new NetReader(bytes);
        r.ReadByte();
        r.ReadByte();
        r.ExpectEnd();
    }

    [Fact]
    public void A_finite_read_refuses_nan_and_infinity_which_a_plain_read_returns()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            using var w = new NetWriter();
            w.WriteSingle(bad);
            var bytes = w.Written.ToArray();
            Assert.Throws<NetFormatException>(() => new NetReader(bytes).ReadFinite());
            Assert.Equal(BitConverter.SingleToUInt32Bits(bad), BitConverter.SingleToUInt32Bits(new NetReader(bytes).ReadSingle()));
        }
    }
}
