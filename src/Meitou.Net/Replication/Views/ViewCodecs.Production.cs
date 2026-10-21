using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Net.Replication;

/// <summary>
/// The production views (docs/net.md, "World and production views"): Machines (12), Research (13, a header plus the available researches as a keyed
/// view) and Crafts (14, one entry per bench site). The shared helpers are <c>ViewCodecs.WorldWire</c>.
/// </summary>
public static partial class ViewCodecs
{
    /// <summary>Kind 12 item: a machine of a complete building, or an open container (key <see cref="MachineSnapshot.Site"/>).</summary>
    public static ItemCodec<MachineSnapshot> Machine { get; } = new(WriteMachine, ReadMachine);

    /// <summary>Kind 13 item: one researchable entry (key <see cref="ResearchOption.Id"/>).</summary>
    public static ItemCodec<ResearchOption> ResearchOptionItem { get; } = new(WriteResearchOption, ReadResearchOption);

    /// <summary>Kind 14 item: the craft options of one bench site.</summary>
    public static ItemCodec<CraftEntry> CraftItem { get; } = new(WriteCraftEntry, ReadCraftEntry);

    public static KeyedViewSync<string, MachineSnapshot> MachinesSync() => new(ViewKind.Machines, m => m.Site, Machine, MachinesInterval);
    public static KeyedViewDecoder<MachineSnapshot> MachinesDecoder() => new(Machine);

    /// <summary>Kind 13 host half over <c>WorldSnapshot.Research</c>.</summary>
    public static ResearchViewSync ResearchSync() => new(ResearchInterval);

    public static ResearchViewDecoder ResearchDecoder() => new();

    /// <summary>Kind 14 host half over <c>WorldSnapshot.Crafts</c>; the dictionary is flattened once per reference, in site order.</summary>
    public static WorldMappedSync<IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>, string, CraftEntry> CraftsSync() =>
        new(new KeyedViewSync<string, CraftEntry>(ViewKind.Crafts, c => c.Site, CraftItem, CraftsInterval), FlattenCrafts);

    public static WorldMappedDecoder<CraftEntry, IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>> CraftsDecoder() => new(CraftItem, BuildCrafts);

    static IReadOnlyList<CraftEntry> FlattenCrafts(IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> crafts)
    {
        var list = new List<CraftEntry>(Math.Min(crafts.Count, NetLimits.MaxViewItems));
        foreach (var (site, options) in crafts) list.Add(new CraftEntry(site, options));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Site, b.Site));
        return list;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> BuildCrafts(IReadOnlyList<CraftEntry> items)
    {
        var crafts = new Dictionary<string, IReadOnlyList<CraftOptionView>>(items.Count, StringComparer.Ordinal);
        foreach (var item in items) crafts[item.Site] = item.Options;
        return crafts;
    }

    // Machines

    static void WriteMachine(NetWriter w, StringTableWriter s, MachineSnapshot m)
    {
        WorldWire.Text(w, s, m.Site);
        WorldWire.Text(w, s, m.Record);
        int inputs = WorldWire.Count(m.Inputs.Count);
        w.WriteVarUInt((uint)inputs);
        for (int i = 0; i < inputs; i++) WriteSlot(w, s, m.Inputs[i]);
        w.WriteBool(m.Output is not null);
        if (m.Output is { } output) WriteSlot(w, s, output);
        w.WriteBool(m.Storage is not null);
        if (m.Storage is { } storage) WriteStore(w, s, storage);
        w.WriteBool(m.Farm is not null);
        if (m.Farm is { } farm) WriteFarm(w, s, farm);
        w.WriteByte(m.Status);
        w.WriteVarInt(m.Operators);
        w.WriteVarInt(m.MaxOperators);
        w.WriteVarInt(m.TechLevel);
        w.WriteByte(WorldWire.Pack(m.IsContainer, m.Operable, m.IsStorage, m.Craft is not null, m.Power is not null));
        if (m.Craft is { } craft) WriteCraft(w, s, craft);
        if (m.Power is { } power) WritePower(w, power);
        WorldWire.Float(w, m.Sound.Efficiency);
        w.WriteBool(m.Sound.Powered);
        CommonCodec.WriteId(w, m.Body);
        WorldWire.OptionalText(w, s, m.Title);
    }

    static MachineSnapshot ReadMachine(ref NetReader r, StringTableReader s)
    {
        string site = WorldWire.ReadText(ref r, s), record = WorldWire.ReadText(ref r, s);
        int inputCount = r.ReadCount(NetLimits.MaxListItems);
        var inputs = new MachineSlotView[inputCount];
        for (int i = 0; i < inputCount; i++) inputs[i] = ReadSlot(ref r, s);
        MachineSlotView? output = r.ReadBool() ? ReadSlot(ref r, s) : null;
        InventoryView? storage = r.ReadBool() ? ReadStore(ref r, s) : null;
        FarmView? farm = r.ReadBool() ? ReadFarm(ref r, s) : null;
        byte status = r.ReadByte();
        int operators = r.ReadVarInt32(), maxOperators = r.ReadVarInt32(), techLevel = r.ReadVarInt32();
        byte flags = WorldWire.ReadFlags(ref r, 31);
        CraftView? craft = (flags & 8) != 0 ? ReadCraft(ref r, s) : null;
        PowerView? power = (flags & 16) != 0 ? ReadPower(ref r) : null;
        var sound = new MachineSound(r.ReadFinite(), r.ReadBool());
        var body = CommonCodec.ReadId(ref r);
        string? title = WorldWire.ReadOptionalText(ref r, s);
        return new MachineSnapshot(site, record, inputs, output, storage, farm, status, operators, maxOperators)
        {
            Body = body,
            Title = title,
            IsContainer = (flags & 1) != 0,
            TechLevel = techLevel,
            Operable = (flags & 2) != 0,
            IsStorage = (flags & 4) != 0,
            Craft = craft,
            Power = power,
            Sound = sound,
        };
    }

    static void WriteSlot(NetWriter w, StringTableWriter s, MachineSlotView v)
    {
        WorldWire.Text(w, s, v.Item);
        WorldWire.Text(w, s, v.Name);
        w.WriteVarInt(v.Count);
        w.WriteVarInt(v.Capacity);
        WorldWire.Float(w, v.Fill);
    }

    static MachineSlotView ReadSlot(ref NetReader r, StringTableReader s) =>
        new(WorldWire.ReadText(ref r, s), WorldWire.ReadText(ref r, s), r.ReadVarInt32(), r.ReadVarInt32(), r.ReadFinite());

    static void WriteFarm(NetWriter w, StringTableWriter s, FarmView f)
    {
        WorldWire.Float(w, f.Growth);
        WorldWire.Float(w, f.Death);
        w.WriteVarInt(f.Harvested);
        w.WriteVarInt(f.Plants);
        w.WriteEnum(f.Status);
        WorldWire.Float(w, f.Fertility);
        WorldWire.Float(w, f.CropMatch);
        WorldWire.Float(w, f.Ripeness);
        int count = WorldWire.Count(f.PlantGrowth.Count);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) WorldWire.Float(w, f.PlantGrowth[i]);
    }

    static FarmView ReadFarm(ref NetReader r, StringTableReader s)
    {
        float growth = r.ReadFinite(), death = r.ReadFinite();
        int harvested = r.ReadVarInt32(), plants = r.ReadVarInt32();
        var status = r.ReadEnum<FarmStatus>();
        float fertility = r.ReadFinite(), cropMatch = r.ReadFinite(), ripeness = r.ReadFinite();
        int count = r.ReadCount(NetLimits.MaxListItems);
        var growths = new float[count];
        for (int i = 0; i < count; i++) growths[i] = r.ReadFinite();
        return new FarmView(growth, death, harvested, plants, status, fertility, cropMatch, ripeness) { PlantGrowth = growths };
    }

    static void WritePower(NetWriter w, PowerView p)
    {
        w.WriteEnum(p.Kind);
        WorldWire.Float(w, p.Demand);
        WorldWire.Float(w, p.Granted);
        WorldWire.Float(w, p.Usage);
        WorldWire.Float(w, p.Charge);
        WorldWire.Float(w, p.Store);
        w.WriteBool(p.Fueled);
    }

    static PowerView ReadPower(ref NetReader r) =>
        new(r.ReadEnum<PowerKind>(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadFinite(), r.ReadBool());

    static void WriteCraft(NetWriter w, StringTableWriter s, CraftView c)
    {
        int queue = WorldWire.Count(c.Queue.Count);
        w.WriteVarUInt((uint)queue);
        for (int i = 0; i < queue; i++)
        {
            var job = c.Queue[i];
            WorldWire.Text(w, s, job.Key);
            WorldWire.Text(w, s, job.Name);
            WorldWire.Float(w, job.Progress);
            WorldWire.Float(w, job.Hours);
        }
        w.WriteBool(c.Repeat);
        int done = WorldWire.Count(c.Done.Count);
        w.WriteVarUInt((uint)done);
        for (int i = 0; i < done; i++) WriteStoreItem(w, s, c.Done[i]);
    }

    static CraftView ReadCraft(ref NetReader r, StringTableReader s)
    {
        int queueCount = r.ReadCount(NetLimits.MaxListItems);
        var queue = new (string Key, string Name, float Progress, float Hours)[queueCount];
        for (int i = 0; i < queueCount; i++) queue[i] = (WorldWire.ReadText(ref r, s), WorldWire.ReadText(ref r, s), r.ReadFinite(), r.ReadFinite());
        bool repeat = r.ReadBool();
        int doneCount = r.ReadCount(NetLimits.MaxListItems);
        var done = new InventoryItemView[doneCount];
        for (int i = 0; i < doneCount; i++) done[i] = ReadStoreItem(ref r, s);
        return new CraftView(queue, repeat, done);
    }

    // A machine's storage grid. N6 writes the same InventoryView for kind 21; these are private to the machine item.

    static void WriteStore(NetWriter w, StringTableWriter s, InventoryView v)
    {
        w.WriteVarInt(v.Width);
        w.WriteVarInt(v.Height);
        w.WriteVarInt(v.BagWidth);
        w.WriteVarInt(v.BagHeight);
        int count = WorldWire.Count(v.Items.Count);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) WriteStoreItem(w, s, v.Items[i]);
        CommonCodec.WriteNullableSingle(w, v.CarryCapacity is { } c && float.IsFinite(c) ? c : null);
        WorldWire.Float(w, v.TotalWeight);
    }

    static InventoryView ReadStore(ref NetReader r, StringTableReader s)
    {
        int width = r.ReadVarInt32(), height = r.ReadVarInt32(), bagWidth = r.ReadVarInt32(), bagHeight = r.ReadVarInt32();
        int count = r.ReadCount(NetLimits.MaxListItems);
        var items = new InventoryItemView[count];
        for (int i = 0; i < count; i++) items[i] = ReadStoreItem(ref r, s);
        float? capacity = CommonCodec.ReadNullableSingle(ref r);
        return new InventoryView(width, height, bagWidth, bagHeight, items) { CarryCapacity = capacity, TotalWeight = r.ReadFinite() };
    }

    static void WriteStoreItem(NetWriter w, StringTableWriter s, InventoryItemView v)
    {
        w.WriteVarInt(v.Index);
        WorldWire.Text(w, s, v.Record);
        WorldWire.Text(w, s, v.Name);
        WorldWire.Text(w, s, v.Section);
        w.WriteVarInt(v.X);
        w.WriteVarInt(v.Y);
        w.WriteVarInt(v.Width);
        w.WriteVarInt(v.Height);
        w.WriteVarInt(v.Quantity);
        WorldWire.Float(w, v.Weight);
        WorldWire.Float(w, v.Quality);
        WorldWire.Text(w, s, v.Material);
        CommonCodec.WriteNullableSingle(w, v.Charge is { } c && float.IsFinite(c) ? c : null);
        WorldWire.Text(w, s, v.Company);
        w.WriteVarInt(v.Level);
        WorldWire.Float(w, v.Charges);
        WorldWire.Float(w, v.MaxCharges);
        w.WriteVarInt(v.StolenFrom);
    }

    static InventoryItemView ReadStoreItem(ref NetReader r, StringTableReader s)
    {
        int index = r.ReadVarInt32();
        string record = WorldWire.ReadText(ref r, s), name = WorldWire.ReadText(ref r, s), section = WorldWire.ReadText(ref r, s);
        int x = r.ReadVarInt32(), y = r.ReadVarInt32(), width = r.ReadVarInt32(), height = r.ReadVarInt32(), quantity = r.ReadVarInt32();
        float weight = r.ReadFinite(), quality = r.ReadFinite();
        string material = WorldWire.ReadText(ref r, s);
        float? charge = CommonCodec.ReadNullableSingle(ref r);
        string company = WorldWire.ReadText(ref r, s);
        int level = r.ReadVarInt32();
        float charges = r.ReadFinite(), maxCharges = r.ReadFinite();
        return new InventoryItemView(index, record, name, section, x, y, width, height, quantity, weight, quality)
        {
            Material = material,
            Charge = charge,
            Company = company,
            Level = level,
            Charges = charges,
            MaxCharges = maxCharges,
            StolenFrom = r.ReadVarInt32(),
        };
    }

    // Research

    static void WriteResearchHeader(NetWriter w, StringTableWriter s, ResearchSnapshot v)
    {
        w.WriteVarInt(v.TechLevel);
        w.WriteVarInt(v.BenchLevel);
        int queue = WorldWire.Count(v.Queue.Count);
        w.WriteVarUInt((uint)queue);
        for (int i = 0; i < queue; i++)
        {
            var q = v.Queue[i];
            WorldWire.Text(w, s, q.Id);
            WorldWire.Text(w, s, q.Name);
            WorldWire.Float(w, q.Progress);
        }
        WorldWire.Float(w, v.Rate);
        w.WriteVarInt(v.Finished);
    }

    static void WriteResearchOption(NetWriter w, StringTableWriter s, ResearchOption o)
    {
        WorldWire.Text(w, s, o.Id);
        WorldWire.Text(w, s, o.Name);
        WorldWire.Text(w, s, o.Category);
        w.WriteVarInt(o.Level);
        WorldWire.Float(w, o.Hours);
        w.WriteByte(WorldWire.Pack(o.Affordable, o.BenchTooLow));
        WorldWire.Float(w, o.Progress);
        WorldWire.Literal(w, s, o.Description);
        int cost = WorldWire.Count(o.Cost.Count);
        w.WriteVarUInt((uint)cost);
        for (int i = 0; i < cost; i++)
        {
            WorldWire.Text(w, s, o.Cost[i].Name);
            w.WriteVarInt(o.Cost[i].Count);
        }
    }

    static ResearchOption ReadResearchOption(ref NetReader r, StringTableReader s)
    {
        string id = WorldWire.ReadText(ref r, s), name = WorldWire.ReadText(ref r, s), category = WorldWire.ReadText(ref r, s);
        int level = r.ReadVarInt32();
        float hours = r.ReadFinite();
        byte flags = WorldWire.ReadFlags(ref r, 3);
        float progress = r.ReadFinite();
        string description = WorldWire.ReadText(ref r, s, NetLimits.MaxString);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var cost = new (string Name, int Count)[count];
        for (int i = 0; i < count; i++) cost[i] = (WorldWire.ReadText(ref r, s), r.ReadVarInt32());
        return new ResearchOption(id, name, category, level, hours, (flags & 1) != 0, (flags & 2) != 0, progress) { Description = description, Cost = cost };
    }

    // Crafts

    static void WriteCraftEntry(NetWriter w, StringTableWriter s, CraftEntry e)
    {
        WorldWire.Text(w, s, e.Site);
        int count = WorldWire.Count(e.Options.Count);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var o = e.Options[i];
            WorldWire.Text(w, s, o.Key);
            WorldWire.Text(w, s, o.Name);
            w.WriteEnum(o.Kind);
            w.WriteVarInt(o.Level);
            WorldWire.Float(w, o.Hours);
            int inputs = WorldWire.Count(o.Inputs.Count);
            w.WriteVarUInt((uint)inputs);
            for (int j = 0; j < inputs; j++)
            {
                WorldWire.Text(w, s, o.Inputs[j].Name);
                WorldWire.Float(w, o.Inputs[j].PerItem);
            }
            w.WriteBool(o.Unlocked);
        }
    }

    static CraftEntry ReadCraftEntry(ref NetReader r, StringTableReader s)
    {
        string site = WorldWire.ReadText(ref r, s);
        int count = r.ReadCount(NetLimits.MaxListItems);
        var options = new CraftOptionView[count];
        for (int i = 0; i < count; i++)
        {
            string key = WorldWire.ReadText(ref r, s), name = WorldWire.ReadText(ref r, s);
            var kind = r.ReadEnum<CraftKind>();
            int level = r.ReadVarInt32();
            float hours = r.ReadFinite();
            int inputCount = r.ReadCount(NetLimits.MaxListItems);
            var inputs = new (string Name, float PerItem)[inputCount];
            for (int j = 0; j < inputCount; j++) inputs[j] = (WorldWire.ReadText(ref r, s), r.ReadFinite());
            options[i] = new CraftOptionView(key, name, kind, level, hours, inputs, r.ReadBool());
        }
        return new CraftEntry(site, options);
    }

    internal static ResearchSnapshot ReadResearch(ref NetReader r, StringTableReader s, KeyedViewDecoder<ResearchOption> options)
    {
        int techLevel = r.ReadVarInt32(), benchLevel = r.ReadVarInt32();
        int queueCount = r.ReadCount(NetLimits.MaxListItems);
        var queue = new (string Id, string Name, float Progress)[queueCount];
        for (int i = 0; i < queueCount; i++) queue[i] = (WorldWire.ReadText(ref r, s), WorldWire.ReadText(ref r, s), r.ReadFinite());
        float rate = r.ReadFinite();
        int finished = r.ReadVarInt32();
        options.Apply(ref r, s);
        if (techLevel == ResearchSnapshot.Empty.TechLevel && benchLevel == 0 && queueCount == 0 && rate == 0 && finished == 0 && options.Items.Count == 0)
            return ResearchSnapshot.Empty;
        return new ResearchSnapshot(techLevel, benchLevel, queue, rate, options.Items) { Finished = finished };
    }

    internal static void WriteResearch(NetWriter w, StringTableWriter s, ResearchSnapshot v) => WriteResearchHeader(w, s, v);
}

/// <summary>One bench site's craft options as a view item (key <see cref="Site"/>).</summary>
public sealed record CraftEntry(string Site, IReadOnlyList<CraftOptionView> Options);

/// <summary>
/// Host half of kind 13: the research header (tech and bench level, queue, rate, finished count) followed by the available researches as a keyed body.
/// The header is written whenever anything is sent; it alone is sent when it changed and <c>minInterval</c> has passed. A source reference equal to the
/// last one fully sent is skipped.
/// </summary>
public sealed class ResearchViewSync(TimeSpan minInterval)
{
    readonly KeyedViewSync<string, ResearchOption> options = new(ViewKind.Research, o => o.Id, ViewCodecs.ResearchOptionItem, minInterval);
    readonly NetWriter canonical = new();
    readonly StringTableWriter literals = StringTableWriter.CreateLiteralOnly();
    byte[]? sentHeader;
    TimeSpan lastSent;

    public void Reset()
    {
        options.Reset();
        sentHeader = null;
    }

    /// <summary>Writes the body to <paramref name="w"/> and returns true, or returns false and writes nothing.</summary>
    /// <param name="structural">Go now even if only items changed.</param>
    public bool Encode(ResearchSnapshot now, bool structural, TimeSpan time, NetWriter w, StringTableWriter s)
    {
        canonical.Reset();
        ViewCodecs.WriteResearch(canonical, literals, now);
        bool headerChanged = sentHeader is null || !canonical.Written.SequenceEqual(sentHeader);
        bool headerDue = headerChanged && (sentHeader is null || structural || time < lastSent || time - lastSent >= minInterval);

        int start = w.Length, mark = s.Mark();
        ViewCodecs.WriteResearch(w, s, now);
        bool listSent = options.Encode(now.Available, structural, time, w, s);
        if (!listSent)
        {
            if (!headerDue)
            {
                w.Truncate(start);
                s.Rollback(mark);
                return false;
            }
            w.WriteByte(0);
            w.WriteVarUInt(0);
        }
        sentHeader = canonical.Written.ToArray();
        lastSent = time;
        return true;
    }
}

/// <summary>Client half of kind 13: <see cref="Value"/> is <see cref="ResearchSnapshot.Empty"/> until a body arrives (and again for an empty one).</summary>
public sealed class ResearchViewDecoder
{
    readonly KeyedViewDecoder<ResearchOption> options = new(ViewCodecs.ResearchOptionItem);

    public ResearchSnapshot Value { get; private set; } = ResearchSnapshot.Empty;

    public void Reset()
    {
        options.Reset();
        Value = ResearchSnapshot.Empty;
    }

    /// <summary>Applies one body. A malformed body throws <see cref="NetFormatException"/> and leaves <see cref="Value"/> as it was.</summary>
    public void Apply(ref NetReader r, StringTableReader strings) => Value = ViewCodecs.ReadResearch(ref r, strings, options);
}
