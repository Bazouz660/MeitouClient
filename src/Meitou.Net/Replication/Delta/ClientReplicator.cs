using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Net.Replication;

/// <summary>What one <see cref="ClientReplicator.Send"/> put on the wire: part and view bytes, parts, records, and changed entities left for a later send (budget).</summary>
public readonly record struct ReplicatorSendStats(int SnapshotBytes, int ViewBytes, int Parts, int Records, int Starved);

/// <summary>
/// The host side of one client's world stream (docs/net.md, "Interest, delta, replicator and mirror"). Each <see cref="Send"/>: the interest set
/// (<see cref="InterestSet"/>), then the look definitions the client lacks, then the changed views (<see cref="ViewSender"/>, Views channel), then the
/// snapshot parts (Snapshots channel). Parts are self-contained: each entity is a delta against the state the client acked for it (a baseline at most
/// <see cref="SnapshotEncoderOptions.BaselineWindow"/> sends old, or older when nothing was sent since that ack), so a lost part costs only its entities.
/// A ring of the last <see cref="SnapshotEncoderOptions.History"/> sent parts turns <see cref="OnClientFrame"/> acks into new baselines, removals done
/// and string definitions confirmed. Frame thread only.
/// <para>
/// Part: <c>0x10 | Epoch u16 | Seq | Tick (zigzag) | ViewSeq | PartIndex byte | PartCount byte | removals: count, ids | projectiles: removal count, ids
/// (zigzag), record count, records | characters: count, records</c>. A character record is <c>slot (zigzag gap from the previous record) | length |
/// back (0: no baseline, the generation follows; else the baseline is Seq - back) | mask | groups</c>; a projectile record is <c>id (zigzag gap) | length |
/// back | <see cref="ProjectileWire"/> body</c>. Pending removals ride in part 0 until a part carrying them is acked.
/// </para>
/// </summary>
public sealed class ClientReplicator
{
    sealed class Entity(CharacterId id)
    {
        public readonly CharacterId Id = id;
        public bool Sent, Removing, Full;
        // RemovedAt: the Seq of the send that started the pending removal; an ack of an older part's removal (from before a return) does not finish it.
        // Floor: the Seq this entity (re)started from; acks of older parts are of a previous life and set no baseline, so baselines only move forward.
        public uint LastSentSeq, AckedSeq, RemovedAt, Floor;
        public CharacterWire? Acked;
        public float Acc;
    }

    sealed class Bolt(int id)
    {
        public readonly int Id = id;
        public bool Sent, Removing, Full, Near;
        public uint LastSentSeq, AckedSeq, RemovedAt, Floor;
        public ProjectileWire? Acked;
    }

    sealed class SentPart(uint seq)
    {
        public readonly uint Seq = seq;
        public readonly List<(CharacterId Id, CharacterWire Wire)> Characters = [];
        public readonly List<(int Id, ProjectileWire Wire)> Projectiles = [];
        public readonly List<CharacterId> Removals = [];
        public readonly List<int> BoltRemovals = [];
        public int[] Strings = [];
        public bool Processed;
    }

    /// <summary>One part being filled: its sections are kept apart because each is a count followed by records.</summary>
    sealed class PartBuilder
    {
        public readonly NetWriter Removals = new(), BoltRemovals = new(), Bolts = new(), Characters = new();
        public int RemovalCount, BoltRemovalCount, BoltCount, CharacterCount, PreviousSlot, PreviousBolt;
        public SentPart Part = new(0);

        public int Length => Removals.Length + BoltRemovals.Length + Bolts.Length + Characters.Length + 4 * 3;
        public bool Empty => RemovalCount + BoltRemovalCount + BoltCount + CharacterCount == 0;

        public void Start(uint seq)
        {
            Removals.Reset(); BoltRemovals.Reset(); Bolts.Reset(); Characters.Reset();
            RemovalCount = BoltRemovalCount = BoltCount = CharacterCount = PreviousSlot = PreviousBolt = 0;
            Part = new SentPart(seq);
        }

        public byte[] Finish(ReadOnlySpan<byte> prefix, int index, int count)
        {
            using var w = new NetWriter(prefix.Length + Length + 2);
            w.WriteBytes(prefix);
            w.WriteByte((byte)index);
            w.WriteByte((byte)count);
            w.WriteVarUInt((uint)RemovalCount); w.WriteBytes(Removals.Written);
            w.WriteVarUInt((uint)BoltRemovalCount); w.WriteBytes(BoltRemovals.Written);
            w.WriteVarUInt((uint)BoltCount); w.WriteBytes(Bolts.Written);
            w.WriteVarUInt((uint)CharacterCount); w.WriteBytes(Characters.Written);
            return w.Written.ToArray();
        }
    }

    readonly record struct Candidate(Entity Entity, CharacterWire Wire, bool New, float Acc, int Order);

    /// <summary>Quantized wires per snapshot object, shared by every client of one look registry (the per-viewer snapshots share unchanged characters).</summary>
    sealed class WireCache(AnimationLibrary animations)
    {
        public readonly AnimationLibrary Animations = animations;
        public readonly ConditionalWeakTable<CharacterSnapshot, CharacterWire> Wires = [];
    }

    static readonly ConditionalWeakTable<LookRegistry, WireCache> Caches = [];

    readonly int player;
    readonly SnapshotEncoderOptions options;
    readonly LookRegistry looks;
    readonly AnimationLibrary animations;
    readonly WireCache cache;
    readonly InterestSet interest = new();
    readonly ViewSender views = new();
    readonly StringTableWriter strings = new(false);
    readonly Dictionary<CharacterId, Entity> entities = [];
    readonly Dictionary<int, Bolt> bolts = [];
    readonly Dictionary<(uint Seq, int Index), SentPart> ring = [];
    readonly Queue<(uint Seq, int Index)> ringOrder = new();
    readonly HashSet<uint> sentLooks = [];
    readonly HashSet<string> logged = [];
    // Per-send scratch.
    readonly List<(CharacterSnapshot Character, float Weight)> members = [];
    readonly List<Candidate> candidates = [];
    readonly List<(Bolt Bolt, ProjectileWire Wire, bool New)> boltCandidates = [];
    readonly HashSet<int> boltsSeen = [];
    readonly List<Entity> stale = [];
    readonly List<byte[]> packets = [];
    readonly List<SentPart> built = [];
    readonly PartBuilder part = new();
    readonly NetWriter scratch = new(), prefix = new(32);
    uint nextSeq = 1, lastFrameSeq;
    bool framed, sentAnyPart;
    TimeSpan? since;
    TimeSpan lastPart;

    public ClientReplicator(int player, SnapshotEncoderOptions options, LookRegistry looks, AnimationLibrary animations)
    {
        this.player = player;
        this.options = options;
        this.looks = looks;
        this.animations = animations;
        var shared = Caches.GetValue(looks, _ => new WireCache(animations));
        cache = ReferenceEquals(shared.Animations, animations) ? shared : new WireCache(animations);
    }

    public ushort Epoch { get; private set; }

    /// <summary>The characters the last <see cref="Send"/> chose for this client (the event filter reads it too).</summary>
    public IReadOnlySet<CharacterId> Interest => interest.Members;

    /// <summary>The client's camera from its newest <see cref="ClientFrame"/>.</summary>
    public ViewState View { get; private set; }

    public ReplicatorSendStats LastSend { get; private set; }

    /// <summary>Where unencodable host data and over-limit views are reported (once per item); null: dropped silently.</summary>
    public Action<string>? Log
    {
        get => views.Log;
        set => views.Log = value;
    }

    /// <summary>A new epoch: no baselines, no sent parts, every view resent. The sent-look set and the string tables are kept (they belong to the connection).</summary>
    public void Reset(ushort epoch)
    {
        Epoch = epoch;
        entities.Clear();
        bolts.Clear();
        ring.Clear();
        ringOrder.Clear();
        interest.Clear();
        views.Reset();
        lastFrameSeq = 0;
        framed = false;
        sentAnyPart = false;
        since = null;
    }

    /// <summary>Sends what <paramref name="view"/> (this player's <c>PublishFor</c>) changes for the client: looks, then views (Views), then parts (Snapshots).</summary>
    public void Send(WorldSnapshot view, TimeSpan now, IPacketSink sink)
    {
        since ??= now;
        uint seq = nextSeq;
        int viewBytesBefore = views.SentBytes;

        interest.Update(view, View, player, members);
        CollectCharacters(seq);
        CollectBolts(view, seq);
        foreach (var c in candidates)
            if (c.Wire.LookId != 0 && sentLooks.Add(c.Wire.LookId) && looks.RecipeOf(c.Wire.LookId) is { } recipe)
                views.SendLook(c.Wire.LookId, recipe, sink);
        bool viewsSent = views.Send(view, interest.Members, Epoch, now, sink);

        // New entities first, then the rest by accumulated weight (ties by slot).
        candidates.Sort(static (a, b) => a.New != b.New ? (a.New ? -1 : 1) : a.Acc != b.Acc ? b.Acc.CompareTo(a.Acc) : a.Order.CompareTo(b.Order));

        int budget = options.BytesPerSecond / Math.Max(1, options.SendHz);
        if (now - since.Value < TimeSpan.FromSeconds(2)) budget *= 2;
        int max = sink.MaxUnreliablePayload;

        prefix.Reset();
        prefix.WriteByte((byte)MessageType.SnapshotPart);
        prefix.WriteUInt16(Epoch);
        prefix.WriteVarUInt(seq);
        prefix.WriteVarInt(view.Tick);
        prefix.WriteVarUInt(views.ViewSeq);
        int header = prefix.Length + 2;

        built.Clear();
        packets.Clear();
        StartPart(seq);
        AddRemovals(header, max);

        int spent = 0, records = 0, starved = 0;
        bool full = false;
        foreach (var (bolt, wire, isNew) in boltCandidates)
        {
            if (full || (spent >= budget && records > 0)) { starved++; continue; }
            int bytes = AddBolt(bolt, wire, isNew, seq, header, max, ref full);
            if (bytes < 0) { starved++; continue; }
            spent += bytes;
            records++;
        }
        foreach (var c in candidates)
        {
            if (full || (spent >= budget && records > 0)) { starved++; continue; }
            int bytes = AddCharacter(c, seq, header, max, ref full);
            if (bytes < 0) { if (bytes == -1) starved++; continue; }
            spent += bytes;
            records++;
        }

        bool keepAlive = !sentAnyPart || now - lastPart >= TimeSpan.FromSeconds(1.0 / Math.Max(1, options.KeepAliveHz)) || now < lastPart;
        if (!part.Empty || (built.Count == 0 && (viewsSent || keepAlive))) FlushPart();

        int snapshotBytes = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            byte[] packet = packets[i];
            packet[header - 1] = (byte)packets.Count;
            sink.Send(NetChannel.Snapshots, packet);
            snapshotBytes += packet.Length;
            Remember(built[i], i);
        }
        if (packets.Count > 0)
        {
            nextSeq++;
            sentAnyPart = true;
            lastPart = now;
        }
        LastSend = new ReplicatorSendStats(snapshotBytes, views.SentBytes - viewBytesBefore, packets.Count, records, starved);
    }

    /// <summary>Takes the client's acks (new baselines, removals done, string definitions confirmed), camera and resync requests; another epoch's frame is ignored.</summary>
    public void OnClientFrame(ClientFrame frame)
    {
        if (frame.Epoch != Epoch) return;
        if (!framed || frame.FrameSeq > lastFrameSeq)
        {
            View = frame.View;
            lastFrameSeq = frame.FrameSeq;
            framed = true;
        }
        foreach (var (seq, parts) in frame.Acks)
            for (int i = 0; i < NetLimits.MaxParts; i++)
                if ((parts & (1UL << i)) != 0 && ring.TryGetValue((seq, i), out var sent) && !sent.Processed)
                    Acknowledge(sent);
        foreach (int slot in frame.Resync)
        {
            if (slot >= 0)
            {
                foreach (var e in entities.Values)
                    if (e.Id.Slot == slot) (e.Acked, e.Full, e.Floor) = (null, true, nextSeq);
            }
            else if (bolts.TryGetValue(-1 - slot, out var bolt))
            {
                (bolt.Acked, bolt.Full, bolt.Floor) = (null, true, nextSeq);
            }
        }
    }

    void Acknowledge(SentPart sent)
    {
        sent.Processed = true;
        foreach (var (id, wire) in sent.Characters)
            if (entities.TryGetValue(id, out var e) && sent.Seq >= e.Floor && (e.Acked is null || e.AckedSeq < sent.Seq))
            {
                e.Acked = wire;
                e.AckedSeq = sent.Seq;
            }
        foreach (var (id, wire) in sent.Projectiles)
            if (bolts.TryGetValue(id, out var b) && sent.Seq >= b.Floor && (b.Acked is null || b.AckedSeq < sent.Seq))
            {
                b.Acked = wire;
                b.AckedSeq = sent.Seq;
            }
        foreach (var id in sent.Removals)
            if (entities.TryGetValue(id, out var e) && e.Removing && sent.Seq >= e.RemovedAt) entities.Remove(id);
        foreach (int id in sent.BoltRemovals)
            if (bolts.TryGetValue(id, out var b) && b.Removing && sent.Seq >= b.RemovedAt) bolts.Remove(id);
        strings.Confirm(sent.Strings);
    }

    /// <summary>The acked state to delta against, if the client still holds it: nothing sent since that ack, or the ack is within the baseline window.</summary>
    bool HasBaseline(uint ackedSeq, uint lastSentSeq, bool acked, uint seq) =>
        acked && (ackedSeq == lastSentSeq || seq - ackedSeq <= (uint)Math.Max(0, options.BaselineWindow));

    void CollectCharacters(uint seq)
    {
        candidates.Clear();
        for (int i = 0; i < members.Count; i++)
        {
            var (c, weight) = members[i];
            if (c.Id.Slot < 0 || c.Id.Generation < 0)
            {
                Note($"character {c.Id}", $"character {c.Id} not sent: a negative slot or generation has no wire form");
                continue;
            }
            CharacterWire? wire;
            try
            {
                if (!cache.Wires.TryGetValue(c, out wire))
                {
                    wire = CharacterWire.Quantize(c, looks.IdOf, animations);
                    cache.Wires.AddOrUpdate(c, wire);
                }
            }
            catch (ArgumentException e)
            {
                Note($"character {c.Id}", $"character {c.Id} not sent: {e.Message}");
                continue;
            }
            if (!entities.TryGetValue(c.Id, out var entity)) entities.Add(c.Id, entity = new Entity(c.Id) { Floor = seq });
            if (entity.Removing)
            {
                // Back before its removal was acked: the client may have applied it, so it starts again from a full record.
                entity.Removing = false;
                entity.Acked = null;
                entity.Floor = seq;
            }
            bool isNew = !entity.Sent || entity.Full || !HasBaseline(entity.AckedSeq, entity.LastSentSeq, entity.Acked is not null, seq);
            if (!isNew && wire.DiffMask(entity.Acked!) == 0)
            {
                entity.Acc = 0;
                continue;
            }
            entity.Acc += weight;
            candidates.Add(new Candidate(entity, wire, isNew, entity.Acc, i));
        }
        stale.Clear();
        foreach (var e in entities.Values)
            if (!e.Removing && !interest.Members.Contains(e.Id)) stale.Add(e);
        foreach (var e in stale)
        {
            if (e.Sent) (e.Removing, e.RemovedAt) = (true, seq);
            else entities.Remove(e.Id);
        }
    }

    void CollectBolts(WorldSnapshot view, uint seq)
    {
        boltCandidates.Clear();
        boltsSeen.Clear();
        float draw = float.IsFinite(View.DrawDistance) ? Math.Clamp(View.DrawDistance, 0f, InterestSet.MaxRadius) : 0f;
        float enter = draw + InterestSet.Margin, leave = enter + InterestSet.Margin;
        foreach (var p in view.Projectiles)
        {
            if (p.Id < 0)
            {
                Note($"projectile {p.Id}", $"projectile {p.Id} not sent: a negative id has no wire form");
                continue;
            }
            if (!boltsSeen.Add(p.Id)) continue;
            float dx = p.Position.X - View.Eye.X, dz = p.Position.Z - View.Eye.Z, d2 = dx * dx + dz * dz;
            bolts.TryGetValue(p.Id, out var bolt);
            bool near = d2 <= enter * enter || (bolt is { Near: true, Removing: false } && d2 <= leave * leave);
            if (!near) { boltsSeen.Remove(p.Id); continue; }
            if (bolt is null) bolts.Add(p.Id, bolt = new Bolt(p.Id) { Floor = seq });
            bolt.Near = true;
            if (bolt.Removing)
            {
                bolt.Removing = false;
                bolt.Acked = null;
                bolt.Floor = seq;
            }
            var wire = ProjectileWire.Quantize(p);
            bool isNew = !bolt.Sent || bolt.Full || !HasBaseline(bolt.AckedSeq, bolt.LastSentSeq, bolt.Acked is not null, seq);
            if (!isNew && wire.DiffMask(bolt.Acked!) == 0) continue;
            boltCandidates.Add((bolt, wire, isNew));
        }
        List<int>? gone = null;
        foreach (var b in bolts.Values)
            if (!b.Removing && !boltsSeen.Contains(b.Id))
            {
                b.Near = false;
                if (b.Sent) (b.Removing, b.RemovedAt) = (true, seq);
                else (gone ??= []).Add(b.Id);
            }
        if (gone is not null)
            foreach (int id in gone) bolts.Remove(id);
    }

    void StartPart(uint seq)
    {
        part.Start(seq);
        strings.BeginPart();
    }

    /// <summary>Closes the current part (its packet gets the part count at the end of the send).</summary>
    void FlushPart()
    {
        part.Part.Strings = [.. strings.PartDefinitions];
        built.Add(part.Part);
        packets.Add(part.Finish(prefix.Written, built.Count - 1, 0));
    }

    void AddRemovals(int header, int max)
    {
        foreach (var e in entities.Values)
        {
            if (!e.Removing) continue;
            if (header + part.Length + 12 > max || part.RemovalCount >= NetLimits.MaxIdList) break;
            CommonCodec.WriteId(part.Removals, e.Id);
            part.RemovalCount++;
            part.Part.Removals.Add(e.Id);
        }
        foreach (var b in bolts.Values)
        {
            if (!b.Removing) continue;
            if (header + part.Length + 6 > max || part.BoltRemovalCount >= NetLimits.MaxIdList) break;
            part.BoltRemovals.WriteVarInt(b.Id);
            part.BoltRemovalCount++;
            part.Part.BoltRemovals.Add(b.Id);
        }
    }

    /// <summary>Encodes one character record into the current part (a new part when it does not fit); returns its bytes, -1 when out of parts, -2 when skipped.</summary>
    int AddCharacter(Candidate c, uint seq, int header, int max, ref bool full)
    {
        var e = c.Entity;
        var baseline = c.New ? null : e.Acked;
        uint mask = c.Wire.DiffMask(baseline ?? CharacterWire.Default);
        var wire = c.Wire;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int mark = strings.Mark();
            try
            {
                scratch.Reset();
                scratch.WriteVarUInt(baseline is null ? 0 : seq - e.AckedSeq);
                if (baseline is null) scratch.WriteVarUInt((uint)e.Id.Generation);
                scratch.WriteVarUInt(mask);
                CharacterWireCodec.WriteGroups(scratch, strings, wire, baseline, mask);
            }
            catch (ArgumentException ex)
            {
                strings.Rollback(mark);
                Note($"character {e.Id}", $"character {e.Id} not sent: {ex.Message}");
                return -2;
            }
            int gap = e.Id.Slot - part.PreviousSlot;
            int bytes = VarIntSize(gap) + VarUIntSize((uint)scratch.Length) + scratch.Length;
            if (header + part.Length + bytes <= max && part.CharacterCount < NetLimits.MaxIdList)
            {
                part.Characters.WriteVarInt(gap);
                part.Characters.WriteVarUInt((uint)scratch.Length);
                part.Characters.WriteBytes(scratch.Written);
                part.CharacterCount++;
                part.PreviousSlot = e.Id.Slot;
                part.Part.Characters.Add((e.Id, wire));
                e.Sent = true;
                e.Full = false;
                e.LastSentSeq = seq;
                e.Acc = 0;
                return bytes;
            }
            strings.Rollback(mark);
            if (!part.Empty)
            {
                if (built.Count + 1 >= NetLimits.MaxParts) { full = true; return -1; }
                FlushPart();
                StartPart(seq);
                continue;
            }
            if ((mask & CharacterWire.Ragdoll) == 0) break;
            // Bigger than a whole part: keep the client's ragdoll for this send.
            Note($"ragdoll {e.Id}", $"character {e.Id}: ragdoll left out of a send (record over {max} bytes)");
            var trimmed = wire.Clone();
            trimmed.RagdollPose = (baseline ?? CharacterWire.Default).RagdollPose;
            wire = trimmed;
            mask &= ~CharacterWire.Ragdoll;
        }
        Note($"oversize {e.Id}", $"character {e.Id} not sent: its record does not fit a part of {max} bytes");
        return -2;
    }

    int AddBolt(Bolt bolt, ProjectileWire wire, bool isNew, uint seq, int header, int max, ref bool full)
    {
        var baseline = isNew ? null : bolt.Acked;
        uint mask = wire.DiffMask(baseline ?? ProjectileWire.Default);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int mark = strings.Mark();
            scratch.Reset();
            scratch.WriteVarUInt(baseline is null ? 0 : seq - bolt.AckedSeq);
            ProjectileWire.Write(scratch, strings, wire, baseline, mask);
            int gap = bolt.Id - part.PreviousBolt;
            int bytes = VarIntSize(gap) + VarUIntSize((uint)scratch.Length) + scratch.Length;
            if (header + part.Length + bytes <= max && part.BoltCount < NetLimits.MaxIdList)
            {
                part.Bolts.WriteVarInt(gap);
                part.Bolts.WriteVarUInt((uint)scratch.Length);
                part.Bolts.WriteBytes(scratch.Written);
                part.BoltCount++;
                part.PreviousBolt = bolt.Id;
                part.Part.Projectiles.Add((bolt.Id, wire));
                bolt.Sent = true;
                bolt.Full = false;
                bolt.LastSentSeq = seq;
                return bytes;
            }
            strings.Rollback(mark);
            if (part.Empty) break;
            if (built.Count + 1 >= NetLimits.MaxParts) { full = true; return -1; }
            FlushPart();
            StartPart(seq);
        }
        return -1;
    }

    void Remember(SentPart sent, int index)
    {
        var key = (sent.Seq, index);
        ring[key] = sent;
        ringOrder.Enqueue(key);
        while (ringOrder.Count > Math.Max(1, options.History)) ring.Remove(ringOrder.Dequeue());
    }

    void Note(string key, string text)
    {
        if (logged.Add(key)) Log?.Invoke(text);
    }

    static int VarUIntSize(uint v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    static int VarIntSize(int v) => VarUIntSize((uint)((v << 1) ^ (v >> 31)));
}
