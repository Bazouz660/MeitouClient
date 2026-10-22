using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>The doors' open amounts from the snapshot to the object renderer (docs/simulation.md "Cages, beds and locks"), made again only when the snapshot's door table is a new one.</summary>
sealed partial class GameHost
{
    IReadOnlyDictionary<(string Building, int Door), DoorView>? shownDoors;

    /// <summary>Once a frame, with the buildings.</summary>
    void UpdateDoors()
    {
        if (ActiveObjects is not { } objects) return;
        var doors = session.CurrentSnapshot.Doors;
        if (ReferenceEquals(doors, shownDoors)) return;
        shownDoors = doors;
        var open = new Dictionary<(string Placement, int Door), float>(doors.Count);
        foreach (var (key, view) in doors)
            if (view.Open > 0) open[key] = view.Open;
        objects.Doors = open.Count == 0 ? null : open;
    }
}
