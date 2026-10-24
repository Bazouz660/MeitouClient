using System.Numerics;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>Why a ghost may not be placed: one value per message of docs/game/buildings-production.md "Placement", plus the data checks of the engine.</summary>
public enum PlacementFailure : byte
{
    None,
    TooCloseToTown, NotOwnedBuilding, NoResources, CropWillNotGrow, InsideIncomplete, MustBeOutside, MustBeInside, NeedsSnapNode,
    TooCloseToBuilding, NoValidUsageNodes, CharactersInTheWay, BlocksUseNodes, InvalidGround, InvalidFloor, NotLevel,
    /// <summary>Engine: the record is not a BUILDING or not research-unlocked.</summary>
    NotAvailable,
}

/// <summary>A placed town as the placement rules see it.</summary>
public readonly record struct PlacementTown(TownData Town, Vector2 Position);

/// <summary>
/// The deterministic half of the placement checks (docs/simulation.md "Buildings"): pure functions of the data, run by the host for the ghost and again by the
/// simulation on <c>PlaceBuildings</c> (the town block; <see cref="ResourceBlock"/> is the host's only). The geometric half lives in the host.
/// </summary>
public static class PlacementRules
{
    /// <summary>A building that creates a player town keeps at least this far from any town centre (<b>Verified</b>, <c>FUN_140926eb0</c>).</summary>
    public const float OutpostMinimumDistance = 2000;

    /// <summary>The block radius multiple of a building that creates a player town: 2.5 of the town's radius, 1 for ruins and points of interest.</summary>
    public const float OutpostRadiusFactor = 2.5f;

    /// <summary>The base radius of a town by kind, before <c>town radius mult</c> (<b>Verified</b>, <c>FUN_140926d50</c>): town 2500, village 937.5, ruins 875, point of interest 437.5, others 1250.</summary>
    public static float BaseRadius(TownType type) => type switch
    {
        TownType.Town => 2500,
        TownType.Village => 937.5f,
        TownType.Ruins => 875,
        TownType.PointOfInterest => 437.5f,
        _ => 1250,
    };

    /// <summary>
    /// The town block of docs/game/buildings-production.md "Placement" (Towns): a town of another faction blocks within its radius (<see cref="BaseRadius"/> x <c>town radius mult</c>);
    /// a building that creates a player town within 2.5 x that radius (1 x for ruins and points of interest) or <see cref="OutpostMinimumDistance"/> of the centre. Towns of the
    /// player's faction never block. <see cref="PlacementFailure.NotOwnedBuilding"/> (the second text of the first row) is the host's: it needs the building under the ghost.
    /// </summary>
    public static PlacementFailure TownBlock(BuildingBuildData building, Vector2 at, IReadOnlyList<PlacementTown> towns, string playerFactionId)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(towns);
        foreach (var placed in towns)
        {
            var town = placed.Town;
            if (playerFactionId.Length > 0 && town.Faction == playerFactionId) continue;
            float radius = BaseRadius(town.Type) * town.TownRadiusMult;
            float limit = radius;
            if (building.CreatesPlayerTown)
            {
                float factor = town.Type is TownType.Ruins or TownType.PointOfInterest ? 1 : OutpostRadiusFactor;
                limit = MathF.Max(factor * radius, OutpostMinimumDistance);
            }
            if (Vector2.DistanceSquared(at, placed.Position) < limit * limit) return PlacementFailure.TooCloseToTown;
        }
        return PlacementFailure.None;
    }

    /// <summary>The resource value or crop yield below which a mine or farm is refused (<b>Verified</b>, docs/game/buildings-production.md "Placement").</summary>
    public const float MinimumYield = 0.01f;

    /// <summary>
    /// The resource row of docs/game/buildings-production.md "Placement": a farm whose crop yield (fertility factor x crop match) or a building with a <c>world resource mining</c>
    /// whose <see cref="ResourceField.Yield"/> at <paramref name="at"/> is below <see cref="MinimumYield"/>. Not decided (None) without an areas map, or for IRON, COPPER and
    /// CARBON without known rocks, so a world without them refuses nothing.
    /// </summary>
    public static PlacementFailure ResourceBlock(MachineData? machine, ResourceField field, Vector3 at)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (machine is null || !field.HasMap) return PlacementFailure.None;
        if (machine.Farm is { } farm)
        {
            float fertility = farm.Indoor ? 1 : field.Value(MachineData.GroundResource, at.X, at.Y, at.Z);
            return farm.FertilityFactor(fertility) * field.CropMatch(farm, at.X, at.Z) < MinimumYield ? PlacementFailure.CropWillNotGrow : PlacementFailure.None;
        }
        if (machine.Resource == 0 || machine.Function == MachineData.NaturalMineFunction) return PlacementFailure.None;
        if (!field.HasNodes && machine.Resource is ResourceField.IronResource or ResourceField.CopperResource or ResourceField.CarbonResource) return PlacementFailure.None;
        return field.Yield(machine.Resource, at) < MinimumYield ? PlacementFailure.NoResources : PlacementFailure.None;
    }

    /// <summary>The text the original shows for a failure ("" for <see cref="PlacementFailure.None"/>); the engine's own for <see cref="PlacementFailure.NotAvailable"/>.</summary>
    public static string Message(PlacementFailure failure) => failure switch
    {
        PlacementFailure.None => "",
        PlacementFailure.TooCloseToTown => "Can't build too close to another town.",
        PlacementFailure.NotOwnedBuilding => "Can't place in a building you don't own.",
        PlacementFailure.NoResources => "No resources at this location, mining would be pointless.",
        PlacementFailure.CropWillNotGrow => "This crop will not grow in this environment.",
        PlacementFailure.InsideIncomplete => "Cannot build inside incomplete buildings.",
        PlacementFailure.MustBeOutside => "Must be placed outside or on a roof.",
        PlacementFailure.MustBeInside => "Must be placed inside a building.",
        PlacementFailure.NeedsSnapNode => "Can only be placed on certain locations (marked by blue nodes).",
        PlacementFailure.TooCloseToBuilding => "Too close to another building.",
        PlacementFailure.NoValidUsageNodes => "No valid usage nodes.",
        PlacementFailure.CharactersInTheWay => "There are characters in the way.",
        PlacementFailure.BlocksUseNodes => "The building blocks another building use nodes.",
        PlacementFailure.InvalidGround => "The building has one or more parts on invalid ground.",
        PlacementFailure.InvalidFloor => "Invalid placing floor number.",
        PlacementFailure.NotLevel => "Ground needs to be more level.",
        PlacementFailure.NotAvailable => "That building is not available yet.",
        _ => "",
    };
}
