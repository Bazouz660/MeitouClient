using System.Collections.Concurrent;
using Meitou.Net.Events;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// Copies the world's display events (sounds, blood marks, floaters) for the clients without taking them from the host's own consumers (docs/net.md,
/// "Host"). <see cref="BeginFrame"/> remembers the queue each consumer installed on the world (null: that consumer is off) and puts the tap's own
/// queue in its place; <see cref="EndFrame"/> drains the tap's queues into <see cref="Batch"/>, hands every item on to the remembered queue and puts
/// that queue back. A world swapped in between (<c>ReplaceWorld</c> copies the floater queue to the new world) is handled the same way.
/// Frame thread only; the simulation's parallel phases enqueue into the tap's concurrent queues.
/// </summary>
sealed class HostEventTap
{
    readonly ConcurrentQueue<SoundEvent> sounds = new();
    readonly ConcurrentQueue<BloodMark> blood = new();
    readonly ConcurrentQueue<Floater> floaters = new();
    readonly List<NetEvent> batch = [];
    ConcurrentQueue<SoundEvent>? soundConsumer;
    ConcurrentQueue<BloodMark>? bloodConsumer;
    ConcurrentQueue<Floater>? floaterConsumer;
    World? hooked;

    /// <summary>The spatial events of the last <see cref="EndFrame"/>, in the order they were queued (sounds, then blood, then floaters).</summary>
    public IReadOnlyList<NetEvent> Batch => batch;

    /// <summary>Whether the tap's queues are installed on a world (between <see cref="BeginFrame"/> and <see cref="EndFrame"/>).</summary>
    public bool Hooked => hooked is not null;

    /// <summary>Installs the tap's queues on <paramref name="world"/>, remembering the consumers' (a queue that already is the tap's is not remembered).</summary>
    public void BeginFrame(World world)
    {
        if (hooked is not null) EndFrame(world);
        if (!ReferenceEquals(world.Sounds, sounds)) soundConsumer = world.Sounds;
        if (!ReferenceEquals(world.BloodMarks, blood)) bloodConsumer = world.BloodMarks;
        if (!ReferenceEquals(world.Floaters, floaters)) floaterConsumer = world.Floaters;
        world.Sounds = sounds;
        world.BloodMarks = blood;
        world.Floaters = floaters;
        hooked = world;
    }

    /// <summary>
    /// Drains the tap's queues into <see cref="Batch"/> (cleared first), forwards each item to its remembered consumer queue and restores the consumers on
    /// <paramref name="world"/> and on the world hooked at <see cref="BeginFrame"/> when that is another one, wherever the tap's queue is still installed.
    /// </summary>
    public void EndFrame(World world)
    {
        batch.Clear();
        while (sounds.TryDequeue(out var s))
        {
            batch.Add(new SoundNet(s));
            soundConsumer?.Enqueue(s);
        }
        while (blood.TryDequeue(out var b))
        {
            batch.Add(new BloodNet(b));
            bloodConsumer?.Enqueue(b);
        }
        while (floaters.TryDequeue(out var f))
        {
            batch.Add(new FloaterNet(f));
            floaterConsumer?.Enqueue(f);
        }
        Restore(world);
        if (hooked is not null && !ReferenceEquals(hooked, world)) Restore(hooked);
        hooked = null;
    }

    void Restore(World world)
    {
        if (ReferenceEquals(world.Sounds, sounds)) world.Sounds = soundConsumer;
        if (ReferenceEquals(world.BloodMarks, blood)) world.BloodMarks = bloodConsumer;
        if (ReferenceEquals(world.Floaters, floaters)) world.Floaters = floaterConsumer;
    }

    /// <summary>Forgets the events of the last frame.</summary>
    public void ClearBatch() => batch.Clear();
}
