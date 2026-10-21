using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Crime;

namespace Meitou.Net.Replication;

/// <summary>Kind 19 (<see cref="ViewKind.Stats"/>): what the HUD and the skills window show of one character (a selected or inspected one).</summary>
public sealed record StatsDetail(CharacterId Id, SkillSummary? Skills, IReadOnlyList<float> AllSkills, IReadOnlyList<float> SkillXpMultipliers, HudView? Hud, IReadOnlyList<BountyLine> Bounties)
{
    /// <summary>A copy of <paramref name="character"/> with these details.</summary>
    public CharacterSnapshot ApplyTo(CharacterSnapshot character) =>
        character with { Skills = Skills, AllSkills = AllSkills, SkillXpMultipliers = SkillXpMultipliers, Hud = Hud, Bounties = Bounties };
}

/// <summary>Kind 20 (<see cref="ViewKind.Health"/>): the body parts of one character.</summary>
public sealed record HealthDetail(CharacterId Id, IReadOnlyList<HealthPartView> Parts)
{
    public CharacterSnapshot ApplyTo(CharacterSnapshot character) => character with { HealthParts = Parts };
}

/// <summary>Kind 21 (<see cref="ViewKind.Inventory"/>): the inventory grid and the one-line item list of one character.</summary>
public sealed record InventoryDetail(CharacterId Id, InventoryView? ItemGrid, IReadOnlyList<string> Inventory)
{
    public CharacterSnapshot ApplyTo(CharacterSnapshot character) => character with { ItemGrid = ItemGrid, Inventory = Inventory };
}

/// <summary>Kind 22 (<see cref="ViewKind.Path"/>): the waypoints a selected character still follows.</summary>
public sealed record PathDetail(CharacterId Id, IReadOnlyList<Vector3> Path)
{
    public CharacterSnapshot ApplyTo(CharacterSnapshot character) => character with { Path = Path };
}

/// <summary>
/// The per-character detail views, kinds 19-22 (docs/net.md, "Player and detail views"). These members of <see cref="CharacterSnapshot"/> are empty for every character but the
/// viewer's selection and the inspected one, so they travel as keyed views on the reliable channel rather than in snapshot parts. The <c>...Of</c> methods list only the
/// characters that have something to show, in the characters' order.
/// </summary>
public static partial class ViewCodecs
{
    /// <summary>The <c>CharacterSnapshot</c> members the detail views carry (the wire coverage test reads these).</summary>
    public static readonly string[] DetailCovered =
    [
        nameof(CharacterSnapshot.Skills), nameof(CharacterSnapshot.AllSkills), nameof(CharacterSnapshot.SkillXpMultipliers), nameof(CharacterSnapshot.Hud), nameof(CharacterSnapshot.Bounties),
        nameof(CharacterSnapshot.HealthParts), nameof(CharacterSnapshot.ItemGrid), nameof(CharacterSnapshot.Inventory), nameof(CharacterSnapshot.Path),
    ];

    public static ItemCodec<StatsDetail> StatsCodec { get; } = new(WriteStats, ReadStats);
    public static ItemCodec<HealthDetail> HealthCodec { get; } = new(WriteHealth, ReadHealth);
    public static ItemCodec<InventoryDetail> InventoryCodec { get; } = new(WriteInventory, ReadInventory);
    public static ItemCodec<PathDetail> PathCodec { get; } = new(WritePath, ReadPath);

    /// <summary>Kind 19, keyed by character, at most every 100 ms.</summary>
    public static KeyedViewSync<CharacterId, StatsDetail> StatsSync() => new(ViewKind.Stats, d => d.Id, StatsCodec, TimeSpan.FromMilliseconds(100));

    /// <summary>Kind 20, keyed by character, at most every 100 ms.</summary>
    public static KeyedViewSync<CharacterId, HealthDetail> HealthSync() => new(ViewKind.Health, d => d.Id, HealthCodec, TimeSpan.FromMilliseconds(100));

    /// <summary>Kind 21, keyed by character, sent as soon as it changes.</summary>
    public static KeyedViewSync<CharacterId, InventoryDetail> InventorySync() => new(ViewKind.Inventory, d => d.Id, InventoryCodec, TimeSpan.Zero);

    /// <summary>Kind 22, keyed by character, at most every 250 ms.</summary>
    public static KeyedViewSync<CharacterId, PathDetail> PathSync() => new(ViewKind.Path, d => d.Id, PathCodec, TimeSpan.FromMilliseconds(250));

    public static List<StatsDetail> StatsOf(IReadOnlyList<CharacterSnapshot> characters)
    {
        var list = new List<StatsDetail>();
        foreach (var c in characters)
            if (c.Skills is not null || c.Hud is not null || c.AllSkills.Count > 0 || c.SkillXpMultipliers.Count > 0 || c.Bounties.Count > 0)
                list.Add(new StatsDetail(c.Id, c.Skills, c.AllSkills, c.SkillXpMultipliers, c.Hud, c.Bounties));
        return list;
    }

    public static List<HealthDetail> HealthOf(IReadOnlyList<CharacterSnapshot> characters)
    {
        var list = new List<HealthDetail>();
        foreach (var c in characters)
            if (c.HealthParts.Count > 0) list.Add(new HealthDetail(c.Id, c.HealthParts));
        return list;
    }

    public static List<InventoryDetail> InventoryOf(IReadOnlyList<CharacterSnapshot> characters)
    {
        var list = new List<InventoryDetail>();
        foreach (var c in characters)
            if (c.ItemGrid is not null || c.Inventory.Count > 0) list.Add(new InventoryDetail(c.Id, c.ItemGrid, c.Inventory));
        return list;
    }

    public static List<PathDetail> PathOf(IReadOnlyList<CharacterSnapshot> characters)
    {
        var list = new List<PathDetail>();
        foreach (var c in characters)
            if (c.Path.Count > 0) list.Add(new PathDetail(c.Id, c.Path));
        return list;
    }

    // ---- Stats (19) ----

    public static void WriteStats(NetWriter w, StringTableWriter strings, StatsDetail v)
    {
        CommonCodec.WriteId(w, v.Id);
        w.WriteByte((byte)((v.Skills.HasValue ? 1 : 0) | (v.Hud is not null ? 2 : 0) | (v.Hud?.Extended is not null ? 4 : 0)));
        if (v.Skills is { } s)
            foreach (float f in (float[])[s.Attack, s.Defence, s.Dodge, s.Toughness, s.Strength, s.Athletics]) w.WriteSingle(PlayerWire.F(f));
        PlayerWire.WriteFloats(w, v.AllSkills);
        PlayerWire.WriteFloats(w, v.SkillXpMultipliers);
        if (v.Hud is { } hud)
        {
            w.WriteSingle(PlayerWire.F(hud.Blood));
            w.WriteSingle(PlayerWire.F(hud.BloodCapacity));
            w.WriteSingle(PlayerWire.F(hud.Bleeding));
            w.WriteSingle(PlayerWire.F(hud.Encumbrance));
            int lines = Math.Min(hud.Stats.Count, NetLimits.MaxListItems);
            w.WriteVarUInt((uint)lines);
            for (int i = 0; i < lines; i++)
            {
                PlayerWire.Name(w, strings, hud.Stats[i].Label);
                w.WriteSingle(PlayerWire.F(hud.Stats[i].Value));
                w.WriteSingle(PlayerWire.F(hud.Stats[i].Base));
            }
            if (hud.Extended is { } e)
                foreach (float f in (float[])[e.AthleticsXp, e.StrengthXp, e.ToughnessXp, e.KoPoint, e.RunSpeed, e.RunValue, e.HungerRate]) w.WriteSingle(PlayerWire.F(f));
        }
        int bounties = Math.Min(v.Bounties.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)bounties);
        for (int i = 0; i < bounties; i++)
        {
            var b = v.Bounties[i];
            PlayerWire.Name(w, strings, b.Faction);
            w.WriteVarInt(b.Amount);
            PlayerWire.Text(w, strings, b.Crimes);
            w.WriteDouble(double.IsNaN(b.HoursLeft) ? 0 : b.HoursLeft);
        }
    }

    public static StatsDetail ReadStats(ref NetReader r, StringTableReader strings)
    {
        var id = CommonCodec.ReadId(ref r);
        byte flags = r.ReadByte();
        if ((flags & ~7) != 0 || ((flags & 4) != 0 && (flags & 2) == 0)) throw new NetFormatException($"Unknown stats flags {flags}.");
        SkillSummary? skills = (flags & 1) != 0 ? new SkillSummary(r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite()) : null;
        var all = PlayerWire.ReadFloats(ref r);
        var multipliers = PlayerWire.ReadFloats(ref r);
        HudView? hud = null;
        if ((flags & 2) != 0)
        {
            float blood = r.ReadFinite(), capacity = r.ReadFinite(), bleeding = r.ReadFinite(), encumbrance = r.ReadFinite();
            int lines = r.ReadCount(NetLimits.MaxListItems);
            var stats = new HudStatLine[lines];
            for (int i = 0; i < lines; i++)
            {
                string label = PlayerWire.ReadName(ref r, strings);
                stats[i] = new HudStatLine(label, r.ReadFinite(), r.ReadFinite());
            }
            ExtendedStats? extended = (flags & 4) != 0
                ? new ExtendedStats(r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite())
                : null;
            hud = new HudView(blood, capacity, bleeding, encumbrance, stats, extended);
        }
        int count = r.ReadCount(NetLimits.MaxListItems);
        var bounties = new BountyLine[count];
        for (int i = 0; i < count; i++)
        {
            string faction = PlayerWire.ReadName(ref r, strings);
            int amount = r.ReadVarInt32();
            string crimes = PlayerWire.ReadText(ref r, strings);
            bounties[i] = new BountyLine(faction, amount, crimes, r.ReadDouble());
        }
        return new StatsDetail(id, skills, all, multipliers, hud, bounties);
    }

    // ---- Health (20) ----

    public static void WriteHealth(NetWriter w, StringTableWriter strings, HealthDetail v)
    {
        CommonCodec.WriteId(w, v.Id);
        int count = Math.Min(v.Parts.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var p = v.Parts[i];
            PlayerWire.Name(w, strings, p.Id);
            PlayerWire.Name(w, strings, p.Name);
            w.WriteSingle(PlayerWire.F(p.Flesh));
            w.WriteSingle(PlayerWire.F(p.Stun));
            w.WriteSingle(PlayerWire.F(p.Bandage));
            w.WriteSingle(PlayerWire.F(p.Rig));
            w.WriteSingle(PlayerWire.F(p.Maximum));
            w.WriteBool(p.Robotic);
        }
    }

    public static HealthDetail ReadHealth(ref NetReader r, StringTableReader strings)
    {
        var id = CommonCodec.ReadId(ref r);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var parts = new HealthPartView[count];
        for (int i = 0; i < count; i++)
        {
            string partId = PlayerWire.ReadName(ref r, strings), name = PlayerWire.ReadName(ref r, strings);
            float flesh = r.ReadFinite(), stun = r.ReadFinite(), bandage = r.ReadFinite(), rig = r.ReadFinite(), max = r.ReadFinite();
            parts[i] = new HealthPartView(partId, name, flesh, stun, bandage, rig, max, r.ReadBool());
        }
        return new HealthDetail(id, parts);
    }

    // ---- Inventory (21) ----

    public static void WriteInventory(NetWriter w, StringTableWriter strings, InventoryDetail v)
    {
        CommonCodec.WriteId(w, v.Id);
        int lines = Math.Min(v.Inventory.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)lines);
        for (int i = 0; i < lines; i++) PlayerWire.Name(w, strings, v.Inventory[i]);
        w.WriteBool(v.ItemGrid is not null);
        if (v.ItemGrid is not { } grid) return;
        w.WriteVarInt(grid.Width);
        w.WriteVarInt(grid.Height);
        w.WriteVarInt(grid.BagWidth);
        w.WriteVarInt(grid.BagHeight);
        w.WriteSingle(PlayerWire.F(grid.TotalWeight));
        CommonCodec.WriteNullableSingle(w, grid.CarryCapacity is { } carry ? PlayerWire.F(carry) : null);
        int count = Math.Min(grid.Items.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var item = grid.Items[i];
            w.WriteVarInt(item.Index);
            PlayerWire.Name(w, strings, item.Record);
            PlayerWire.Name(w, strings, item.Name);
            PlayerWire.Name(w, strings, item.Section);
            w.WriteVarInt(item.X);
            w.WriteVarInt(item.Y);
            w.WriteVarInt(item.Width);
            w.WriteVarInt(item.Height);
            w.WriteVarInt(item.Quantity);
            w.WriteSingle(PlayerWire.F(item.Weight));
            w.WriteSingle(PlayerWire.F(item.Quality));
            PlayerWire.Name(w, strings, item.Material);
            CommonCodec.WriteNullableSingle(w, item.Charge is { } charge ? PlayerWire.F(charge) : null);
            PlayerWire.Name(w, strings, item.Company);
            w.WriteVarInt(item.Level);
            w.WriteSingle(PlayerWire.F(item.Charges));
            w.WriteSingle(PlayerWire.F(item.MaxCharges));
            w.WriteVarInt(item.StolenFrom);
        }
    }

    public static InventoryDetail ReadInventory(ref NetReader r, StringTableReader strings)
    {
        var id = CommonCodec.ReadId(ref r);
        int lineCount = r.ReadCount(NetLimits.MaxListItems);
        var lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++) lines[i] = PlayerWire.ReadName(ref r, strings);
        if (!r.ReadBool()) return new InventoryDetail(id, null, lines);
        int width = r.ReadVarInt32(), height = r.ReadVarInt32(), bagWidth = r.ReadVarInt32(), bagHeight = r.ReadVarInt32();
        float totalWeight = r.ReadFinite();
        float? carry = CommonCodec.ReadNullableSingle(ref r);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var items = new InventoryItemView[count];
        for (int i = 0; i < count; i++)
        {
            int index = r.ReadVarInt32();
            string record = PlayerWire.ReadName(ref r, strings), name = PlayerWire.ReadName(ref r, strings), section = PlayerWire.ReadName(ref r, strings);
            int x = r.ReadVarInt32(), y = r.ReadVarInt32(), w = r.ReadVarInt32(), h = r.ReadVarInt32(), quantity = r.ReadVarInt32();
            float weight = r.ReadFinite(), quality = r.ReadFinite();
            string material = PlayerWire.ReadName(ref r, strings);
            float? charge = CommonCodec.ReadNullableSingle(ref r);
            string company = PlayerWire.ReadName(ref r, strings);
            int level = r.ReadVarInt32();
            float charges = r.ReadFinite(), maxCharges = r.ReadFinite();
            items[i] = new InventoryItemView(index, record, name, section, x, y, w, h, quantity, weight, quality)
                { Material = material, Charge = charge, Company = company, Level = level, Charges = charges, MaxCharges = maxCharges, StolenFrom = r.ReadVarInt32() };
        }
        return new InventoryDetail(id, new InventoryView(width, height, bagWidth, bagHeight, items) { CarryCapacity = carry, TotalWeight = totalWeight }, lines);
    }

    // ---- Path (22): waypoints at 1/64 ----

    public static void WritePath(NetWriter w, StringTableWriter strings, PathDetail v)
    {
        CommonCodec.WriteId(w, v.Id);
        int count = Math.Min(v.Path.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            w.WriteVarInt(Quant.Q64(v.Path[i].X));
            w.WriteVarInt(Quant.Q64(v.Path[i].Y));
            w.WriteVarInt(Quant.Q64(v.Path[i].Z));
        }
    }

    public static PathDetail ReadPath(ref NetReader r, StringTableReader strings)
    {
        var id = CommonCodec.ReadId(ref r);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var path = new Vector3[count];
        for (int i = 0; i < count; i++) path[i] = new Vector3(Quant.FromQ64(r.ReadVarInt32()), Quant.FromQ64(r.ReadVarInt32()), Quant.FromQ64(r.ReadVarInt32()));
        return new PathDetail(id, path);
    }
}
