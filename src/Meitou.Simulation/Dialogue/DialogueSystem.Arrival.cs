using System.Numerics;

namespace Meitou.Simulation.Dialogue;

/// <summary>The biome banner has faded for <see cref="GroupId"/> (a BIOME_GROUP): the active squad may banter about the area (<see cref="DialogueSystem"/>, "arrival dialog").</summary>
public sealed record AreaArrival(string GroupId) : SimCommand;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// <c>PlayerInterface::triggerAreaArrivalDialogue</c> (<c>FUN_1407ffe40</c>, docs/game/dialogue.md "Arrival banter"): with the active squad of at least 2, its members awake, not carried,
    /// not in a bed or cage, not animals, not fighting and not in a town are the candidates; nothing when a member senses an enemy; with 2 or more, a line of the group's
    /// <c>arrival dialog</c> is said between two of them. Engine: the original scores every candidate with every dialogue (<c>scoreCustomDialog</c>) and runs the best; here the first
    /// dialogue, speaker and partner (squad order) that have a line win, "on screen" is not tested, and an enemy sensed is one hostile awake within sight range.
    /// </summary>
    void Arrive(World world, AreaArrival a)
    {
        var table = world.Characters;
        if (world.Squads.Find(world.Player.Squad) is not { Members.Count: >= 2 } squad || data.Db.Find(a.GroupId) is not { } group) return;
        var roots = group.GetReferences("arrival dialog").Select(r => dialogue.Node(r.TargetStringId)).Where(n => n is { IsRoot: true }).ToList();
        if (roots.Count == 0) return;
        var candidates = new List<CharacterId>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c) continue;
            if (SensesEnemy(world, s, c)) return;
            if (!Awake(table, s) || !c.CarriedBy.IsNone || c.InSomething != Locks.HeldState.None || SquadRegistry.IsAnimal(c) || c.InCombat || data.TownAt(table.Next[s].Position) >= 0) continue;
            candidates.Add(id);
        }
        if (candidates.Count < 2) return;
        foreach (var root in roots)
            foreach (var speaker in candidates)
                foreach (var partner in candidates)
                    if (partner != speaker && StartBubbles(world, speaker, partner, root!, 0)) return;
    }

    /// <summary>Whether a character awake and hostile to <paramref name="c"/>'s faction is within its sight range and in view (the original's <c>numEnemies</c> &gt; 0).</summary>
    bool SensesEnemy(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        float range = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        var at = table.Next[slot].Position;
        for (int o = 0; o < table.HighWater; o++)
        {
            if (o == slot || !Awake(table, o) || table.Cold(o) is not { } oc || !data.Relations.Contains(oc.Faction) || !AggroSystem.IsEnemy(data.Relations, c.Faction, oc.Faction)) continue;
            var there = table.Next[o].Position;
            if (Vector3.DistanceSquared(at, there) <= range * range && world.Sight.Sees(slot, o, at, table.Next[slot].Yaw, there, range) > 0) return true;
        }
        return false;
    }
}
