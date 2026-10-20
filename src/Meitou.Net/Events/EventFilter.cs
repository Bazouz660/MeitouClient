using System.Numerics;
using Meitou.Net.Replication;
using Meitou.Simulation;

namespace Meitou.Net.Events;

/// <summary>Which events a client gets (docs/net.md, "Events"): the spatial kinds only when they happen near its camera or to a character it is sent.</summary>
public static class EventFilter
{
    /// <summary>Kinds 1-3 (sound, blood, floater) are spatial; every other kind is wanted by whoever the host sent it to.</summary>
    public static bool IsSpatial(NetEvent e) => e is SoundNet or BloodNet or FloaterNet;

    /// <summary>
    /// A non-spatial event is always wanted. A spatial one is wanted when its position lies within <paramref name="radius"/> of the viewer's eye
    /// (straight-line distance), or when a character it names (subject, other, who, target) is in <paramref name="interest"/>.
    /// </summary>
    public static bool Wants(NetEvent e, ViewState view, IReadOnlySet<CharacterId> interest, float radius) => e switch
    {
        SoundNet s => Near(s.Sound.Position, view, radius) || interest.Contains(s.Sound.Subject) || interest.Contains(s.Sound.Other),
        BloodNet b => Near(b.Mark.Position, view, radius) || interest.Contains(b.Mark.Who),
        FloaterNet f => Near(f.Floater.Position, view, radius) || interest.Contains(f.Floater.Target),
        _ => true,
    };

    static bool Near(Vector3 at, ViewState view, float radius) => Vector3.DistanceSquared(at, view.Eye) <= radius * radius;
}
