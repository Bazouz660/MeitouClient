using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// Severed limbs on the ground, falling and at rest (docs/game/blood-decals.md "On the body and on severed limbs"): drawn by the debris renderer with the original's SeveredLimb look
/// (its material's textures, the skin-tone mask, blood with the cut end solid), not as a world object.
/// </summary>
sealed partial class GameHost
{
    /// <summary>A severed limb at rest: its mesh and look, its rotation and where its origin is.</summary>
    sealed record RestingLimb(string Mesh, LimbLook Look, Quaternion Rotation, Vector3 Origin);

    readonly Dictionary<int, RestingLimb> restLimbs = [];
    Dictionary<string, GameRecord>? limbRaces;
    readonly Dictionary<string, LimbLook?> limbLooks = new(StringComparer.Ordinal);

    /// <summary>Sets a landed limb down where the object path would have put it, drawn as debris with the severed limb's look.</summary>
    void RestLimb(GroundItemSnapshot g, Quaternion rotation, float y)
    {
        if (scene.Database?.Find(g.Record) is not { } item || WorldObjectLayout.ItemMesh(item) is not { Length: > 0 } mesh || LimbLookOf(g.Record) is not { } look) { ShowGround(g, rotation, y); return; }
        restLimbs[g.Id] = new RestingLimb(mesh, look, rotation, new Vector3(g.Position.X, y, g.Position.Z));
    }

    /// <summary>
    /// The look of a ground item that is a race's <c>severed limbs</c> entry (null for any other item): its MATERIAL record's texture map and colour map (the skin-tone mask), the race's blood
    /// colour. The skin tone the original keeps on the item (the cut character's) is not carried by our ground items: none is applied (the mask then has no effect).
    /// </summary>
    LimbLook? LimbLookOf(string record)
    {
        if (limbLooks.TryGetValue(record, out var known)) return known;
        LimbLook? look = null;
        if (scene.Database is { } db)
        {
            if (limbRaces is null)
            {
                limbRaces = new(StringComparer.Ordinal);
                foreach (var race in db.Records.Values)
                    if (race.Type == FcsRecordType.RACE)
                        foreach (var limb in race.GetReferences("severed limbs")) limbRaces.TryAdd(limb.TargetStringId, race);
            }
            if (limbRaces.TryGetValue(record, out var owner) && db.Find(record) is { } item)
            {
                var spec = item.GetReferences("material").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(r => r is not null);
                string? Name(string key) => spec?.GetPath(key) is { Length: > 0 } p ? Path.GetFileName(p.Replace('\\', '/')) : null;
                if (Name("texture map") is { } diffuse)
                    look = new LimbLook(diffuse, Name("color map"), Vector3.Zero, CharacterAppearance.Rgb(owner.GetInt("blood colour", 0x800000) & 0xFFFFFF), owner.StringId, Meitou.Rendering.Characters.CharacterRenderer.SkinByDefault(owner));
            }
        }
        return limbLooks[record] = look;
    }

    /// <summary>The position of a piece whose mesh's origin is at <paramref name="origin"/> (a piece's position is its bounds' middle).</summary>
    Vector3 LimbMiddle(string mesh, LimbLook? look, Quaternion rotation, Vector3 origin) =>
        gpu.Debris?.Centre(mesh, look) is { } centre ? origin + Vector3.Transform(centre, rotation) : origin;

    void AddRestingLimbs()
    {
        foreach (var r in restLimbs.Values)
            if (gpu.Debris?.Extent(r.Mesh, null, r.Look) is not null)
                gpu.DebrisPieces.Add(new DebrisPiece(r.Mesh, 1, r.Rotation, LimbMiddle(r.Mesh, r.Look, r.Rotation, r.Origin), 1, r.Look));
    }
}
