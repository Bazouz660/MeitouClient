using Meitou.Net.Events;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// The spatial events (sounds, blood, floaters) a client holds until the presentation shows their host tick (docs/net.md, "Client and jitter buffer"):
/// <see cref="Release"/> hands each to the replica world's own queue once the presented tick reaches the batch's tick or it has waited
/// <see cref="HoldMicroseconds"/>. At most <see cref="MaxPending"/> wait; beyond that the oldest are dropped. Frame thread only.
/// </summary>
internal sealed class ClientEvents
{
    public const int MaxPending = 4096;

    /// <summary>Longest an event waits for the presentation, in microseconds of real time.</summary>
    public const long HoldMicroseconds = 1_000_000;

    readonly Queue<(long Tick, long ArrivalUs, NetEvent Event)> pending = new();

    /// <summary>Events waiting.</summary>
    public int Count => pending.Count;

    /// <summary>Events dropped because too many were waiting.</summary>
    public int Dropped { get; private set; }

    /// <summary>Queues a spatial event of host tick <paramref name="tick"/>.</summary>
    public void Add(long tick, long arrivalUs, NetEvent e)
    {
        if (pending.Count == MaxPending)
        {
            pending.Dequeue();
            Dropped++;
        }
        pending.Enqueue((tick, arrivalUs, e));
    }

    /// <summary>
    /// Releases, in arrival order, the events whose tick is at most <paramref name="presentedTick"/> (null: nothing presented) or that waited long enough,
    /// into <paramref name="world"/>'s queues (a null world or queue drops them: no consumer).
    /// </summary>
    public void Release(World? world, long? presentedTick, long nowUs)
    {
        while (pending.TryPeek(out var p) && (p.Tick <= presentedTick || nowUs - p.ArrivalUs >= HoldMicroseconds))
        {
            pending.Dequeue();
            switch (p.Event)
            {
                case SoundNet s: world?.Sounds?.Enqueue(s.Sound); break;
                case BloodNet b: world?.BloodMarks?.Enqueue(b.Mark); break;
                case FloaterNet f: world?.Floaters?.Enqueue(f.Floater); break;
            }
        }
    }

    public void Clear() => pending.Clear();
}
