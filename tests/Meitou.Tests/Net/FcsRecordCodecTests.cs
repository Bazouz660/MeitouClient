using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Net;

namespace Meitou.Tests.Net;

/// <summary><see cref="FcsRecordCodec"/>: a record with every section filled survives the wire (docs/net.md, "FCS records").</summary>
public class FcsRecordCodecTests
{
    static FcsRecord SampleRecord(string suffix)
    {
        var record = new FcsRecord
        {
            ByteSize = 4321,
            Type = 41,
            Id = -5,
            Name = "Üñïcode 名前 " + suffix,
            StringId = "1292-gamedata.base",
            Flags = 0xFFFFFFF3,
        };
        for (int i = 0; i < 11; i++) record.Bools["flag" + i] = i % 3 != 1;
        record.Bools[FcsRecord.RemovedKey] = true;
        record.Floats["scale"] = -0f;
        record.Floats["height"] = 1.8125f;
        record.Floats["big"] = float.MaxValue;
        record.Ints["negative"] = -1_000_000;
        record.Ints["max"] = int.MaxValue;
        record.Ints["min"] = int.MinValue;
        record.Vector3s["offset"] = new Vector3(1.5f, -2.25f, 3e6f);
        record.Vector3s["zero"] = Vector3.Zero;
        record.Vector4s["colour"] = new Vector4(0.1f, 0.2f, 0.3f, 1f);
        record.Strings["description"] = "short";
        record.Strings["empty"] = "";
        record.Strings["long"] = new string('x', 300) + suffix;
        record.Strings["twin"] = "short";
        record.Filenames["mesh"] = "body/male01.mesh";
        record.Filenames["texture"] = "body/male01.dds";
        record.References["conditions"] = [new("2-test.mod", 1, 0, -7), new("3-test.mod", 0, 0, 0)];
        record.References["removed"] = [FcsReference.Removed("4-test.mod")];
        record.References["none"] = [];

        var placed = new FcsInstance { Id = "i1", Target = "5-test.mod", Position = new Vector3(10, 20, 30), Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f) };
        placed.States.AddRange(["open", "locked"]);
        record.Instances.Add(placed);
        record.Instances.Add(new FcsInstance { Id = "i2", Target = "", Position = new Vector3(-1, 0, 1) });
        return record;
    }

    static void AssertSame<T>(OrderedDictionary<string, T> expected, OrderedDictionary<string, T> actual) =>
        Assert.Equal(expected.ToArray(), actual.ToArray());

    static void AssertSame(FcsRecord expected, FcsRecord actual)
    {
        Assert.Equal(expected.ByteSize, actual.ByteSize);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.StringId, actual.StringId);
        Assert.Equal(expected.Flags, actual.Flags);
        AssertSame(expected.Bools, actual.Bools);
        Assert.Equal(expected.Floats.Select(p => (p.Key, BitConverter.SingleToUInt32Bits(p.Value))), actual.Floats.Select(p => (p.Key, BitConverter.SingleToUInt32Bits(p.Value))));
        AssertSame(expected.Ints, actual.Ints);
        AssertSame(expected.Vector3s, actual.Vector3s);
        AssertSame(expected.Vector4s, actual.Vector4s);
        AssertSame(expected.Strings, actual.Strings);
        AssertSame(expected.Filenames, actual.Filenames);
        Assert.Equal(expected.References.Keys, actual.References.Keys);
        foreach (var (category, references) in expected.References) Assert.Equal(references, actual.References[category]);
        Assert.Equal(expected.Instances.Count, actual.Instances.Count);
        for (int i = 0; i < expected.Instances.Count; i++)
        {
            Assert.Equal(expected.Instances[i].Id, actual.Instances[i].Id);
            Assert.Equal(expected.Instances[i].Target, actual.Instances[i].Target);
            Assert.Equal(expected.Instances[i].Position, actual.Instances[i].Position);
            Assert.Equal(expected.Instances[i].Rotation, actual.Instances[i].Rotation);
            Assert.Equal(expected.Instances[i].States, actual.Instances[i].States);
        }
    }

    [Fact]
    public void A_full_record_round_trips_and_the_second_copy_uses_string_references()
    {
        var first = SampleRecord("a");
        var second = SampleRecord("b");
        var table = new StringTableWriter(confirmOnSend: true);
        using var w = new NetWriter();

        FcsRecordCodec.Write(w, table, first);
        int firstLength = w.Length;
        FcsRecordCodec.Write(w, table, second);
        int secondLength = w.Length - firstLength;
        Assert.True(secondLength < firstLength, $"The second record ({secondLength} B) should be smaller than the first ({firstLength} B).");

        var strings = new StringTableReader();
        var r = new NetReader(w.Written);
        AssertSame(first, FcsRecordCodec.Read(ref r, strings));
        AssertSame(second, FcsRecordCodec.Read(ref r, strings));
        r.ExpectEnd();
    }

    [Fact]
    public void A_record_round_trips_through_a_snapshot_channel_table()
    {
        var record = SampleRecord("c");
        var table = new StringTableWriter(confirmOnSend: false);
        using var w = new NetWriter();
        table.BeginPart();
        FcsRecordCodec.Write(w, table, record);

        var r = new NetReader(w.Written);
        AssertSame(record, FcsRecordCodec.Read(ref r, new StringTableReader()));
        r.ExpectEnd();
    }

    [Fact]
    public void An_empty_record_round_trips()
    {
        var record = new FcsRecord();
        using var w = new NetWriter();
        FcsRecordCodec.Write(w, new StringTableWriter(confirmOnSend: true), record);

        var r = new NetReader(w.Written);
        AssertSame(record, FcsRecordCodec.Read(ref r, new StringTableReader()));
        r.ExpectEnd();
    }
}
