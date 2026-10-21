using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>
/// The client side of the world stream (docs/net.md, "Interest, delta, replicator and mirror"): it decodes the snapshot parts and views a
/// <see cref="ClientReplicator"/> sends and assembles full <see cref="WorldSnapshot"/>s from them.
/// <list type="bullet">
/// <item><b>Parts.</b> Each record decodes onto the state this client holds for that entity at the record's baseline Seq; the result is stored at the
/// part's Seq in a per-entity history (the newest state is always kept, older ones for 64 Seqs, and states older than a named baseline are dropped). A
/// record whose baseline is missing (or that does not decode) is skipped by its length, its slot asked for in full (<see cref="ClientFrame.Resync"/>),
/// and its part is not acked, so the host keeps its old baselines and redefines the part's strings. A part that does not parse is dropped whole.
/// Duplicates are ignored; parts may arrive in any order (one 64 Seqs older than the newest is ignored).</item>
/// <item><b>Removals</b> apply when nothing at their Seq or newer (nor a newer generation) is held for the slot, and are kept even for an entity not held
/// yet, so a late older part cannot bring it back; a new generation in a slot replaces the entity.</item>
/// <item><b>Assembly.</b> A Seq is ready when all its parts arrived or a newer Seq has a part. <see cref="TryAssemble"/> takes the newest ready Seq,
/// commits the views whose Tick it reached, and builds every entity's newest state at or before that Seq, characters by slot and projectiles by id, with
/// the detail views merged and looks resolved through the <see cref="ILookResolver"/> (a look not built yet keeps the character's last one).</item>
/// <item><b>Frames.</b> <see cref="WriteFrame"/> acks the newest 8 Seqs (redundantly) and lists the resync requests.</item>
/// </list>
/// Frame thread only.
/// </summary>
public sealed class ClientMirror(AnimationLibrary animations, ILookResolver looks)
{
    /// <summary>Seqs of history kept per entity besides its newest state (more than the host's baseline window).</summary>
    public const int HistorySeqs = 64;

    readonly record struct Entry(uint Seq, long Tick, CharacterId Id, CharacterWire? Wire);

    readonly record struct BoltEntry(uint Seq, long Tick, ProjectileWire? Wire);

    sealed class Slot
    {
        public readonly List<Entry> History = [];
        public CharacterId LookOwner = CharacterId.None;
        public CharacterAppearance? Look;
        // The snapshot last built for this slot and what it was built from.
        public CharacterWire? BuiltWire;
        public CharacterAppearance? BuiltLook;
        public object? BuiltStats, BuiltHealth, BuiltInventory, BuiltPath;
        public CharacterSnapshot? Built;
    }

    sealed class SeqInfo(long tick, int count)
    {
        public readonly long Tick = tick;
        public readonly int Count = count;
        public ulong Received, Acked;
        public bool Complete => Received == (Count == 64 ? ulong.MaxValue : (1UL << Count) - 1);
    }

    readonly record struct StagedCharacter(int Slot, uint BaseSeq, CharacterId Id, CharacterWire Wire);

    readonly record struct StagedBolt(int Id, uint BaseSeq, ProjectileWire Wire);

    readonly ViewReceiver views = new(looks);
    readonly StringTableReader strings = new();
    readonly SortedDictionary<int, Slot> slots = [];
    readonly SortedDictionary<int, List<BoltEntry>> bolts = [];
    readonly SortedDictionary<uint, SeqInfo> seqs = [];
    readonly Dictionary<int, uint> resync = [];
    readonly List<CharacterId> stagedRemovals = [];
    readonly List<int> stagedBoltRemovals = [];
    readonly List<StagedCharacter> stagedCharacters = [];
    readonly List<StagedBolt> stagedBolts = [];
    readonly List<int> scratchKeys = [];
    uint frameSeq, newestSeq, assembledSeq;
    bool assembled;
    long assembledTick;
    WorldSnapshot? last;

    public ushort Epoch { get; private set; }

    /// <summary>Parts received since the last <see cref="WriteFrame"/> (the client then sends a frame at once rather than on its timer).</summary>
    public bool HasNewParts { get; private set; }

    /// <summary>Parts dropped because they did not parse.</summary>
    public int DroppedParts { get; private set; }

    /// <summary>Records skipped (baseline missing or undecodable) and asked for in full.</summary>
    public int SkippedRecords { get; private set; }

    /// <summary>The newest Seq any part arrived for, and the ViewSeq of the newest view update.</summary>
    public uint NewestSeq => newestSeq;
    public uint ViewSeq => views.ViewSeq;

    public Action<string>? Log { get; set; }

    /// <summary>A new epoch: every entity, Seq, view and resync request is forgotten. The string tables and looks stay (they belong to the connection).</summary>
    public void Reset(ushort epoch)
    {
        Epoch = epoch;
        slots.Clear();
        bolts.Clear();
        seqs.Clear();
        resync.Clear();
        views.Reset();
        frameSeq = newestSeq = assembledSeq = 0;
        assembled = false;
        assembledTick = 0;
        last = null;
        HasNewParts = false;
    }

    /// <summary>
    /// Takes one packet. Snapshots: a part of another epoch is ignored and one that does not parse is dropped. Views: a malformed message throws
    /// <see cref="NetFormatException"/> (the reliable stream is lost; the caller disconnects).
    /// </summary>
    public void Receive(NetChannel channel, ReadOnlySpan<byte> packet)
    {
        switch (channel)
        {
            case NetChannel.Snapshots:
                try
                {
                    ReadPart(packet);
                }
                catch (NetFormatException e)
                {
                    DroppedParts++;
                    Log?.Invoke($"snapshot part dropped: {e.Message}");
                }
                break;
            case NetChannel.Views:
                views.Receive(packet, Epoch);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "The mirror takes the Snapshots and Views channels only.");
        }
    }

    /// <summary>
    /// Builds the newest snapshot when a newer Seq is ready or views were committed; false (and the last snapshot, or <see cref="WorldSnapshot.Empty"/>)
    /// when nothing changed.
    /// </summary>
    public bool TryAssemble(out WorldSnapshot snapshot)
    {
        uint target = 0;
        foreach (var (seq, info) in seqs.Reverse())
        {
            if (assembled && seq <= assembledSeq) break;
            if (seq == newestSeq && !info.Complete) continue;
            target = seq;
            break;
        }
        bool fresh = target != 0;
        if (fresh)
        {
            assembledSeq = target;
            assembledTick = seqs[target].Tick;
            assembled = true;
        }
        bool committed = assembled && views.Commit(assembledTick);
        if (!fresh && !committed)
        {
            snapshot = last ?? WorldSnapshot.Empty;
            return false;
        }
        Prune();
        snapshot = last = Build();
        return true;
    }

    /// <summary>The Tick of the newest state held for <paramref name="id"/> (-1: none), for per-entity interpolation; an entity not resent because it did not change keeps its older Tick.</summary>
    public long TickOf(CharacterId id)
    {
        if (!slots.TryGetValue(id.Slot, out var slot)) return -1;
        for (int i = slot.History.Count - 1; i >= 0; i--)
        {
            var e = slot.History[i];
            if (e.Id == id) return e.Wire is null ? -1 : e.Tick;
        }
        return -1;
    }

    /// <summary>Writes a <see cref="ClientFrame"/>: the newest 8 Seqs with their acked parts, the camera, and up to 32 resync requests.</summary>
    public void WriteFrame(NetWriter w, ViewState view)
    {
        var acks = new List<(uint Seq, ulong Parts)>(ClientFrame.MaxAcks);
        foreach (var (seq, info) in seqs.Reverse())
        {
            if (acks.Count == ClientFrame.MaxAcks) break;
            if (info.Acked != 0) acks.Add((seq, info.Acked));
        }
        var asks = new List<int>(Math.Min(resync.Count, ClientFrame.MaxResync));
        foreach (int key in resync.Keys)
        {
            if (asks.Count == ClientFrame.MaxResync) break;
            asks.Add(key);
        }
        ClientFrameCodec.Write(w, new ClientFrame(Epoch, ++frameSeq, acks, view, asks));
        HasNewParts = false;
    }

    void ReadPart(ReadOnlySpan<byte> packet)
    {
        var r = new NetReader(packet);
        if (r.ReadByte() != (byte)MessageType.SnapshotPart) throw new NetFormatException("Not a snapshot part.");
        ushort epoch = r.ReadUInt16();
        if (epoch != Epoch) return;
        uint seq = r.ReadVarUInt32();
        if (seq == 0) throw new NetFormatException("Seq 0 is never sent.");
        if (newestSeq >= HistorySeqs && seq <= newestSeq - HistorySeqs) return;   // older than any history kept: its states and removals are moot
        long tick = r.ReadVarInt();
        r.ReadVarUInt32();   // ViewSeq: the views sent before this part (informational)
        int index = r.ReadByte(), count = r.ReadByte();
        if (count == 0 || count > NetLimits.MaxParts || index >= count) throw new NetFormatException($"Part {index} of {count}.");
        if (seqs.TryGetValue(seq, out var known))
        {
            if (known.Tick != tick || known.Count != count) throw new NetFormatException($"Parts of Seq {seq} disagree on their header.");
            if ((known.Received & (1UL << index)) != 0) return;   // a duplicate
        }

        stagedRemovals.Clear();
        stagedBoltRemovals.Clear();
        stagedCharacters.Clear();
        stagedBolts.Clear();
        bool undecodable = false;

        int removals = r.ReadCount(NetLimits.MaxIdList);
        for (int i = 0; i < removals; i++) stagedRemovals.Add(CommonCodec.ReadId(ref r));
        int boltRemovals = r.ReadCount(NetLimits.MaxIdList);
        for (int i = 0; i < boltRemovals; i++) stagedBoltRemovals.Add(r.ReadVarInt32());

        int boltCount = r.ReadCount(NetLimits.MaxIdList);
        long previous = 0;
        for (int i = 0; i < boltCount; i++)
        {
            long id = previous + r.ReadVarInt();
            if (id is < 0 or > int.MaxValue) throw new NetFormatException($"Projectile id {id} is out of range.");
            previous = id;
            var body = r.ReadBytes(r.ReadCount(r.Remaining));
            if (!TryDecodeBolt(body, (int)id, seq)) { undecodable = true; Ask(-1 - (int)id, seq); }
        }

        int characterCount = r.ReadCount(NetLimits.MaxIdList);
        previous = 0;
        for (int i = 0; i < characterCount; i++)
        {
            long slot = previous + r.ReadVarInt();
            if (slot is < 0 or > int.MaxValue) throw new NetFormatException($"Slot {slot} is out of range.");
            previous = slot;
            var body = r.ReadBytes(r.ReadCount(r.Remaining));
            if (!TryDecodeCharacter(body, (int)slot, seq)) { undecodable = true; Ask((int)slot, seq); }
        }
        r.ExpectEnd();

        // The part parsed: apply it.
        if (known is null) seqs.Add(seq, known = new SeqInfo(tick, count));
        known.Received |= 1UL << index;
        if (!undecodable) known.Acked |= 1UL << index;
        if (seq > newestSeq) newestSeq = seq;
        HasNewParts = true;

        foreach (var id in stagedRemovals) RemoveCharacter(id, seq, tick);
        foreach (int id in stagedBoltRemovals) RemoveBolt(id, seq, tick);
        foreach (var b in stagedBolts)
        {
            var history = bolts.TryGetValue(b.Id, out var h) ? h : bolts[b.Id] = [];
            if (b.BaseSeq != 0) history.RemoveAll(e => e.Seq < b.BaseSeq);
            else resync.Remove(-1 - b.Id);
            InsertBolt(history, new BoltEntry(seq, tick, b.Wire));
        }
        foreach (var c in stagedCharacters)
        {
            if (!slots.TryGetValue(c.Slot, out var slot)) slots.Add(c.Slot, slot = new Slot());
            if (c.BaseSeq != 0) slot.History.RemoveAll(e => e.Seq < c.BaseSeq);
            else resync.Remove(c.Slot);
            Insert(slot.History, new Entry(seq, tick, c.Id, c.Wire));
        }
    }

    bool TryDecodeCharacter(ReadOnlySpan<byte> body, int slot, uint seq)
    {
        try
        {
            var r = new NetReader(body);
            uint back = r.ReadVarUInt32();
            CharacterWire? baseline = null;
            CharacterId id;
            uint baseSeq = 0;
            if (back == 0)
            {
                uint generation = r.ReadVarUInt32();
                if (generation > int.MaxValue) return false;
                id = new CharacterId(slot, (int)generation);
            }
            else
            {
                if (back >= seq || !slots.TryGetValue(slot, out var held)) return false;
                baseSeq = seq - back;
                int at = Find(held.History, baseSeq);
                if (at < 0 || held.History[at].Wire is null) return false;
                id = held.History[at].Id;
                baseline = held.History[at].Wire;
            }
            uint mask = r.ReadVarUInt32();
            var wire = CharacterWireCodec.ReadGroups(ref r, strings, baseline, mask, animations);
            r.ExpectEnd();
            stagedCharacters.Add(new StagedCharacter(slot, baseSeq, id, wire));
            return true;
        }
        catch (NetFormatException e)
        {
            Log?.Invoke($"character record for slot {slot} skipped: {e.Message}");
            return false;
        }
    }

    bool TryDecodeBolt(ReadOnlySpan<byte> body, int id, uint seq)
    {
        try
        {
            var r = new NetReader(body);
            uint back = r.ReadVarUInt32();
            ProjectileWire? baseline = null;
            uint baseSeq = 0;
            if (back != 0)
            {
                if (back >= seq || !bolts.TryGetValue(id, out var held)) return false;
                baseSeq = seq - back;
                int at = held.FindIndex(e => e.Seq == baseSeq);
                if (at < 0 || held[at].Wire is null) return false;
                baseline = held[at].Wire;
            }
            var wire = ProjectileWire.Read(ref r, strings, baseline);
            r.ExpectEnd();
            stagedBolts.Add(new StagedBolt(id, baseSeq, wire));
            return true;
        }
        catch (NetFormatException e)
        {
            Log?.Invoke($"projectile record {id} skipped: {e.Message}");
            return false;
        }
    }

    void Ask(int key, uint seq)
    {
        SkippedRecords++;
        resync.TryAdd(key, seq);
    }

    /// <summary>
    /// A removal applies when nothing at its Seq or newer is held for its slot and no newer generation lives there. It is kept as a removal entry even
    /// when the character is not held yet, so a late part with an older state cannot bring it back after the host forgot it.
    /// </summary>
    void RemoveCharacter(CharacterId id, uint seq, long tick)
    {
        resync.Remove(id.Slot);
        if (!slots.TryGetValue(id.Slot, out var slot)) slots.Add(id.Slot, slot = new Slot());
        var history = slot.History;
        if (history.Count > 0 && (history[^1].Seq >= seq || (history[^1].Wire is null && history[^1].Id == id))) return;
        foreach (var e in history)
            if (e.Wire is not null && e.Id.Generation > id.Generation) return;
        history.Add(new Entry(seq, tick, id, null));
    }

    void RemoveBolt(int id, uint seq, long tick)
    {
        resync.Remove(-1 - id);
        var history = bolts.TryGetValue(id, out var h) ? h : bolts[id] = [];
        if (history.Count > 0 && (history[^1].Seq >= seq || history[^1].Wire is null)) return;
        history.Add(new BoltEntry(seq, tick, null));
    }

    /// <summary>Inserts by Seq; a state replaces a removal at the same Seq, anything else at an equal Seq is a duplicate. Old states are dropped, the newest kept.</summary>
    void Insert(List<Entry> history, Entry entry)
    {
        int i = history.Count;
        while (i > 0 && history[i - 1].Seq > entry.Seq) i--;
        if (i > 0 && history[i - 1].Seq == entry.Seq)
        {
            if (history[i - 1].Wire is null) history[i - 1] = entry;
        }
        else
        {
            history.Insert(i, entry);
        }
        while (history.Count > 1 && history[1].Seq + HistorySeqs <= newestSeq) history.RemoveAt(0);
    }

    void InsertBolt(List<BoltEntry> history, BoltEntry entry)
    {
        int i = history.Count;
        while (i > 0 && history[i - 1].Seq > entry.Seq) i--;
        if (i > 0 && history[i - 1].Seq == entry.Seq)
        {
            if (history[i - 1].Wire is null) history[i - 1] = entry;
        }
        else
        {
            history.Insert(i, entry);
        }
        while (history.Count > 1 && history[1].Seq + HistorySeqs <= newestSeq) history.RemoveAt(0);
    }

    static int Find(List<Entry> history, uint seq)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Seq == seq) return i;
            if (history[i].Seq < seq) break;
        }
        return -1;
    }

    /// <summary>Drops old Seqs, old resync requests, and entities whose last state is a removal older than the history.</summary>
    void Prune()
    {
        uint floor = newestSeq > 2 * HistorySeqs ? newestSeq - 2 * HistorySeqs : 0;
        while (seqs.Count > 0 && seqs.First().Key < floor) seqs.Remove(seqs.First().Key);

        scratchKeys.Clear();
        foreach (var (key, asked) in resync)
            if (asked < floor) scratchKeys.Add(key);
        foreach (int key in scratchKeys) resync.Remove(key);

        uint gone = newestSeq > HistorySeqs ? newestSeq - HistorySeqs : 0;
        scratchKeys.Clear();
        foreach (var (key, slot) in slots)
            if (slot.History.Count == 0 || (slot.History[^1].Wire is null && slot.History[^1].Seq < gone)) scratchKeys.Add(key);
        foreach (int key in scratchKeys) slots.Remove(key);
        scratchKeys.Clear();
        foreach (var (key, history) in bolts)
            if (history.Count == 0 || (history[^1].Wire is null && history[^1].Seq < gone)) scratchKeys.Add(key);
        foreach (int key in scratchKeys) bolts.Remove(key);
    }

    WorldSnapshot Build()
    {
        var characters = new List<CharacterSnapshot>(slots.Count);
        foreach (var slot in slots.Values)
        {
            var entry = Newest(slot.History, assembledSeq);
            if (entry?.Wire is not { } wire) continue;
            var id = entry.Value.Id;
            var look = Resolve(slot, id, wire.LookId);
            views.Stats.TryGetValue(id, out var stats);
            views.Health.TryGetValue(id, out var health);
            views.Inventory.TryGetValue(id, out var inventory);
            views.Path.TryGetValue(id, out var path);
            if (slot.Built is null || !ReferenceEquals(slot.BuiltWire, wire) || !ReferenceEquals(slot.BuiltLook, look) || slot.Built.Id != id
                || !ReferenceEquals(slot.BuiltStats, stats) || !ReferenceEquals(slot.BuiltHealth, health)
                || !ReferenceEquals(slot.BuiltInventory, inventory) || !ReferenceEquals(slot.BuiltPath, path))
            {
                var snapshot = wire.ToSnapshot(id, _ => look, animations);
                if (stats is not null) snapshot = stats.ApplyTo(snapshot);
                if (health is not null) snapshot = health.ApplyTo(snapshot);
                if (inventory is not null) snapshot = inventory.ApplyTo(snapshot);
                if (path is not null) snapshot = path.ApplyTo(snapshot);
                (slot.Built, slot.BuiltWire, slot.BuiltLook) = (snapshot, wire, look);
                (slot.BuiltStats, slot.BuiltHealth, slot.BuiltInventory, slot.BuiltPath) = (stats, health, inventory, path);
            }
            characters.Add(slot.Built);
        }
        var projectiles = new List<ProjectileSnapshot>(bolts.Count);
        foreach (var (id, history) in bolts)
        {
            BoltEntry? found = null;
            for (int i = history.Count - 1; i >= 0; i--)
                if (history[i].Seq <= assembledSeq) { found = history[i]; break; }
            if (found?.Wire is { } wire) projectiles.Add(wire.ToSnapshot(id));
        }
        return views.Committed with { Tick = assembledTick, Characters = characters, Projectiles = projectiles };
    }

    static Entry? Newest(List<Entry> history, uint seq)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Seq <= seq) return history[i];
        return null;
    }

    /// <summary>The character's look: 0 is none; a look not built yet keeps the last one this character had.</summary>
    CharacterAppearance? Resolve(Slot slot, CharacterId id, uint lookId)
    {
        if (slot.LookOwner != id)
        {
            slot.LookOwner = id;
            slot.Look = null;
        }
        if (lookId == 0) return slot.Look = null;
        if (looks.Resolve(lookId) is { } look) slot.Look = look;
        return slot.Look;
    }
}
