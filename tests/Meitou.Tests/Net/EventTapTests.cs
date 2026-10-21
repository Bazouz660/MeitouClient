using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Engine;
using Meitou.Net.Events;
using Meitou.Net.Session;
using Meitou.Simulation;

namespace Meitou.Tests.Net;

/// <summary>The host's event tap (docs/net.md, "Host"): clients get copies of the frame's sounds, blood and floaters, and the host's own consumers still get every one.</summary>
public class EventTapTests
{
    static readonly CharacterId Someone = new(3, 1);

    static WorldSession NewSession() => new(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);

    [Fact]
    public void Host_consumers_still_get_every_event_while_the_tap_forwards_copies()
    {
        using var session = NewSession();
        var world = session.World;
        var sounds = new ConcurrentQueue<SoundEvent>();
        var floaters = new ConcurrentQueue<Floater>();
        world.Sounds = sounds;
        world.Floaters = floaters;   // blood stays off on the host
        var tap = new HostEventTap();

        tap.BeginFrame(world);
        Assert.NotSame(sounds, world.Sounds);
        Assert.NotNull(world.BloodMarks);   // clients still get blood the host does not draw
        world.EmitSound(SoundKind.Hit, -1, new Vector3(1, 0, 0));
        world.EmitSound(SoundKind.Blocked, -1, new Vector3(2, 0, 0));
        world.BloodMarks!.Enqueue(new BloodMark(BloodMarkKind.Pool, Someone, Vector3.One, Vector2.UnitX, 0.5f, 0xAA0000));
        world.Floaters!.Enqueue(Floater.Heal(Someone, Vector3.One, 2));
        var copies = new List<NetEvent>();
        Assert.True(tap.EndFrame(world, copies));

        Assert.Same(sounds, world.Sounds);
        Assert.Null(world.BloodMarks);
        Assert.Same(floaters, world.Floaters);
        Assert.Equal([SoundKind.Hit, SoundKind.Blocked], sounds.Select(s => s.Kind));
        Assert.Single(floaters);
        Assert.Collection(copies,
            e => Assert.Equal(SoundKind.Hit, Assert.IsType<SoundNet>(e).Sound.Kind),
            e => Assert.Equal(SoundKind.Blocked, Assert.IsType<SoundNet>(e).Sound.Kind),
            e => Assert.Equal(Someone, Assert.IsType<BloodNet>(e).Mark.Who),
            e => Assert.Equal(2, Assert.IsType<FloaterNet>(e).Floater.Healed));

        // Outside a frame (a settle tick) events go straight to the host's consumers and are not copied.
        world.EmitSound(SoundKind.Hit, -1, Vector3.Zero);
        copies.Clear();
        tap.BeginFrame(world);
        Assert.True(tap.EndFrame(world, copies));
        Assert.Empty(copies);
        Assert.Equal(3, sounds.Count);
    }

    [Fact]
    public void Consumer_queues_come_back_on_a_world_that_replaced_the_hooked_one()
    {
        using var session = NewSession();
        var floaters = new ConcurrentQueue<Floater>();
        var sounds = new ConcurrentQueue<SoundEvent>();
        session.World.Floaters = floaters;
        session.World.Sounds = sounds;
        var tap = new HostEventTap();

        tap.BeginFrame(session.World);
        session.World.Floaters!.Enqueue(Floater.Heal(Someone, Vector3.Zero, 1));
        var next = session.ReplaceWorld([]);   // hands the new world the floater queue it holds: the tap's
        Assert.NotSame(floaters, next.Floaters);
        next.Floaters!.Enqueue(Floater.Heal(Someone, Vector3.Zero, 2));
        var copies = new List<NetEvent>();
        Assert.False(tap.EndFrame(next, copies));

        Assert.Same(floaters, next.Floaters);
        Assert.Null(next.Sounds);   // never the tap's on the new world: the host hooks its own again
        Assert.Equal([1f, 2f], floaters.Select(f => f.Healed));
    }
}
