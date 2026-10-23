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

    /// <summary>The colour of the [PAUSED] caption (the exe's static <c>#777777</c>).</summary>
    const string PausedColour = "777777";

    /// <summary>The BUILDING record's construction target, the sum of its materials' required amounts (docs/game/buildings-production.md); null when unknown. The host sets it.</summary>
    public Func<string, float?>? BuildTarget { get; set; }

    /// <summary>
    /// The building panels' lines (docs/game/ui-screens.md "Selected building"): the name bar (name, owner), the state lines (Type, Town and, for the player's own site, the
    /// Dismantle and PAUSE CONSTRUCTION buttons), the stats lines (the first door's Open/Close and Security, buttons on the player's own building; Open to public for another
    /// faction's) and the status word of the medical header. A house speaks through its first door as the original selects it as that door: the door's lines come first.
    /// </summary>
    (HudLine Name, HudLine[] State, HudLine[] Stats, string Status, bool Bad) BuildingLines(SelectedBuilding focus)
    {
        var state = new List<HudLine>();
        var stats = new List<HudLine>();
        string status = "Normal";
        bool bad = false;
        bool own = focus.SiteId is not null;
        bool player = focus.SiteId is { } siteId && !Meitou.Simulation.Production.ProductionSystem.IsNatural(siteId);
        if (focus.Type is { Length: > 0 } type) state.Add(new("Type:", type));
        if (focus.Town is { Length: > 0 } town) state.Add(new("Town:", town));
        var site = focus.SiteId is { } id ? session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) : null;
        // The construction buttons (Building slot 117, FUN_1402fea10): Dismantle in BadBright, and while unfinished PAUSE CONSTRUCTION, captioned [PAUSED] in #777777 while paused.
        if (player && site is not null)
        {
            state.Add(new("", Loc.Tr("Dismantle"), ValueColour: BadBrightColour, Action: "meitou:building-dismantle"));
            if (!site.Complete) state.Add(site.Paused ? new("", Loc.Tr("[PAUSED]"), ValueColour: PausedColour, Action: "meitou:building-pause")
                : new("", Loc.Tr("PAUSE CONSTRUCTION"), ValueColour: MainColour, Action: "meitou:building-pause"));
        }
        if (site is { Complete: false })
        {
            status = site.Dismantling ? "Being dismantled" : site.Paused ? "Paused" : "Under construction";
            bad = true;
        }
        else
        {
            if (FirstDoor(focus) is { } door)
            {
                // DoorStuff slot 40 (FUN_140304120): the value in the exe's green #a8b774, BadBright for a broken door, a closed locked one and Locked; buttons on the player's own door.
                if (door.Broken) { status = "Broken"; bad = true; }
                stats.Add(new("Open/Close:", door.Broken ? "The door is broken" : door.Open ? "The door is open" : "The door is closed", MainColour,
                    door.Broken || !door.Open && door.Locked ? BadBrightColour : GoodColour, door.Broken ? "It will no longer close and needs to be repaired." : null,
                    player ? "meitou:building-doors" : null));
                // Meitou: an open door that wants its lock (the lock button's setting; it locks when it closes) reads Locked, else the button changed nothing visible.
                bool locked = door.Locked || door.WantsLock && door.Open;
                stats.Add(new("Security:", $"{Loc.Tr(locked ? "Locked" : "Unlocked")} ({Loc.Tr("lvl {1,num}", door.Level)})", MainColour, locked ? BadBrightColour : GoodColour,
                    door.Locked ? "The door is locked and will keep out intruders." : locked ? "The door will lock when it closes." : "The door is unlocked.", player ? "meitou:building-locks" : null));
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

    /// <summary>The selected building's first door as the simulation publishes it, or null.</summary>
    Meitou.Simulation.Locks.DoorDetail? FirstDoor(SelectedBuilding focus) =>
        session.CurrentSnapshot.InspectedBuilding is { } detail && detail.Instance == focus.Instance && detail.Doors.Count > 0 ? detail.Doors[0] : null;

    /// <summary>
    /// The medical panel's rows of a selected building (its section 4, docs/game/ui-screens.md "Selected building"): the first door's <c>Door Condition</c>, then <c>Building Condition</c>
    /// (<c>Condition</c> without doors), each the construction progress as the number and its fraction of the target as the bar, then while unfinished a row per material, delivered
    /// over required. Meitou models no building damage, so a finished building's progress is its target (the original's damaged state lowers it).
    /// </summary>
    List<MedicalRow> BuildingRows(SelectedBuilding focus)
    {
        var rows = new List<MedicalRow>();
        if (focus.SiteId is { } natural && Meitou.Simulation.Production.ProductionSystem.IsNatural(natural)) return rows;
        var door = FirstDoor(focus);
        if (door is { Record.Length: > 0 } && BuildTarget?.Invoke(door.Value.Record) is { } doorTarget)
            rows.Add(Bar(Loc.Tr("Door Condition"), 1, ((int)doorTarget).ToString(CultureInfo.InvariantCulture), "The door is in good condition."));
        var site = focus.SiteId is { } id ? session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) : null;
        float? target = site?.Target ?? (focus.Record is { } record ? BuildTarget?.Invoke(record) : null);
        if (target is not { } total) return rows;
        float progress = site?.Progress ?? total;
        bool complete = site?.Complete ?? true;
        float fraction = total > 0 ? Math.Min(progress / total, 1) : 1;
        string tip = !complete ? Loc.Tr("The building is under construction and at {1,num}% completion.", (int)(fraction * 100))
            : fraction >= 1 ? Loc.Tr("The building is in good condition.") : Loc.Tr("The building is damaged.");
        rows.Add(Bar(Loc.Tr(door is null ? "Condition" : "Building Condition"), fraction, ((int)progress).ToString(CultureInfo.InvariantCulture), tip));
        if (site is { Complete: false })
            foreach (var m in site.Materials)
            {
                float share = m.Required > 0 ? Math.Min(m.Delivered / m.Required, 1) : 1;
                string need = m.Delivered >= m.Required ? Loc.Tr("All necessary materials are ready.")
                    : Loc.Tr("{1} more 'Building Materials' will be needed to complete this construction.", (m.Required - m.Delivered).ToString("0.##", CultureInfo.InvariantCulture));
                rows.Add(Bar(buildHost?.ItemName(m.Item) ?? m.Item, share, $"{(int)m.Delivered}/{(int)m.Required}", need));
            }
        return rows;

        // The rows a DataPanel adds with a fraction (FUN_140729f90): the bar in the healed (yellow) layer, as a base-game screenshot of a town house shows it.
        static MedicalRow Bar(string name, float fraction, string value, string tooltip) => new(name, 0, fraction, 0, value, "", false, Loc.Tr(tooltip));
    }

    /// <summary>The panel's own buttons on a selected building of the player's: Dismantle (after the original's "Are you sure?"), pause, the doors and their locks.</summary>
    void BuildingAction(string action)
    {
        if (BuildingFocus() is not { SiteId: { } id } || Meitou.Simulation.Production.ProductionSystem.IsNatural(id)) return;
        var site = session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id);
        if (action == "building-dismantle")
        {
            var selection = SelectedIds();
            Confirm("Dismantle", "Are you sure?", () => Say(new Meitou.Simulation.Construction.DismantleOrder(selection, id) { Tick = session.World.Tick }));
        }
        else if (action == "building-pause" && site is { Complete: false }) Say(new Meitou.Simulation.Construction.PauseSite(id, !site.Paused) { Tick = session.World.Tick });
        else if (action == "building-doors") Say(new Meitou.Simulation.Locks.ToggleDoorsOrder(id) { Tick = session.World.Tick });
        else if (action == "building-locks") Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(id) { Tick = session.World.Tick });
    }
}
