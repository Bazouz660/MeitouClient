using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation;

/// <summary>
/// The homeless squads of the loaded area sectors after they are made (docs/game/factions-squads-towns.md "Homeless squads"), at every roaming look (2 s):
/// <list type="bullet">
/// <item><b>Patrols</b> (<c>Task_Patrol</c>, <c>FUN_1408fc730</c>): a squad whose leader acts out a PATROL goal outside a patrol-class package walks from sector to
/// sector of its biome group: a neighbouring sector (the four diagonal or the four straight ones, a coin flip), a random point in it, or with <c>patrol approaches
/// towns</c> sometimes a town of it; within 500 of the point it picks the next. The neighbours' weights are <b>Unknown</b> (equal here); the walk is the squad's
/// wander round the point (<see cref="Squad.HomeCentre"/>, <see cref="PopulationSettings.HomelessHomeRadius"/>).</item>
/// <item><b>Refill</b> (area tick <c>FUN_1408f9240</c>): while the loaded sectors hold fewer squads than the sum of P × <c>homeless spawn amount</c>, one sector
/// below its own share makes one squad. Engine choice: the loaded sectors are one island.</item>
/// <item><b>Sectors by place</b>: a squad belongs to the sector it stands in (<see cref="Refile"/>), so one that walks out of the loaded sectors leaves the world
/// (after a grace and away from the player, <see cref="MayRefile"/>)
/// (kept by its new sector with <see cref="KeepWild"/>, up to that sector's count) and its old sector is refilled, while the total stays bounded.</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>Each patrolling homeless squad's point and how many it has picked, by squad id.</summary>
    readonly Dictionary<int, (Vector2 Point, int Picks)> homelessPatrols = [];
    readonly Dictionary<string, bool> approachesTowns = new(StringComparer.Ordinal);
    readonly HashSet<int> patrolSeen = [];

    /// <summary>The distance to its point at which a patrol picks the next (<c>FUN_1407ee790</c>, the off-screen step; the loaded rule is taken to match).</summary>
    const float PatrolArrive = 500;

    void StepHomeless(World world)
    {
        if (wildSites is null || data.Biomes is null) return;
        PatrolHomeless(world);
        RefillSectors(world);
    }

    void PatrolHomeless(World world)
    {
        var table = world.Characters;
        patrolSeen.Clear();
        sectorSeen.Clear();
        refiles.Clear();
        foreach (int i in wildLoaded)
        {
            var site = wildSites![i];
            if (site.Kind != WildKind.Sector || wildStates[i].Status != SiteStatus.Loaded) continue;
            foreach (int id in wildStates[i].Squads)
            {
                if (world.Squads.Find(id) is not { } squad || !table.TryResolveNext(squad.Leader, out int slot) || !table.IsAlive(squad.Leader)) continue;
                sectorSeen.Add(id);
                var zone = WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                if (zone != site.Zone && SectorAt(zone) is var to and >= 0 && MayRefile(world, id, to, zone, slot)) refiles.Add((id, i, to));
                // Not on a patrol package's route (laid only round a home town, so a homeless squad's patrol package walks the sectors too), nor with a mission town (a
                // trader on its way, an assault or occupation roaming the town it took).
                if (table.Cold(slot)?.Ai is not { Behaviour: AiBehaviour.Patrol } || squad.Ai is { CurrentClass: PackageClass.Patrol or PackageClass.PatrolRunning } board
                    && board.RouteIndex < board.Route.Length || squad.Ai is { Destination.IsNone: false }) continue;
                patrolSeen.Add(id);
                var at = new Vector2(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                homelessPatrols.TryGetValue(id, out var patrol);
                if (patrol.Picks > 0 && Vector2.DistanceSquared(at, patrol.Point) > PatrolArrive * PatrolArrive) continue;
                if (PatrolPoint(world, squad, site.Group, at, patrol.Picks) is not { } next) continue;
                homelessPatrols[id] = (next, patrol.Picks + 1);
                squad.HomeCentre = next;
                squad.HomeRadius = settings.HomelessHomeRadius;
            }
        }
        foreach (var (id, from, to) in refiles) Refile(world, id, from, to);
        if (homelessPatrols.Count > patrolSeen.Count)
            foreach (int id in homelessPatrols.Keys.Where(k => !patrolSeen.Contains(k)).ToList()) homelessPatrols.Remove(id);
        if (outsideSince.Count > 0)
            foreach (int id in outsideSince.Keys.Where(k => !sectorSeen.Contains(k)).ToList()) outsideSince.Remove(id);
    }

    readonly HashSet<int> sectorSeen = [];

    readonly List<(int Squad, int From, int To)> refiles = [];
    /// <summary>The tick each homeless squad's leader was first seen outside the active zones, by squad id.</summary>
    readonly Dictionary<int, long> outsideSince = [];

    /// <summary>
    /// Whether a squad standing in another sector moves to it now: at once into a loaded one; into one that is not loaded only once its leader has been outside the
    /// active zones for <see cref="PopulationSettings.RoamUnloadGraceSeconds"/> and is <see cref="PopulationSettings.HomelessPlayerDistance"/> from every player
    /// character (engine choice, as roaming squads leave the world, so it does not vanish in view).
    /// </summary>
    bool MayRefile(World world, int id, int to, ZoneCoordinate zone, int slot)
    {
        if (wildStates[to].Status == SiteStatus.Loaded) return true;
        if (active.Contains(zone))
        {
            outsideSince.Remove(id);
            return false;
        }
        if (!outsideSince.TryGetValue(id, out long since)) outsideSince[id] = since = world.Tick;
        if (world.Tick - since < (long)Math.Ceiling(settings.RoamUnloadGraceSeconds / world.TickSeconds)) return false;
        var p = world.Characters.Previous[slot].Position;
        var at = new Vector2(p.X, p.Z);
        foreach (var player in PlayerSpots(world))
            if (Vector2.DistanceSquared(player, at) < settings.HomelessPlayerDistance * settings.HomelessPlayerDistance) return false;
        outsideSince.Remove(id);
        return true;
    }

    /// <summary>The sector site of a zone, or -1.</summary>
    int SectorAt(ZoneCoordinate zone)
    {
        if (wildByZone.TryGetValue(zone, out var list))
            foreach (int i in list)
                if (wildSites![i].Kind == WildKind.Sector) return i;
        return -1;
    }

    /// <summary>
    /// A homeless squad that walked into another sector is that sector's (the original files a platoon in the sector it stands in, <c>FUN_1408f6c80</c>): a loaded
    /// one takes it as it is; an unloaded one keeps it as its kept squads (the Meitou <see cref="KeepWild"/> switch), or, Faithful, it leaves the world as the
    /// original's off-screen homeless platoons do.
    /// </summary>
    void Refile(World world, int id, int from, int to)
    {
        wildStates[from].Squads.Remove(id);
        var state = wildStates[to];
        if (state.Status == SiteStatus.Loaded)
        {
            state.Squads.Add(id);
            return;
        }
        var one = new SiteState();
        one.Squads.Add(id);
        // Kept only up to the sector's own count, so sectors round where the player goes do not pile them up (engine choice).
        if (!KeepWild || KeptWildOf(to).Count >= HomelessCount(data.Biomes!.Groups[wildSites![to].Group], settings.PopulationMultiplier))
        {
            Drop(world, one);
            return;
        }
        keptWild.Remove(to, out var before);
        KeepWildSquads(world, to, one);
        if (before is null) return;
        if (keptWild.TryGetValue(to, out var now)) before.AddRange(now);
        keptWild[to] = before;
    }

    /// <summary>
    /// The next point of a patrol from <paramref name="at"/> (<c>FUN_1408fc730</c>): a neighbouring sector of the group, then in it a town (a player's at even odds with
    /// weight 1, another's at 1 in 4 with weight 0.2) when the template approaches towns, else a placed spot (<c>FUN_1408f50d0</c>); null when none is found.
    /// </summary>
    Vector2? PatrolPoint(World world, Squad squad, int group, Vector2 at, int picks)
    {
        var biomes = data.Biomes!;
        ulong key = Rng.Mix(SeedSalts.HomelessPatrol ^ (ulong)squad.Id << 20 ^ (uint)picks);
        ulong n = 0;
        ulong Roll() => Rng.Hash(world.Seed, key, RngPurpose.Wander, n++);
        var (cx, cz) = WeatherAreas.CellOf(at.X, at.Y);
        Span<(int X, int Z)> near = stackalloc (int, int)[4];
        bool diagonal = Rng.Float(Roll()) < 0.5f;
        int count = 0;
        for (int k = 0; k < 4; k++)
        {
            (int X, int Z) cell = diagonal ? (cx + ((k & 1) == 0 ? 1 : -1), cz + (k < 2 ? 1 : -1)) : k switch { 0 => (cx + 1, cz), 1 => (cx, cz + 1), 2 => (cx - 1, cz), _ => (cx, cz - 1) };
            if (biomes.GroupOf(cell.X, cell.Z) == group) near[count++] = cell;
        }
        // No neighbour of the group: a point of its own sector (the original's fallback, FUN_1408fc1a0, picks another sector of the group; Unknown which).
        var (tx, tz) = count == 0 ? (cx, cz) : near[Rng.Int(Roll(), count)];
        if (ApproachesTowns(squad.TemplateId))
        {
            var zone = new ZoneCoordinate(tx, tz);
            float total = 0;
            TownSite? pick = null;
            foreach (var s in data.Sites)
            {
                if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null || WorldLayout.ZoneOf(s.Position.X, s.Position.Z) != zone) continue;
                bool players = data.PlayerFaction >= 0 && data.SiteFactions[s.Index] == data.PlayerFaction;
                if (Rng.Float(Roll()) >= (players ? 0.5f : 0.25f)) continue;
                float w = players ? 1 : 0.2f;
                total += w;
                if (Rng.Float(Roll()) * total < w) pick = s;   // a running weighted pick
            }
            if (pick is not null) return new Vector2(pick.Position.X, pick.Position.Z);
        }
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(tx, tz);
        return PlaceInSector(minX, minZ, maxX, maxZ, squad.Faction, world.Seed, key, world.Walkability, []);
    }

    bool ApproachesTowns(string templateId)
    {
        if (!approachesTowns.TryGetValue(templateId, out bool yes))
            approachesTowns[templateId] = yes = data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r).PatrolApproachesTowns;
        return yes;
    }

    /// <summary>
    /// The area tick's homeless branch (<c>FUN_1408f9240</c>): when the loaded sectors' squads (a sector with its own town counts at most 2) are below the sum of their
    /// shares, one sector is drawn by weight max(0, share + 1 − standing), halved with its own town, (w + 1) × 2 when nobody stands in it; one below its share makes
    /// one squad (built on the population thread), else the next draw.
    /// </summary>
    void RefillSectors(World world)
    {
        var groups = data.Biomes!.Groups;
        var table = world.Characters;
        var standing = new Dictionary<ZoneCoordinate, int>();
        var sectors = new List<int>();
        float target = 0, load = 0;
        foreach (int i in wildLoaded)
        {
            var site = wildSites![i];
            if (site.Kind != WildKind.Sector) continue;
            var state = wildStates[i];
            if (state.Status != SiteStatus.Loaded || state.Pending) return;   // one at a time, as the original's tick
            sectors.Add(i);
            target += settings.PopulationMultiplier * groups[site.Group].HomelessSpawnAmount;
            int held = 0;
            foreach (int id in state.Squads)
            {
                if (world.Squads.Find(id) is not { } squad) continue;
                CharacterId? first = null;
                foreach (var m in squad.Members)
                    if (Living(world, m)) { first = m; break; }
                if (first is not { } who || !table.TryResolveNext(who, out int slot)) continue;
                held++;
                var p = table.Previous[slot].Position;
                var zone = WorldLayout.ZoneOf(p.X, p.Z);
                standing[zone] = standing.GetValueOrDefault(zone) + 1;
            }
            load += OwnTown(site) ? Math.Min(held, 2) : held;
        }
        if (sectors.Count == 0 || load >= target) return;
        var weights = new float[sectors.Count];
        for (int k = 0; k < sectors.Count; k++)
        {
            var site = wildSites![sectors[k]];
            float share = settings.PopulationMultiplier * groups[site.Group].HomelessSpawnAmount;
            int here = standing.GetValueOrDefault(site.Zone);
            float w = MathF.Max(0, share + 1 - here);
            if (OwnTown(site)) w *= 0.5f;
            if (here == 0) w = (w + 1) * 2;
            weights[k] = here < share ? w : 0;   // a sector at its share is drawn and passed over in the original; it never makes one
        }
        ulong key = Rng.Mix(SeedSalts.HomelessRefill ^ (ulong)world.Tick);
        int picked = Pick(weights, Rng.Hash(world.Seed, key, RngPurpose.Spawn, 0));
        if (picked < 0) return;
        int index = sectors[picked];
        var at = wildSites![index];
        var st = wildStates[index];
        st.Pending = true;
        var hold = new WildHold(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
        foreach (int id in st.Squads)
            if (world.Squads.Find(id) is { } squad && squad.Members.Any(m => Living(world, m))) hold.Templates[squad.TemplateId] = hold.Templates.GetValueOrDefault(squad.TemplateId) + 1;
        long loadId = st.LoadId;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        var players = PlayerSpots(world);
        int number = st.Made;
        if (work is null) SpawnWild(world, BuildSector(at, loadId, seed, walk, players, hold, number));
        else work.Add(() => builtWild.Enqueue(BuildSector(at, loadId, seed, walk, players, hold, number)));
    }

    /// <summary>Whether the town nearest a sector's centre stands in it (<c>FUN_1408f62e0</c>).</summary>
    bool OwnTown(WildSite site)
    {
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(site.Zone.X, site.Zone.Y);
        return NearestTown(new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2), out _) is { } town && WorldLayout.ZoneOf(town.Position.X, town.Position.Z) == site.Zone;
    }
}
