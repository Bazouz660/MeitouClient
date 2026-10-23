using System.Numerics;

namespace Meitou.Simulation.Items;

/// <summary>
/// An item lying in the world, as the host draws it (docs/simulation.md "Ground items"): a saved one by its whole <see cref="Rotation"/>, a <see cref="Dropped"/> one turned by it
/// about Y and laid flat on the ground by the host. <see cref="Type"/>, <see cref="Level"/> and <see cref="Quality"/> are the item's, for its label's grade colour.
/// </summary>
public readonly record struct GroundItemSnapshot(int Id, string Record, string Name, Vector3 Position, Quaternion Rotation, bool Dropped,
    Meitou.Data.Fcs.FcsRecordType Type = default, int Level = 0, float Quality = 0);

/// <summary>One loose item: where it lies and how it is turned (a saved item's whole rotation, written back as read; a dropped one turned about Y).</summary>
public sealed record GroundItem(int Id, ItemInstance Item, Vector3 Position, Quaternion Rotation)
{
    /// <summary>Dropped in play (turned about Y only), not put back from the save.</summary>
    public bool Dropped { get; init; }
}

/// <summary>
/// Loose items lying in the world (docs/simulation.md "Ground items"): the save's (its zones' <c>1-itemlist</c>, docs/formats/save.md "Zone files") and the severed limbs combat drops.
/// Oldest first; beyond <see cref="Limit"/> items dropped in play the oldest of those goes. Hashed by <see cref="World.StateHash"/> only while it holds one.
/// </summary>
public sealed class GroundItems
{
    /// <summary>The default and largest <see cref="Limit"/>.</summary>
    public const int DefaultLimit = 1024, MaxLimit = 4096;

    int limit = DefaultLimit;

    /// <summary>
    /// How many items dropped in play (not read from the save) lie in the world at most (an engine choice; the original's limbs carry a <c>timeout</c>, meaning
    /// <b>Unknown</b>): 1 to <see cref="MaxLimit"/>, the host's "Dropped items kept" setting. Lowered, the oldest beyond it go at the next drop.
    /// </summary>
    public int Limit { get => limit; set => limit = Math.Clamp(value, 1, MaxLimit); }

    readonly List<GroundItem> items = [];
    int nextId = 1;

    public int Count => items.Count;
    public IReadOnlyList<GroundItem> All => items;
    /// <summary>Bumped when an item is dropped, loaded or goes: the host diffs <see cref="Published"/> by id when it changes.</summary>
    public long Revision { get; private set; }

    /// <summary>Drops an item made in play, turned <paramref name="yaw"/> about Y.</summary>
    public void Drop(ItemInstance item, Vector3 position, float yaw)
    {
        items.Add(new GroundItem(nextId++, item, position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)) { Dropped = true });
        for (int extra = items.Count(i => i.Item.Saved is null) - limit; extra > 0; extra--) items.RemoveAt(items.FindIndex(i => i.Item.Saved is null));
        Revision++;
    }

    /// <summary>Puts a saved item back where the save had it (no limit).</summary>
    public void Load(ItemInstance item, Vector3 position, Quaternion rotation)
    {
        items.Add(new GroundItem(nextId++, item, position, rotation));
        Revision++;
    }

    public GroundItem? Find(int id) => items.Find(i => i.Id == id);

    /// <summary>Takes an item off the ground (null when it is gone).</summary>
    public GroundItem? Take(int id)
    {
        int k = items.FindIndex(i => i.Id == id);
        if (k < 0) return null;
        var taken = items[k];
        items.RemoveAt(k);
        Revision++;
        return taken;
    }

    /// <summary>The items as published, made again only when <see cref="Revision"/> changed.</summary>
    internal IReadOnlyList<GroundItemSnapshot> Published()
    {
        if (publishedAt != Revision) (published, publishedAt) = ([.. items.Select(i => new GroundItemSnapshot(i.Id, i.Item.Record, i.Item.Name, i.Position, i.Rotation, i.Dropped, i.Item.Type, i.Item.Level, i.Item.Quality))], Revision);
        return published;
    }
    IReadOnlyList<GroundItemSnapshot> published = [];
    long publishedAt;

    public void Hash(ref StateHasher h)
    {
        h.Add(items.Count);
        foreach (var i in items)
        {
            h.Add(i.Id);
            h.Add(Rng.StableHash(i.Item.Record));
            h.Add(i.Item.Quantity);
            h.Add(i.Position);
            h.Add(i.Rotation.X);
            h.Add(i.Rotation.Y);
            h.Add(i.Rotation.Z);
            h.Add(i.Rotation.W);
        }
    }
}
