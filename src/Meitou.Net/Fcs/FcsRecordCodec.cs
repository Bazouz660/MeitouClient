using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Net;

/// <summary>
/// An <see cref="FcsRecord"/> on the wire: the appearance record an <c>EditCharacter</c> carries and the loadouts of looks (docs/net.md, "FCS records").
/// The layout follows the record's own sections: type, id, name, string id, flags, byte size; the bool map (keys, then the values packed 8 to a byte,
/// low bit first); floats, ints, vectors and quaternions; strings and filenames; references by category; instances with their states.
/// Map keys and most values go through the string table, map order is kept, and a reader refuses duplicate keys and over-long maps.
/// </summary>
public static class FcsRecordCodec
{
    /// <summary>Strings and filenames longer than this are written as literals so a long text does not fill the table.</summary>
    const int MaxInternedLength = 128;

    public static void Write(NetWriter w, StringTableWriter strings, FcsRecord record)
    {
        w.WriteVarUInt((uint)record.Type);
        w.WriteVarInt(record.Id);
        strings.Write(w, record.Name, intern: false);
        strings.Write(w, record.StringId);
        w.WriteVarUInt(record.Flags);
        w.WriteVarUInt(record.ByteSize);

        CheckCount(record.Bools.Count, nameof(record.Bools));
        w.WriteVarUInt((uint)record.Bools.Count);
        foreach (var (key, _) in record.Bools) strings.Write(w, key);
        byte packed = 0;
        int bit = 0;
        foreach (var (_, value) in record.Bools)
        {
            if (value) packed |= (byte)(1 << bit);
            if (++bit == 8)
            {
                w.WriteByte(packed);
                packed = 0;
                bit = 0;
            }
        }
        if (bit != 0) w.WriteByte(packed);

        WriteMap(w, strings, record.Floats, nameof(record.Floats), (output, _, v) => output.WriteSingle(v));
        WriteMap(w, strings, record.Ints, nameof(record.Ints), (output, _, v) => output.WriteVarInt(v));
        WriteMap(w, strings, record.Vector3s, nameof(record.Vector3s), (output, _, v) => CommonCodec.WriteVector3(output, v));
        WriteMap(w, strings, record.Vector4s, nameof(record.Vector4s), (output, _, v) => CommonCodec.WriteVector4(output, v));
        WriteMap(w, strings, record.Strings, nameof(record.Strings), WriteText);
        WriteMap(w, strings, record.Filenames, nameof(record.Filenames), WriteText);

        CheckCount(record.References.Count, nameof(record.References));
        w.WriteVarUInt((uint)record.References.Count);
        foreach (var (category, references) in record.References)
        {
            strings.Write(w, category);
            if (references.Count > NetLimits.MaxListItems) throw new ArgumentException($"A reference list of {references.Count} exceeds the limit of {NetLimits.MaxListItems}.", nameof(record));
            w.WriteVarUInt((uint)references.Count);
            foreach (var reference in references)
            {
                strings.Write(w, reference.TargetStringId);
                w.WriteVarInt(reference.Value0);
                w.WriteVarInt(reference.Value1);
                w.WriteVarInt(reference.Value2);
            }
        }

        if (record.Instances.Count > NetLimits.MaxListItems) throw new ArgumentException($"{record.Instances.Count} instances exceed the limit of {NetLimits.MaxListItems}.", nameof(record));
        w.WriteVarUInt((uint)record.Instances.Count);
        foreach (var instance in record.Instances)
        {
            strings.Write(w, instance.Id);
            strings.Write(w, instance.Target);
            CommonCodec.WriteVector3(w, instance.Position);
            CommonCodec.WriteQuaternion(w, instance.Rotation);
            if (instance.States.Count > NetLimits.MaxListItems) throw new ArgumentException($"{instance.States.Count} states exceed the limit of {NetLimits.MaxListItems}.", nameof(record));
            w.WriteVarUInt((uint)instance.States.Count);
            foreach (var state in instance.States) strings.Write(w, state);
        }
    }

    public static FcsRecord Read(ref NetReader r, StringTableReader strings)
    {
        var record = new FcsRecord
        {
            Type = (int)r.ReadVarUInt32(),
            Id = r.ReadVarInt32(),
            Name = Text(strings.Read(ref r, NetLimits.MaxName)),
            StringId = Text(strings.Read(ref r, NetLimits.MaxName)),
            Flags = r.ReadVarUInt32(),
            ByteSize = r.ReadVarUInt32(),
        };

        int boolCount = r.ReadCount(NetLimits.MaxFcsMapEntries);
        var boolKeys = new string[boolCount];
        for (int i = 0; i < boolCount; i++) boolKeys[i] = Text(strings.Read(ref r, NetLimits.MaxName));
        var boolBytes = r.ReadBytes((boolCount + 7) / 8);
        for (int i = 0; i < boolCount; i++)
            Add(record.Bools, boolKeys[i], (boolBytes[i >> 3] >> (i & 7) & 1) != 0);

        ReadMap(ref r, strings, record.Floats, (ref NetReader input, StringTableReader _) => input.ReadFinite());
        ReadMap(ref r, strings, record.Ints, (ref NetReader input, StringTableReader _) => input.ReadVarInt32());
        ReadMap(ref r, strings, record.Vector3s, (ref NetReader input, StringTableReader _) => CommonCodec.ReadVector3(ref input));
        ReadMap(ref r, strings, record.Vector4s, (ref NetReader input, StringTableReader _) => CommonCodec.ReadVector4(ref input));
        ReadMap(ref r, strings, record.Strings, ReadText);
        ReadMap(ref r, strings, record.Filenames, ReadText);

        int categories = r.ReadCount(NetLimits.MaxFcsMapEntries);
        for (int i = 0; i < categories; i++)
        {
            string category = Text(strings.Read(ref r, NetLimits.MaxName));
            int count = r.ReadCount(NetLimits.MaxListItems);
            var references = new List<FcsReference>(count);
            for (int k = 0; k < count; k++)
                references.Add(new FcsReference(Text(strings.Read(ref r, NetLimits.MaxName)), r.ReadVarInt32(), r.ReadVarInt32(), r.ReadVarInt32()));
            Add(record.References, category, references);
        }

        int instances = r.ReadCount(NetLimits.MaxListItems);
        for (int i = 0; i < instances; i++)
        {
            var instance = new FcsInstance
            {
                Id = Text(strings.Read(ref r, NetLimits.MaxName)),
                Target = Text(strings.Read(ref r, NetLimits.MaxName)),
                Position = CommonCodec.ReadVector3(ref r),
                Rotation = CommonCodec.ReadQuaternion(ref r),
            };
            int states = r.ReadCount(NetLimits.MaxListItems);
            for (int k = 0; k < states; k++) instance.States.Add(Text(strings.Read(ref r, NetLimits.MaxName)));
            record.Instances.Add(instance);
        }
        return record;
    }

    delegate void MapWriter<in T>(NetWriter w, StringTableWriter strings, T value);

    delegate T MapReader<out T>(ref NetReader r, StringTableReader strings);

    static void WriteMap<T>(NetWriter w, StringTableWriter strings, OrderedDictionary<string, T> map, string name, MapWriter<T> write)
    {
        CheckCount(map.Count, name);
        w.WriteVarUInt((uint)map.Count);
        foreach (var (key, value) in map)
        {
            strings.Write(w, key);
            write(w, strings, value);
        }
    }

    static void ReadMap<T>(ref NetReader r, StringTableReader strings, OrderedDictionary<string, T> map, MapReader<T> read)
    {
        int count = r.ReadCount(NetLimits.MaxFcsMapEntries);
        for (int i = 0; i < count; i++)
        {
            string key = Text(strings.Read(ref r, NetLimits.MaxName));
            Add(map, key, read(ref r, strings));
        }
    }

    static void WriteText(NetWriter w, StringTableWriter strings, string value) => strings.Write(w, value, value.Length <= MaxInternedLength);

    static string ReadText(ref NetReader r, StringTableReader strings) => Text(strings.Read(ref r, NetLimits.MaxString));

    static void Add<T>(OrderedDictionary<string, T> map, string key, T value)
    {
        if (!map.TryAdd(key, value)) throw new NetFormatException($"Duplicate record key \"{key}\".");
    }

    static string Text(string? value) => value ?? throw new NetFormatException("A record string is null.");

    static void CheckCount(int count, string name)
    {
        if (count > NetLimits.MaxFcsMapEntries) throw new ArgumentException($"{name} has {count} entries; the limit is {NetLimits.MaxFcsMapEntries}.");
    }
}
