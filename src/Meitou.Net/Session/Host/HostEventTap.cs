using System.Collections.Concurrent;
using Meitou.Net.Events;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// Copies the world's display events (sounds, blood, floaters) for clients without taking them from the host's own consumers (docs/net.md, "Host").
/// <see cref="BeginFrame"/> remembers the world's three queues (the consumers': audio, blood decals, floaters; null when one is off) and puts the tap's own
/// in their place, so the frame's ticks fill the tap's. <see cref="EndFrame"/> dequeues everything, re-enqueues each item into the remembered consumer queue
/// (when there is one) and lists a copy for the network, then puts the consumer queues back, also on a new world <c>WorldSession.ReplaceWorld</c> handed the
/// tap's queue mid-frame. Ticks outside a frame (settle and post-creation ticks) feed the consumers directly and stay host-only. Frame thread only.
/// </summary>
sealed class HostEventTap
{
    readonly ConcurrentQueue<SoundEvent> sounds = new();
    readonly ConcurrentQueue<BloodMark> blood = new();
    readonly ConcurrentQueue<Floater> floaters = new();
    ConcurrentQueue<SoundEvent>? soundConsumer;
    ConcurrentQueue<BloodMark>? bloodConsumer;
    ConcurrentQueue<Floater>? floaterConsumer;
    World? hooked;

    /// <summary>Whether the tap's queues are in a world now (between <see cref="BeginFrame"/> and <see cref="EndFrame"/>).</summary>
    public bool Hooked => hooked is not null;

    /// <summary>Remembers <paramref name="world"/>'s consumer queues (a queue that is already the tap's is not remembered) and installs the tap's.</summary>
    public void BeginFrame(World world)
    {
        if (hooked is not null) Restore(hooked);
        if (!ReferenceEquals(world.Sounds, sounds)) soundConsumer = world.Sounds;
        if (!ReferenceEquals(world.BloodMarks, blood)) bloodConsumer = world.BloodMarks;
        if (!ReferenceEquals(world.Floaters, floaters)) floaterConsumer = world.Floaters;
        world.Sounds = sounds;
        world.BloodMarks = blood;
        world.Floaters = floaters;
        hooked = world;
    }

    /// <summary>
    /// Forwards everything the frame queued to the consumers, adds a copy of each to <paramref name="into"/> (null: none wanted) in queue order (sounds, then
    /// blood, then floaters), and restores the consumer queues on the hooked world and on <paramref name="world"/>. False when the world was replaced since
    /// <see cref="BeginFrame"/> (the copies then mix two worlds; the caller drops them) or the tap was not hooked.
    /// </summary>
    public bool EndFrame(World world, List<NetEvent>? into)
    {
        bool same = ReferenceEquals(hooked, world);
        while (sounds.TryDequeue(out var s))
        {
            soundConsumer?.Enqueue(s);
            into?.Add(new SoundNet(s));
        }
        while (blood.TryDequeue(out var b))
        {
            bloodConsumer?.Enqueue(b);
            into?.Add(new BloodNet(b));
        }
        while (floaters.TryDequeue(out var f))
        {
            floaterConsumer?.Enqueue(f);
            into?.Add(new FloaterNet(f));
        }
        if (hooked is not null) Restore(hooked);
        Restore(world);
        hooked = null;
        return same;
    }

    /// <summary>Hosting stopped: the consumers get what is queued and their queues back.</summary>
    public void Detach(World world) => EndFrame(world, null);

    void Restore(World world)
    {
        if (ReferenceEquals(world.Sounds, sounds)) world.Sounds = soundConsumer;
        if (ReferenceEquals(world.BloodMarks, blood)) world.BloodMarks = bloodConsumer;
        if (ReferenceEquals(world.Floaters, floaters)) world.Floaters = floaterConsumer;
    }
}
