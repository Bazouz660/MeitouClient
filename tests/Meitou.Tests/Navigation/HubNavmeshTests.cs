using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>The navmesh pipeline on The Hub (zones 20.32 and 21.32) against the Kenshi install.</summary>
[Slow]
public class HubNavmeshTests
{
    static readonly object Gate = new();
    static readonly Dictionary<ZoneCoordinate, (ZoneGeometry Geometry, ZoneNavMesh Full)> built = [];

    // With the zones' interior files, as the game and `meitou-tools navmesh --fingerprint` load them (the pins come from the latter).
    static readonly Lazy<WorldLevelData?> levels = new(() => InstallData.Install is { } i ? WorldLevelData.Load(i, includeInteriors: true) : null);

    static (GameInstall Install, GameDatabase Db, WorldLevelData Levels)? Load() =>
        InstallData.Install is { } install ? (install, InstallData.FullLoadOrder!, levels.Value!) : null;

    static (ZoneGeometry Geometry, ZoneNavMesh Full) Zone(ZoneCoordinate zone)
    {
        var (install, db, levels) = Load()!.Value;
        lock (Gate)
        {
            if (built.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
            var g = gatherer.Gather(zone);
            var mesh = NavMeshPipeline.BuildZone(gatherer, g, new NavBuildSettings(), out _);
            return built[zone] = (g, mesh);
        }
    }

    static readonly ZoneCoordinate West = new(20, 32), East = new(21, 32);

    static string Pin(PathResult path) =>
        path.Found ? string.Join("\n", path.Points.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}"))) : "none";

    [Fact]
    public void The_hub_paths_keep_their_exact_points()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var west = Zone(West).Full;
        var east = Zone(East).Full;
        var house = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()), new NavDoors());
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        Assert.Equal(HubPathPins.StreetToHouseFloor.ReplaceLineEndings("\n"), Pin(house.FindPath(street, floor)));
        Assert.Equal(HubPathPins.HouseFloorToStreet.ReplaceLineEndings("\n"), Pin(house.FindPath(floor, street)));
        var query = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()).With(east.WithoutPruned()));
        Vector3 At(float x, float z) => new(x, 0, z);
        var outsideWest = At(-53500, 2000);
        var centre = At(-51000, 2900);
        var outsideEast = At(-49500, 3500);
        Assert.Equal(HubPathPins.WestToCentre.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre)));
        Assert.Equal(HubPathPins.CentreToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(centre, outsideEast)));
        Assert.Equal(HubPathPins.WestToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, outsideEast)));
        Assert.Equal(HubPathPins.WestToCentreDoorsClosed.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre, new NavAgent { DoorsClosed = true })));
    }

    [Fact]
    public void The_hub_zone_gathers_terrain_buildings_doors_and_seeds()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (g, _) = Zone(West);
        // 132 x 132 quads of every second heightmap sample (the zone plus the margin), two triangles each.
        Assert.Equal(132 * 132 * 2, g.Stats.TerrainTriangles + g.Stats.WaterTriangles);
        Assert.InRange(g.Stats.Buildings, 100, 200);
        Assert.True(g.Stats.PartsWithCollision > 100);
        Assert.True(g.Stats.WalkableTriangles > 500 && g.Stats.CuttingTriangles > 5000);
        Assert.True(g.Painters.Count >= 3, $"{g.Painters.Count} door painters");
        Assert.True(g.Carvers.Count > 50);
        Assert.True(g.Seeds.Count >= 9);
        Assert.Equal(0, g.Stats.MissingFiles);
        // Every vertex finite and inside a sane box.
        Assert.All(g.Vertices, v => Assert.True(float.IsFinite(v)));
        Assert.True(g.MaxY < 6000 && g.MinY > -50);
    }

    [Fact]
    public void A_path_leads_from_the_street_into_a_house_through_its_door_and_back()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var (_, west) = Zone(West);
        var world = NavWorld.Empty.With(west.WithoutPruned());
        var doors = new NavDoors();
        var query = new NavQuery(world, doors);
        // The Storm House by the Hub's west wall (placement 4933): street outside its door, a point on its floor.
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        var inward = query.FindPath(street, floor);
        Assert.True(inward.Found, "no way in");
        var outward = query.FindPath(floor, street);
        Assert.True(outward.Found, "no way out");
        foreach (var path in new[] { inward, outward })
        {
            float length = 0;
            for (int i = 1; i < path.Points.Count; i++) length += Vector3.Distance(path.Points[i - 1], path.Points[i]);
            Assert.InRange(length, 130, 400);
            // It stands on the floor at the end, 25 above the ground outside, and crosses a door polygon on the way.
            Assert.Contains(path.Points, p => p.Y > 1575);
            bool crossedDoor = false;
            for (int i = 1; i < path.Points.Count; i++)
            {
                int steps = (int)(Vector3.Distance(path.Points[i - 1], path.Points[i]) / 1.5f) + 1;
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(path.Points[i - 1], path.Points[i], k / (float)steps);
                    if (world.TryFindPolygon(p, 2, out var r, out _) && world.Mesh(r.Zone).Areas[r.Polygon] == NavArea.Door) crossedDoor = true;
                }
            }
            Assert.True(crossedDoor, "the way does not cross the door");
        }
        // With that door closed the room is shut (its walls are solid).
        doors.Close("20.32:4933-Newwworld-INGAME");
        Assert.False(query.FindPath(street, floor).Found);
        doors.Open("20.32:4933-Newwworld-INGAME");
        Assert.True(query.FindPath(street, floor).Found);
    }
}
