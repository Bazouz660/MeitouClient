using System.Numerics;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// Walks an attacker to its target through the path service (docs/simulation.md "Combat"). The combat system is run with <c>SelfApproach</c> off, so a character with the
/// <see cref="CharacterTask.Attack"/> task would stand still; this system, placed before the movement system, asks for a path to the target, or to a point ahead of a fleeing
/// target (a new one when the goal has moved more than <see cref="RepathDistance"/>, at most every <see cref="RepathSeconds"/>) and drops the path once the target is in reach
/// or a swing is on.
/// </summary>
public sealed class PursuitSystem(CombatSystem combat) : ITickSystem
{
    public const float RepathDistance = 12, RepathSeconds = 0.5f;

    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Attack || table.Cold(i)?.Bashing is not null) continue;   // a basher walks to its door (Locks.LockSystem)
            var st = combat.StateOf(new CharacterId(i, n.Generation));
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) continue;
            const ushort Path = (ushort)MoveFlags.AnyPath;
            var d = next[t].Position - n.Position;
            var cold = table.Cold(i)!;
            var tc = table.Cold(t);
            float gap = MathF.Sqrt(d.X * d.X + d.Z * d.Z) - CombatTuning.Footprint(cold) - CombatTuning.Footprint(tc);
            // It runs at the target out of the combat stance from RunGap, back to its footwork under FootworkGap, and after a target that keeps moving away at any gap.
            var tv = next[t].Velocity;
            bool away = tc is { InCombat: false } && tv.X * tv.X + tv.Z * tv.Z > CombatTuning.FleeingSpeed * CombatTuning.FleeingSpeed;
            cold.TargetFleeingTime = away ? cold.TargetFleeingTime + dt : 0;
            if (gap > CombatTuning.RunGap) cold.CombatRunning = true;
            else if (gap < CombatTuning.FootworkGap) cold.CombatRunning = false;
            bool fleeing = cold.TargetFleeingTime >= CombatTuning.FleeingSeconds;
            if (fleeing) cold.CombatRunning = true;
            n.TaskTime -= dt;
            // After a fleeing target it aims ahead of it (CombatTuning.LeadBase), so a faster chaser runs past; the lead follows the target's velocity, so it swings round
            // when the target turns. An engine rule: the lead and the run past hold only while the chaser is behind the target; once level or ahead it turns on the target
            // and stops within reach, so the runner comes into it.
            var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
            bool behind = fleeing && tv.X * d.X + tv.Z * d.Z > 0 && cold.Fighter is not null && cold.Stats is not null && cold.Medical is not null;
            if (behind)
            {
                float lead = (1 - combat.AttackRate(cold)) * CombatTuning.LeadSlope + CombatTuning.LeadBase;
                var ahead = goal + new Vector2(tv.X, tv.Z) * lead;
                if (world.Walkability.IsWalkable(ahead.X, ahead.Y, next[t].Position.Y)) goal = ahead;   // off the navmesh (a wall, a cliff) it goes for the target itself
            }
            // A crossbowman walks only while closing in (out of range or the line of fire blocked); aiming and reloading it stands.
            bool shooting = st.Ranged && st.RangedState != (byte)RangedStates.Closing;
            bool holding = Orders.Has(cold, CharacterOrders.Hold);
            if (st.Down || st.AttackTech >= 0 || st.ReactTech >= 0 || world.Tick < st.StunUntil || shooting || !st.Ranged && !behind && gap <= CombatTuning.CloseInGap || holding)
            {
                // In reach, mid-swing or staggered (the hit reaction carries it), or holding position: stand and fight.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    table.Cold(i)!.PathRequest = 0;
                }
                continue;
            }
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > RepathDistance * RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = RepathSeconds;
        }
    }
}


/// <summary>
/// A minimal answer to being attacked (an <b>engine rule</b>, the original's AI is stage 9): every <see cref="CheckEveryTicks"/> ticks, a character that is awake, armed with a
/// <see cref="Fighter"/>, not fighting already, and that someone is attacking, fights that attacker, and so do its squad mates within <see cref="JoinRange"/> units of it that are
/// free too. An NPC always answers; one of the player's characters answers only while it has no order (task <see cref="CharacterTask.Idle"/>: not walking to an order, treating or
/// resting), so a player who sends a character away is obeyed; the original's Passive toggle (non-combat mode) would turn it off and is not modelled (its default is off).
/// The orders go into the world's deferred list for the next tick (<see cref="World.Defer"/>) and are marked <see cref="AttackOrder.Defensive"/>.
/// </summary>
public sealed class RetaliationSystem(CombatSystem combat) : ITickSystem
{
    public const int CheckEveryTicks = 15;
    public const float JoinRange = 250;

    public void SlowWorld(World world)
    {
        if (world.Tick % CheckEveryTicks != 0) return;
        var table = world.Characters;
        var next = table.Next;
        var taken = new HashSet<int>();
        for (int a = 0; a < table.HighWater; a++)
        {
            if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) continue;
            var attacker = new CharacterId(a, next[a].Generation);
            var st = combat.StateOf(attacker);
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration || table.Cold(a) is not { Medical.Incapacitated: false }) continue;
            if (!CanFight(table, t, taken)) continue;
            // A gunner leaves its turret only for a melee attacker (task 146's "!iAmBeingMeleeAttacked"); bolts do not bring it down.
            if (st.Ranged && table.Cold(t)?.Gunning is not null) continue;
            var defenders = new List<CharacterId> { new(t, next[t].Generation) };
            taken.Add(t);
            JoinMates(world, table, t, defenders, taken, CanFight);
            // The Hold order answers only an attacker within reach (Orders.HoldReach).
            var from = next[a].Position;
            for (int k = defenders.Count - 1; k >= 0; k--)
                if (Orders.Has(table.Cold(defenders[k].Slot), CharacterOrders.Hold) && !Orders.InHoldReach(next[defenders[k].Slot].Position, from)) defenders.RemoveAt(k);
            if (defenders.Count == 0) continue;
            world.Defer(new AttackOrder(defenders, attacker) { Tick = world.Tick + 1, Defensive = true });
        }
    }

    /// <summary>
    /// Adds the squad mates of <paramref name="slot"/> within <see cref="JoinRange"/> of it that <paramref name="free"/> accepts to <paramref name="fighters"/> and to
    /// <paramref name="taken"/>, in the squad's member order (the join rule retaliation and <see cref="AggroSystem"/> share).
    /// </summary>
    public static void JoinMates(World world, CharacterTable table, int slot, List<CharacterId> fighters, HashSet<int> taken, Func<CharacterTable, int, HashSet<int>, bool> free)
    {
        var next = table.Next;
        var squad = table.Cold(slot)!.SquadId >= 0 ? world.Squads.Find(table.Cold(slot)!.SquadId) : null;
        if (squad is null) return;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || s == slot || !free(table, s, taken)) continue;
            var d = next[s].Position - next[slot].Position;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange) continue;
            fighters.Add(m);
            taken.Add(s);
        }
    }

    static bool CanFight(CharacterTable table, int slot, HashSet<int> taken)
    {
        if (taken.Contains(slot)) return false;
        byte task = table.Next[slot].Task;
        if (task == (byte)CharacterTask.Attack) return false;
        if (table.Cold(slot) is not { Fighter.CanAttack: true, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false, Carrying.IsNone: true } cold) return false;   // a carrier does not answer (the original's engagement weight is 0 while carrying)
        // The Passive order (non-combat mode) does not answer attacks by itself.
        return !cold.IsPlayer || task == (byte)CharacterTask.Idle && (cold.Orders & CharacterOrders.Passive) == 0;
    }
}
