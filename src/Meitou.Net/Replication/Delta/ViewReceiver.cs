using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Replication;

/// <summary>
/// The client half of the <see cref="NetChannel.Views"/> channel (docs/net.md, "Interest, delta, replicator and mirror"). A <c>ViewUpdate</c> is decoded
/// on arrival (the decoders and the string table follow the stream) and queued with its Tick; <see cref="Commit"/> applies the queued values whose Tick
/// the presented snapshot has reached, in arrival order, onto <see cref="Committed"/> and the per-character detail maps. An update of another epoch is
/// still decoded, into throwaway decoders, so the string definitions it carries are not lost, and then dropped. A <c>LookDefinition</c> goes straight to
/// the look resolver.
/// </summary>
internal sealed class ViewReceiver(ILookResolver looks)
{
    /// <summary>One set of decoders: the epoch's live ones, or throwaway ones for an update of another epoch.</summary>
    sealed class Decoders
    {
        public readonly WorldSingleDecoder<HeaderView> Header = new(ViewCodecs.HeaderCodec);
        public readonly WorldSingleDecoder<IReadOnlyList<TownKnowledge>> Towns = new(ViewCodecs.TownsCodec);
        public readonly KeyedViewDecoder<CampView> Camps = new(ViewCodecs.CampCodec);
        public readonly KeyedViewDecoder<SquadView> Squads = new(ViewCodecs.SquadCodec);
        public readonly KeyedViewDecoder<BuildingSnapshot> Buildings = ViewCodecs.BuildingsDecoder();
        public readonly KeyedViewDecoder<GroundItemSnapshot> GroundItems = ViewCodecs.GroundItemsDecoder();
        public readonly KeyedViewDecoder<ContainerSpot> Containers = ViewCodecs.ContainersDecoder();
        public readonly KeyedViewDecoder<DoorPick> DoorPicks = ViewCodecs.DoorPicksDecoder();
        public readonly WorldMappedDecoder<DoorEntry, IReadOnlyDictionary<(string Building, int Door), DoorView>> Doors = ViewCodecs.DoorsDecoder();
        public readonly WorldMappedDecoder<string, IReadOnlySet<string>> Unlocked = ViewCodecs.UnlockedDecoder();
        public readonly WorldSingleDecoder<BuildingDetail?> Inspected = ViewCodecs.InspectedDecoder();
        public readonly KeyedViewDecoder<MachineSnapshot> Machines = ViewCodecs.MachinesDecoder();
        public readonly ResearchViewDecoder Research = ViewCodecs.ResearchDecoder();
        public readonly WorldMappedDecoder<CraftEntry, IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>> Crafts = ViewCodecs.CraftsDecoder();
        public readonly WorldSingleDecoder<TradeView?> Trade = new(ViewCodecs.TradeCodec);
        public readonly ConversationViewReader Conversation = new();
        public readonly WorldSingleDecoder<DialogueRequest?> Request = new(ViewCodecs.DialogueRequestCodec);
        public readonly KeyedViewDecoder<SpeechBubble> Speech = new(ViewCodecs.SpeechCodec);
        public readonly KeyedViewDecoder<StatsDetail> Stats = new(ViewCodecs.StatsCodec);
        public readonly KeyedViewDecoder<HealthDetail> Health = new(ViewCodecs.HealthCodec);
        public readonly KeyedViewDecoder<InventoryDetail> Inventory = new(ViewCodecs.InventoryCodec);
        public readonly KeyedViewDecoder<PathDetail> Path = new(ViewCodecs.PathCodec);
        public readonly WorldSingleDecoder<RelationsView> Relations = new(ViewCodecs.RelationsCodec);
        public readonly WorldSingleDecoder<IReadOnlySet<CharacterId>> Talkable = new(ViewCodecs.TalkableCodec);
        public readonly WorldSingleDecoder<ProspectReport?> Prospected = new(ViewCodecs.ProspectedCodec);

        /// <summary>Decodes one body of <paramref name="kind"/>; returns the view's value after it and the revision that follows the body (0 for kinds without one).</summary>
        public (object? Value, long Revision) Decode(ViewKind kind, ref NetReader r, StringTableReader s)
        {
            switch (kind)
            {
                case ViewKind.Header: Header.Apply(ref r, s); return (Header.Value, 0);
                case ViewKind.Towns: Towns.Apply(ref r, s); return (Towns.Value, 0);
                case ViewKind.Camps: Camps.Apply(ref r, s); return (Camps.Items, 0);
                case ViewKind.Squads: Squads.Apply(ref r, s); return (Squads.Items, 0);
                case ViewKind.Buildings: Buildings.Apply(ref r, s); return (Buildings.Items, r.ReadVarInt());
                case ViewKind.GroundItems: GroundItems.Apply(ref r, s); return (GroundItems.Items, r.ReadVarInt());
                case ViewKind.Containers: Containers.Apply(ref r, s); return (Containers.Items, 0);
                case ViewKind.DoorPicks: DoorPicks.Apply(ref r, s); return (DoorPicks.Items, 0);
                case ViewKind.Doors: Doors.Apply(ref r, s); return (Doors.Value, 0);
                case ViewKind.Unlocked: Unlocked.Apply(ref r, s); return (Unlocked.Value, 0);
                case ViewKind.Inspected: Inspected.Apply(ref r, s); return (Inspected.Value, 0);
                case ViewKind.Machines: Machines.Apply(ref r, s); return (Machines.Items, r.ReadVarInt());
                case ViewKind.Research: Research.Apply(ref r, s); return (Research.Value, 0);
                case ViewKind.Crafts: Crafts.Apply(ref r, s); return (Crafts.Value, 0);
                case ViewKind.Trade: Trade.Apply(ref r, s); return (Trade.Value, 0);
                case ViewKind.Conversation: return (Conversation.Read(ref r, s), 0);
                case ViewKind.DialogueRequest: Request.Apply(ref r, s); return (Request.Value, 0);
                case ViewKind.Speech: Speech.Apply(ref r, s); return (Speech.Items, 0);
                case ViewKind.Stats: Stats.Apply(ref r, s); return (Stats.Items, 0);
                case ViewKind.Health: Health.Apply(ref r, s); return (Health.Items, 0);
                case ViewKind.Inventory: Inventory.Apply(ref r, s); return (Inventory.Items, 0);
                case ViewKind.Path: Path.Apply(ref r, s); return (Path.Items, 0);
                case ViewKind.Relations: Relations.Apply(ref r, s); return (Relations.Value, 0);
                case ViewKind.Talkable: Talkable.Apply(ref r, s); return (Talkable.Value, 0);
                case ViewKind.Prospected: Prospected.Apply(ref r, s); return (Prospected.Value, 0);
                default: throw new NetFormatException($"Unknown view kind {(byte)kind}.");
            }
        }
    }

    readonly record struct Pending(long Tick, ViewKind Kind, object? Value, long Revision);

    static readonly WorldSnapshot Blank = new(-1, []);

    readonly StringTableReader strings = new();
    readonly Queue<Pending> pending = new();
    Decoders decoders = new();

    /// <summary>The committed views as a snapshot (its Tick, characters and projectiles are not meaningful).</summary>
    public WorldSnapshot Committed { get; private set; } = Blank;

    public IReadOnlyDictionary<CharacterId, StatsDetail> Stats { get; private set; } = new Dictionary<CharacterId, StatsDetail>();
    public IReadOnlyDictionary<CharacterId, HealthDetail> Health { get; private set; } = new Dictionary<CharacterId, HealthDetail>();
    public IReadOnlyDictionary<CharacterId, InventoryDetail> Inventory { get; private set; } = new Dictionary<CharacterId, InventoryDetail>();
    public IReadOnlyDictionary<CharacterId, PathDetail> Path { get; private set; } = new Dictionary<CharacterId, PathDetail>();

    /// <summary>The ViewSeq of the newest <c>ViewUpdate</c> received.</summary>
    public uint ViewSeq { get; private set; }

    /// <summary>Forgets the views of the old epoch (the string table and the looks stay: they belong to the connection).</summary>
    public void Reset()
    {
        decoders = new Decoders();
        pending.Clear();
        Committed = Blank;
        Stats = new Dictionary<CharacterId, StatsDetail>();
        Health = new Dictionary<CharacterId, HealthDetail>();
        Inventory = new Dictionary<CharacterId, InventoryDetail>();
        Path = new Dictionary<CharacterId, PathDetail>();
    }

    /// <summary>Takes one packet of the Views channel; a malformed one throws <see cref="NetFormatException"/> (the caller disconnects).</summary>
    public void Receive(ReadOnlySpan<byte> packet, ushort epoch)
    {
        var r = new NetReader(packet);
        if (packet.Length > 0 && packet[0] == (byte)MessageType.LookDefinition)
        {
            var recipe = LookCodec.ReadDefinition(ref r, strings, out uint lookId);
            r.ExpectEnd();
            looks.Define(lookId, recipe);
            return;
        }
        if (r.ReadByte() != (byte)MessageType.ViewUpdate) throw new NetFormatException("Not a view message.");
        ushort messageEpoch = r.ReadUInt16();
        var kind = (ViewKind)r.ReadByte();
        if (kind is < ViewKind.Header or > ViewKind.Prospected) throw new NetFormatException($"Unknown view kind {(byte)kind}.");
        uint viewSeq = r.ReadVarUInt32();
        long tick = r.ReadVarInt();
        if (messageEpoch != epoch)
        {
            // Stale: decode into throwaway state so the definitions in it reach the table, then drop it.
            try { new Decoders().Decode(kind, ref r, strings); }
            catch (NetFormatException) { }
            return;
        }
        var (value, revision) = decoders.Decode(kind, ref r, strings);
        r.ExpectEnd();
        ViewSeq = viewSeq;
        pending.Enqueue(new Pending(tick, kind, value, revision));
    }

    /// <summary>Commits the queued values with a Tick at most <paramref name="tick"/>; returns whether any was committed.</summary>
    public bool Commit(long tick)
    {
        bool any = false;
        while (pending.TryPeek(out var p) && p.Tick <= tick)
        {
            pending.Dequeue();
            Apply(p);
            any = true;
        }
        return any;
    }

    void Apply(Pending p)
    {
        var c = Committed;
        Committed = p.Kind switch
        {
            ViewKind.Header => ((HeaderView)p.Value!).ApplyTo(c),
            ViewKind.Towns => c with { Towns = (IReadOnlyList<TownKnowledge>?)p.Value ?? [] },
            ViewKind.Camps => c with { Camps = (IReadOnlyList<CampView>)p.Value! },
            ViewKind.Squads => c with { PlayerSquads = (IReadOnlyList<SquadView>)p.Value! },
            ViewKind.Buildings => c with { Buildings = (IReadOnlyList<BuildingSnapshot>)p.Value!, BuildingsRevision = p.Revision },
            ViewKind.GroundItems => c with { GroundItems = (IReadOnlyList<GroundItemSnapshot>)p.Value!, GroundItemsRevision = p.Revision },
            ViewKind.Containers => c with { Containers = (IReadOnlyList<ContainerSpot>)p.Value! },
            ViewKind.DoorPicks => c with { DoorPicks = (IReadOnlyList<DoorPick>)p.Value! },
            ViewKind.Doors => c with { Doors = (IReadOnlyDictionary<(string Building, int Door), DoorView>)p.Value! },
            ViewKind.Unlocked => c with { UnlockedBuildings = (IReadOnlySet<string>)p.Value! },
            ViewKind.Inspected => c with { InspectedBuilding = (BuildingDetail?)p.Value },
            ViewKind.Machines => c with { Machines = (IReadOnlyList<MachineSnapshot>)p.Value!, MachinesVersion = p.Revision },
            ViewKind.Research => c with { Research = (ResearchSnapshot)p.Value! },
            ViewKind.Crafts => c with { Crafts = (IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>)p.Value! },
            ViewKind.Trade => c with { Trade = (TradeView?)p.Value },
            ViewKind.Conversation => c with { Conversation = (ConversationView?)p.Value },
            ViewKind.DialogueRequest => c with { DialogueRequest = (DialogueRequest?)p.Value },
            ViewKind.Speech => c with { Speech = (IReadOnlyList<SpeechBubble>)p.Value! },
            ViewKind.Relations => ((RelationsView)p.Value!).ApplyTo(c),
            ViewKind.Talkable => c with { Talkable = (IReadOnlySet<CharacterId>)p.Value! },
            ViewKind.Prospected => c with { Prospected = (ProspectReport?)p.Value },
            _ => c,
        };
        switch (p.Kind)
        {
            case ViewKind.Stats: Stats = ById((IReadOnlyList<StatsDetail>)p.Value!, d => d.Id); break;
            case ViewKind.Health: Health = ById((IReadOnlyList<HealthDetail>)p.Value!, d => d.Id); break;
            case ViewKind.Inventory: Inventory = ById((IReadOnlyList<InventoryDetail>)p.Value!, d => d.Id); break;
            case ViewKind.Path: Path = ById((IReadOnlyList<PathDetail>)p.Value!, d => d.Id); break;
        }
    }

    static Dictionary<CharacterId, T> ById<T>(IReadOnlyList<T> items, Func<T, CharacterId> id)
    {
        var map = new Dictionary<CharacterId, T>(items.Count);
        foreach (var item in items) map[id(item)] = item;
        return map;
    }
}
