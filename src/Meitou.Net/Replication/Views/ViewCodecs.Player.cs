using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Replication;

/// <summary>
/// Kind 1 (<see cref="ViewKind.Header"/>): the small per-viewer scalars of a <see cref="WorldSnapshot"/> that are not in a snapshot part (docs/net.md, "Player and detail views").
/// </summary>
public sealed record HeaderView(SquadAi SquadAi, int ActiveSquad, int Viewer, int PlayerFaction, long Money, bool InCreation, IReadOnlyList<string> CreationRaces)
{
    /// <summary>The header fields of <paramref name="snapshot"/>.</summary>
    public static HeaderView From(WorldSnapshot snapshot) =>
        new(snapshot.SquadAi, snapshot.ActiveSquad, snapshot.Viewer, snapshot.PlayerFaction, snapshot.Money, snapshot.InCreation, snapshot.CreationRaces);

    /// <summary>A copy of <paramref name="snapshot"/> with these header fields.</summary>
    public WorldSnapshot ApplyTo(WorldSnapshot snapshot) =>
        snapshot with { SquadAi = SquadAi, ActiveSquad = ActiveSquad, Viewer = Viewer, PlayerFaction = PlayerFaction, Money = Money, InCreation = InCreation, CreationRaces = CreationRaces };
}

/// <summary>Kind 23 (<see cref="ViewKind.Relations"/>): the player faction's relation towards each faction and the revision it was built at.</summary>
public sealed record RelationsView(long Revision, IReadOnlyList<float> Values)
{
    public static RelationsView From(WorldSnapshot snapshot) => new(snapshot.RelationsRevision, snapshot.Relations);

    public WorldSnapshot ApplyTo(WorldSnapshot snapshot) => snapshot with { Relations = Values, RelationsRevision = Revision };
}

/// <summary>
/// The codecs of the player views, kinds 1-4, 15-18 and 23-25, as <see cref="ItemCodec{T}"/> bodies for <c>KeyedViewSync</c>/<c>KeyedViewDecoder</c> (keyed kinds) or direct
/// use (single kinds); the conversation (16) is stateful and has its own writer and reader; the detail kinds 19-22 are in <c>ViewCodecs.Detail.cs</c> (docs/net.md, "Player and
/// detail views"). Every writer clamps strings to their byte limits and lists to their counts and replaces non-finite floats with 0, so a reader never refuses what a writer produced.
/// </summary>
public static partial class ViewCodecs
{
    /// <summary>The <c>WorldSnapshot</c> members the player views carry (the wire coverage test reads these).</summary>
    public static readonly string[] PlayerCovered =
    [
        nameof(WorldSnapshot.SquadAi), nameof(WorldSnapshot.ActiveSquad), nameof(WorldSnapshot.Viewer), nameof(WorldSnapshot.PlayerFaction), nameof(WorldSnapshot.Money),
        nameof(WorldSnapshot.InCreation), nameof(WorldSnapshot.CreationRaces), nameof(WorldSnapshot.Towns), nameof(WorldSnapshot.Camps), nameof(WorldSnapshot.PlayerSquads),
        nameof(WorldSnapshot.Trade), nameof(WorldSnapshot.Conversation), nameof(WorldSnapshot.DialogueRequest), nameof(WorldSnapshot.Speech), nameof(WorldSnapshot.Relations),
        nameof(WorldSnapshot.RelationsRevision), nameof(WorldSnapshot.Talkable), nameof(WorldSnapshot.Prospected),
    ];

    public static ItemCodec<HeaderView> HeaderCodec { get; } = new(WriteHeader, ReadHeader);
    public static ItemCodec<IReadOnlyList<TownKnowledge>> TownsCodec { get; } = new(WriteTowns, ReadTowns);
    public static ItemCodec<CampView> CampCodec { get; } = new(WriteCamp, ReadCamp);
    public static ItemCodec<SquadView> SquadCodec { get; } = new(WriteSquad, ReadSquad);
    public static ItemCodec<TradeView?> TradeCodec { get; } = new(WriteTrade, ReadTrade);
    public static ItemCodec<DialogueRequest?> DialogueRequestCodec { get; } = new(WriteDialogueRequest, ReadDialogueRequest);
    public static ItemCodec<SpeechBubble> SpeechCodec { get; } = new(WriteSpeech, ReadSpeech);
    public static ItemCodec<RelationsView> RelationsCodec { get; } = new(WriteRelations, ReadRelations);
    public static ItemCodec<IReadOnlySet<CharacterId>> TalkableCodec { get; } = new(WriteTalkable, ReadTalkable);
    public static ItemCodec<ProspectReport?> ProspectedCodec { get; } = new(WriteProspected, ReadProspected);

    /// <summary>Kind 3, keyed by the wild site, at most once a second.</summary>
    public static KeyedViewSync<int, CampView> CampSync() => new(ViewKind.Camps, c => c.Site, CampCodec, TimeSpan.FromSeconds(1));

    /// <summary>Kind 4, keyed by the squad id, sent as soon as it changes.</summary>
    public static KeyedViewSync<int, SquadView> SquadSync() => new(ViewKind.Squads, s => s.Id, SquadCodec, TimeSpan.Zero);

    /// <summary>Kind 18, keyed by the speaker, sent as soon as it changes. The source must list a speaker once.</summary>
    public static KeyedViewSync<CharacterId, SpeechBubble> SpeechSync() => new(ViewKind.Speech, b => b.Speaker, SpeechCodec, TimeSpan.Zero);

    // ---- Header (1) ----

    public static void WriteHeader(NetWriter w, StringTableWriter strings, HeaderView v)
    {
        w.WriteEnum(v.SquadAi);
        w.WriteVarInt(v.ActiveSquad);
        w.WriteVarInt(v.Viewer);
        w.WriteVarInt(v.PlayerFaction);
        w.WriteVarInt(v.Money);
        w.WriteBool(v.InCreation);
        int count = Math.Min(v.CreationRaces.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) PlayerWire.Name(w, strings, v.CreationRaces[i]);
    }

    public static HeaderView ReadHeader(ref NetReader r, StringTableReader strings)
    {
        var ai = r.ReadEnum<SquadAi>();
        int active = r.ReadVarInt32(), viewer = r.ReadVarInt32(), faction = r.ReadVarInt32();
        long money = r.ReadVarInt();
        bool creation = r.ReadBool();
        int count = r.ReadCount(NetLimits.MaxListItems);
        var races = new string[count];
        for (int i = 0; i < count; i++) races[i] = PlayerWire.ReadName(ref r, strings);
        return new HeaderView(ai, active, viewer, faction, money, creation, races);
    }

    // ---- Towns (2): one knowledge byte per placed town ----

    public static void WriteTowns(NetWriter w, StringTableWriter strings, IReadOnlyList<TownKnowledge> towns)
    {
        int count = Math.Min(towns.Count, NetLimits.MaxViewItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) w.WriteEnum(towns[i]);
    }

    public static IReadOnlyList<TownKnowledge> ReadTowns(ref NetReader r, StringTableReader strings)
    {
        int count = r.ReadCount(NetLimits.MaxViewItems);
        var towns = new TownKnowledge[count];
        for (int i = 0; i < count; i++) towns[i] = r.ReadEnum<TownKnowledge>();
        return towns;
    }

    // ---- Camps (3) ----

    public static void WriteCamp(NetWriter w, StringTableWriter strings, CampView v)
    {
        w.WriteVarInt(v.Site);
        PlayerWire.Name(w, strings, v.TownId);
        PlayerWire.Name(w, strings, v.Name);
        PlayerWire.Name(w, strings, v.FactionId);
        w.WriteByte((byte)((v.Wildlife ? 1 : 0) | (v.Position.HasValue ? 2 : 0)));
        w.WriteEnum(v.Known);
        if (v.Position is { } position) PlayerWire.Vector(w, position);
    }

    public static CampView ReadCamp(ref NetReader r, StringTableReader strings)
    {
        int site = r.ReadVarInt32();
        string town = PlayerWire.ReadName(ref r, strings), name = PlayerWire.ReadName(ref r, strings), faction = PlayerWire.ReadName(ref r, strings);
        byte flags = r.ReadByte();
        if ((flags & ~3) != 0) throw new NetFormatException($"Unknown camp flags {flags}.");
        var known = r.ReadEnum<TownKnowledge>();
        Vector3? position = (flags & 2) != 0 ? CommonCodec.ReadVector3(ref r) : null;
        return new CampView(site, town, name, faction, (flags & 1) != 0, position, known);
    }

    // ---- Squads (4) ----

    public static void WriteSquad(NetWriter w, StringTableWriter strings, SquadView v)
    {
        w.WriteVarInt(v.Id);
        PlayerWire.Name(w, strings, v.Name);
        PlayerWire.Ids(w, v.Members);
    }

    public static SquadView ReadSquad(ref NetReader r, StringTableReader strings)
    {
        int id = r.ReadVarInt32();
        string name = PlayerWire.ReadName(ref r, strings);
        return new SquadView(id, name, CommonCodec.ReadIds(ref r));
    }

    // ---- Trade (15) ----

    public static void WriteTrade(NetWriter w, StringTableWriter strings, TradeView? v)
    {
        w.WriteBool(v is not null);
        if (v is null) return;
        CommonCodec.WriteId(w, v.Customer);
        CommonCodec.WriteId(w, v.Trader);
        PlayerWire.Name(w, strings, v.TraderName);
        w.WriteVarInt(v.ShopMoney);
        w.WriteVarInt(v.PlayerMoney);
        w.WriteVarInt(v.StockWidth);
        w.WriteVarInt(v.StockHeight);
        w.WriteBool(v.BuysStolen);
        WriteTradeItems(w, strings, v.Stock);
        WriteTradeItems(w, strings, v.Goods);
    }

    public static TradeView? ReadTrade(ref NetReader r, StringTableReader strings)
    {
        if (!r.ReadBool()) return null;
        var customer = CommonCodec.ReadId(ref r);
        var trader = CommonCodec.ReadId(ref r);
        string traderName = PlayerWire.ReadName(ref r, strings);
        long shop = r.ReadVarInt(), player = r.ReadVarInt();
        int width = r.ReadVarInt32(), height = r.ReadVarInt32();
        bool stolen = r.ReadBool();
        var stock = ReadTradeItems(ref r, strings);
        var goods = ReadTradeItems(ref r, strings);
        return new TradeView(customer, trader, traderName, shop, player, width, height, stock, goods, stolen);
    }

    static void WriteTradeItems(NetWriter w, StringTableWriter strings, IReadOnlyList<TradeItemView> items)
    {
        int count = Math.Min(items.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            var v = items[i];
            w.WriteVarInt(v.Index);
            PlayerWire.Name(w, strings, v.Record);
            PlayerWire.Name(w, strings, v.Name);
            PlayerWire.Name(w, strings, v.Section);
            w.WriteVarInt(v.X);
            w.WriteVarInt(v.Y);
            w.WriteVarInt(v.Width);
            w.WriteVarInt(v.Height);
            w.WriteVarInt(v.Quantity);
            w.WriteSingle(PlayerWire.F(v.Quality));
            w.WriteVarInt(v.Price);
            w.WriteVarInt(v.UnitPrice);
            PlayerWire.Name(w, strings, v.Material);
            w.WriteVarInt(v.Value);
            w.WriteVarInt(v.SellValue);
            w.WriteSingle(PlayerWire.F(v.Weight));
            w.WriteByte((byte)((v.Stolen ? 1 : 0) | (v.Charge.HasValue ? 2 : 0) | (v.FenceChance.HasValue ? 4 : 0) | (v.FenceOdds.HasValue ? 8 : 0)));
            if (v.Charge is { } charge) w.WriteSingle(PlayerWire.F(charge));
            if (v.FenceChance is { } chance) w.WriteSingle(PlayerWire.F(chance));
            if (v.FenceOdds is { } odds) w.WriteSingle(PlayerWire.F(odds));
            PlayerWire.Name(w, strings, v.Company);
            w.WriteVarInt(v.Level);
            w.WriteSingle(PlayerWire.F(v.Charges));
            w.WriteSingle(PlayerWire.F(v.MaxCharges));
            w.WriteSingle(PlayerWire.F(v.PriceFactor));
            w.WriteVarInt(v.StolenFrom);
        }
    }

    static TradeItemView[] ReadTradeItems(ref NetReader r, StringTableReader strings)
    {
        int count = r.ReadCount(NetLimits.MaxListItems);
        var items = new TradeItemView[count];
        for (int i = 0; i < count; i++)
        {
            int index = r.ReadVarInt32();
            string record = PlayerWire.ReadName(ref r, strings), name = PlayerWire.ReadName(ref r, strings), section = PlayerWire.ReadName(ref r, strings);
            int x = r.ReadVarInt32(), y = r.ReadVarInt32(), width = r.ReadVarInt32(), height = r.ReadVarInt32(), quantity = r.ReadVarInt32();
            float quality = r.ReadFinite();
            long price = r.ReadVarInt(), unit = r.ReadVarInt();
            string material = PlayerWire.ReadName(ref r, strings);
            long value = r.ReadVarInt(), sell = r.ReadVarInt();
            float weight = r.ReadFinite();
            byte flags = r.ReadByte();
            if ((flags & ~15) != 0) throw new NetFormatException($"Unknown trade item flags {flags}.");
            float? charge = (flags & 2) != 0 ? r.ReadFinite() : null;
            float? chance = (flags & 4) != 0 ? r.ReadFinite() : null;
            float? odds = (flags & 8) != 0 ? r.ReadFinite() : null;
            string company = PlayerWire.ReadName(ref r, strings);
            int level = r.ReadVarInt32();
            float charges = r.ReadFinite(), maxCharges = r.ReadFinite(), factor = r.ReadFinite();
            items[i] = new TradeItemView(index, record, name, section, x, y, width, height, quantity, quality, price, unit, (flags & 1) != 0)
            {
                Material = material, Charge = charge, Value = value, SellValue = sell, Weight = weight, FenceChance = chance, FenceOdds = odds,
                Company = company, Level = level, Charges = charges, MaxCharges = maxCharges, PriceFactor = factor, StolenFrom = r.ReadVarInt32(),
            };
        }
        return items;
    }

    // ---- DialogueRequest (17) ----

    public static void WriteDialogueRequest(NetWriter w, StringTableWriter strings, DialogueRequest? v)
    {
        w.WriteBool(v is not null);
        if (v is null) return;
        w.WriteVarInt(v.Id);
        w.WriteEnum(v.Kind);
        CommonCodec.WriteId(w, v.Player);
        CommonCodec.WriteId(w, v.Npc);
    }

    public static DialogueRequest? ReadDialogueRequest(ref NetReader r, StringTableReader strings)
    {
        if (!r.ReadBool()) return null;
        long id = r.ReadVarInt();
        var kind = r.ReadEnum<DialogueRequestKind>();
        var player = CommonCodec.ReadId(ref r);
        var npc = CommonCodec.ReadId(ref r);
        return new DialogueRequest(id, kind, player, npc);
    }

    // ---- Speech (18): lines repeat, so the text is interned ----

    public static void WriteSpeech(NetWriter w, StringTableWriter strings, SpeechBubble v)
    {
        CommonCodec.WriteId(w, v.Speaker);
        PlayerWire.Name(w, strings, v.Text);
        w.WriteBool(v.Large);
    }

    public static SpeechBubble ReadSpeech(ref NetReader r, StringTableReader strings)
    {
        var speaker = CommonCodec.ReadId(ref r);
        string text = PlayerWire.ReadName(ref r, strings);
        return new SpeechBubble(speaker, text, r.ReadBool());
    }

    // ---- Relations (23) ----

    public static void WriteRelations(NetWriter w, StringTableWriter strings, RelationsView v)
    {
        w.WriteVarInt(v.Revision);
        int count = Math.Min(v.Values.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) w.WriteSingle(PlayerWire.F(v.Values[i]));
    }

    public static RelationsView ReadRelations(ref NetReader r, StringTableReader strings)
    {
        long revision = r.ReadVarInt();
        int count = r.ReadCount(NetLimits.MaxListItems);
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = r.ReadFinite();
        return new RelationsView(revision, values);
    }

    // ---- Talkable (24): the id set, in slot order so equal sets are equal bytes ----

    public static void WriteTalkable(NetWriter w, StringTableWriter strings, IReadOnlySet<CharacterId> ids) =>
        PlayerWire.Ids(w, ids.OrderBy(i => i.Slot).ThenBy(i => i.Generation).ToList());

    public static IReadOnlySet<CharacterId> ReadTalkable(ref NetReader r, StringTableReader strings) => new HashSet<CharacterId>(CommonCodec.ReadIds(ref r));

    // ---- Prospected (25) ----

    public static void WriteProspected(NetWriter w, StringTableWriter strings, ProspectReport? v)
    {
        w.WriteBool(v is not null);
        if (v is null) return;
        w.WriteVarInt(v.Serial);
        PlayerWire.Vector(w, v.Position);
        PlayerWire.Name(w, strings, v.Prospector);
        w.WriteVarInt(v.Skill);
        PlayerWire.Name(w, strings, v.Zone);
        w.WriteVarInt(v.Altitude);
        w.WriteVarInt(v.AreaMetres);
        WriteProspectLines(w, strings, v.Environment);
        WriteProspectLines(w, strings, v.Resources);
    }

    public static ProspectReport? ReadProspected(ref NetReader r, StringTableReader strings)
    {
        if (!r.ReadBool()) return null;
        long serial = r.ReadVarInt();
        var position = CommonCodec.ReadVector3(ref r);
        string prospector = PlayerWire.ReadName(ref r, strings);
        int skill = r.ReadVarInt32();
        string zone = PlayerWire.ReadName(ref r, strings);
        int altitude = r.ReadVarInt32(), area = r.ReadVarInt32();
        var environment = ReadProspectLines(ref r, strings);
        var resources = ReadProspectLines(ref r, strings);
        return new ProspectReport(serial, position, prospector, skill, zone, altitude, area, environment, resources);
    }

    static void WriteProspectLines(NetWriter w, StringTableWriter strings, IReadOnlyList<ProspectLine> lines)
    {
        int count = Math.Min(lines.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++)
        {
            PlayerWire.Name(w, strings, lines[i].Name);
            w.WriteSingle(PlayerWire.F(lines[i].Value));
        }
    }

    static ProspectLine[] ReadProspectLines(ref NetReader r, StringTableReader strings)
    {
        int count = r.ReadCount(NetLimits.MaxListItems);
        var lines = new ProspectLine[count];
        for (int i = 0; i < count; i++)
        {
            string name = PlayerWire.ReadName(ref r, strings);
            lines[i] = new ProspectLine(name, r.ReadFinite());
        }
        return lines;
    }
}

/// <summary>
/// Kind 16 (<see cref="ViewKind.Conversation"/>), host half: one per client. The log only grows while a conversation runs, so the body sends how many leading log lines equal
/// the log last sent (<c>kept</c>) and then only the new lines. <see cref="Encode"/> skips an unchanged conversation. <see cref="Reset"/> on a new epoch or connection.
/// </summary>
public sealed class ConversationViewWriter
{
    ConversationView? last;
    bool sent;
    string[] lastLog = [];

    public void Reset()
    {
        last = null;
        sent = false;
        lastLog = [];
    }

    /// <summary>Writes the body and returns true, or returns false and writes nothing when the client already holds <paramref name="now"/> (the first call always writes, even for null).</summary>
    public bool Encode(ConversationView? now, NetWriter w, StringTableWriter strings)
    {
        if (sent && Same(last, now)) return false;
        w.WriteBool(now is not null);
        if (now is null)
        {
            lastLog = [];
        }
        else
        {
            CommonCodec.WriteId(w, now.Npc);
            CommonCodec.WriteId(w, now.Player);
            CommonCodec.WriteId(w, now.Speaker);
            PlayerWire.Name(w, strings, now.SpeakerName);
            PlayerWire.Name(w, strings, now.PlayerName);
            PlayerWire.Text(w, strings, now.Text);
            w.WriteVarInt(now.PlayerMoney);
            int choices = Math.Min(now.Choices.Count, NetLimits.MaxListItems);
            w.WriteVarUInt((uint)choices);
            for (int i = 0; i < choices; i++) PlayerWire.Text(w, strings, now.Choices[i]);

            int skip = Math.Max(0, now.Log.Count - NetLimits.MaxListItems);
            int total = now.Log.Count - skip;
            var log = new string[total];
            for (int i = 0; i < total; i++) log[i] = PlayerWire.Fit(now.Log[skip + i], NetLimits.MaxString);
            int kept = 0;
            while (kept < log.Length && kept < lastLog.Length && string.Equals(log[kept], lastLog[kept], StringComparison.Ordinal)) kept++;
            w.WriteVarUInt((uint)kept);
            w.WriteVarUInt((uint)(total - kept));
            for (int i = kept; i < total; i++) strings.Write(w, log[i], intern: false);
            lastLog = log;
        }
        last = now;
        sent = true;
        return true;
    }

    static bool Same(ConversationView? a, ConversationView? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Npc == b.Npc && a.Player == b.Player && a.Speaker == b.Speaker && a.SpeakerName == b.SpeakerName && a.PlayerName == b.PlayerName && a.Text == b.Text
            && a.PlayerMoney == b.PlayerMoney && a.Choices.SequenceEqual(b.Choices) && a.Log.SequenceEqual(b.Log);
    }
}

/// <summary>Kind 16, client half: applies the bodies <see cref="ConversationViewWriter"/> writes and remembers the log between them.</summary>
public sealed class ConversationViewReader
{
    string[] log = [];

    public void Reset() => log = [];

    /// <summary>Reads one body; a body that does not parse throws <see cref="NetFormatException"/> and leaves the remembered log as it was.</summary>
    public ConversationView? Read(ref NetReader r, StringTableReader strings)
    {
        if (!r.ReadBool())
        {
            log = [];
            return null;
        }
        var npc = CommonCodec.ReadId(ref r);
        var player = CommonCodec.ReadId(ref r);
        var speaker = CommonCodec.ReadId(ref r);
        string speakerName = PlayerWire.ReadName(ref r, strings), playerName = PlayerWire.ReadName(ref r, strings), text = PlayerWire.ReadText(ref r, strings);
        long money = r.ReadVarInt();
        int choiceCount = r.ReadCount(NetLimits.MaxListItems);
        var choices = new string[choiceCount];
        for (int i = 0; i < choiceCount; i++) choices[i] = PlayerWire.ReadText(ref r, strings);
        int kept = r.ReadCount(NetLimits.MaxListItems);
        if (kept > log.Length) throw new NetFormatException($"A conversation keeps {kept} log lines but the client holds {log.Length}.");
        int added = r.ReadCount(NetLimits.MaxListItems);
        if (kept + added > NetLimits.MaxListItems) throw new NetFormatException("A conversation log is over the list limit.");
        var next = new string[kept + added];
        Array.Copy(log, next, kept);
        for (int i = 0; i < added; i++) next[kept + i] = PlayerWire.ReadText(ref r, strings);
        log = next;
        return new ConversationView(npc, player, speaker, speakerName, playerName, text, choices, next, money);
    }
}

/// <summary>Shared writer-side clamps and reader helpers of the player and detail view codecs.</summary>
internal static class PlayerWire
{
    /// <summary>A string cut (at a character boundary) to at most <paramref name="maxBytes"/> UTF-8 bytes; null becomes "".</summary>
    public static string Fit(string? s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length * 3L <= maxBytes || Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        int bytes = 0, chars = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes) break;
            bytes += size;
            chars += rune.Utf16SequenceLength;
        }
        return s[..chars];
    }

    /// <summary>An interned name, cut to <see cref="NetLimits.MaxName"/> so every field that can refer to it accepts it.</summary>
    public static void Name(NetWriter w, StringTableWriter strings, string? s) => strings.Write(w, Fit(s, NetLimits.MaxName));

    /// <summary>Free text as a literal, cut to <see cref="NetLimits.MaxString"/>.</summary>
    public static void Text(NetWriter w, StringTableWriter strings, string? s) => strings.Write(w, Fit(s, NetLimits.MaxString), intern: false);

    public static string ReadName(ref NetReader r, StringTableReader strings) => strings.Read(ref r, NetLimits.MaxName) ?? "";

    public static string ReadText(ref NetReader r, StringTableReader strings) => strings.Read(ref r, NetLimits.MaxString) ?? "";

    /// <summary>The float, or 0 when it is NaN or infinite (readers refuse those).</summary>
    public static float F(float v) => float.IsFinite(v) ? v : 0;

    public static Vector3 Finite(Vector3 v) => new(F(v.X), F(v.Y), F(v.Z));

    public static void Vector(NetWriter w, Vector3 v) => CommonCodec.WriteVector3(w, Finite(v));

    /// <summary>An id list cut to <see cref="NetLimits.MaxIdList"/>.</summary>
    public static void Ids(NetWriter w, IReadOnlyList<CharacterId> ids) =>
        CommonCodec.WriteIds(w, ids.Count <= NetLimits.MaxIdList ? ids : ids.Take(NetLimits.MaxIdList).ToList());

    /// <summary>A float list cut to <see cref="NetLimits.MaxListItems"/>.</summary>
    public static void WriteFloats(NetWriter w, IReadOnlyList<float> values)
    {
        int count = Math.Min(values.Count, NetLimits.MaxListItems);
        w.WriteVarUInt((uint)count);
        for (int i = 0; i < count; i++) w.WriteSingle(F(values[i]));
    }

    public static float[] ReadFloats(ref NetReader r)
    {
        int count = r.ReadCount(NetLimits.MaxListItems);
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = r.ReadFinite();
        return values;
    }
}
