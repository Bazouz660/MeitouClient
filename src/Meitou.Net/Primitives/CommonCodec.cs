using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Net;

/// <summary>
/// The encodings every codec shares (docs/net.md, "Common encodings"). Floats are raw and read with <see cref="NetReader.ReadFinite"/>, so a
/// non-finite vector from the other end is a format error.
/// </summary>
public static class CommonCodec
{
    /// <summary>The slot as a zigzag varint, then the generation as a varint; <see cref="CharacterId.None"/> is (-1, 0), two bytes.</summary>
    public static void WriteId(NetWriter w, CharacterId id)
    {
        w.WriteVarInt(id.Slot);
        w.WriteVarUInt((uint)id.Generation);
    }

    public static CharacterId ReadId(ref NetReader r)
    {
        int slot = r.ReadVarInt32();
        int generation = (int)r.ReadVarUInt32();
        return new CharacterId(slot, generation);
    }

    /// <summary>A count (at most <see cref="NetLimits.MaxIdList"/>), then the ids.</summary>
    public static void WriteIds(NetWriter w, IReadOnlyList<CharacterId> ids)
    {
        if (ids.Count > NetLimits.MaxIdList) throw new ArgumentException($"An id list of {ids.Count} exceeds the limit of {NetLimits.MaxIdList}.", nameof(ids));
        w.WriteVarUInt((uint)ids.Count);
        foreach (var id in ids) WriteId(w, id);
    }

    public static CharacterId[] ReadIds(ref NetReader r)
    {
        int count = r.ReadCount(NetLimits.MaxIdList);
        if (count == 0) return [];
        var ids = new CharacterId[count];
        for (int i = 0; i < count; i++) ids[i] = ReadId(ref r);
        return ids;
    }

    public static void WriteVector2(NetWriter w, Vector2 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
    }

    public static Vector2 ReadVector2(ref NetReader r) => new(r.ReadFinite(), r.ReadFinite());

    public static void WriteVector3(NetWriter w, Vector3 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
    }

    public static Vector3 ReadVector3(ref NetReader r) => new(r.ReadFinite(), r.ReadFinite(), r.ReadFinite());

    public static void WriteVector4(NetWriter w, Vector4 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
        w.WriteSingle(v.W);
    }

    public static Vector4 ReadVector4(ref NetReader r) => new(r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite());

    /// <summary>x, y, z, w as raw floats; the reader does not normalise.</summary>
    public static void WriteQuaternion(NetWriter w, Quaternion q)
    {
        w.WriteSingle(q.X);
        w.WriteSingle(q.Y);
        w.WriteSingle(q.Z);
        w.WriteSingle(q.W);
    }

    public static Quaternion ReadQuaternion(ref NetReader r) => new(r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite());

    /// <summary>A varint 0 for null, else <c>zigzag(v) + 1</c>, so a small present value stays one byte.</summary>
    public static void WriteNullableInt(NetWriter w, int? v)
    {
        if (v is not { } value)
        {
            w.WriteVarUInt(0);
            return;
        }
        w.WriteVarUInt(((uint)(value << 1) ^ (uint)(value >> 31)) + 1UL);
    }

    public static int? ReadNullableInt(ref NetReader r)
    {
        ulong v = r.ReadVarUInt();
        if (v == 0) return null;
        if (v - 1 > uint.MaxValue) throw new NetFormatException("A nullable int is longer than 32 bits.");
        uint u = (uint)(v - 1);
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    /// <summary>A presence byte, then the raw float.</summary>
    public static void WriteNullableSingle(NetWriter w, float? v)
    {
        w.WriteBool(v.HasValue);
        if (v is { } value) w.WriteSingle(value);
    }

    public static float? ReadNullableSingle(ref NetReader r) => r.ReadBool() ? r.ReadFinite() : null;
}
