using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>One confirmed ghost: the BUILDING record, its absolute position, its rotation (tilt included), floor and parent building instance (null outdoors).</summary>
public sealed record PlannedBuilding(string Record, Vector3 Position, Quaternion Rotation, int Floor = 0, string? Parent = null);

/// <summary>Build mode's Confirm: creates a site per plan, in order, for the player faction (docs/simulation.md "Buildings"). A plan failing the data rules is dropped with a message.</summary>
public sealed record PlaceBuildings(IReadOnlyList<PlannedBuilding> Plans) : SimCommand;

/// <summary>Player orders 2 Build / 95 Repair: the characters (the selection when empty) deliver what they carry to <see cref="Site"/> and work it.</summary>
public sealed record BuildOrder(IReadOnlyList<CharacterId> Characters, string Site, bool Repair = false) : SimCommand;

/// <summary>Player order 96 Dismantle: the characters work <see cref="Site"/> down to 0, then it is removed with the refund.</summary>
public sealed record DismantleOrder(IReadOnlyList<CharacterId> Characters, string Site) : SimCommand;

/// <summary>Removes a site with nothing delivered at once (engine); a site with deliveries needs <see cref="DismantleOrder"/>.</summary>
public sealed record CancelSite(string Site) : SimCommand;

/// <summary>The site's <c>pause</c> flag: auto-jobs skip a paused site.</summary>
public sealed record PauseSite(string Site, bool Paused) : SimCommand;

/// <summary>The panel's [UPGRADE]: the site becomes its record's <c>upgrades to</c> BUILDING in place and is built up to it (<see cref="BuildingUpgrades"/>).</summary>
public sealed record UpgradeSite(string Site) : SimCommand;

/// <summary>The panel's [DOWNGRADE] on an unfinished upgrade: the site goes back to the BUILDING that upgrades to it (<see cref="BuildingUpgrades"/>).</summary>
public sealed record DowngradeSite(string Site) : SimCommand;

/// <summary>The panel's Open to public toggle on a building of the player's: a site's <see cref="BuildSite.Public"/>, else an owned town building's flag in the fixtures.</summary>
public sealed record SetBuildingPublic(string Building, bool Public) : SimCommand;
