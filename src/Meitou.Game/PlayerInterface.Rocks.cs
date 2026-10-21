using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Game;

/// <summary>
/// Mining rocks by hand (docs/simulation.md "Production", natural sites): a right click on a resource rock with characters selected has them operate it, with Shift it becomes a job
/// (Operating machine). A left click selects it; its machine window holds the mined ore, taken out by hand. Rocks are the foliage system's (<see cref="Rocks"/>); a rock is hit as a sphere of its radius around its position (engine choice: the original picks the
/// rock building's collision).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The world's resource rocks (null: none, e.g. headless or before the world is loaded).</summary>
    public Func<IResourceNodes?>? Rocks { get; set; }

    /// <summary>How far (X/Z) from the ground under the cursor a rock is looked for: a rock in front of the cursor's ground point is still hit.</summary>
    const float RockSearch = 200;

    /// <summary>The nearest rock the pixel's ray meets before the ground.</summary>
    ResourceNode? PickRock(Vector2 pixel) => PickRockAt(pixel)?.Rock;

    /// <summary>The nearest rock the pixel's ray meets before the ground, with the ray distance.</summary>
    (ResourceNode Rock, float T)? PickRockAt(Vector2 pixel)
    {
        if (Rocks?.Invoke() is not { } rocks || Ray(pixel) is not { } ray || GroundHit(ray.Origin, ray.Direction) is not { } ground) return null;
        float bestT = Vector3.Distance(ray.Origin, ground) + 1;
        ResourceNode? best = null;
        foreach (var rock in rocks.Near(0, ground.X, ground.Z, RockSearch))
        {
            if (rock.Record.Length == 0) continue;
            var to = rock.Position - ray.Origin;
            float along = Vector3.Dot(to, ray.Direction);
            float miss = to.LengthSquared() - along * along, r2 = rock.Radius * rock.Radius;
            if (miss > r2) continue;
            float t = along - MathF.Sqrt(r2 - miss);
            if (t >= 0 && t < bestT) (bestT, best) = (t, rock);
        }
        return best is { } found ? (found, bestT) : null;
    }

    /// <summary>A rock as a selected building: its site is the natural site, so the machine window shows its output once it has been mined.</summary>
    SelectedBuilding RockBuilding(ResourceNode rock) =>
        new(buildHost?.ItemName(rock.Record) ?? rock.Record, null, null, null, ProductionSystem.NaturalId(rock.Record, rock.Position), ProductionSystem.NaturalId(rock.Record, rock.Position),
            rock.Position, null, Record: rock.Record);

    /// <summary>A right click on a rock: the selection mines it, with Shift as a job (true: taken).</summary>
    bool RockClick(Vector2 p, bool shift)
    {
        if (SelectedIds() is not { Count: > 0 } selection || PickRock(p) is not { } rock) return false;
        string site = ProductionSystem.NaturalId(rock.Record, rock.Position);
        if (shift) Say(new AddJob(selection, TaskType.OPERATE_MACHINERY, site) { Tick = session.World.Tick });
        else Say(new OperateOrder(selection, site) { Tick = session.World.Tick });
        return true;
    }
}
