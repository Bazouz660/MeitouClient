namespace Meitou.Net.Replication;

/// <summary>
/// The host half of one keyed list view (buildings, ground items, doors, ...): turns the current list into the smallest <c>ViewUpdate</c> body for one
/// client (docs/net.md, "Keyed views"). It keeps, per key, the canonical bytes of what the client holds and the client's key order.
/// <para>
/// Keys never go on the wire. Each key gets a slot number the first time it is sent; the body names items by slot, so the client
/// (<see cref="KeyedViewDecoder{TItem}"/>) needs no key type. Body: a flags byte (bit 0: the order changed); if so, the new order as a count and the
/// slots (a slot missing from it is removed); then the upserts as a count and (slot, item) pairs.
/// </para>
/// <para>
/// Change detection: a source reference equal to the last fully sent one is skipped. Otherwise every item is encoded with a literal-only string
/// table (so table state cannot make equal items differ, and nothing is defined for items that are not sent) and compared with the bytes last sent.
/// Item changes wait for <c>minInterval</c>; a change of membership or order, or a <c>structural</c> call, goes at once. A body over the size cap
/// holds back the items that do not fit; they go in the next call.
/// </para>
/// </summary>
public sealed class KeyedViewSync<TKey, TItem>(ViewKind kind, Func<TItem, TKey> key, ItemCodec<TItem> codec, TimeSpan minInterval) where TKey : notnull
{
    sealed class Held(uint slot, byte[] canonical)
    {
        public readonly uint Slot = slot;
        public byte[] Canonical = canonical;
    }

    readonly record struct Change(int Index, TKey Key, byte[] Canonical, bool IsNew);

    readonly record struct Sent(TKey Key, uint Slot, byte[] Canonical, bool IsNew);

    readonly Dictionary<TKey, Held> held = [];
    readonly List<Change> changes = [];
    readonly List<Sent> committed = [];
    readonly List<TKey> keys = [];
    readonly HashSet<TKey> keySet = [];
    readonly NetWriter canonical = new();
    readonly NetWriter body = new();
    readonly StringTableWriter literals = StringTableWriter.CreateLiteralOnly();
    List<TKey> heldOrder = [];
    List<TKey> effective = [];
    IReadOnlyList<TItem>? settled;
    bool sentAny, backlog;
    TimeSpan lastSent;
    uint nextSlot;

    public ViewKind Kind => kind;

    /// <summary>Forgets what the client holds (a new epoch or connection): the next <see cref="Encode"/> sends everything, even an empty list.</summary>
    public void Reset()
    {
        held.Clear();
        heldOrder.Clear();
        settled = null;
        sentAny = backlog = false;
        lastSent = default;
        nextSlot = 0;
    }

    /// <summary>
    /// Writes the update for <paramref name="now"/> to <paramref name="w"/> and returns true, or returns false and writes nothing when the client
    /// already holds this list or the change must wait for the minimum interval. A list over <see cref="NetLimits.MaxViewItems"/> is cut to it.
    /// </summary>
    /// <param name="now">The source list; one that returns the same reference until it changes is skipped at once.</param>
    /// <param name="structural">A revision bump the caller knows about: go now even if only items changed.</param>
    /// <param name="time">A monotonic time, used for the minimum interval.</param>
    /// <param name="s">The string table of the view channel, shared by every view of this client.</param>
    /// <param name="maxBody">Soft cap on the upsert bytes of one body; at least one item is always sent.</param>
    public bool Encode(IReadOnlyList<TItem> now, bool structural, TimeSpan time, NetWriter w, StringTableWriter s, int maxBody = NetLimits.MaxViewBody)
    {
        if (sentAny && !structural && !backlog && ReferenceEquals(now, settled)) return false;

        Survey(now);
        bool orderDiffers = !SameKeys(keys, heldOrder);
        if (sentAny && !orderDiffers && changes.Count == 0)
        {
            settled = now;
            backlog = false;
            return false;
        }
        if (sentAny && !structural && !backlog && !orderDiffers && time >= lastSent && time - lastSent < minInterval) return false;

        // The upserts first: how many fit decides which keys the client holds, and so the order list.
        body.Reset();
        committed.Clear();
        uint cursor = nextSlot;
        bool deferred = false;
        foreach (var change in changes)
        {
            uint slot = change.IsNew ? cursor : held[change.Key].Slot;
            int mark = s.Mark(), before = body.Length;
            body.WriteVarUInt(slot);
            codec.Write(body, s, now[change.Index]);
            if (committed.Count > 0 && body.Length > maxBody)
            {
                body.Truncate(before);
                s.Rollback(mark);
                deferred = true;
                break;
            }
            if (change.IsNew) cursor++;
            committed.Add(new Sent(change.Key, slot, change.Canonical, change.IsNew));
        }

        foreach (var sent in committed)
        {
            if (sent.IsNew) held.Add(sent.Key, new Held(sent.Slot, sent.Canonical));
            else held[sent.Key].Canonical = sent.Canonical;
        }
        nextSlot = cursor;

        effective.Clear();
        foreach (var k in keys)
            if (held.ContainsKey(k)) effective.Add(k);
        bool orderChanged = !SameKeys(effective, heldOrder);

        w.WriteByte(orderChanged ? (byte)1 : (byte)0);
        if (orderChanged)
        {
            w.WriteVarUInt((uint)effective.Count);
            foreach (var k in effective) w.WriteVarUInt(held[k].Slot);
            foreach (var k in heldOrder)
                if (!keySet.Contains(k)) held.Remove(k);
        }
        w.WriteVarUInt((uint)committed.Count);
        w.WriteBytes(body.Written);

        (heldOrder, effective) = (effective, heldOrder);
        sentAny = true;
        lastSent = time;
        backlog = deferred;
        settled = deferred ? null : now;
        return true;
    }

    /// <summary>Fills <see cref="keys"/> and <see cref="changes"/> from the source: its keys in order, and the items that are new or differ from what the client holds.</summary>
    void Survey(IReadOnlyList<TItem> now)
    {
        keys.Clear();
        keySet.Clear();
        changes.Clear();
        int count = Math.Min(now.Count, NetLimits.MaxViewItems);
        for (int i = 0; i < count; i++)
        {
            var item = now[i];
            var k = key(item);
            if (!keySet.Add(k)) throw new ArgumentException($"View {kind} lists key {k} twice.", nameof(now));
            keys.Add(k);
            canonical.Reset();
            codec.Write(canonical, literals, item);
            if (!held.TryGetValue(k, out var current)) changes.Add(new Change(i, k, canonical.Written.ToArray(), true));
            else if (!canonical.Written.SequenceEqual(current.Canonical)) changes.Add(new Change(i, k, canonical.Written.ToArray(), false));
        }
    }

    static bool SameKeys(List<TKey> a, List<TKey> b)
    {
        if (a.Count != b.Count) return false;
        var comparer = EqualityComparer<TKey>.Default;
        for (int i = 0; i < a.Count; i++)
            if (!comparer.Equals(a[i], b[i])) return false;
        return true;
    }
}
