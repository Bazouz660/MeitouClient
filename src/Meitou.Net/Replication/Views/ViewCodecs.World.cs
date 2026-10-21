using System.Numerics;
using System.Text;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Net.Replication;

/// <summary>
/// The item codecs and the host/client halves of the reliable views (docs/net.md, "World and production views"). This file holds the world views:
/// Buildings (5), GroundItems (6), Containers (7), DoorPicks (8), Doors (9), Unlocked (10) and Inspected (11); <c>ViewCodecs.Production.cs</c> the
/// production ones (12-14).
/// <para>
/// Item fields go in declaration order, positional first, then init properties; bools are packed into a flags byte, floats and vectors are raw (a
/// non-finite float is written as 0 so a reader's <see cref="NetReader.ReadFinite"/> never refuses what a writer produced), record ids and names are
/// interned and free text is a literal. Every writer clamps strings to their byte limit and lists to <see cref="NetLimits.MaxListItems"/>.
/// </para>
/// </summary>
public static partial class ViewCodecs
{
    /// <summary>Minimum seconds between item-only changes of each view (D2 section 6); membership and order changes go at once.</summary>
    public static readonly TimeSpan BuildingsInterval = TimeSpan.FromMilliseconds(200), ContainersInterval = TimeSpan.FromSeconds(1),
        DoorPicksInterval = TimeSpan.FromSeconds(1), MachinesInterval = TimeSpan.FromMilliseconds(200),
        ResearchInterval = TimeSpan.FromMilliseconds(200), CraftsInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Kind 5 item: a player-built building or site (key <see cref="BuildingSnapshot.Id"/>).</summary>
    public static ItemCodec<BuildingSnapshot> Building { get; } = new(WriteBuilding, ReadBuilding);

    /// <summary>Kind 6 item: a loose item (key <see cref="GroundItemSnapshot.Id"/>).</summary>
    public static ItemCodec<GroundItemSnapshot> GroundItem { get; } = new(WriteGroundItem, ReadGroundItem);

    /// <summary>Kind 7 item: a container spot (key <see cref="ContainerSpot.Id"/>).</summary>
    public static ItemCodec<ContainerSpot> Container { get; } = new(WriteContainer, ReadContainer);

    /// <summary>Kind 8 item: a door for picking (key <see cref="DoorPick.Id"/>).</summary>
    public static ItemCodec<DoorPick> DoorPickItem { get; } = new(WriteDoorPick, ReadDoorPick);

    /// <summary>Kind 9 item: one entry of <c>WorldSnapshot.Doors</c> (key Building and Door).</summary>
    public static ItemCodec<DoorEntry> DoorItem { get; } = new(WriteDoorEntry, ReadDoorEntry);

    /// <summary>Kind 10 item: one unlocked building record id (the key is the string itself).</summary>
    public static ItemCodec<string> UnlockedItem { get; } = new((w, s, v) => WorldWire.Text(w, s, v), (ref NetReader r, StringTableReader s) => WorldWire.ReadText(ref r, s));

    /// <summary>Kind 11 body: a presence byte, then the inspected building's doors.</summary>
    public static ItemCodec<BuildingDetail?> InspectedItem { get; } = new(WriteInspected, ReadInspected);

    public static KeyedViewSync<string, BuildingSnapshot> BuildingsSync() => new(ViewKind.Buildings, b => b.Id, Building, BuildingsInterval);
    public static KeyedViewDecoder<BuildingSnapshot> BuildingsDecoder() => new(Building);

    public static KeyedViewSync<int, GroundItemSnapshot> GroundItemsSync() => new(ViewKind.GroundItems, g => g.Id, GroundItem, TimeSpan.Zero);
    public static KeyedViewDecoder<GroundItemSnapshot> GroundItemsDecoder() => new(GroundItem);

    public static KeyedViewSync<string, ContainerSpot> ContainersSync() => new(ViewKind.Containers, c => c.Id, Container, ContainersInterval);
    public static KeyedViewDecoder<ContainerSpot> ContainersDecoder() => new(Container);

    public static KeyedViewSync<string, DoorPick> DoorPicksSync() => new(ViewKind.DoorPicks, d => d.Id, DoorPickItem, DoorPicksInterval);
    public static KeyedViewDecoder<DoorPick> DoorPicksDecoder() => new(DoorPickItem);

    /// <summary>Kind 9 host half over <c>WorldSnapshot.Doors</c>; the dictionary is flattened once per reference, in key order.</summary>
    public static WorldMappedSync<IReadOnlyDictionary<(string Building, int Door), DoorView>, (string, int), DoorEntry> DoorsSync() =>
        new(new KeyedViewSync<(string, int), DoorEntry>(ViewKind.Doors, d => (d.Building, d.Door), DoorItem, TimeSpan.Zero), FlattenDoors);

    public static WorldMappedDecoder<DoorEntry, IReadOnlyDictionary<(string Building, int Door), DoorView>> DoorsDecoder() => new(DoorItem, BuildDoors);

    /// <summary>Kind 10 host half over <c>WorldSnapshot.UnlockedBuildings</c>; the set is sent in ordinal order so a rebuilt set does not look reordered.</summary>
    public static WorldMappedSync<IReadOnlySet<string>, string, string> UnlockedSync() =>
        new(new KeyedViewSync<string, string>(ViewKind.Unlocked, s => s, UnlockedItem, TimeSpan.Zero), SortUnlocked);

    public static WorldMappedDecoder<string, IReadOnlySet<string>> UnlockedDecoder() => new(UnlockedItem, items => new HashSet<string>(items, StringComparer.Ordinal));

    /// <summary>Kind 11 host half over <c>WorldSnapshot.InspectedBuilding</c> (null: none).</summary>
    public static WorldSingleSync<BuildingDetail?> InspectedSync() => new(InspectedItem, TimeSpan.Zero);

    public static WorldSingleDecoder<BuildingDetail?> InspectedDecoder() => new(InspectedItem);

    static IReadOnlyList<DoorEntry> FlattenDoors(IReadOnlyDictionary<(string Building, int Door), DoorView> doors)
    {
        var list = new List<DoorEntry>(Math.Min(doors.Count, NetLimits.MaxViewItems));
        foreach (var (key, view) in doors) list.Add(new DoorEntry(key.Building, key.Door, view));
        list.Sort(static (a, b) => { int c = string.CompareOrdinal(a.Building, b.Building); return c != 0 ? c : a.Door.CompareTo(b.Door); });
        return list;
    }

    static IReadOnlyDictionary<(string Building, int Door), DoorView> BuildDoors(IReadOnlyList<DoorEntry> items)
    {
        var doors = new Dictionary<(string Building, int Door), DoorView>(items.Count);
        foreach (var item in items) doors[(item.Building, item.Door)] = item.View;
        return doors;
    }

    static IReadOnlyList<string> SortUnlocked(IReadOnlySet<string> set)
    {
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // Buildings

    static void WriteBuilding(NetWriter w, StringTableWriter s, BuildingSnapshot b)
    {
        WorldWire.Text(w, s, b.Id);
        WorldWire.Text(w, s, b.Record);
        WorldWire.Vector(w, b.Position);
        WorldWire.Rotation(w, b.Rotation);
        w.WriteVarInt(b.Floor);
        WorldWire.OptionalText(w, s, b.Parent);
        w.WriteVarInt(b.Owner);
        WorldWire.Float(w, b.Progress);
        WorldWire.Float(w, b.Target);
        int count = WorldWire.Count(b.Materials.Count);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var m = b.Materials[i];
            WorldWire.Text(w, s, m.Item);
            WorldWire.Float(w, m.Required);
            WorldWire.Float(w, m.Delivered);
        }
        w.WriteByte(WorldWire.Pack(b.Complete, b.Paused, b.Dismantling));
        WorldWire.OptionalText(w, s, b.WallParent);
    }

    static BuildingSnapshot ReadBuilding(ref NetReader r, StringTableReader s)
    {
        string id = WorldWire.ReadText(ref r, s), record = WorldWire.ReadText(ref r, s);
        var position = CommonCodec.ReadVector3(ref r);
        var rotation = CommonCodec.ReadQuaternion(ref r);
        int floor = r.ReadVarInt32();
        string? parent = WorldWire.ReadOptionalText(ref r, s);
        int owner = r.ReadVarInt32();
        float progress = r.ReadFinite(), target = r.ReadFinite();
        int count = r.ReadCount(NetLimits.MaxListItems);
        var materials = new MaterialView[count];
        for (int i = 0; i < count; i++) materials[i] = new MaterialView(WorldWire.ReadText(ref r, s), r.ReadFinite(), r.ReadFinite());
        byte flags = WorldWire.ReadFlags(ref r, 7);
        string? wallParent = WorldWire.ReadOptionalText(ref r, s);
        return new BuildingSnapshot(id, record, position, rotation, floor, parent, owner, progress, target, materials, (flags & 1) != 0, (flags & 2) != 0)
        {
            Dismantling = (flags & 4) != 0,
            WallParent = wallParent,
        };
    }

    // Ground items, containers, door picks

    static void WriteGroundItem(NetWriter w, StringTableWriter s, GroundItemSnapshot g)
    {
        w.WriteVarInt(g.Id);
        WorldWire.Text(w, s, g.Record);
        WorldWire.Text(w, s, g.Name);
        WorldWire.Vector(w, g.Position);
        WorldWire.Rotation(w, g.Rotation);
        w.WriteByte(WorldWire.Pack(g.Dropped));
    }

    static GroundItemSnapshot ReadGroundItem(ref NetReader r, StringTableReader s)
    {
        int id = r.ReadVarInt32();
        string record = WorldWire.ReadText(ref r, s), name = WorldWire.ReadText(ref r, s);
        var position = CommonCodec.ReadVector3(ref r);
        var rotation = CommonCodec.ReadQuaternion(ref r);
        return new(id, record, name, position, rotation, (WorldWire.ReadFlags(ref r, 1) & 1) != 0);
    }

    static void WriteContainer(NetWriter w, StringTableWriter s, ContainerSpot c)
    {
        WorldWire.Text(w, s, c.Id);
        WorldWire.Text(w, s, c.Record);
        WorldWire.Vector(w, c.Position);
    }

    static ContainerSpot ReadContainer(ref NetReader r, StringTableReader s) =>
        new(WorldWire.ReadText(ref r, s), WorldWire.ReadText(ref r, s), CommonCodec.ReadVector3(ref r));

    static void WriteDoorPick(NetWriter w, StringTableWriter s, DoorPick d)
    {
        WorldWire.Text(w, s, d.Id);
        WorldWire.Text(w, s, d.Building);
        w.WriteVarInt(d.Door);
        w.WriteVarInt(d.Owner);
        WorldWire.Vector(w, d.Position);
    }

    static DoorPick ReadDoorPick(ref NetReader r, StringTableReader s) =>
        new(WorldWire.ReadText(ref r, s), WorldWire.ReadText(ref r, s), r.ReadVarInt32(), r.ReadVarInt32(), CommonCodec.ReadVector3(ref r));

    // Doors

    static void WriteDoorEntry(NetWriter w, StringTableWriter s, DoorEntry e)
    {
        WorldWire.Text(w, s, e.Building);
        w.WriteVarInt(e.Door);
        var v = e.View;
        WorldWire.Float(w, v.Open);
        w.WriteEnum(v.Motion);
        w.WriteByte(WorldWire.Pack(v.Locked, v.WantsLock, v.Broken));
        WorldWire.Vector(w, v.Position);
        WorldWire.Text(w, s, v.Record);
    }

    static DoorEntry ReadDoorEntry(ref NetReader r, StringTableReader s)
    {
        string building = WorldWire.ReadText(ref r, s);
        int door = r.ReadVarInt32();
        float open = r.ReadFinite();
        var motion = r.ReadEnum<DoorMotion>();
        byte flags = WorldWire.ReadFlags(ref r, 7);
        var position = CommonCodec.ReadVector3(ref r);
        string record = WorldWire.ReadText(ref r, s);
        return new DoorEntry(building, door, new DoorView(open, motion, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, position, record));
    }

    // Inspected building

    static void WriteInspected(NetWriter w, StringTableWriter s, BuildingDetail? d)
    {
        w.WriteBool(d is not null);
        if (d is null) return;
        WorldWire.Text(w, s, d.Instance);
        int count = WorldWire.Count(d.Doors.Count);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var door = d.Doors[i];
            w.WriteByte(WorldWire.Pack(door.Open, door.Locked, door.Broken));
            w.WriteVarInt(door.Level);
        }
    }

    static BuildingDetail? ReadInspected(ref NetReader r, StringTableReader s)
    {
        if (!r.ReadBool()) return null;
        string instance = WorldWire.ReadText(ref r, s);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var doors = new DoorDetail[count];
        for (int i = 0; i < count; i++)
        {
            byte flags = WorldWire.ReadFlags(ref r, 7);
            doors[i] = new DoorDetail((flags & 1) != 0, (flags & 2) != 0, r.ReadVarInt32(), (flags & 4) != 0);
        }
        return new BuildingDetail(instance, doors);
    }

    /// <summary>The small encodings the world and production codecs share.</summary>
    internal static class WorldWire
    {
        public static int Count(int count) => Math.Min(count, NetLimits.MaxListItems);

        public static void Float(NetWriter w, float v) => w.WriteSingle(float.IsFinite(v) ? v : 0f);

        public static void Vector(NetWriter w, Vector3 v)
        {
            Float(w, v.X);
            Float(w, v.Y);
            Float(w, v.Z);
        }

        public static void Rotation(NetWriter w, Quaternion q)
        {
            Float(w, q.X);
            Float(w, q.Y);
            Float(w, q.Z);
            Float(w, q.W);
        }

        public static byte Pack(bool a, bool b = false, bool c = false, bool d = false, bool e = false) =>
            (byte)((a ? 1 : 0) | (b ? 2 : 0) | (c ? 4 : 0) | (d ? 8 : 0) | (e ? 16 : 0));

        public static byte ReadFlags(ref NetReader r, int mask)
        {
            byte flags = r.ReadByte();
            if ((flags & ~mask) != 0) throw new NetFormatException($"Unknown flag bits {flags:X2}.");
            return flags;
        }

        /// <summary>A string cut to <paramref name="maxBytes"/> of UTF-8 without splitting a surrogate pair.</summary>
        public static string Clamp(string? text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length * 3L <= maxBytes || Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
            int bytes = 0, end = 0;
            while (end < text.Length)
            {
                int width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                int size = width == 2 ? 4 : text[end] < 0x80 ? 1 : text[end] < 0x800 ? 2 : 3;
                if (bytes + size > maxBytes) break;
                bytes += size;
                end += width;
            }
            return text[..end];
        }

        public static void Text(NetWriter w, StringTableWriter s, string? text, int maxBytes = NetLimits.MaxName) => s.Write(w, Clamp(text, maxBytes));

        /// <summary>Free text: a literal, never entered in the table.</summary>
        public static void Literal(NetWriter w, StringTableWriter s, string? text, int maxBytes = NetLimits.MaxString) => s.Write(w, Clamp(text, maxBytes), intern: false);

        public static void OptionalText(NetWriter w, StringTableWriter s, string? text, int maxBytes = NetLimits.MaxName) =>
            s.Write(w, text is null ? null : Clamp(text, maxBytes));

        public static string ReadText(ref NetReader r, StringTableReader s, int maxBytes = NetLimits.MaxName) => s.Read(ref r, maxBytes) ?? "";

        public static string? ReadOptionalText(ref NetReader r, StringTableReader s, int maxBytes = NetLimits.MaxName) => s.Read(ref r, maxBytes);
    }
}

/// <summary>One entry of <c>WorldSnapshot.Doors</c> as a view item: the building instance id, the door index and its state.</summary>
public readonly record struct DoorEntry(string Building, int Door, Meitou.Simulation.Locks.DoorView View);

/// <summary>
/// Host half of a keyed view whose source is not a list (the doors dictionary, the unlocked set, the crafts dictionary): the source is flattened once
/// per reference, so <see cref="KeyedViewSync{TKey, TItem}"/> sees the same list reference until the source changes and skips it at once.
/// </summary>
public sealed class WorldMappedSync<TSource, TKey, TItem>(KeyedViewSync<TKey, TItem> inner, Func<TSource, IReadOnlyList<TItem>> map)
    where TSource : class where TKey : notnull
{
    TSource? last;
    IReadOnlyList<TItem> list = [];

    public ViewKind Kind => inner.Kind;

    public void Reset() => inner.Reset();

    /// <inheritdoc cref="KeyedViewSync{TKey, TItem}.Encode"/>
    public bool Encode(TSource now, bool structural, TimeSpan time, NetWriter w, StringTableWriter s)
    {
        if (!ReferenceEquals(now, last))
        {
            list = map(now);
            last = now;
        }
        return inner.Encode(list, structural, time, w, s);
    }
}

/// <summary>Client half of a <see cref="WorldMappedSync{TSource, TKey, TItem}"/>: <see cref="Value"/> is rebuilt when a body changed something and is the same reference until then.</summary>
public sealed class WorldMappedDecoder<TItem, TValue>(ItemCodec<TItem> codec, Func<IReadOnlyList<TItem>, TValue> build)
{
    readonly KeyedViewDecoder<TItem> inner = new(codec);
    TValue value = build([]);

    public TValue Value => value;

    public void Reset()
    {
        inner.Reset();
        value = build([]);
    }

    /// <summary>Applies one body; returns true when <see cref="Value"/> changed. A malformed body throws <see cref="NetFormatException"/> and changes nothing.</summary>
    public bool Apply(ref NetReader r, StringTableReader strings)
    {
        if (!inner.Apply(ref r, strings)) return false;
        value = build(inner.Items);
        return true;
    }
}

/// <summary>
/// Host half of a single-value view (kind 11): the value is sent when its canonical bytes differ from the last ones sent and <c>minInterval</c> has
/// passed. A source reference equal to the last one sent is skipped at once.
/// </summary>
public sealed class WorldSingleSync<T>(ItemCodec<T> codec, TimeSpan minInterval)
{
    readonly NetWriter canonical = new();
    readonly StringTableWriter literals = StringTableWriter.CreateLiteralOnly();
    byte[]? sent;
    T? settled;
    TimeSpan lastSent;

    public void Reset()
    {
        sent = null;
        settled = default;
    }

    /// <summary>Writes the value to <paramref name="w"/> and returns true, or returns false and writes nothing when the client already holds it or the change must wait.</summary>
    public bool Encode(T now, TimeSpan time, NetWriter w, StringTableWriter s)
    {
        if (sent is not null && ReferenceEquals(now, settled)) return false;
        canonical.Reset();
        codec.Write(canonical, literals, now);
        if (sent is not null && canonical.Written.SequenceEqual(sent))
        {
            settled = now;
            return false;
        }
        if (sent is not null && time >= lastSent && time - lastSent < minInterval) return false;
        codec.Write(w, s, now);
        sent = canonical.Written.ToArray();
        settled = now;
        lastSent = time;
        return true;
    }
}

/// <summary>Client half of a <see cref="WorldSingleSync{T}"/>.</summary>
public sealed class WorldSingleDecoder<T>(ItemCodec<T> codec)
{
    public T? Value { get; private set; }

    public void Reset() => Value = default;

    /// <summary>Reads one value; a malformed body throws <see cref="NetFormatException"/> and leaves <see cref="Value"/> as it was.</summary>
    public void Apply(ref NetReader r, StringTableReader strings) => Value = codec.Read(ref r, strings);
}
