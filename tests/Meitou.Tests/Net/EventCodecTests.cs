using System.Numerics;
using Meitou.Net;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Net;

/// <summary>The event batch codec and the spatial filter (docs/net.md, "Events").</summary>
public class EventCodecTests
{
    static EventBatch RoundTrip(EventBatch batch)
    {
        using var w = new NetWriter();
        EventCodec.Write(w, batch);
        var r = new NetReader(w.Written);
        var back = EventCodec.Read(ref r);
        r.ExpectEnd();
        return back;
    }

    [Fact]
    public void Every_kind_round_trips()
    {
        var a = new CharacterId(5, 2);
        var b = new CharacterId(900, 17);
        var events = new NetEvent[]
        {
            new SoundNet(new SoundEvent(SoundKind.Hit, a, b, new Vector3(10.5f, -2.25f, 300.125f), true, false, true, false, true, "Katana")),
            new SoundNet(new SoundEvent(SoundKind.Shot, CharacterId.None, CharacterId.None, new Vector3(1, 2, 3))),
            new BloodNet(new BloodMark(BloodMarkKind.Explosion, a, new Vector3(-5, 0, 7.5f), new Vector2(0.5f, -0.25f), 1, 0x8A1020, "Skeleton")),
            new BloodNet(new BloodMark(BloodMarkKind.Pool, b, new Vector3(1, 1, 1), default, 0.125f, 0xFFFFFF)),
            new FloaterNet(new Floater(FloaterKind.Damage, a, new Vector3(4, 5, 6), true, false, "Chest", new HitDamage(1.5f, 2.5f, 3.5f, 0.5f, 2), new ArmourAbsorbed(0.25f, 0.5f, 0.75f), 0)),
            new FloaterNet(Floater.Heal(b, new Vector3(0, 0, 0), 4.5f)),
            new MessageNet("The caravan was robbed."),
            new KnockoutNet(new StealthKoEntry(123456789012L, a, b, 0.375f, true)),
            new ChatNet(3, "Mina", "hello there"),
            new NoticeNet(NoticeKind.Kicked, 2, "Bob", "Too loud"),
            new NoticeNet(NoticeKind.Joined, 1, "Ann", ""),
        };
        var back = RoundTrip(new EventBatch(7, 424242, events));
        Assert.Equal((ushort)7, back.Epoch);
        Assert.Equal(424242, back.Tick);
        Assert.Equal(events, back.Events);
    }

    static readonly ViewState View = new(new Vector3(100, 0, 100), new Vector3(100, 0, 100), 500);
    static readonly CharacterId Seen = new(1, 1);
    static readonly CharacterId Unseen = new(2, 1);
    static readonly IReadOnlySet<CharacterId> Interest = new HashSet<CharacterId> { Seen };
    static readonly IReadOnlySet<CharacterId> Nobody = new HashSet<CharacterId>();

    [Fact]
    public void Spatial_events_are_wanted_inside_the_radius_only()
    {
        var near = new SoundNet(new SoundEvent(SoundKind.Hit, Unseen, Unseen, new Vector3(150, 0, 100)));
        var far = new SoundNet(new SoundEvent(SoundKind.Hit, Unseen, Unseen, new Vector3(5000, 0, 100)));
        Assert.True(EventFilter.IsSpatial(near));
        Assert.True(EventFilter.Wants(near, View, Nobody, 1000));
        Assert.False(EventFilter.Wants(far, View, Nobody, 1000));
        Assert.True(EventFilter.Wants(far, View, Nobody, 10_000));

        Assert.False(EventFilter.Wants(new BloodNet(new BloodMark(BloodMarkKind.Pool, Unseen, new Vector3(5000, 0, 0), default, 1, 0)), View, Nobody, 1000));
        Assert.False(EventFilter.Wants(new FloaterNet(Floater.Heal(Unseen, new Vector3(5000, 0, 0), 1)), View, Nobody, 1000));
    }

    [Fact]
    public void Interest_overrides_distance()
    {
        var farSubject = new SoundNet(new SoundEvent(SoundKind.Hit, Seen, Unseen, new Vector3(5000, 0, 0)));
        var farOther = new SoundNet(new SoundEvent(SoundKind.Hit, Unseen, Seen, new Vector3(5000, 0, 0)));
        Assert.True(EventFilter.Wants(farSubject, View, Interest, 1000));
        Assert.True(EventFilter.Wants(farOther, View, Interest, 1000));
        Assert.True(EventFilter.Wants(new BloodNet(new BloodMark(BloodMarkKind.Pool, Seen, new Vector3(5000, 0, 0), default, 1, 0)), View, Interest, 1000));
        Assert.True(EventFilter.Wants(new FloaterNet(Floater.Heal(Seen, new Vector3(5000, 0, 0), 1)), View, Interest, 1000));
        Assert.False(EventFilter.Wants(farSubject, View, Nobody, 1000));
    }

    [Fact]
    public void Non_spatial_events_are_always_wanted()
    {
        NetEvent[] events =
        [
            new MessageNet("x"), new KnockoutNet(default), new ChatNet(0, "a", "b"), new NoticeNet(NoticeKind.Left, 1, "a", ""),
        ];
        foreach (var e in events)
        {
            Assert.False(EventFilter.IsSpatial(e));
            Assert.True(EventFilter.Wants(e, View, Nobody, 0));
        }
    }
}
