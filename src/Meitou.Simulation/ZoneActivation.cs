using Meitou.Simulation.Items;
using Meitou.Simulation.Bodies;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// The zones the original keeps loaded (docs/game/character-stats.md "Corpses", <b>Verified</b>): the camera's 3 x 3 block and the box round the player's characters.
/// </summary>
public static class ZoneActivation
{
    /// <summary>Half the side of the box round each player character whose zones are asked for (<c>FUN_140a0f640</c>; its constant is 340.0, <b>Verified</b>).</summary>
    public const float Box = 340;

    /// <summary>The camera's distance from the centre zone's middle, per axis, past which the original's loaded block moves (0.9 zones, <c>FUN_140a0f820</c>, <b>Verified</b>).</summary>
    public const float RecentreDistance = 4147.2f;

    /// <summary>How long a zone of the camera's block stays loaded once nothing asks for it (timer index 0 of <c>FUN_140a0e570</c>, <b>Verified</b>: 4 s).</summary>
    public const float LoadedLingerSeconds = 4;

    /// <summary>How long a zone of the player's box stays loaded once nothing asks for it (timer index 1: the table default, 6 s; docs/game/character-stats.md "Corpses").</summary>
    public const float BoxLingerSeconds = 6;

    /// <summary>The middle of a zone, X and Z.</summary>
    public static Vector2 CentreOf(ZoneCoordinate zone)
    {
        var (x, z) = WorldLayout.ZoneOrigin(zone);
        return new Vector2((float)x + WorldLayout.ZoneSize / 2f, (float)z + WorldLayout.ZoneSize / 2f);
    }

    /// <summary>Every zone overlapping the box of +-<see cref="Box"/> round <paramref name="p"/>, plus <paramref name="ring"/> more zones on every side (0 or 1, the <c>Fast zone hopping</c> setting).</summary>
    public static HashSet<ZoneCoordinate> ZonesAround(Vector3 p, int ring)
    {
        var set = new HashSet<ZoneCoordinate>();
        AddZonesAround(p, ring, set);
        return set;
    }

    /// <summary>Adds the zones of <see cref="ZonesAround"/> to <paramref name="set"/>, in the same order, without making a set of their own.</summary>
    public static void AddZonesAround(Vector3 p, int ring, HashSet<ZoneCoordinate> set)
    {
        var lo = WorldLayout.ZoneOf(p.X - Box, p.Z - Box);
        var hi = WorldLayout.ZoneOf(p.X + Box, p.Z + Box);
        for (int x = lo.X - ring; x <= hi.X + ring; x++)
            for (int y = lo.Y - ring; y <= hi.Y + ring; y++)
                set.Add(new ZoneCoordinate(x, y));
    }
}
