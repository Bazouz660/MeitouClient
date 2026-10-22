namespace Meitou.Data;

/// <summary>
/// The iteration order of the original's boost 1.5x <c>unordered_map</c> / <c>unordered_set</c> with integer keys (docs/formats/boost-unordered.md): the
/// game lists some tables in that order (RACE STATS, the random groups of the appearance rolls).
/// </summary>
public static class BoostHashOrder
{
    /// <summary>
    /// The keys of a map that got <paramref name="inserted"/> in that order (repeats ignored), starting at <paramref name="buckets"/> buckets with a maximum load
    /// factor of <paramref name="maxLoad"/>, as the map iterates them.
    /// </summary>
    public static List<long> Of(IEnumerable<long> inserted, int buckets = 16, float maxLoad = 1)
    {
        var order = new List<long>();
        var seen = new HashSet<long>();
        int count = 0, limit = 0;
        foreach (long key in inserted)
        {
            if (!seen.Add(key)) continue;
            int size = order.Count + 1;
            if (count == 0)
            {
                count = Math.Max(buckets, BucketsFor(size, maxLoad));
                limit = (int)(count * maxLoad);
            }
            else if (size > limit && BucketsFor(Math.Max(size, order.Count + (order.Count >> 1)), maxLoad) is int grown && grown != count)
            {
                // Rehash: the list is walked in order; a node whose new bucket is empty stays where it is, one whose bucket has nodes moves to the front of their run.
                count = grown;
                limit = (int)(count * maxLoad);
                var rehashed = new List<long>(order.Count);
                foreach (long k in order) Place(rehashed, k, count, newRunFirst: false);
                order = rehashed;
            }
            Place(order, key, count, newRunFirst: true);
        }
        return order;
    }

    /// <summary>A key into the list: at the front of its bucket's run, else (a bucket new to the list) first (an insert) or last (a rehash's walk).</summary>
    static void Place(List<long> order, long key, int count, bool newRunFirst)
    {
        ulong bucket = Mix64((ulong)key) & (ulong)(count - 1);
        int run = order.FindIndex(k => (Mix64((ulong)k) & (ulong)(count - 1)) == bucket);
        if (run >= 0) order.Insert(run, key);
        else if (newRunFirst) order.Insert(0, key);
        else order.Add(key);
    }

    /// <summary>boost's bucket count for a size: the power of two (at least 4) above size / max load.</summary>
    static int BucketsFor(int size, float maxLoad)
    {
        int wanted = (int)(size / maxLoad) + 1, n = 4;
        while (n < wanted) n <<= 1;
        return n;
    }

    /// <summary>Thomas Wang's 64-bit mix, boost's hash policy for power-of-two bucket counts (an integer key hashes to itself first).</summary>
    public static ulong Mix64(ulong k)
    {
        k = ~k + (k << 21);
        k ^= k >> 24;
        k = k + (k << 3) + (k << 8);
        k ^= k >> 14;
        k = k + (k << 2) + (k << 4);
        k ^= k >> 28;
        return k + (k << 31);
    }
}
