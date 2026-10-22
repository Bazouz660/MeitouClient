using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Locks;

namespace Meitou.Navigation;

/// <summary>
/// Where a building's doors are: the box of door i's collision (the shapes the navmesh paints as the door, docs/game/pathfinding.md) in the building's frame, scaled by the
/// building's <c>scale</c>, not rotated or moved. Read once per (BUILDING, door) from the data; safe from any thread.
/// </summary>
public sealed class NavDoorGeometry(GameInstall install, GameDatabase db)
{
    readonly CollisionCache collision = new(install);
    readonly ConcurrentDictionary<(string Building, int Door), DoorSpot?> spots = new();

    /// <summary>The leaf's centre, the normal of its thinner horizontal side and half its width along the other (null: no collision found).</summary>
    public DoorSpot? Spot(string buildingRecordId, int doorIndex) => spots.GetOrAdd((buildingRecordId, doorIndex), Find);

    DoorSpot? Find((string Building, int Door) key)
    {
        if (db.Find(key.Building) is not { Type: FcsRecordType.BUILDING } building) return null;
        float scale = building.GetFloat("scale", 1);
        var node = Matrix4x4.CreateScale(scale <= 0 ? 1 : scale);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var mesh in WorldObjectLayout.Building(db, building, "", Vector3.Zero, Quaternion.Identity))
        {
            if (mesh.DoorIndex != key.Door || collision.Get(mesh.Source.GetPath("xml collision")) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (var v in shape.Vertices)
                {
                    var p = Vector3.Transform(v, node);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
        }
        if (lo.X > hi.X) return null;
        var size = hi - lo;
        return size.X <= size.Z ? new DoorSpot((lo + hi) / 2, Vector3.UnitX, size.Z / 2, size.Y / 2) : new DoorSpot((lo + hi) / 2, Vector3.UnitZ, size.X / 2, size.Y / 2);
    }
}
