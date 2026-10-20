namespace Meitou.Net.Replication;

/// <summary>
/// The client half of one keyed list view: applies the bodies <see cref="KeyedViewSync{TKey, TItem}"/> writes and keeps the current list
/// (docs/net.md, "Keyed views"). <see cref="Items"/> is the same reference until a body changes something, because host code diffs these lists by
/// reference. A body that does not parse or does not fit the state throws <see cref="NetFormatException"/> and leaves the list as it was.
/// </summary>
public sealed class KeyedViewDecoder<TItem>(ItemCodec<TItem> codec)
{
    readonly Dictionary<uint, TItem> items = [];
    List<uint> order = [];

    /// <summary>The list in the host's order.</summary>
    public IReadOnlyList<TItem> Items { get; private set; } = [];

    /// <summary>Forgets everything (a new epoch or connection); the host resets its <see cref="KeyedViewSync{TKey, TItem}"/> at the same time.</summary>
    public void Reset()
    {
        items.Clear();
        order = [];
        Items = [];
    }

    /// <summary>Applies one body; returns true when <see cref="Items"/> changed.</summary>
    public bool Apply(ref NetReader r, StringTableReader strings)
    {
        byte flags = r.ReadByte();
        if ((flags & ~1) != 0) throw new NetFormatException($"Unknown keyed view flags {flags}.");

        List<uint>? newOrder = null;
        if (flags != 0)
        {
            int count = r.ReadCount(NetLimits.MaxViewItems);
            newOrder = new List<uint>(count);
            for (int i = 0; i < count; i++) newOrder.Add(r.ReadVarUInt32());
        }
        int upsertCount = r.ReadCount(NetLimits.MaxViewItems);
        var upserts = new List<(uint Slot, TItem Item)>(upsertCount);
        for (int i = 0; i < upsertCount; i++)
        {
            uint slot = r.ReadVarUInt32();
            upserts.Add((slot, codec.Read(ref r, strings)));
        }

        var listed = new HashSet<uint>(newOrder ?? order);
        if (newOrder is not null && listed.Count != newOrder.Count) throw new NetFormatException("A keyed view lists a slot twice.");
        foreach (var (slot, _) in upserts)
            if (!listed.Contains(slot)) throw new NetFormatException($"A keyed view updates slot {slot}, which it does not list.");
        if (newOrder is not null)
        {
            var upserted = new HashSet<uint>(upserts.Select(u => u.Slot));
            foreach (uint slot in newOrder)
                if (!items.ContainsKey(slot) && !upserted.Contains(slot))
                    throw new NetFormatException($"A keyed view lists slot {slot}, which it never sent.");
        }

        foreach (var (slot, item) in upserts) items[slot] = item;
        if (newOrder is not null)
        {
            foreach (uint slot in order)
                if (!listed.Contains(slot)) items.Remove(slot);
            order = newOrder;
        }
        else if (upserts.Count == 0)
        {
            return false;
        }

        var list = new List<TItem>(order.Count);
        foreach (uint slot in order) list.Add(items[slot]);
        Items = list;
        return true;
    }
}
