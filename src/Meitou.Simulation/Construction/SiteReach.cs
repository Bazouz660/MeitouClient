using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>
/// How near a worker must stand to a building to build, operate or mine it (docs/game/buildings-production.md "Work distance"): within the building's reach of it, twice that once the
/// worker has stopped (the original's <c>isMoving</c> doubling). Engine choice: the gap is to the building's footprint hull (the original measures to its position marker and also counts a
/// worker touching its collision), else to its position.
/// </summary>
public static class SiteReach
{
    /// <summary>The construction reach of any building (<c>Building::getReachRange</c>, <b>Observed</b>).</summary>
    public const float Building = 40;
    /// <summary>The construction reach of a wall (<c>WallBuilding::getReachRange</c>, <b>Observed</b>).</summary>
    public const float Wall = 60;

    /// <summary>The X/Z gap from <paramref name="from"/> to the site: its footprint hull by <paramref name="edge"/> (0 inside), else its position.</summary>
    public static float Gap(Func<BuildSite, Vector2, float?>? edge, BuildSite site, Vector3 from) =>
        edge?.Invoke(site, new Vector2(from.X, from.Z)) ?? Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(site.Position.X, site.Position.Z));

    /// <summary>Within <paramref name="reach"/>, or twice it when the character has no path (it has stopped).</summary>
    public static bool Within(in CharacterHot n, float gap, float reach) =>
        gap < reach || ((n.Flags & (ushort)MoveFlags.AnyPath) == 0 && gap < 2 * reach);
}
