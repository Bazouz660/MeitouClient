using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>How long a fighter keeps its target before it chooses again (CombatClassAI +0x2f0, docs/game/combat.md "Attack slots and circling"): 1 s, 0.3 s on Hold.</summary>
    public const float PickSeconds = 1, HoldPickSeconds = 0.3f;

    static int PickTicks(World world, CharacterCold cold) => Math.Max(1, (int)MathF.Round((Orders.Holding(cold) ? HoldPickSeconds : PickSeconds) / world.TickSeconds));

    /// <summary>The chooser's side of a target choice and the best candidate so far.</summary>
    struct Choice
    {
        public int Self, Current;
        public CharacterCold Cold;
        public bool Player, Hold, Prone, Animal;
        public Vector2 HoldPoint;
        public int Best;
        public float BestScore;
    }

    /// <summary>
    /// A fighter's target choice (<c>CombatClassAI::chooseAttackTarget</c>, scored by <see cref="TargetScore"/>): its current target (when <paramref name="keep"/>), the ones
    /// fighting it, and, unless it is Passive, the ones fighting its squad mates within <see cref="RetaliationSystem.JoinRange"/> (engine reading of the protectee's attackers:
    /// every mate counts). Every candidate fights someone of its side, so the leave-alone test holds. Reads the last tick's state only; -1 when none qualifies.
    /// </summary>
    int ChooseTarget(World world, int i, in CombatSlot s, CharacterCold cold, bool keep)
    {
        var table = world.Characters;
        var prev = table.Previous;
        bool hold = Orders.Holding(cold) && !s.Ranged;
        var q = new Choice
        {
            Self = i, Current = s.TargetSlot, Cold = cold, Player = cold.IsPlayer, Hold = hold, Prone = cold.Medical!.Crawling, Animal = cold.Fighter!.IsAnimal,
            HoldPoint = hold ? Orders.HoldPointOf(cold, prev[i].Position) : default, Best = -1, BestScore = float.NegativeInfinity,
        };
        // The current target first, so a tie keeps it.
        if (keep) Consider(world, ref q, s.TargetSlot, false);
        int generation = prev[i].Generation;
        foreach (var (_, _, _, a) in index.AttackersOf(i)) if (read[a].TargetGeneration == generation) Consider(world, ref q, a, false);
        if (!Orders.Has(cold, CharacterOrders.Passive) && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad)
        {
            foreach (var m in squad.Members)
            {
                int ms = m.Slot;
                if (ms == i || !table.IsAlive(m)) continue;
                float dx = prev[ms].Position.X - prev[i].Position.X, dz = prev[ms].Position.Z - prev[i].Position.Z;
                if (dx * dx + dz * dz > RetaliationSystem.JoinRange * RetaliationSystem.JoinRange) continue;
                foreach (var (_, _, _, a) in index.AttackersOf(ms))
                    if (read[a].TargetGeneration == m.Generation) Consider(world, ref q, a, read[ms].TargetSlot != a);
            }
        }
        return q.Best;
    }

    void Consider(World world, ref Choice q, int c, bool protect)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int i = q.Self;
        if (c == i || (uint)c >= (uint)prev.Length || !prev[c].Alive || read[c].Down || table.Cold(c) is not { Medical.Incapacitated: false, IsImprisoned: false } tc) return;
        if (tc.Faction == q.Cold.Faction || q.Cold.SquadId >= 0 && tc.SquadId == q.Cold.SquadId) return;
        ref readonly var o = ref read[c];
        if (q.Hold && !Orders.HoldPost(prev[i].Position, q.HoldPoint, prev[c].Position, prev[c].Velocity, false, o.TargetSlot == i)) return;
        int attackers = AttackerCount(world, c);
        float dx = prev[c].Position.X - prev[i].Position.X, dz = prev[c].Position.Z - prev[i].Position.Z;
        var candidate = new TargetCandidate
        {
            Current = c == q.Current,
            Staggered = o.StumbleClip >= 0 && world.Tick < o.StunUntil,
            Attackers = attackers,
            ChooserAttacks = c == q.Current,
            Taunt = Orders.Has(tc, CharacterOrders.Taunt),
            Passive = Orders.Has(tc, CharacterOrders.Passive),
            Protect = protect && attackers < 2,
            Distance = MathF.Sqrt(dx * dx + dz * dz),
            Fighting = o.TargetSlot >= 0,
            Gunner = tc.Gunning is not null,
            Crippled = tc.Medical.Crawling || tc.Fighter is not { CanAttack: true },
        };
        if (TargetScore.Score(candidate, q.Player, q.Hold, q.Prone, q.Animal) is { } score && score > q.BestScore)
        {
            q.Best = c;
            q.BestScore = score;
        }
    }

    /// <summary>How many fight the character in <paramref name="slot"/> (standing fighters whose target it is), as of the last tick; any thread after Schedule.</summary>
    public int AttackerCount(World world, int slot)
    {
        int n = 0, generation = world.Characters.Previous[slot].Generation;
        foreach (var (_, _, _, a) in index.AttackersOf(slot)) if (read[a].TargetGeneration == generation && !read[a].Down) n++;
        return n;
    }

    /// <summary>Turns a fighter on a new target: it asks its target again for a place and weighs its techniques afresh.</summary>
    static void Retarget(ref CombatSlot s, int target, int generation)
    {
        s.TargetSlot = target;
        s.TargetGeneration = generation;
        s.Engaged = false;
        s.LastTech = -1;
        s.Picked = false;
    }
}
