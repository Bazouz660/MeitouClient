using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Engine;
using Meitou.Net.Events;
using Meitou.Net.Session;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Net;

/// <summary>The host's event tap (docs/net.md, "Host"): clients get copies while the host's own sound, blood and floater consumers still get every event.</summary>
public class EventTapTests
{
    static readonly CharacterId Someone = new(3, 1);

    static SoundEvent Sound(int i) => new(SoundKind.Hit, Someone, CharacterId.None, new Vector3(i, 0, 0));
    static BloodMark Blood(int i) => new(BloodMarkKind.Pool, Someone, new Vector3(0, i, 0), default, 1, 0xFF0000);
    static Floater Number(int i) => new(FloaterKind.Damage, Someone, new Vector3(0, 0, i), false, false, "Chest", default, default, 0);

    static WorldSession Session() => new(Vector3.Zero, (0, 0, 100, 100), (_, _) => 0);

    [Fact]
    public void The_hosts_consumers_get_every_event_while_the_tap_copies_them()
    {
        using var session = Session();
        var world = session.World;
        var sounds = new ConcurrentQueue<SoundEvent>();
        var blood = new ConcurrentQueue<BloodMark>();
        var floaters = new ConcurrentQueue<Floater>();
        world.Sounds = sounds;
        world.BloodMarks = blood;
        world.Floaters = floaters;
        var tap = new HostEventTap();

        for (int frame = 0; frame < 3; frame++)
        {
            tap.BeginFrame(world);
            Assert.NotSame(sounds, world.Sounds);
            for (int i = 0; i < 4; i++)
            {
                world.Sounds!.Enqueue(Sound(frame * 10 + i));
                world.BloodMarks!.Enqueue(Blood(frame * 10 + i));
                world.Floaters!.Enqueue(Number(frame * 10 + i));
            }
            tap.EndFrame(world);
            Assert.Same(sounds, world.Sounds);
            Assert.Same(blood, world.BloodMarks);
            Assert.Same(floaters, world.Floaters);
            Assert.Equal(12, tap.Batch.Count);
            Assert.Equal(Enumerable.Range(0, 4).Select(i => Sound(frame * 10 + i)), tap.Batch.OfType<SoundNet>().Select(e => e.Sound));
        }

        Assert.Equal([.. from f in Enumerable.Range(0, 3) from i in Enumerable.Range(0, 4) select Sound(f * 10 + i)], sounds.ToArray());
        Assert.Equal([.. from f in Enumerable.Range(0, 3) from i in Enumerable.Range(0, 4) select Blood(f * 10 + i)], blood.ToArray());
        Assert.Equal([.. from f in Enumerable.Range(0, 3) from i in Enumerable.Range(0, 4) select Number(f * 10 + i)], floaters.ToArray());
    }

    [Fact]
    public void A_consumer_that_is_off_stays_off_and_clients_still_get_its_events()
    {
        using var session = Session();
        var world = session.World;
        var tap = new HostEventTap();
        tap.BeginFrame(world);
        world.EmitSound(SoundKind.Hit, -1, Vector3.One);
        tap.EndFrame(world);
        Assert.Null(world.Sounds);
        Assert.Null(world.BloodMarks);
        Assert.Null(world.Floaters);
        Assert.IsType<SoundNet>(Assert.Single(tap.Batch));
    }

    [Fact]
    public void The_consumers_are_restored_across_a_world_swap()
    {
        using var session = Session();
        var floaters = new ConcurrentQueue<Floater>();
        var sounds = new ConcurrentQueue<SoundEvent>();
        var old = session.World;
        old.Floaters = floaters;
        old.Sounds = sounds;
        var tap = new HostEventTap();

        tap.BeginFrame(old);
        old.Floaters!.Enqueue(Number(1));
        var next = session.ReplaceWorld([]);   // the swap carries the floater queue (the tap's) to the new world
        next.Floaters!.Enqueue(Number(2));
        tap.EndFrame(next);

        Assert.Same(floaters, next.Floaters);
        Assert.Same(floaters, old.Floaters);
        Assert.Same(sounds, old.Sounds);
        Assert.Null(next.Sounds);
        Assert.Equal([Number(1), Number(2)], floaters.ToArray());
        Assert.Equal(2, tap.Batch.Count);

        // The next frame hooks the new world and remembers its consumer.
        tap.BeginFrame(next);
        next.Floaters!.Enqueue(Number(3));
        tap.EndFrame(next);
        Assert.Same(floaters, next.Floaters);
        Assert.Equal([Number(1), Number(2), Number(3)], floaters.ToArray());
    }
}
