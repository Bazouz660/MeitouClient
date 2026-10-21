using Meitou.Net.Events;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// The client's spatial events waiting for the playback point (docs/net.md, "Client and jitter buffer"): each is released into the replica world's own
/// queues when the presented tick reaches its batch's tick, or after <see cref="HoldSeconds"/>, so sounds and blood line up with the motion they belong to.
/// A queue the game left null (that consumer is off) drops its events. Frame thread only.
/// </summary>
sealed class ClientEvents
{
    /// <summary>Longest an event waits, in seconds.</summary>
    public const double HoldSeconds = 1;

    /// <summary>Most events waiting; the oldest go first.</summary>
    public const int MaxPending = 4096;

    readonly Queue<(long Tick, long ArrivalUs, NetEvent Event)> pending = new();

    public int Count => pending.Count;

    /// <summary>Queues the spatial events of a batch for <paramref name="tick"/>; non-spatial ones are the caller's.</summary>
    public void Add(long tick, long arrivalUs, IEnumerable<NetEvent> events)
    {
        foreach (var e in events)
        {
            if (!EventFilter.IsSpatial(e)) continue;
            if (pending.Count == MaxPending) pending.Dequeue();
            pending.Enqueue((tick, arrivalUs, e));
        }
    }

    /// <summary>Releases, in arrival order, every event whose tick <paramref name="presentedTick"/> reached or that waited too long, into <paramref name="world"/>'s queues.</summary>
    public void Release(World world, double presentedTick, long nowUs)
    {
        while (pending.TryPeek(out var next) && (next.Tick <= presentedTick || nowUs - next.ArrivalUs >= HoldSeconds * 1e6))
        {
            pending.Dequeue();
            switch (next.Event)
            {
                case SoundNet s: world.Sounds?.Enqueue(s.Sound); break;
                case BloodNet b: world.BloodMarks?.Enqueue(b.Mark); break;
                case FloaterNet f: world.Floaters?.Enqueue(f.Floater); break;
            }
        }
    }

    public void Clear() => pending.Clear();
}
