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
    /// not standing; 15 / 288 <c>scoreProtectAllies</c> (<c>FUN_140599b90</c>, <b>Verified</b>) 0 while Passive or sneaking outside a fight and for a slave holding job 187 (Meitou's mark of it:
    /// a slave with a master squad), x0.5 while carrying (never here: the AI does not run a carrier); the rest 1 (144's 0.5 is its profile's). Engine gates for all: a character that cannot
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
    /// The character a combat goal would fight (<see cref="CharacterId.None"/>: the finder's factor 0). Attack goals: <see cref="AggroSystem.FindFoe"/>. Protect goals: <see cref="ProtectTarget"/>.
    /// Reads only the last tick's state (any thread in the think).
    /// </summary>
    CharacterId FightTargetFor(World world, int slot, TaskType type, AiMind mind) => type switch
    {
        TaskType.ATTACK_ENEMIES => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Enemies) ?? CharacterId.None,
        TaskType.ATTACK_ENEMIES_AND_NEUTRALS => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.EnemiesAndNeutrals) ?? CharacterId.None,
        TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.Territory) ?? CharacterId.None,
        TaskType.ATTACK_TROUBLE_MAKERS => aggro?.FindFoe(world, slot, AggroSystem.FoeFinder.TroubleMakers) ?? CharacterId.None,
        _ when Protects(type) => ProtectTarget(world, slot, type, mind),
        _ => CharacterId.None,
    };

    /// <summary>
    /// The protect goals' finders and their plan row 13 (docs/game/ai-tasks.md "Combat goals", <b>Verified</b> finders): 143 PROTECT_OWN_SQUAD (<c>FUN_14099d510</c>) takes a squad mate that
    /// its last look sensed now (seen or heard, <c>FUN_140855960</c> with flag 1) and whose fight holds a target (<c>FUN_14099a190</c> non-zero), 15 / 288 PROTECT_ALLIES (<c>FUN_14099ca10</c>)
    /// an entry of its senses tagged ALLY (same faction or relation 50 or more), not down, seen or heard now, holding a target, a slave or escaped slave only where the looker may help one
    /// (<c>FUN_140997e80</c>, <see cref="RetaliationSystem.SlaveHelpAllowed(World, PopulationData?, Meitou.Data.Gameplay.FactionRelations?, CharacterCold, System.Numerics.Vector3)"/>); then
    /// row 13 (<c>AI::findAttackerOf</c>, <see cref="Combat.CombatSystem.BestAttackerOf"/>) the protected one's attacker within 150 of it that is not the looker's ally, best by the target score.
    /// The protected one is the first (squad member order, sense entries in slot order) for which 13 finds an attacker: <c>FUN_14099a190</c>'s value is <b>Unknown</b>, so every candidate counts
    /// alike (engine choice), and 143's fallback hand list (+0x2f8) is not modelled. Reads only the last tick's state (any thread in the think).
    /// </summary>
    CharacterId ProtectTarget(World world, int slot, TaskType type, AiMind mind)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (combat is not { } fights || aggro is not { } look || look.SensesOf(slot) is not { } senses || table.Cold(slot) is not { } me) return CharacterId.None;
        if (type == TaskType.PROTECT_OWN_SQUAD)
        {
            foreach (var m in mind.Squad.Members)
            {
                if (m.Slot == slot || (uint)m.Slot >= (uint)prev.Length || !prev[m.Slot].Alive || prev[m.Slot].Generation != m.Generation || !senses.Knows(m, now: true)) continue;
                if (fights.TargetOf(m.Slot, m.Generation).Slot < 0 || table.Cold(m.Slot) is not { Medical.Incapacitated: false }) continue;
                int foe = fights.BestAttackerOf(world, m.Slot, slot);
                if (foe >= 0) return new CharacterId(foe, prev[foe].Generation);
            }
            return CharacterId.None;
        }
        foreach (var e in senses.Entries)
        {
            int a = e.Target;
            if (a == slot || !e.Now || (e.Tags & (SenseTag.Ally | SenseTag.Ko)) != SenseTag.Ally || (uint)a >= (uint)prev.Length || !prev[a].Alive || prev[a].Generation != e.Generation) continue;
            if (fights.TargetOf(a, e.Generation).Slot < 0 || table.Cold(a) is not { Medical.Incapacitated: false } ally) continue;
            if (ally.SlaveState is 1 or 2 && !RetaliationSystem.SlaveHelpAllowed(world, data, look.Relations, me, prev[slot].Position)) continue;
            int foe = fights.BestAttackerOf(world, a, slot);
            if (foe >= 0) return new CharacterId(foe, prev[foe].Generation);
        }
        return CharacterId.None;
    }

    /// <summary>
    /// Starts the fights the thinks asked for (serial, slot order; docs/game/ai-tasks.md "Combat goals"): an attack goal's as a goal's attack (row 16), a protect goal's as a defence (row 13),
    /// both of the goal's provenance, for the next tick. A character fighting by then, or whose pick is gone or down, starts none (it thinks again). Each character asks for one fight at
    /// most, so none joins two in a tick. The squad mates of one that starts are asked to think at the next tick, when its fight holds a target, so 143 finds it at once.
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
            aggro?.GoalFight(world, s, f, voice: !protects);
            foreach (var m in mind.Squad.Members)
                if (m.Slot != s && table.TryResolveNext(m, out int ms) && table.Cold(ms) is { InCombat: false, Ai: { } mate }) mate.Replan = true;
        }
    }
}
