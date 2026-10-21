using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Natural sites (docs/simulation.md "Production"; the original's rocks are ProductionBuildings with "is foliage", docs/game/buildings-production.md "Resources"): a rock the foliage
// system places is worked like a player's mine. It is no BuildSite of the registry: its site is made from its id, a pure function of the rock's BUILDING and position, so orders and
// perma-jobs name it and a saved job finds it again. Its machine lives in World.Machines like any other and is not saved (the original saves no rock either, Observed).
public sealed partial class ProductionSystem
{
    const string NaturalPrefix = "natural:";

    readonly Dictionary<string, BuildSite?> naturalSites = new(StringComparer.Ordinal);

    /// <summary>The site id of the rock of BUILDING <paramref name="record"/> at <paramref name="at"/>.</summary>
    public static string NaturalId(string record, Vector3 at) =>
        string.Create(CultureInfo.InvariantCulture, $"{NaturalPrefix}{record}@{at.X:R},{at.Y:R},{at.Z:R}");

    /// <summary>Whether a site id names a rock (<see cref="NaturalId"/>).</summary>
    public static bool IsNatural(string id) => id.StartsWith(NaturalPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a remote command may name <paramref name="id"/> as a rock (<see cref="CommandGate"/>): it is in <see cref="NaturalId"/>'s exact form for the record and position of a
    /// rock of the rock source, and <see cref="Natural"/> takes it. Without a rock source none passes. Only ids of placed rocks reach the site cache, so ids a client makes up
    /// cannot grow it.
    /// </summary>
    public bool IsRock(string id)
    {
        int at = id.LastIndexOf('@');
        if (nodes is null || !IsNatural(id) || at <= NaturalPrefix.Length || Position(id[(at + 1)..]) is not { } position) return false;
        string record = id[NaturalPrefix.Length..at];
        return NaturalId(record, position) == id && nodes.Near(0, position.X, position.Z, 1).Any(n => n.Record == record && n.Position == position) && Natural(id) is not null;
    }

    static Vector3? Position(string text)
    {
        var parts = text.Split(',');
        return parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            ? new Vector3(x, y, z) : null;
    }

    /// <summary>A player site of the registry, else the rock a natural id names; null for neither.</summary>
    BuildSite? Site(World world, string id) => world.Buildings.Find(id) ?? Natural(id);

    /// <summary>
    /// The rock a natural id names: a BUILDING with <c>is foliage</c> whose machine is an operable mine of a <c>world resource mining</c>; with a rock source, one of that BUILDING must
    /// stand within a unit of the position. Owned by no faction, complete.
    /// </summary>
    BuildSite? Natural(string id)
    {
        if (!IsNatural(id)) return null;
        if (naturalSites.TryGetValue(id, out var known)) return known;
        BuildSite? site = null;
        int at = id.LastIndexOf('@');
        if (at > 0 && Position(id[(at + 1)..]) is { } position)
        {
            string record = id[NaturalPrefix.Length..at];
            if (db.Find(record) is { Type: FcsRecordType.BUILDING } building && building.GetBool("is foliage") && Data(record) is { Operable: true, Resource: not 0 })
            {
                // No rock there is not remembered: the host's rock source may not have placed that zone yet.
                if (nodes is not null && !nodes.Near(0, position.X, position.Z, 1).Any(n => n.Record == record)) return null;
                site = new BuildSite { Id = id, Record = record, Name = building.Name, Position = position, Owner = -1, Materials = [], Complete = true };
            }
        }
        return naturalSites[id] = site;
    }
}
