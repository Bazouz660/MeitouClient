using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Production;

public sealed partial class ProductionSystem
{
    /// <summary>The seconds of game time a prospecting takes (<b>Verified</b>: the progress adds the character's update time up to 1, <c>Task_Prospecting</c> update, docs/game/ai-tasks.md "Prospecting").</summary>
    public const float ProspectSeconds = 1;

    /// <summary>The results window's resources in its order, with the names of <c>FUN_140a0c300</c> (<b>Verified</b>; carbon is not listed).</summary>
    static readonly (int Resource, string Name)[] ProspectResources =
    [
        (MachineData.WaterResource, "Water"), (MachineData.GroundResource, "Fertility"), (ResourceField.StoneResource, "Stone"),
        (ResourceField.IronResource, "Iron"), (ResourceField.CopperResource, "Copper"),
    ];

    /// <summary>Characters mid-prospecting; <see cref="ProspectStep"/> looks for them only while this is above 0.</summary>
    int prospectors;

    /// <summary>The order: the character stops where it stands and starts the task (serial phase). A character already at it keeps its progress.</summary>
    void ProspectBegin(World world, ProspectOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || !Able(cold) || cold.WaterState == 3) return;
        ref var n = ref table.Next[slot];
        if (n.Task == (byte)CharacterTask.Prospect) return;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Prospect;
        n.TaskTime = 0;
        cold.Prospecting = 0;
        prospectors++;
    }

    /// <summary>
    /// Once a tick (serial): each prospector adds the tick's game seconds; at <see cref="ProspectSeconds"/> the task ends and the results are published (<see cref="World.Prospected"/>). Another
    /// order (a changed task) or a downed, fighting or carried character ends it without a result.
    /// </summary>
    void ProspectStep(World world)
    {
        if (prospectors <= 0) return;
        var table = world.Characters;
        int found = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive || table.Cold(i) is not { Prospecting: { } progress } cold) continue;
            if (n.Task != (byte)CharacterTask.Prospect || !Able(cold))
            {
                cold.Prospecting = null;
                if (n.Task == (byte)CharacterTask.Prospect) n.Task = (byte)CharacterTask.Idle;
                continue;
            }
            progress += world.TickSeconds;
            if (progress < ProspectSeconds)
            {
                cold.Prospecting = progress;
                found++;
                continue;
            }
            cold.Prospecting = null;
            n.Task = (byte)CharacterTask.Idle;
            PublishProspect(world, cold, n.Position);
        }
        prospectors = found;
    }

    /// <summary>The window's content for a finished prospecting (<b>Verified</b>/<b>Observed</b> per docs/game/ui-screens.md "Prospecting"), read from the ground at the prospector's feet.</summary>
    void PublishProspect(World world, CharacterCold prospector, System.Numerics.Vector3 at)
    {
        float skill = prospector.Stats?[StatsEnumerated.Science] ?? 0;
        var environment = new List<ProspectLine>(3);
        var (green, arid, swamp) = resources.Coverage(at.X, at.Z);
        if (green > 0) environment.Add(new("Green", green));
        if (arid > 0) environment.Add(new("Arid", arid));
        if (swamp > 0) environment.Add(new("Swamp", swamp));
        var found = new ProspectLine[ProspectResources.Length];
        for (int k = 0; k < found.Length; k++) found[k] = new(ProspectResources[k].Name, resources.Yield(ProspectResources[k].Resource, at));
        world.Prospected = new ProspectReport((world.Prospected?.Serial ?? 0) + 1, at, prospector.Name, (int)skill, resources.ZoneName(at.X, at.Z) ?? "", (int)at.Y,
            (int)((skill * 0.01f * 6000 + 2000) * 0.2f), environment, found);
    }
}
