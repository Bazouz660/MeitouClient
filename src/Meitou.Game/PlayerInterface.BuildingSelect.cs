using System.Globalization;
using System.Numerics;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Clicking a building (docs/game/ui-input.md "Building picking and selection", docs/game/ui-screens.md "Selected building"): a left click on the player's sites and on the level data's
/// buildings selects one, which fills the HUD's name, state, stats and medical-header panels in place of the character's and leaves the character selection alone. Picking uses the buildings'
/// collision footprints (a vertical prism over the convex hull of the record's shapes), not the original's per-part collision rays; characters under the cursor win over a building.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>A building selected by a click: what it is, where it stands (hull in the world, for the outline) and what the panels say of it.</summary>
    sealed record SelectedBuilding(string Name, string? Type, string? Owner, string? Town, string? SiteId, string Instance, Vector3 Position, Vector2[]? Hull,
        bool PublicDay = false, bool PublicNight = false, string? Record = null);

    SelectedBuilding? selectedBuilding;

    /// <summary>Makes <paramref name="building"/> the selected one (null: none) and tells the simulation, which then publishes its doors.</summary>
    void SetBuilding(SelectedBuilding? building)
    {
        if (ReferenceEquals(selectedBuilding, building)) return;
        selectedBuilding = building;
        closedMachine = null;   // clicking a building asks for its window again, also when the player closed it before (the mark only held until another was selected)
        // Selecting calls the building's select sound (FUN_1405504a0: Buildings_Click = its `select sound`, none for 0); a click on the same building picks it anew and sounds again.
        if (building?.Record is { } record) Sound("Building_Click", building: record);
        Say(new InspectBuildingCommand(building?.Instance) { Tick = session.World.Tick });
    }

    /// <summary>The selected building while it still exists (a site that was cancelled or dismantled drops the selection).</summary>
    SelectedBuilding? BuildingFocus()
    {
        if (selectedBuilding is { SiteId: { } id } && !Meitou.Simulation.Production.ProductionSystem.IsNatural(id) && !session.CurrentSnapshot.Buildings.Any(s => s.Id == id)) SetBuilding(null);
        return selectedBuilding;
    }

    /// <summary>A site of the player's as a selected building (what a left click on it selects).</summary>
    SelectedBuilding SiteBuilding(Meitou.Simulation.Construction.BuildingSnapshot site) =>
        new(SiteName(site), null, FactionName?.Invoke(session.World.Player.Faction), null, site.Id, site.Id, site.Position,
            buildHost?.Placement.Footprint(site.Record) is { } f ? f.Place(site.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Meitou.Game.Building.PlacementCheck.YawOf(site.Rotation))) : null,
            Record: site.Record);

    /// <summary>The nearest building under the pixel, a site of the player's or a level-data one, when the ray meets it before the ground.</summary>
    SelectedBuilding? PickBuilding(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        float limit = GroundHit(ray.Origin, ray.Direction) is { } groundPoint ? Vector3.Distance(ray.Origin, groundPoint) : 6000;
        float bestT = limit;
        SelectedBuilding? best = null;
        if (PickSiteAt(pixel) is { } site && site.T < bestT)
        {
            bestT = site.T;
            best = SiteBuilding(site.Site);
        }
        if (buildHost is { } host)
            foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
            {
                var b = candidate.Placement;
                float y = b.WorldY ?? ground(b.Position.X, b.Position.Z) + b.Position.Y;
                var hull = candidate.Footprint.Place(b.Position, b.Rotation);
                if (BuildingRayT(ray, hull, candidate.Footprint.YMin, candidate.Footprint.YMax, b.BuildingId, b.Position, y, b.Rotation) is not { } t || t >= bestT) continue;
                bestT = t;
                var info = host.WorldInfo(b);
                best = new(UiMode == Meitou.Data.Ui.UiMode.Meitou && info.Sells is { } sells ? sells : info.Name, info.Type, info.Owner, info.Town, null, b.Key, new Vector3(b.Position.X, y, b.Position.Z), hull, info.PublicDay, info.PublicNight, b.BuildingId);
            }
        if (PickRockAt(pixel) is { } rock && rock.T < bestT) best = RockBuilding(rock.Rock);
        return best;
    }

    /// <summary>
    /// The building panels' lines (docs/game/ui-screens.md "Selected building"): the name bar (name, owner), the state lines (Type, Town, a site's construction), the stats lines (the doors'
    /// Open/Close and Security with the lock's level, Open to public for another faction's, a site's materials) and the status word of the medical header.
    /// </summary>
    (HudLine Name, HudLine[] State, HudLine[] Stats, string Status, bool Bad) BuildingLines(SelectedBuilding focus)
    {
        var state = new List<HudLine>();
        var stats = new List<HudLine>();
        string status = "Normal";
        bool bad = false;
        bool own = focus.SiteId is not null;
        if (focus.Type is { Length: > 0 } type) state.Add(new("Type:", type));
        if (focus.Town is { Length: > 0 } town) state.Add(new("Town:", town));
        var site = focus.SiteId is { } id ? session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) : null;
        if (site is { Complete: false })
        {
            status = site.Dismantling ? "Being dismantled" : site.Paused ? "Paused" : "Under construction";
            bad = true;
            int percent = site.Target > 0 ? (int)Math.Min(100, site.Progress / site.Target * 100) : 0;
            state.Add(new("Progress:", percent.ToString(CultureInfo.InvariantCulture) + "%"));
            foreach (var m in site.Materials)
                stats.Add(new(buildHost?.ItemName(m.Item) ?? m.Item, $"{m.Delivered.ToString("0.#", CultureInfo.InvariantCulture)}/{m.Required.ToString("0.#", CultureInfo.InvariantCulture)}", MainColour));
        }
        else
        {
            // The first door speaks for the building (the original selects a house as its first door).
            if (session.CurrentSnapshot.InspectedBuilding is { } detail && detail.Instance == focus.Instance && detail.Doors.Count > 0)
            {
                var door = detail.Doors[0];
                if (door.Broken) { status = "Broken"; bad = true; }
                stats.Add(new("Open/Close:", door.Broken ? "The door is broken" : door.Open ? "The door is open" : "The door is closed", MainColour, door.Open && !door.Broken ? GoodColour : null,
                    door.Broken ? "It will no longer close and needs to be repaired." : null));
                stats.Add(new("Security:", $"{Loc.Tr(door.Locked ? "Locked" : "Unlocked")} ({Loc.Tr("lvl {1,num}", door.Level)})", MainColour, door.Locked ? null : GoodColour,
                    door.Locked ? "The door is locked and will keep out intruders." : "The door is unlocked."));
            }
            if (own && Machine(focus.SiteId) is { } machine) AddMachineLines(machine, stats, ref status, ref bad);
            if (!own && (focus.PublicDay || focus.PublicNight))
            {
                bool open = session.Clock.IsDaytime ? focus.PublicDay : focus.PublicNight;
                stats.Add(new("Open to public:", open ? "Public" : "Private", MainColour, open ? GoodColour : BadColour));
            }
        }
        return (new(focus.Name, focus.Owner ?? "", TitleColour, TitleColour), [.. state], [.. stats], status, bad);
    }

}
