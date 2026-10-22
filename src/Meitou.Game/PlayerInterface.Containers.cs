using System.Numerics;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Containers of a building's interior (docs/game/locks.md "Containers"): a right click on one sends the nearest selected character to open it (pointer <c>search</c>); its inventory
/// then shows in the storage window the research bench uses (<c>PlayerInterface.Production.cs</c>), beside the character's.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer a container's projected middle must be, in pixels (an engine choice: the containers have no pick mesh).</summary>
    const float ContainerPickPixels = 30;

    /// <summary>The container whose middle (half a unit above its origin) projects nearest the pixel within <see cref="ContainerPickPixels"/>, if any, and is not hidden behind a wall.</summary>
    ContainerSpot? PickContainer(Vector2 pixel) => PickContainerAt(pixel)?.Container;

    /// <summary><see cref="PickContainer"/> with the ray distance to the container's middle.</summary>
    (ContainerSpot Container, float T)? PickContainerAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        (ContainerSpot, float)? best = null;
        float bestDistance = ContainerPickPixels;
        foreach (var c in session.CurrentSnapshot.Containers)
        {
            var middle = c.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), t = Vector3.Distance(ray.Origin, middle);
            if (d < bestDistance && !Hidden(ray, t)) (best, bestDistance) = ((c, t), d);
        }
        return best;
    }

    /// <summary>A right click on a container: the open order (true), with a selection; with Alt another faction's locked one is picked (the sim gives the hint without it).</summary>
    bool ContainerClick(Vector2 p, bool alt)
    {
        if (SelectedIds().Count == 0 || PickContainer(p) is not { } container) return false;
        closedMachine = null;   // a container whose window was closed opens again when it is asked for (the sim may not have dropped it yet, e.g. while paused)
        Say(new Meitou.Simulation.Items.OpenContainerOrder([], container.Id, alt) { Tick = session.World.Tick });
        return true;
    }

    /// <summary>
    /// The pointer over a container (docs/game/ui-input.md "Building hover and click", StorageBuilding): <c>search</c> for the player's own or an unowned one, else <c>lockpick</c> while
    /// locked and <c>scavenge</c> when not.
    /// </summary>
    string ContainerPointer(ContainerSpot container) =>
        container.Owner < 0 || container.Owner == session.World.Player.Faction ? "search" : container.Locked ? "lockpick" : "scavenge";
}
