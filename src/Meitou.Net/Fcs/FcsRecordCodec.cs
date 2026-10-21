using System.Numerics;
using System.Text;
using Meitou.Data.Fcs;

namespace Meitou.Net;

/// <summary>
/// An <see cref="FcsRecord"/> on the wire: the appearance record an <c>EditCharacter</c> carries and the loadouts of looks (docs/net.md, "FCS records").
/// The layout follows the record's own sections: type, id, name, string id, flags, byte size; the bool map (keys, then the values packed 8 to a byte,
/// low bit first); floats, ints, vectors and quaternions; strings and filenames; references by category; instances with their states.
/// Map keys and most values go through the string table, map order is kept, and a reader refuses duplicate keys and over-long maps.
/// The writer enforces the reader's limits (counts, <see cref="NetLimits.MaxName"/> for names, keys and ids, <see cref="NetLimits.MaxString"/> for string
/// values, null strings, non-finite floats, vectors and quaternions) with <see cref="ArgumentException"/>, so a reader never refuses what a writer produced.
/// </summary>
public static class FcsRecordCodec
{
    /// <summary>Strings and filenames longer than this are written as literals so a long text does not fill the table.</summary>
    const int MaxInternedLength = 128;

    public static void Write(NetWriter w, StringTableWriter strings, FcsRecord record)
    {
        w.WriteVarUInt((uint)record.Type);
        w.WriteVarInt(record.Id);
        WriteString(w, strings, record.Name, NetLimits.MaxName, nameof(record.Name), intern: false);
        WriteString(w, strings, record.StringId, NetLimits.MaxName, nameof(record.StringId));
        w.WriteVarUInt(record.Flags);
        w.WriteVarUInt(record.ByteSize);

        CheckCount(record.Bools.Count, nameof(record.Bools));
        w.WriteVarUInt((uint)record.Bools.Count);
        foreach (var (key, _) in record.Bools) WriteString(w, strings, key, NetLimits.MaxName, "a bool key");
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

        WriteMap(w, strings, record.Floats, nameof(record.Floats), (output, _, v) => output.WriteSingle(Finite(v)));
        WriteMap(w, strings, record.Ints, nameof(record.Ints), (output, _, v) => output.WriteVarInt(v));
        WriteMap(w, strings, record.Vector3s, nameof(record.Vector3s), (output, _, v) => CommonCodec.WriteVector3(output, Finite(v)));
        WriteMap(w, strings, record.Vector4s, nameof(record.Vector4s), (output, _, v) => CommonCodec.WriteVector4(output, Finite(v)));
        WriteMap(w, strings, record.Strings, nameof(record.Strings), WriteText);
        WriteMap(w, strings, record.Filenames, nameof(record.Filenames), WriteText);

        CheckCount(record.References.Count, nameof(record.References));
        w.WriteVarUInt((uint)record.References.Count);
        foreach (var (category, references) in record.References)
        {
            WriteString(w, strings, category, NetLimits.MaxName, "a reference category");
            if (references.Count > NetLimits.MaxListItems) throw new ArgumentException($"A reference list of {references.Count} exceeds the limit of {NetLimits.MaxListItems}.", nameof(record));
            w.WriteVarUInt((uint)references.Count);
            foreach (var reference in references)
            {
                WriteString(w, strings, reference.TargetStringId, NetLimits.MaxName, "a reference target");
                w.WriteVarInt(reference.Value0);
                w.WriteVarInt(reference.Value1);
                w.WriteVarInt(reference.Value2);
            }
        }

        if (record.Instances.Count > NetLimits.MaxListItems) throw new ArgumentException($"{record.Instances.Count} instances exceed the limit of {NetLimits.MaxListItems}.", nameof(record));
        w.WriteVarUInt((uint)record.Instances.Count);
        foreach (var instance in record.Instances)
        {
            WriteString(w, strings, instance.Id, NetLimits.MaxName, "an instance id");
            WriteString(w, strings, instance.Target, NetLimits.MaxName, "an instance target");
            CommonCodec.WriteVector3(w, Finite(instance.Position));
            CommonCodec.WriteQuaternion(w, Finite(instance.Rotation));
            if (instance.States.Count > NetLimits.MaxListItems) throw new ArgumentException($"{instance.States.Count} states exceed the limit of {NetLimits.MaxListItems}.", nameof(record));
            w.WriteVarUInt((uint)instance.States.Count);
            foreach (var state in instance.States) WriteString(w, strings, state, NetLimits.MaxName, "an instance state");
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
            WriteString(w, strings, key, NetLimits.MaxName, $"a key of {name}");
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

    static void WriteText(NetWriter w, StringTableWriter strings, string value) =>
        WriteString(w, strings, value, NetLimits.MaxString, "a string value", value?.Length <= MaxInternedLength);

    /// <summary>Writes <paramref name="value"/> through the table when it is non-null and at most <paramref name="maxBytes"/> UTF-8 bytes; else <see cref="ArgumentException"/>.</summary>
    static void WriteString(NetWriter w, StringTableWriter strings, string? value, int maxBytes, string what, bool intern = true)
    {
        if (value is null) throw new ArgumentException($"{what} is null.");
        if (value.Length > maxBytes || (value.Length * 3L > maxBytes && Encoding.UTF8.GetByteCount(value) > maxBytes))
            throw new ArgumentException($"{what} exceeds the limit of {maxBytes} bytes.");
        strings.Write(w, value, intern);
    }

    static float Finite(float v) => float.IsFinite(v) ? v : throw new ArgumentException("A record float is not finite.");

    static Vector3 Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : throw new ArgumentException("A record vector is not finite.");

    static Vector4 Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W) ? v : throw new ArgumentException("A record vector is not finite.");

    static Quaternion Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) ? q : throw new ArgumentException("A record quaternion is not finite.");

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
