using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The combat goals of an NPC's lists as priority-3 goals of the chooser (<c>chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Combat goals"): 16, 21, 144 and 34 attack whom their
/// finder takes, 143, 15 and 288 the attacker of a squad mate or an ally in a fight (row 13, <c>AI::findAttackerOf</c>). The think weighs them with every other goal (desire x finder factor x
/// weight x crowding, x1.25 for the current one) and writes only its own mind's request (<see cref="AiMind.FightTarget"/>); the serial step starts the fight.
/// </summary>
public sealed partial class AiSystem
{
    // Resolved in the serial step, read by the think (null before the first tick: no finder finds anything).
    AggroSystem? aggro;

    // Set by a think that asked for a fight (AiMind.FightTarget).
    int fighting;

    /// <summary>Whether a combat goal defends someone (its plan row is 13 Attacking attackers of, a defence) rather than attacking (row 4 / 16).</summary>
    static bool Protects(TaskType type) => type is TaskType.PROTECT_OWN_SQUAD or TaskType.PROTECT_ALLIES or TaskType.PROTECT_ALLIES_STAY_IN_TOWN;

    /// <summary>
    /// A combat goal's desire before its finder (docs/game/ai-tasks.md "Combat goals" and "Desire and commitment"): 16 <c>scoreAttackEnemies</c> is 0 while Passive, sneaking outside a fight or
    /// not standing; 15 / 288 <c>scoreProtectAllies</c> (<c>FUN_140599b90</c>, <b>Verified</b>) 0 while Passive (even in a fight), while sneaking and not in combat mode and for a slave holding
    /// job 187 (Meitou's mark of it: a slave with a master squad), x0.5 while carrying (a carrier on a town errand, <see cref="Preempts"/>); the rest 1 (144's 0.5 is its profile's). The finder's factor
    /// multiplies it (<see cref="FightPick"/>). Engine gates for all: a character that cannot
    /// fight (no working fighter, crawling, or one Self preservation runs away with, <see cref="SelfPreservation.Flees"/>) and a turret's gunner (it keeps to its turret) want none.
    /// </summary>
    static float FightDesire(World world, int slot, TaskType type, AiMind mind)
    {
        if (world.Characters.Cold(slot) is not { Fighter.CanAttack: true, Medical: { Crawling: false } medical, Gunning: null } cold || SelfPreservation.Flees(cold, 1)) return 0;
        bool passive = (cold.Orders & CharacterOrders.Passive) != 0, sneaking = (cold.Orders & CharacterOrders.Sneak) != 0 && !cold.InCombat;
        return type switch
        {
            TaskType.ATTACK_ENEMIES => passive || sneaking || medical.Prone ? 0 : 1,
            TaskType.PROTECT_ALLIES or TaskType.PROTECT_ALLIES_STAY_IN_TOWN => passive || sneaking || cold.SlaveState == 1 && cold.SlaveMaster >= 0 ? 0 : cold.Carrying.IsNone ? 1 : 0.5f,
            _ => 1,
        };
    }

    /// <summary>
    /// A combat goal's finder: its factor and the character it would fight (<see cref="CharacterId.None"/> with the factor 0). Attack goals: <see cref="AggroSystem.FindFoe"/>, factor 1 with a
    /// pick. Protect goals: <see cref="ProtectPick"/>. Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) FightPick(World world, int slot, TaskType type, AiMind mind)
    {
        if (Protects(type)) return ProtectPick(world, slot, type, mind);
        var foe = type switch
        {
            TaskType.ATTACK_ENEMIES => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Enemies),
            TaskType.ATTACK_ENEMIES_AND_NEUTRALS => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.EnemiesAndNeutrals),
            TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Territory),
            TaskType.ATTACK_TROUBLE_MAKERS => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.TroubleMakers),
            _ => null,
        } ?? CharacterId.None;
        return (foe.IsNone ? 0 : 1, foe);
    }

    /// <summary>
    /// A combat goal's finder factor for the chooser (<see cref="FightPick"/>). The current goal whose fight runs (<see cref="OwnsFight"/>) is kept while its goal is not done (the original's plan
    /// step ends a goal only when its list A holds, its hours end or its row cannot run, <c>FUN_14050d970</c>, <b>Observed</b>; the running fight is the queued task its plan accepts): an attack goal
    /// while the looker senses an enemy (16's effect, state 6 <c>isNoEnemiesInVicinity</c>, <c>numEnemies</c>; engine reading for 21, 144 and 34) or its finder still picks one; a protect goal while
    /// its finder values a mate or ally above 0 (its own fight counts for 143: <c>FUN_14099a190</c>'s 0.01 floor), row 13 not asked again.
    /// </summary>
    float FightFactor(World world, int slot, TaskType type, AiMind mind)
    {
        if (mind.Goal?.Task.Type == type && world.Characters.Cold(slot) is { InCombat: true } cold && OwnsFight(cold, mind))
        {
            if (Protects(type)) return ProtectPick(world, slot, type, mind, attacker: false).Factor;
            if (aggro?.SensesOf(slot) is { Enemies: > 0 }) return 1;
        }
        return FightPick(world, slot, type, mind).Factor;
    }

    /// <summary>
    /// The protect goals' finders and their plan row 13 (docs/game/ai-tasks.md "Combat goals", <b>Verified</b> finders): 143 PROTECT_OWN_SQUAD (<c>FUN_14099d510</c>) values each squad mate that
    /// is the looker or that its last look sensed now (seen or heard, <c>FUN_140855960</c> with flag 1), 15 / 288 PROTECT_ALLIES (<c>FUN_14099ca10</c>, the same for both) each entry of its
    /// senses tagged ALLY (same faction or relation 50 or more), not down, seen or heard now, a slave or escaped slave only where the looker may help one (<c>FUN_140997e80</c>,
    /// <see cref="RetaliationSystem.SlaveHelpAllowed(World, PopulationData?, Meitou.Data.Gameplay.FactionRelations?, CharacterCold, System.Numerics.Vector3)"/>), by its fight threat
    /// (<c>FUN_14099a190</c>, <see cref="Combat.CombatSystem.FightThreat"/>; no distance factor for an NPC looker). The best (the first on a tie), capped at 1.2, is the factor; then row 13
    /// (<c>AI::findAttackerOf</c>, <see cref="Combat.CombatSystem.BestAttackerOf"/>) takes that one's attacker within 150 of it that is not the looker's ally, best by the target score; none
    /// fails the plan (factor 0). 143's fallback hand list (+0x2f8, x0.1) is not modelled. Without <paramref name="attacker"/> only the finder's value (row 13 not asked, the target none).
    /// Reads only the last tick's state (any thread in the think).
    /// </summary>
    (float Factor, CharacterId Target) ProtectPick(World world, int slot, TaskType type, AiMind mind, bool attacker = true)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (combat is not { } fights || aggro is not { } look || look.SensesOf(slot) is not { } senses || table.Cold(slot) is not { } me) return (0, CharacterId.None);
        int best = -1;
        float value = 0;
        if (type == TaskType.PROTECT_OWN_SQUAD)
        {
            foreach (var m in mind.Squad.Members)
            {
                if ((uint)m.Slot >= (uint)prev.Length || !prev[m.Slot].Alive || prev[m.Slot].Generation != m.Generation || m.Slot != slot && !senses.Knows(m, now: true)) continue;
                float v = fights.FightThreat(world, m.Slot, look.SensesOf(m.Slot));
                if (v > value) (best, value) = (m.Slot, v);
            }
        }
        else
        {
            bool slaveChecked = false, slaveOk = false;
            foreach (var e in senses.Entries)
            {
                int a = e.Target;
                if (a == slot || !e.Now || (e.Tags & (SenseTag.Ally | SenseTag.Ko)) != SenseTag.Ally || (uint)a >= (uint)prev.Length || !prev[a].Alive || prev[a].Generation != e.Generation
                    || table.Cold(a) is not { } ally) continue;
                if (ally.SlaveState is 1 or 2)
                {
                    if (!slaveChecked) (slaveChecked, slaveOk) = (true, RetaliationSystem.SlaveHelpAllowed(world, data, look.Relations, me, prev[slot].Position));
                    if (!slaveOk) continue;
                }
                float v = fights.FightThreat(world, a, look.SensesOf(a));
                if (v > value) (best, value) = (a, v);
            }
        }
        if (best < 0) return (0, CharacterId.None);
        if (!attacker) return (MathF.Min(value, Combat.CombatSystem.ProtectCap), CharacterId.None);
        int foe = fights.BestAttackerOf(world, best, slot);
        return foe < 0 ? (0, CharacterId.None) : (MathF.Min(value, Combat.CombatSystem.ProtectCap), new CharacterId(foe, prev[foe].Generation));
    }

    /// <summary>
    /// Starts the fights the thinks asked for (serial, slot order; docs/game/ai-tasks.md "Combat goals"): an attack goal's as a goal's attack (row 16), a protect goal's as a defence (row 13),
    /// both of the goal's provenance, for the next tick; the fight is the AI's own (<see cref="AiMind.FightFrom"/>), so the character thinks in it. A character fighting by then, or whose pick
    /// is gone or down, starts none (it thinks again). Each character asks for one fight at most, so none joins two in a tick. The squad mates of one that starts are asked to think at the next
    /// tick, when its fight holds a target, so 143 finds it at once.
    /// </summary>
    void SendFights(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { } mind } cold || mind.FightTarget.IsNone) continue;
            var foe = mind.FightTarget;
            var goal = mind.FightGoal;
            mind.FightTarget = CharacterId.None;
            if (!table.Next[s].Alive || cold.InCombat || !table.TryResolveNext(foe, out int f) || table.Cold(f) is not { Medical.Incapacitated: false }) continue;
            bool protects = Protects(goal);
            world.Defer(new AttackOrder([table.IdOf(s)], foe) { Tick = world.Tick + 1, Source = TaskSource.Goal, Defensive = protects });
            mind.FightFrom = world.Tick + 1;
            aggro?.GoalFight(world, s, f, voice: !protects);
            foreach (var m in mind.Squad.Members)
                if (m.Slot != s && table.TryResolveNext(m, out int ms) && table.Cold(ms) is { InCombat: false, Ai: { } mate }) mate.Replan = true;
        }
    }

    /// <summary>
    /// Ends the fights whose fighter's think chose a goal that is no fight (<see cref="InFight"/>; serial, slot order): the fight task ends as any task's start ends it (<see cref="TaskStart.Become"/>,
    /// the character idle), and the character thinks at the next tick, which acts the chosen goal out.
    /// </summary>
    void LeaveFights(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { LeaveFight: true } mind } cold) continue;
            mind.LeaveFight = false;
            if (table.Next[s].Alive && cold.InCombat) TaskStart.Become(world, s, CharacterTask.Idle, TaskType.IDLE, TaskSource.Goal);
            mind.Replan = true;
        }
    }
}
