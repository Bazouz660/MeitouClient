using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Net.Commands;

/// <summary>
/// The field encodings of the command codec, writing side (docs/net.md, "Command codec"). Ints are zigzag varints, floats raw bits, strings go through the
/// connection's string table (record and instance keys interned, free text literal). The writer clamps what a reader would refuse, so a reliable-channel
/// message never makes the host drop its sender: strings are cut to <see cref="NetLimits.MaxName"/> bytes on a character boundary, id lists to
/// <see cref="NetLimits.MaxIdList"/>, plans to <see cref="NetLimits.MaxPlans"/>, and a non-finite float is sent as 0.
/// </summary>
readonly struct CommandWriter(NetWriter w, StringTableWriter strings)
{
    public NetWriter Out => w;

    public void Bool(bool v) => w.WriteBool(v);
    public void Int(int v) => w.WriteVarInt(v);
    public void Long(long v) => w.WriteVarInt(v);
    public void Float(float v) => w.WriteSingle(float.IsFinite(v) ? v : 0f);
    public void Value<T>(T v) where T : struct, Enum => w.WriteEnum(v);

    /// <summary>A presence byte, then the value.</summary>
    public void Byte(byte? v)
    {
        w.WriteBool(v.HasValue);
        if (v is { } b) w.WriteByte(b);
    }

    public void Bool(bool? v)
    {
        w.WriteBool(v.HasValue);
        if (v is { } b) w.WriteBool(b);
    }

    public void Float(float? v) => CommonCodec.WriteNullableSingle(w, v is { } f ? (float.IsFinite(f) ? f : 0f) : null);
    public void Int(int? v) => CommonCodec.WriteNullableInt(w, v);

    public void Id(CharacterId id) => CommonCodec.WriteId(w, id);

    public void Ids(IReadOnlyList<CharacterId> ids)
    {
        int count = Math.Min(ids.Count, NetLimits.MaxIdList);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) CommonCodec.WriteId(w, ids[i]);
    }

    public void Vec2(Vector2 v)
    {
        Float(v.X);
        Float(v.Y);
    }

    public void Vec3(Vector3 v)
    {
        Float(v.X);
        Float(v.Y);
        Float(v.Z);
    }

    /// <summary>A record, instance or site key: interned.</summary>
    public void Key(string? s) => strings.Write(w, Clamp(s ?? ""));

    public void KeyOrNull(string? s) => strings.Write(w, s is null ? null : Clamp(s));

    /// <summary>Free text (a squad or character name): a literal.</summary>
    public void Text(string? s) => strings.Write(w, Clamp(s ?? ""), intern: false);

    /// <summary>Index, record, section, cell, quantity, then the quality as raw bits: tokens are compared by equality (<c>InventoryCommands</c>), so it must be exact.</summary>
    public void Token(InventoryItemToken t)
    {
        Int(t.Index);
        Key(t.Record);
        Key(t.Section);
        Int(t.X);
        Int(t.Y);
        Int(t.Quantity);
        Float(t.Quality);
    }

    /// <summary>An appearance record (<see cref="FcsRecordCodec"/>); its maps are bounded by <see cref="NetLimits.MaxFcsMapEntries"/> (the codec throws <see cref="ArgumentException"/> beyond).</summary>
    public void Fcs(Meitou.Data.Fcs.FcsRecord record) => FcsRecordCodec.Write(w, strings, record);

    public void Plans(IReadOnlyList<PlannedBuilding> plans)
    {
        int count = Math.Min(plans.Count, NetLimits.MaxPlans);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var p = plans[i];
            Key(p.Record);
            Vec3(p.Position);
            Float(p.Rotation.X);
            Float(p.Rotation.Y);
            Float(p.Rotation.Z);
            Float(p.Rotation.W);
            Int(p.Floor);
            KeyOrNull(p.Parent);
        }
    }

    /// <summary>The longest prefix of <paramref name="s"/> that fits <see cref="NetLimits.MaxName"/> UTF-8 bytes, never splitting a surrogate pair.</summary>
    static string Clamp(string s)
    {
        if (s.Length * 3L <= NetLimits.MaxName || Encoding.UTF8.GetByteCount(s) <= NetLimits.MaxName) return s;
        int bytes = 0, chars = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > NetLimits.MaxName) break;
            chars += rune.Utf16SequenceLength;
        }
        return s[..chars];
    }
}

/// <summary>The reading side of <see cref="CommandWriter"/>. Every failure is a <see cref="NetFormatException"/>; floats are read finite, enums checked, counts bounded.</summary>
ref struct CommandReader(NetReader reader, StringTableReader strings)
{
    NetReader r = reader;

    public readonly NetReader Reader => r;

    public bool Bool() => r.ReadBool();
    public int Int() => r.ReadVarInt32();
    public long Long() => r.ReadVarInt();
    public float Float() => r.ReadFinite();
    public T Value<T>() where T : struct, Enum => r.ReadEnum<T>();

    public byte? Byte() => r.ReadBool() ? r.ReadByte() : null;
    public bool? NullableBool() => r.ReadBool() ? r.ReadBool() : null;
    public float? NullableFloat() => CommonCodec.ReadNullableSingle(ref r);
    public int? NullableInt() => CommonCodec.ReadNullableInt(ref r);

    public CharacterId Id() => CommonCodec.ReadId(ref r);
    public CharacterId[] Ids() => CommonCodec.ReadIds(ref r);
    public Vector2 Vec2() => CommonCodec.ReadVector2(ref r);
    public Vector3 Vec3() => CommonCodec.ReadVector3(ref r);

    public string Key() => strings.Read(ref r, NetLimits.MaxName) ?? throw new NetFormatException("A command string is null.");
    public string? KeyOrNull() => strings.Read(ref r, NetLimits.MaxName);
    public string Text() => Key();

    public InventoryItemToken Token() => new(Int(), Key(), Key(), Int(), Int(), Int(), Float());

    public PlannedBuilding[] Plans()
    {
        int count = r.ReadCount(NetLimits.MaxPlans);
        var plans = new PlannedBuilding[count];
        for (int i = 0; i < count; i++)
            plans[i] = new PlannedBuilding(Key(), Vec3(), new Quaternion(Float(), Float(), Float(), Float()), Int(), KeyOrNull());
        return plans;
    }

    public Meitou.Data.Fcs.FcsRecord Fcs() => FcsRecordCodec.Read(ref r, strings);

    public ushort Tag()
    {
        uint tag = r.ReadVarUInt32();
        if (tag > ushort.MaxValue) throw new NetFormatException($"Unknown command tag {tag}.");
        return (ushort)tag;
    }
}
