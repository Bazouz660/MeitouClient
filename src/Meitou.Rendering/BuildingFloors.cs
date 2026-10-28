using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>One placed building's floors for the footstep surface and the camera: its two ground materials and its floor collision, made on a worker when first needed.</summary>
sealed class BuildingFloor
{
    /// <summary>The placement's key (the interior view's id for it).</summary>
    public required string Key { get; init; }
    public required Vector3 Position { get; init; }
    /// <summary>The building's <c>interior material</c> and <c>exterior material</c> (GroundType).</summary>
    public required int Interior { get; init; }
    public required int Exterior { get; init; }
    public required Func<FloorSurfaces> Make { get; init; }

    Task<FloorSurfaces>? job;

    /// <summary>The floor collision, or null while it is being made.</summary>
    public FloorSurfaces? Surfaces => job is { IsCompletedSuccessfully: true } j ? j.Result : null;

    public void Request() => job ??= BackgroundWork.Run(Make);
}

/// <summary>
/// The building floors near a point: for <c>Surface_Material</c> (docs/audio.md "Surface"; the game casts a ray down from 20 units above the character and, when the
/// closest hit is a building, takes that building's <c>exterior material</c> or, with the character inside a building, its <c>interior material</c>) and for the strategy
/// camera's ground (docs/formats/camera.md "Height"). Display only: the floors are read from the placements and their collision files, nothing of it reaches the simulation.
/// A zone's buildings are listed on a worker on first need, a building's floor collision when a point first comes within <see cref="Reach"/>; until it is there the answer
/// is "no building" and the caller keeps to the terrain.
/// </summary>
sealed class BuildingFloors(WorldObjects objects)
{
    /// <summary>How far (units, X/Z) from a building's position its floors are looked for (engine choice: the largest buildings reach about this far).</summary>
    public const float Reach = 400;
    /// <summary>How far above the feet the ray starts (<c>FUN_14065f160</c>: 20), and how far below the feet a floor still counts as the one stood on (engine choice: the heights characters stand at come from the navigation mesh).</summary>
    public const float RayStart = 20, Tolerance = 4;

    readonly Dictionary<ZoneCoordinate, Task<BuildingFloor[]>> zones = [];
    readonly List<(BuildingFloor Floor, FloorSurfaces Surfaces)> near = [];
    int version;

    /// <summary>The GroundType of the building floor under <paramref name="feet"/> (the closest of the buildings' floors within the tolerance), or -1 for none.</summary>
    public int MaterialUnder(Vector3 feet, bool inside)
    {
        BuildingFloor? hit = null;
        float best = float.NegativeInfinity;
        foreach (var (floor, surfaces) in Near(feet.X, feet.Z))
            if (surfaces.HighestBelow(feet.X, feet.Z, feet.Y + RayStart) is { } h && h > best)
            {
                best = h;
                hit = floor;
            }
        return hit is null || best < feet.Y - Tolerance ? -1 : inside ? hit.Interior : hit.Exterior;
    }

    /// <summary>
    /// The highest ordinary floor part (no stairs) of any building at (<paramref name="x"/>, <paramref name="z"/>) on floors up to <paramref name="floorOf"/> of its key,
    /// or null for none.
    /// </summary>
    public float? HighestFloorAt(float x, float z, Func<string, int> floorOf)
    {
        float? best = null;
        foreach (var (floor, surfaces) in Near(x, z))
            if (surfaces.HighestUpTo(x, z, floorOf(floor.Key)) is { } h && (best is null || h > best)) best = h;
        return best;
    }

    /// <summary>The buildings within <see cref="Reach"/> whose floors are made (asking for the rest); the list is reused by the next call.</summary>
    List<(BuildingFloor, FloorSurfaces)> Near(float x, float z)
    {
        if (version != objects.PlacementVersion)
        {
            zones.Clear();
            version = objects.PlacementVersion;
        }
        near.Clear();
        // The zones the reach touches: its four corners.
        Span<ZoneCoordinate> touched = stackalloc ZoneCoordinate[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            var zone = WorldLayout.ZoneOf(x + ((i & 1) == 0 ? -Reach : Reach), z + ((i & 2) == 0 ? -Reach : Reach));
            if (!zone.IsInsideGrid || touched[..count].Contains(zone)) continue;
            touched[count++] = zone;
        }
        foreach (var zone in touched[..count])
        {
            if (!zones.TryGetValue(zone, out var job)) zones[zone] = job = BackgroundWork.Run(() => objects.FloorsOf(zone));
            if (!job.IsCompletedSuccessfully) continue;
            foreach (var floor in job.Result)
            {
                if (Vector2.DistanceSquared(new(floor.Position.X, floor.Position.Z), new(x, z)) > Reach * Reach) continue;
                if (floor.Surfaces is { } surfaces) near.Add((floor, surfaces));
                else floor.Request();
            }
        }
        return near;
    }
}
