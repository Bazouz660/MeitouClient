using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Locks;

public sealed partial class FixtureRegistry
{
    /// <summary>The weight factor of a law enforcer for a cage inside a building that is not open to the public (<c>DAT_1416a2578</c>, <b>Verified</b>; the weight floor's value).</summary>
    public const float NotPublicPolice = 1e-5f;

    // The population data the registered buildings came from (relations, factions, towns): what the usable test reads. Derived, not hashed.
    PopulationData? usableData;
    // A town building's Open to public flag at registration (the saved state, else the level's <c>is public day</c>, public when absent: the AI's reading, AiSystem places); derived.
    readonly Dictionary<string, bool> levelPublic = new(StringComparer.Ordinal);

    /// <summary>Whether a building is open to the public (the original's <c>Building::isPublic</c> <c>+0xc8</c>, <b>Observed</b>): as the player set it, else as its state reads, else public.</summary>
    public bool IsPublic(string building) => PublicOf(building) ?? (levelPublic.TryGetValue(building, out bool open) ? open : true);

    /// <summary>The town a fixture's building stands in (<c>FUN_1400f6c20</c>): its site, else the town whose radius holds it.</summary>
    CampaignTown TownOfFixture(World world, Fixture f) =>
        f.Site >= 0 ? CampaignTown.OfSite(f.Site) : usableData is { } data ? AiSystem.TownAt(data, world, f.Position) : CampaignTown.None;

    /// <summary>
    /// <c>Ownerships::canIUseThisBuilding</c> (<c>FUN_140621230</c>, <b>Verified</b>, docs/game/locks.md) for a cage and the carrier whose squad's ownerships it reads: true for a squad with no faction, for a
    /// cage of its faction, and for one in the town its occupation holds; false when the cage owner's faction counts the squad's as an enemy; false for a fixture with an inventory; a cage on its
    /// own (no parent building) is usable in the squad's home town, one inside a building when that building is open to the public, the squad's faction is real and the owner is not the player's;
    /// else when the owner regards the squad's faction as an ally. The original also lets a carrier with a slave state use any cage, but its gatherer (<c>FUN_140a00690</c>) tests with no character
    /// and so drops these first: not modelled. Without population data only the carrier's own faction passes.
    /// </summary>
    public bool CanUse(World world, CharacterCold carrier, Fixture f)
    {
        var squad = world.Squads.Find(carrier.SquadId);
        int faction = squad?.Faction ?? carrier.Faction;
        if (faction < 0 || f.Owner == faction) return true;
        if (usableData is not { } data) return false;
        if (squad?.Ai is { CurrentClass: PackageClass.OccupyTown, Destination.IsNone: false } board && TownOfFixture(world, f) == board.Destination) return true;
        int owner = f.Owner;
        var relations = data.Relations;
        bool known = owner >= 0 && relations.Contains(owner) && relations.Contains(faction);
        if (known && relations.IsHostile(owner, faction) || f.Contents is not null) return false;
        if (f.Id == f.Building)
        {
            if (squad is { Town: >= 0 } && f.Site == squad.Town) return true;
        }
        else if (IsPublic(f.Building) && faction < data.Factions.Count && !data.Factions[faction].NotReal && owner != world.Player.Faction) return true;
        return known && relations.IsAlly(owner, faction);
    }
}
