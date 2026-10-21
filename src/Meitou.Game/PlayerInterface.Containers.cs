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
    ContainerSpot? PickContainer(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        ContainerSpot? best = null;
        float bestDistance = ContainerPickPixels;
        foreach (var c in session.CurrentSnapshot.Containers)
        {
            var middle = c.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel);
            if (d < bestDistance && !Hidden(ray, Vector3.Distance(ray.Origin, middle))) (best, bestDistance) = (c, d);
        }
        return best;
    }

    /// <summary>A right click on a container: the open order (true), with a selection.</summary>
    bool ContainerClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickContainer(p) is not { } container) return false;
        Say(new Meitou.Simulation.Items.OpenContainerOrder([], container.Id) { Tick = session.World.Tick });
        return true;
    }
}
