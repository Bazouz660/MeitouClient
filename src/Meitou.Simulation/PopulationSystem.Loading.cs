using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>Queues nearby population and applies completed builds during loading, without moving squads or advancing their timers.</summary>
    internal bool PrepareLoading(World world, Vector3 cameraFocus)
    {
        focus = cameraFocus;
        world.Uniques.SeedPrisoners(data.Db);
        RefreshStates(world);
        Inputs(world, []);
        // Towns loaded behind the loading screen keep their TOWN, as the original checks no override then (its ZoneManager +8 byte).
        loadingScreen = true;
        try { RefreshZones(world); }
        finally { loadingScreen = false; }
        // A copy, as the tick's own loop: activating can change the registry (a town's squad that lost too many is made afresh as a new platoon).
        foreach (var platoon in world.Platoons.All.ToList())
            if (platoon.State == PlatoonState.Unloaded && Wanted(platoon) && Ready(platoon))
                Activate(world, platoon);

        foreach (var site in data.Sites)
            if (active.Contains(site.Zone) && states[site.Index].Status != SiteStatus.Loaded) return false;
        foreach (var zone in active)
            if (wildByZone.TryGetValue(zone, out var sites))
                foreach (int site in sites)
                    if (wildStates[site].Status != SiteStatus.Loaded) return false;
        return !world.Platoons.All.Any(p => p.State == PlatoonState.Activating && Wanted(p));
    }
}
