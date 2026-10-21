using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Net.Replication;

/// <summary>
/// The host half of one client's <see cref="NetChannel.Views"/> channel (docs/net.md, "Interest, delta, replicator and mirror"): one sync per
/// <see cref="ViewKind"/> (N5/N6 codecs) and the look definitions, all through one reliable string table kept for the connection. Each changed view goes
/// out as <c>0x30 | Epoch u16 | kind | ViewSeq | Tick | body</c>; Buildings, GroundItems and Machines append their revision (zigzag) after the body. A
/// Buildings or GroundItems revision bump with no list change sends an empty keyed body, so the client's revision follows. Change detection is the syncs':
/// a source reference equal to the last one sent is skipped, else canonical bytes are compared. A view a codec cannot encode
/// (<see cref="ArgumentException"/>) or that would exceed <see cref="NetLimits.MaxReliableMessage"/> is withdrawn (writer and string table rolled back)
/// and logged once.
/// </summary>
internal sealed class ViewSender
{
    readonly WorldSingleSync<HeaderView> header = new(ViewCodecs.HeaderCodec, TimeSpan.Zero);
    readonly WorldSingleSync<IReadOnlyList<TownKnowledge>> towns = new(ViewCodecs.TownsCodec, TimeSpan.FromSeconds(1));
    readonly KeyedViewSync<int, CampView> camps = ViewCodecs.CampSync();
    readonly KeyedViewSync<int, SquadView> squads = ViewCodecs.SquadSync();
    readonly KeyedViewSync<string, BuildingSnapshot> buildings = ViewCodecs.BuildingsSync();
    readonly KeyedViewSync<int, GroundItemSnapshot> groundItems = ViewCodecs.GroundItemsSync();
    readonly KeyedViewSync<string, ContainerSpot> containers = ViewCodecs.ContainersSync();
    readonly KeyedViewSync<string, DoorPick> doorPicks = ViewCodecs.DoorPicksSync();
    readonly WorldMappedSync<IReadOnlyDictionary<(string Building, int Door), DoorView>, (string, int), DoorEntry> doors = ViewCodecs.DoorsSync();
    readonly WorldMappedSync<IReadOnlySet<string>, string, string> unlocked = ViewCodecs.UnlockedSync();
    readonly WorldSingleSync<BuildingDetail?> inspected = ViewCodecs.InspectedSync();
    readonly KeyedViewSync<string, MachineSnapshot> machines = ViewCodecs.MachinesSync();
    readonly ResearchViewSync research = ViewCodecs.ResearchSync();
    readonly WorldMappedSync<IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>, string, CraftEntry> crafts = ViewCodecs.CraftsSync();
    readonly WorldSingleSync<TradeView?> trade = new(ViewCodecs.TradeCodec, TimeSpan.Zero);
    readonly ConversationViewWriter conversation = new();
    readonly WorldSingleSync<DialogueRequest?> request = new(ViewCodecs.DialogueRequestCodec, TimeSpan.Zero);
    readonly KeyedViewSync<CharacterId, SpeechBubble> speech = ViewCodecs.SpeechSync();
    readonly KeyedViewSync<CharacterId, StatsDetail> stats = ViewCodecs.StatsSync();
    readonly KeyedViewSync<CharacterId, HealthDetail> health = ViewCodecs.HealthSync();
    readonly KeyedViewSync<CharacterId, InventoryDetail> inventory = ViewCodecs.InventorySync();
    readonly KeyedViewSync<CharacterId, PathDetail> path = ViewCodecs.PathSync();
    readonly WorldSingleSync<RelationsView> relations = new(ViewCodecs.RelationsCodec, TimeSpan.Zero);
    readonly WorldSingleSync<IReadOnlySet<CharacterId>> talkable = new(ViewCodecs.TalkableCodec, TimeSpan.Zero);
    readonly WorldSingleSync<ProspectReport?> prospected = new(ViewCodecs.ProspectedCodec, TimeSpan.Zero);

    readonly StringTableWriter strings = new(true);
    readonly NetWriter message = new(1024);
    readonly HashSet<ViewKind> failed = [];
    readonly HashSet<CharacterId> spoken = [];
    HeaderView? lastHeader;
    RelationsView? lastRelations;
    long buildingsRevision = long.MinValue, groundItemsRevision = long.MinValue;

    public Action<string>? Log { get; set; }

    /// <summary>The ViewSeq of the last <c>ViewUpdate</c> sent (0: none yet); it grows by one per message for the whole connection.</summary>
    public uint ViewSeq { get; private set; }

    /// <summary>Bytes of every view and look message sent so far.</summary>
    public int SentBytes { get; private set; }

    /// <summary>Every view is sent again at the next <see cref="Send"/>, as the client's decoders start empty at a new epoch.</summary>
    public void Reset()
    {
        header.Reset(); towns.Reset(); camps.Reset(); squads.Reset(); buildings.Reset(); groundItems.Reset(); containers.Reset(); doorPicks.Reset();
        doors.Reset(); unlocked.Reset(); inspected.Reset(); machines.Reset(); research.Reset(); crafts.Reset(); trade.Reset(); conversation.Reset();
        request.Reset(); speech.Reset(); stats.Reset(); health.Reset(); inventory.Reset(); path.Reset(); relations.Reset(); talkable.Reset();
        prospected.Reset();
        lastHeader = null;
        lastRelations = null;
        buildingsRevision = groundItemsRevision = long.MinValue;
    }

    /// <summary>Sends every view of <paramref name="view"/> the client does not hold yet; returns whether anything went out.</summary>
    public bool Send(WorldSnapshot view, IReadOnlySet<CharacterId> interest, ushort epoch, TimeSpan now, IPacketSink sink)
    {
        bool any = false;
        for (var kind = ViewKind.Header; kind <= ViewKind.Prospected; kind++)
        {
            message.Reset();
            message.WriteByte((byte)MessageType.ViewUpdate);
            message.WriteUInt16(epoch);
            message.WriteByte((byte)kind);
            message.WriteVarUInt(ViewSeq + 1);
            message.WriteVarInt(view.Tick);
            int mark = strings.Mark();
            bool written;
            try
            {
                written = Body(kind, view, interest, now, message);
            }
            catch (ArgumentException e)
            {
                strings.Rollback(mark);
                Note(kind, $"view {kind} not sent: {e.Message}");
                continue;
            }
            if (!written) continue;
            if (message.Length > NetLimits.MaxReliableMessage)
            {
                strings.Rollback(mark);
                ResetKind(kind);
                Note(kind, $"view {kind} not sent: {message.Length} bytes exceed {NetLimits.MaxReliableMessage}");
                continue;
            }
            sink.Send(NetChannel.Views, message.Written);
            SentBytes += message.Length;
            ViewSeq++;
            any = true;
        }
        return any;
    }

    /// <summary>Sends a <c>LookDefinition</c>; false (logged) when the codec refuses the recipe.</summary>
    public bool SendLook(uint lookId, AppearanceRecipe recipe, IPacketSink sink)
    {
        message.Reset();
        int mark = strings.Mark();
        try
        {
            LookCodec.WriteDefinition(message, strings, lookId, recipe);
        }
        catch (ArgumentException e)
        {
            strings.Rollback(mark);
            Log?.Invoke($"look {lookId} not sent: {e.Message}");
            return false;
        }
        if (message.Length > NetLimits.MaxReliableMessage)
        {
            strings.Rollback(mark);
            Log?.Invoke($"look {lookId} not sent: {message.Length} bytes exceed {NetLimits.MaxReliableMessage}");
            return false;
        }
        sink.Send(NetChannel.Views, message.Written);
        SentBytes += message.Length;
        return true;
    }

    void Note(ViewKind kind, string text)
    {
        if (failed.Add(kind)) Log?.Invoke(text);
    }

    bool Body(ViewKind kind, WorldSnapshot view, IReadOnlySet<CharacterId> interest, TimeSpan now, NetWriter w)
    {
        var s = strings;
        switch (kind)
        {
            case ViewKind.Header:
                var h = HeaderView.From(view);
                if (h == lastHeader) h = lastHeader;
                lastHeader = h;
                return header.Encode(h, now, w, s);
            case ViewKind.Towns: return towns.Encode(view.Towns, now, w, s);
            case ViewKind.Camps: return camps.Encode(view.Camps, false, now, w, s);
            case ViewKind.Squads: return squads.Encode(view.PlayerSquads, false, now, w, s);
            case ViewKind.Buildings:
                return Revised(buildings.Encode(view.Buildings, view.BuildingsRevision != buildingsRevision, now, w, s), view.BuildingsRevision, ref buildingsRevision, w);
            case ViewKind.GroundItems:
                return Revised(groundItems.Encode(view.GroundItems, view.GroundItemsRevision != groundItemsRevision, now, w, s), view.GroundItemsRevision, ref groundItemsRevision, w);
            case ViewKind.Containers: return containers.Encode(view.Containers, false, now, w, s);
            case ViewKind.DoorPicks: return doorPicks.Encode(view.DoorPicks, false, now, w, s);
            case ViewKind.Doors: return doors.Encode(view.Doors, false, now, w, s);
            case ViewKind.Unlocked: return unlocked.Encode(view.UnlockedBuildings, false, now, w, s);
            case ViewKind.Inspected: return inspected.Encode(view.InspectedBuilding, now, w, s);
            case ViewKind.Machines:
                if (!machines.Encode(view.Machines, false, now, w, s)) return false;
                w.WriteVarInt(view.MachinesVersion);
                return true;
            case ViewKind.Research: return research.Encode(view.Research, false, now, w, s);
            case ViewKind.Crafts: return crafts.Encode(view.Crafts, false, now, w, s);
            case ViewKind.Trade: return trade.Encode(view.Trade, now, w, s);
            case ViewKind.Conversation: return conversation.Encode(view.Conversation, w, s);
            case ViewKind.DialogueRequest: return request.Encode(view.DialogueRequest, now, w, s);
            case ViewKind.Speech:
                var speakers = new List<SpeechBubble>(view.Speech.Count);   // a new list each time: the sync skips a list reference it already sent
                spoken.Clear();
                foreach (var bubble in view.Speech)
                    if (interest.Contains(bubble.Speaker) && spoken.Add(bubble.Speaker)) speakers.Add(bubble);
                return speech.Encode(speakers, false, now, w, s);
            case ViewKind.Stats: return stats.Encode(Interested(ViewCodecs.StatsOf(view.Characters), d => d.Id, interest), false, now, w, s);
            case ViewKind.Health: return health.Encode(Interested(ViewCodecs.HealthOf(view.Characters), d => d.Id, interest), false, now, w, s);
            case ViewKind.Inventory: return inventory.Encode(Interested(ViewCodecs.InventoryOf(view.Characters), d => d.Id, interest), false, now, w, s);
            case ViewKind.Path: return path.Encode(Interested(ViewCodecs.PathOf(view.Characters), d => d.Id, interest), false, now, w, s);
            case ViewKind.Relations:
                if (lastRelations is null || !ReferenceEquals(lastRelations.Values, view.Relations) || lastRelations.Revision != view.RelationsRevision)
                    lastRelations = RelationsView.From(view);
                return relations.Encode(lastRelations, now, w, s);
            case ViewKind.Talkable: return talkable.Encode(view.Talkable, now, w, s);
            case ViewKind.Prospected: return prospected.Encode(view.Prospected, now, w, s);
            default: return false;
        }
    }

    /// <summary>Appends the revision to a sent body; a revision bump without a list change sends an empty keyed body (no order change, no upserts).</summary>
    static bool Revised(bool sent, long revision, ref long last, NetWriter w)
    {
        if (!sent)
        {
            if (revision == last) return false;
            w.WriteByte(0);
            w.WriteVarUInt(0);
        }
        w.WriteVarInt(revision);
        last = revision;
        return true;
    }

    static List<T> Interested<T>(List<T> details, Func<T, CharacterId> id, IReadOnlySet<CharacterId> interest)
    {
        details.RemoveAll(d => !interest.Contains(id(d)));
        return details;
    }

    void ResetKind(ViewKind kind)
    {
        switch (kind)
        {
            case ViewKind.Header: header.Reset(); lastHeader = null; break;
            case ViewKind.Towns: towns.Reset(); break;
            case ViewKind.Camps: camps.Reset(); break;
            case ViewKind.Squads: squads.Reset(); break;
            case ViewKind.Buildings: buildings.Reset(); buildingsRevision = long.MinValue; break;
            case ViewKind.GroundItems: groundItems.Reset(); groundItemsRevision = long.MinValue; break;
            case ViewKind.Containers: containers.Reset(); break;
            case ViewKind.DoorPicks: doorPicks.Reset(); break;
            case ViewKind.Doors: doors.Reset(); break;
            case ViewKind.Unlocked: unlocked.Reset(); break;
            case ViewKind.Inspected: inspected.Reset(); break;
            case ViewKind.Machines: machines.Reset(); break;
            case ViewKind.Research: research.Reset(); break;
            case ViewKind.Crafts: crafts.Reset(); break;
            case ViewKind.Trade: trade.Reset(); break;
            case ViewKind.Conversation: conversation.Reset(); break;
            case ViewKind.DialogueRequest: request.Reset(); break;
            case ViewKind.Speech: speech.Reset(); break;
            case ViewKind.Stats: stats.Reset(); break;
            case ViewKind.Health: health.Reset(); break;
            case ViewKind.Inventory: inventory.Reset(); break;
            case ViewKind.Path: path.Reset(); break;
            case ViewKind.Relations: relations.Reset(); lastRelations = null; break;
            case ViewKind.Talkable: talkable.Reset(); break;
            case ViewKind.Prospected: prospected.Reset(); break;
        }
    }
}
