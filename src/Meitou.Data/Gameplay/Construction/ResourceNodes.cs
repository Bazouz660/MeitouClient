using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>A mineable rock (docs/game/buildings-production.md "Resources"): where it stands, its <c>world resource mining</c>, its radius and its BUILDING record.</summary>
public readonly record struct ResourceNode(Vector3 Position, int Resource, float Radius, string Record = "");

/// <summary>Where the world's mineable rocks are.</summary>
public interface IResourceNodes
{
    /// <summary>The rocks of <paramref name="resource"/> (0: of any) within <paramref name="range"/> (horizontally) of a point.</summary>
    IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range);
}

/// <summary>
/// The rocks the foliage system places (docs/formats/foliage.md, "building type"): instances whose FOLIAGE_MESH names a BUILDING, the rock's resource being that building's
/// functionality <c>world resource mining</c>. Zones are placed once and kept (through the foliage world's layout cache when it has one). Thread-safe.
/// </summary>
public sealed class FoliageResourceNodes(FoliageWorld world, GameDatabase db) : IResourceNodes, IDisposable
{
    /// <summary>The radius used for every rock: the original reads the rock's bounds and takes at least 25 (<c>uses_resources</c>); Meitou does not read the meshes (engine choice).</summary>
    public const float NodeRadius = 25;

    readonly Dictionary<ZoneCoordinate, List<ResourceNode>> zones = [];
    readonly Dictionary<string, int> resourceOf = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    public IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range)
    {
        var found = new List<ResourceNode>();
        lock (gate)
        {
            var first = WorldLayout.ZoneOf(x - range, z - range);
            var last = WorldLayout.ZoneOf(x + range, z + range);
            for (int zx = first.X; zx <= last.X; zx++)
                for (int zy = first.Y; zy <= last.Y; zy++)
                    foreach (var node in Zone(new ZoneCoordinate(zx, zy)))
                        if ((resource == 0 || node.Resource == resource) && Vector2.DistanceSquared(new(node.Position.X, node.Position.Z), new(x, z)) <= range * range) found.Add(node);
        }
        return found;
    }

    List<ResourceNode> Zone(ZoneCoordinate zone)
    {
        if (zones.TryGetValue(zone, out var list)) return list;
        list = [];
        if (zone.IsInsideGrid)
            foreach (var instance in world.Zone(zone).Instances)
                if (instance.Mesh.BuildingType is { } building && ResourceOf(building) is > 0 and var resource)
                    list.Add(new ResourceNode(instance.Position, resource, NodeRadius, building.StringId));
        zones[zone] = list;
        return list;
    }

    int ResourceOf(GameRecord building)
    {
        if (resourceOf.TryGetValue(building.StringId, out int r)) return r;
        r = building.GetReferences("functionality") is [var link, ..] && db.Find(link.TargetStringId) is { } function ? function.GetInt("world resource mining") : 0;
        return resourceOf[building.StringId] = r;
    }

    public void Dispose() => world.Dispose();
}
