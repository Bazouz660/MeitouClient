using System.Text;
using Meitou.Data.Characters;

namespace Meitou.Net.Replication;

/// <summary>
/// The <c>LookDefinition</c> message (<c>0x31</c>, Views) and the recipe body (docs/net.md, "Looks"). Ids go through the channel's string table; ints are zigzag
/// varints; the appearance record is <see cref="FcsRecordCodec"/>. The writer checks what the reader limits (id lengths, list counts) and writes a non-finite
/// shape float as 0, so a reader never refuses what a writer produced.
/// </summary>
public static class LookCodec
{
    /// <summary>The whole message: <c>0x31</c>, the look id (varint, never 0), then <see cref="Write(NetWriter, StringTableWriter, AppearanceRecipe)"/>.</summary>
    public static void WriteDefinition(NetWriter w, StringTableWriter strings, uint lookId, AppearanceRecipe recipe)
    {
        if (lookId == 0) throw new ArgumentOutOfRangeException(nameof(lookId), "Look id 0 means no look and is never defined.");
        w.WriteByte((byte)MessageType.LookDefinition);
        w.WriteVarUInt(lookId);
        Write(w, strings, recipe);
    }

    /// <summary>Reads a whole <see cref="WriteDefinition"/> message, from its type byte; the caller checks the end (one message per packet).</summary>
    public static AppearanceRecipe ReadDefinition(ref NetReader r, StringTableReader strings, out uint lookId)
    {
        if (r.ReadByte() != (byte)MessageType.LookDefinition) throw new NetFormatException("Not a LookDefinition.");
        lookId = r.ReadVarUInt32();
        if (lookId == 0) throw new NetFormatException("A LookDefinition defines look 0.");
        return Read(ref r, strings);
    }

    /// <summary>
    /// The recipe: its kind byte, the kind's fields, then the shape (a presence bool). Rolled: record id, faction id (nullable), seed. Kit: record id, then the
    /// loadout (see docs/net.md for the field order).
    /// </summary>
    public static void Write(NetWriter w, StringTableWriter strings, AppearanceRecipe recipe)
    {
        w.WriteByte((byte)recipe.Kind);
        switch (recipe)
        {
            case AppearanceRecipe.Rolled rolled:
                WriteId(w, strings, rolled.RecordId);
                WriteId(w, strings, rolled.FactionId);
                w.WriteVarInt(rolled.Seed);
                break;
            case AppearanceRecipe.Kit kit:
                WriteId(w, strings, kit.RecordId);
                WriteLoadout(w, strings, kit.Loadout);
                break;
            default:
                throw new ArgumentException($"Unknown recipe {recipe.GetType().Name}.", nameof(recipe));
        }
        w.WriteBool(recipe.Shape is not null);
        if (recipe.Shape is { } shape) WriteShape(w, strings, shape);
    }

    public static AppearanceRecipe Read(ref NetReader r, StringTableReader strings)
    {
        AppearanceRecipe recipe = r.ReadEnum<RecipeKind>() switch
        {
            RecipeKind.Rolled => new AppearanceRecipe.Rolled(ReadId(ref r, strings), ReadNullableId(ref r, strings), r.ReadVarInt32()),
            RecipeKind.Kit => new AppearanceRecipe.Kit(ReadId(ref r, strings), ReadLoadout(ref r, strings)),
            var kind => throw new NetFormatException($"Unknown recipe kind {kind}."),
        };
        return r.ReadBool() ? recipe with { Shape = ReadShape(ref r, strings) } : recipe;
    }

    // Loadout: seed; character id (nullable); race id; flags (b0 female, b1 generated); morph index; the appearance record; clothing (count, items); backpack
    // (presence, item); carried (count, items); crossbow (presence, weapon); weapons (count, weapons); four limbs (varint 0 natural, else state + 1, then the
    // prosthetic id, nullable).
    static void WriteLoadout(NetWriter w, StringTableWriter strings, LoadoutWire l)
    {
        w.WriteVarInt(l.Seed);
        WriteId(w, strings, l.Character);
        WriteId(w, strings, l.Race);
        w.WriteVarUInt((l.Female ? 1u : 0) | (l.Generated ? 2u : 0));
        w.WriteVarInt(l.MorphIndex);
        FcsRecordCodec.Write(w, strings, l.Appearance);
        WriteList(w, strings, l.Clothing, WriteItem);
        w.WriteBool(l.Backpack is not null);
        if (l.Backpack is { } pack) WriteItem(w, strings, pack);
        WriteList(w, strings, l.Carried, WriteItem);
        w.WriteBool(l.Crossbow is not null);
        if (l.Crossbow is { } bow) WriteWeapon(w, strings, bow);
        WriteList(w, strings, l.Weapons, WriteWeapon);
        for (int k = 0; k < LoadoutWire.LimbCount; k++)
        {
            var limb = k < l.Limbs.Count ? l.Limbs[k] : null;
            if (limb is null)
            {
                w.WriteVarUInt(0);
                continue;
            }
            if (limb.State < 0) throw new ArgumentException($"Limb {k} has state {limb.State}.", nameof(l));
            w.WriteVarUInt((ulong)limb.State + 1);
            WriteId(w, strings, limb.Prosthetic);
        }
    }

    static LoadoutWire ReadLoadout(ref NetReader r, StringTableReader strings)
    {
        int seed = r.ReadVarInt32();
        string? character = ReadNullableId(ref r, strings);
        string race = ReadId(ref r, strings);
        uint flags = r.ReadVarUInt32();
        if ((flags & ~3u) != 0) throw new NetFormatException($"Unknown loadout flags {flags}.");
        int morph = r.ReadVarInt32();
        var appearance = FcsRecordCodec.Read(ref r, strings);
        var clothing = ReadList(ref r, strings, ReadItem);
        var backpack = r.ReadBool() ? ReadItem(ref r, strings) : null;
        var carried = ReadList(ref r, strings, ReadItem);
        var crossbow = r.ReadBool() ? ReadWeapon(ref r, strings) : null;
        var weapons = ReadList(ref r, strings, ReadWeapon);
        var limbs = new LoadoutLimbWire?[LoadoutWire.LimbCount];
        for (int k = 0; k < limbs.Length; k++)
        {
            uint state = r.ReadVarUInt32();
            if (state == 0) continue;
            if (state - 1 > int.MaxValue) throw new NetFormatException($"Limb state {state - 1} is out of range.");
            limbs[k] = new LoadoutLimbWire((int)(state - 1), ReadNullableId(ref r, strings));
        }
        return new LoadoutWire(seed, character, race, (flags & 1) != 0, appearance, (flags & 2) != 0, morph, clothing, backpack, carried, crossbow, weapons, limbs);
    }

    static void WriteItem(NetWriter w, StringTableWriter strings, LoadoutItemWire i)
    {
        WriteId(w, strings, i.Record);
        w.WriteVarInt(i.Quality);
        WriteId(w, strings, i.Material);
    }

    static LoadoutItemWire ReadItem(ref NetReader r, StringTableReader strings) => new(ReadId(ref r, strings), r.ReadVarInt32(), ReadNullableId(ref r, strings));

    static void WriteWeapon(NetWriter w, StringTableWriter strings, LoadoutWeaponWire x)
    {
        WriteId(w, strings, x.Weapon);
        WriteId(w, strings, x.Manufacturer);
        WriteId(w, strings, x.Model);
        w.WriteVarInt(x.Level);
        WriteId(w, strings, x.Section);
        w.WriteVarInt(x.Row);
    }

    static LoadoutWeaponWire ReadWeapon(ref NetReader r, StringTableReader strings) =>
        new(ReadId(ref r, strings), ReadNullableId(ref r, strings), ReadNullableId(ref r, strings), r.ReadVarInt32(), ReadId(ref r, strings), r.ReadVarInt32());

    // Shape: flags (b0 female, b1 narrow left thigh, b2 narrow right thigh); muscle, starvation, leg ratio (f32); hide stump (byte); missing limbs (a byte,
    // bit k for Limb k); sliders (count, then key and f32).
    static void WriteShape(NetWriter w, StringTableWriter strings, CharacterShapeInput s)
    {
        w.WriteVarUInt((s.Female ? 1u : 0) | (s.NarrowLeftThigh ? 2u : 0) | (s.NarrowRightThigh ? 4u : 0));
        w.WriteSingle(Finite(s.Muscle));
        w.WriteSingle(Finite(s.Starvation));
        w.WriteSingle(Finite(s.LegRatio));
        w.WriteEnum(s.HideStump);
        int missing = 0;
        foreach (var limb in s.Missing)
            if ((uint)limb < LoadoutWire.LimbCount) missing |= 1 << (int)limb;
        w.WriteByte((byte)missing);
        if (s.Sliders.Count > NetLimits.MaxListItems) throw new ArgumentException($"{s.Sliders.Count} sliders exceed the limit of {NetLimits.MaxListItems}.", nameof(s));
        w.WriteVarUInt((uint)s.Sliders.Count);
        foreach (var (key, value) in s.Sliders)
        {
            WriteId(w, strings, key);
            w.WriteSingle(Finite(value));
        }
    }

    static CharacterShapeInput ReadShape(ref NetReader r, StringTableReader strings)
    {
        uint flags = r.ReadVarUInt32();
        if ((flags & ~7u) != 0) throw new NetFormatException($"Unknown shape flags {flags}.");
        float muscle = r.ReadFinite(), starvation = r.ReadFinite(), legRatio = r.ReadFinite();
        var hideStump = r.ReadEnum<HideStump>();
        byte missingBits = r.ReadByte();
        if (missingBits >> LoadoutWire.LimbCount != 0) throw new NetFormatException($"Unknown missing-limb bits {missingBits}.");
        var missing = new HashSet<Limb>();
        for (int k = 0; k < LoadoutWire.LimbCount; k++)
            if ((missingBits >> k & 1) != 0) missing.Add((Limb)k);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var sliders = new Dictionary<string, float>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadId(ref r, strings);
            if (!sliders.TryAdd(key, r.ReadFinite())) throw new NetFormatException($"Duplicate shape slider \"{key}\".");
        }
        return new CharacterShapeInput
        {
            Sliders = sliders, Female = (flags & 1) != 0, Muscle = muscle, Starvation = starvation, Missing = missing, HideStump = hideStump, LegRatio = legRatio,
            NarrowLeftThigh = (flags & 2) != 0, NarrowRightThigh = (flags & 4) != 0,
        };
    }

    delegate T ListReader<out T>(ref NetReader r, StringTableReader strings);

    static void WriteList<T>(NetWriter w, StringTableWriter strings, IReadOnlyList<T> items, Action<NetWriter, StringTableWriter, T> write)
    {
        if (items.Count > NetLimits.MaxListItems) throw new ArgumentException($"A loadout list of {items.Count} exceeds the limit of {NetLimits.MaxListItems}.", nameof(items));
        w.WriteVarUInt((uint)items.Count);
        foreach (var item in items) write(w, strings, item);
    }

    static T[] ReadList<T>(ref NetReader r, StringTableReader strings, ListReader<T> read)
    {
        int count = r.ReadCount(NetLimits.MaxListItems);
        var items = new T[count];
        for (int i = 0; i < count; i++) items[i] = read(ref r, strings);
        return items;
    }

    /// <summary>A record id, key or section (interned); longer than <see cref="NetLimits.MaxName"/> bytes is refused here, as the reader would.</summary>
    static void WriteId(NetWriter w, StringTableWriter strings, string? id)
    {
        if (id is not null && id.Length * 3L > NetLimits.MaxName && Encoding.UTF8.GetByteCount(id) > NetLimits.MaxName)
            throw new ArgumentException($"The id \"{id}\" is longer than {NetLimits.MaxName} bytes.", nameof(id));
        strings.Write(w, id);
    }

    static string? ReadNullableId(ref NetReader r, StringTableReader strings) => strings.Read(ref r, NetLimits.MaxName);

    static string ReadId(ref NetReader r, StringTableReader strings) => strings.Read(ref r, NetLimits.MaxName) ?? throw new NetFormatException("A required look id is null.");

    static float Finite(float v) => float.IsFinite(v) ? v : 0;
}
