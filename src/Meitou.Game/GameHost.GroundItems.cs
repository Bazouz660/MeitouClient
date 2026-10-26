using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// Loose items on the ground (docs/simulation.md "Ground items"): the snapshot's <see cref="WorldSnapshot.GroundItems"/> are diffed by id whenever
/// <see cref="WorldSnapshot.GroundItemsRevision"/> changes, each one a runtime placement of the world objects (its record's mesh, <c>WorldObjectLayout.Item</c>).
/// </summary>
sealed partial class GameHost
{
    long groundRevision = -1;
    readonly HashSet<int> shownGround = [];

    static string GroundId(int id) => "ground:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Once a frame with <see cref="UpdateBuildings"/>, after a new world's runtime placements were cleared.</summary>
    void UpdateGroundItems(WorldSnapshot snapshot)
    {
        session.World.GroundItems.Limit = config.GroundItemLimit;   // the setting, applied at the next drop
        if (snapshot.GroundItemsRevision == groundRevision) return;
        groundRevision = snapshot.GroundItemsRevision;
        var current = snapshot.GroundItems.Select(g => g.Id).ToHashSet();
        foreach (int id in shownGround.Where(id => !current.Contains(id)).ToArray())
        {
            ActiveObjects?.RemoveRuntime(GroundId(id));
            shownGround.Remove(id);
        }
        foreach (var g in snapshot.GroundItems)
        {
            if (!shownGround.Add(g.Id)) continue;
            var (rotation, y) = g.Dropped ? LaidFlat(g) : (g.Rotation, g.Position.Y);
            ActiveObjects?.AddRuntime(new BuildingPlacement(WorldLayout.ZoneOf(g.Position.X, g.Position.Z), GroundId(g.Id), g.Record, g.Position, rotation, null, y) { Key = GroundId(g.Id) });
        }
    }

    readonly Dictionary<string, OgreBounds?> groundBounds = [];

    /// <summary>
    /// A dropped item as it would come to rest (engine choice; the original lets a physics body settle it): its mesh's thinnest bounds axis turned upright, then its yaw, then tilted to the
    /// slope of the terrain under it, and set down so that no point of its bounds is below the ground at that point. A weapon (modelled along +Y) lies on the flat of its blade; a limb
    /// on a slope lies along it instead of hanging by its downhill end. Where the simulation's ground is not the terrain (a floor of a building) the ground is flat at the drop point.
    /// </summary>
    (Quaternion Rotation, float Y) LaidFlat(GroundItemSnapshot g)
    {
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        if (!groundBounds.TryGetValue(g.Record, out var bounds))
        {
            bounds = null;
            if (scene.Database?.Find(g.Record) is { } record && assets.Find(WorldObjectLayout.ItemMesh(record)) is { } file)
            {
                try { bounds = OgreMeshReader.TryReadBounds(file, out var b) ? b : OgreMeshReader.ReadFile(file).Bounds; }
                catch (Exception e) when (e is IOException or OgreFormatException) { }
            }
            groundBounds[g.Record] = bounds;
        }
        if (bounds is not { } box) return (yaw, g.Position.Y + 0.5f);
        var size = box.Max - box.Min;
        var flat = size.X <= size.Y && size.X <= size.Z ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)   // X up
            : size.Z < size.Y ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)                           // Z up
            : Quaternion.Identity;
        var rotation = Quaternion.Normalize(Quaternion.Concatenate(flat, yaw));
        var at = g.Position;
        // The terrain only where the drop point lies on it (a building's floor is flat for this).
        bool onTerrain = MathF.Abs(ActiveGround(at.X, at.Z) - at.Y) < 3;
        float Ground(float x, float z) => onTerrain ? ActiveGround(x, z) : at.Y;
        float reach = MathF.Max(MathF.Max(size.X, size.Z) * 0.5f, 1);
        var slope = Vector3.Normalize(new Vector3(Ground(at.X - reach, at.Z) - Ground(at.X + reach, at.Z), 2 * reach, Ground(at.X, at.Z - reach) - Ground(at.X, at.Z + reach)));
        float tilt = MathF.Acos(Math.Clamp(slope.Y, -1, 1));
        if (tilt > 1e-3f)
            rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, slope)), tilt)));
        // Every point of a lattice over its bounds must be at or above the ground below it (a limb on a crest touches the crest, not only the dips round it).
        float y = float.MinValue;
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                for (int k = 0; k < 5; k++)
                {
                    var local = box.Min + size * new Vector3(i, j, k) / 4;
                    var w = Vector3.Transform(local, rotation);
                    y = MathF.Max(y, Ground(at.X + w.X, at.Z + w.Z) - w.Y);
                }
        return (rotation, y);
    }

    void ForgetGroundItems()
    {
        shownGround.Clear();
        groundRevision = -1;
    }
}
