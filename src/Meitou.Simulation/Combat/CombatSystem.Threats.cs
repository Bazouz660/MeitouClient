using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// Self preservation's desire for a character (goal 32, AI slot 17 <c>FUN_140599d20</c>, <b>Verified</b>; docs/game/combat.md "Fight lifecycle and orders"): 1 while a threat stands
    /// within <see cref="RetaliationSystem.ImmediateReach"/> (Character <c>+0x2b0</c>, <c>iAmBeingMeleeAttacked immediate</c>), else 0.7 while one of positive aggression stands within
    /// <see cref="RetaliationSystem.SelfDefenceReach"/>, else 0. The threats are the fighters of another faction whose target the character is (the original's list is the sensed characters
    /// whose intended target it is, <c>SensoryData::addToThreatsAndFlockingList</c>, <b>Observed</b>; an attacker is put in the senses of whom it attacks); the immediate ones
    /// are those <see cref="ImmediateThreat"/> counts. <paramref name="centre"/> is the mean position of the threats within reach (the origin <c>Task_Runaway</c> flees from, <c>FUN_14034b440</c>).
    /// Reads the last tick's state, so any thread after Schedule.
    /// </summary>
    public float ThreatLevel(World world, int self, out Vector3 centre)
    {
        centre = default;
        var table = world.Characters;
        var prev = table.Previous;
        if ((uint)self >= (uint)prev.Length || table.Cold(self) is not { } me) return 0;
        float level = 0;
        int count = 0;
        var sum = Vector3.Zero;
        int generation = prev[self].Generation;
        var here = prev[self].Position;
        float reach2 = RetaliationSystem.SelfDefenceReach * RetaliationSystem.SelfDefenceReach, near2 = RetaliationSystem.ImmediateReach * RetaliationSystem.ImmediateReach;
        foreach (var (_, _, _, a) in index.AttackersOf(self))
        {
            ref readonly var o = ref read[a];
            if (o.TargetGeneration != generation || o.Down || table.Cold(a) is not { } c || c.Faction == me.Faction) continue;
            var at = prev[a].Position;
            var d = at - here;
            float d2 = d.X * d.X + d.Z * d.Z;
            if (d2 > reach2) continue;
            // The immediate flag also wants the threat free (not in a bed or cage, not carried) and not behind a closed gate (ImmediateThreat).
            bool immediate = d2 <= near2 && c.InSomething == global::Meitou.Simulation.Locks.HeldState.None && c.CarriedBy.IsNone && (Locks is not { } locks || locks.NotBlocked(world, here, at));
            level = MathF.Max(level, immediate ? 1 : 0.7f);
            sum += at;
            count++;
        }
        if (count > 0) centre = sum / count;
        return level;
    }
}
