using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The simulation's rooms and lamps of the town buildings (<see cref="SightMap"/>): the building's indoor volume from its <c>interior mask</c> collision and its lights with those of
/// its layouts, both from the level data the renderer builds its own from. Called on the simulation thread when a town registers; the collision files are cached.
/// </summary>
sealed class BuildingSight(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts) : ISightSource
{
    readonly InteriorCollisions collisions = new(install);

    public InteriorVolume? Room(TownBuilding building) =>
        building.Resolved is { } r ? InteriorVolume.For(db, r.Record, building.Key, r.Position, r.Placement.Rotation, collisions) : null;

    public IReadOnlyList<WorldLight> Lights(TownBuilding building) =>
        building.Resolved is { } r ? WorldLights.ForBuilding(db, levels, r, layouts) : [];
}
