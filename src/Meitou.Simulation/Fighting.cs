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
            // One of more than MaxEngaged fighters that its target has not let close in circles it (WaitingStateAI, docs/game/combat.md "Attack slots and circling").
            int around = !st.Ranged && !st.Engaged && !behind ? combat.FightersAt(world, t) : 0;
            if (around > CombatSystem.MaxEngaged && !st.Down && world.Tick >= st.StunUntil && st.ReactTech < 0 && !holding)
            {
                Circle(world, i, ref n, cold, tc, t, d, gap, around);
                continue;
            }
            float stand = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0);
            if (st.Down || st.AttackTech >= 0 || st.ReactTech >= 0 || world.Tick < st.StunUntil || shooting || !st.Ranged && !behind && gap <= stand || holding)
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

    /// <summary>
    /// Walks a fighter its target has not let close in round the target (<see cref="CombatTuning.CircleSpeedOf"/>): every <see cref="RepathSeconds"/> a path to the point
    /// <see cref="CircleLookAhead"/> seconds of side-stepping further round, at the gap kept within the band of <see cref="CombatTuning.CircleNear"/> /
    /// <see cref="CombatTuning.CircleFar"/>. The original steers by forces and faces its target while it side-steps; here the walker follows the path and faces its way (engine).
    /// </summary>
    static void Circle(World world, int i, ref CharacterHot n, CharacterCold cold, CharacterCold? tc, int t, System.Numerics.Vector3 d, float gap, int around)
    {
        const ushort Path = (ushort)MoveFlags.AnyPath;
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || n.TaskTime > 0) return;
        float k = cold.Medical?.ScaleK ?? 1;
        float own = 1.5f * CombatTuning.Footprint(cold) * k;
        float near = MathF.Max(1, around * 0.25f + CombatTuning.CircleNear - own);
        float far = MathF.Max(3 * around + CombatTuning.CircleFar - own, near + 5);
        float bodies = CombatTuning.Footprint(cold) + CombatTuning.Footprint(tc);
        float radius = Math.Clamp(gap, near, far) + bodies;
        var table = world.Characters;
        ulong key = Rng.Key(table.IdOf(i)), counter = (ulong)(uint)t * 3;
        float speed = CombatTuning.CircleSpeedOf(CombatRolls.Float(world.Seed, key, CombatRoll.Circle, counter), CombatRolls.Float(world.Seed, key, CombatRoll.Circle, counter + 1),
            CombatRolls.Float(world.Seed, key, CombatRoll.Circle, counter + 2), cold.Fighter?.EncumbranceFactor ?? 1, cold.Fighter is not { IsAnimal: true });
        var target = table.Next[t].Position;
        float angle = MathF.Atan2(-d.Z, -d.X) + speed * CircleLookAhead / MathF.Max(radius, 1);
        var goal = new Vector2(target.X + MathF.Cos(angle) * radius, target.Z + MathF.Sin(angle) * radius);
        if (!world.Walkability.IsWalkable(goal.X, goal.Y, target.Y))
        {
            // Off the navmesh: it stands where it is (engine choice).
            if ((n.Flags & Path) != 0) { n.Flags = (ushort)(n.Flags & ~Path); n.PathCursor = 0; cold.PathRequest = 0; }
            return;
        }
        n.Goal = goal;
        n.Mode = SpeedMode.Free;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        n.TaskTime = RepathSeconds;
    }

    /// <summary>Seconds of side-stepping the circling walker's path point lies ahead (engine choice: twice the path interval, so the walker never reaches it).</summary>
    public const float CircleLookAhead = 1;
}


/// <summary>
/// A minimal answer to being attacked (an <b>engine rule</b>, the original's AI is stage 9): every <see cref="CheckEveryTicks"/> ticks, a character that is awake, armed with a
/// <see cref="Fighter"/>, not fighting already, and that someone is attacking, fights that attacker, and so do its squad mates within <see cref="JoinRange"/> units of it that are
/// free too. An NPC always answers; one of the player's characters answers only while it has no order (task <see cref="CharacterTask.Idle"/>: not walking to an order, treating or
/// resting), so a player who sends a character away is obeyed; the original's Passive toggle (non-combat mode) would turn it off and is not modelled (its default is off).
/// The orders go into the world's deferred list for the next tick (<see cref="World.Defer"/>) and are marked <see cref="AttackOrder.Defensive"/>. With <see cref="SquadAi.Help"/> the player's
/// free characters also defend characters of allied factions (<see cref="HelpAllies"/>).
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
        ResumeGuards(world, table);
        var taken = new HashSet<int>();
        for (int a = 0; a < table.HighWater; a++)
        {
            if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) continue;
            var attacker = new CharacterId(a, next[a].Generation);
            var st = combat.StateOf(attacker);
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration || table.Cold(a) is not { Medical.Incapacitated: false }) continue;
            // A gunner leaves its turret only for a melee attacker (task 146's "!iAmBeingMeleeAttacked"); bolts do not bring it down.
            if (st.Ranged && table.Cold(t)?.Gunning is not null) continue;
            var defenders = new List<CharacterId>();
            if (CanFight(table, t, taken))
            {
                defenders.Add(new(t, next[t].Generation));
                taken.Add(t);
                JoinMates(world, table, t, defenders, taken, CanFight);
            }
            // Bodyguards of the attacked character come too (Bodyguard, order 45).
            Guards(world, table, t, a, defenders, taken);
            // Allies come to a defender that is already fighting too.
            HelpAllies(world, table, t, a, defenders, taken);
            // The Hold order answers only an attacker within reach (Orders.HoldReach).
            var from = next[a].Position;
            for (int k = defenders.Count - 1; k >= 0; k--)
                if (Orders.Has(table.Cold(defenders[k].Slot), CharacterOrders.Hold) && !Orders.InHoldReach(next[defenders[k].Slot].Position, from)) defenders.RemoveAt(k);
            if (defenders.Count == 0) continue;
            world.Defer(new AttackOrder(defenders, attacker) { Tick = world.Tick + 1, Defensive = true });
        }
    }

    /// <summary>
    /// Bodyguard (order 45, <b>engine</b> reading): the free characters following <paramref name="slot"/> as bodyguards (<see cref="CharacterCold.Guarding"/>) within <see cref="JoinRange"/> of it
    /// defend it against <paramref name="attacker"/>, in slot order; never against a character of their own faction.
    /// </summary>
    static void Guards(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken)
    {
        var next = table.Next;
        var target = new CharacterId(slot, next[slot].Generation);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || s == attacker || table.Cold(s) is not { Guarding: true } c || c.FollowOrder != target || table.Cold(attacker)?.Faction == c.Faction) continue;
            var d = next[s].Position - next[slot].Position;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange || !CanFight(table, s, taken)) continue;
            defenders.Add(new CharacterId(s, next[s].Generation));
            taken.Add(s);
        }
    }

    /// <summary>A bodyguard whose fight ended (its task fell back to idle) goes back to its target.</summary>
    static void ResumeGuards(World world, CharacterTable table)
    {
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || next[s].Task != (byte)CharacterTask.Idle || table.Cold(s) is not { Guarding: true, InCombat: false } c) continue;
            if (c.FollowOrder.IsNone || !table.TryResolveNext(c.FollowOrder, out _)) { c.Guarding = false; c.FollowOrder = CharacterId.None; continue; }
            next[s].Task = (byte)CharacterTask.Follow;
            next[s].TaskTime = 0;
            c.FollowPathTime = 0;
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

    /// <summary>
    /// <see cref="SquadAi.Help"/> (the protect-allies finders <c>FUN_14099ca10</c> / <c>FUN_14099cf80</c>, which take a fighting character of another faction only with the global
    /// +0x111 set, and then with the weight squared): the player's free characters within <see cref="JoinRange"/> of a defender of another, allied faction (relation 50 or more) join
    /// its defence, in slot order. The original's distance weighting is not modelled (engine reduction).
    /// </summary>
    void HelpAllies(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken)
    {
        if ((world.Player.SquadAi & SquadAi.Help) == 0 || combat.Relations is not { } relations || table.Cold(slot) is not { IsPlayer: false, Medical.Incapacitated: false } ally || !relations.Contains(ally.Faction)) return;
        if (table.Cold(attacker) is not { IsPlayer: false } foe || foe.Faction == world.Player.Faction) return;   // never against its own side
        var next = table.Next;
        var at = next[slot].Position;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { IsPlayer: true } c || c.Faction == ally.Faction || !relations.Contains(c.Faction) || !relations.IsAlly(c.Faction, ally.Faction)) continue;
            var d = next[s].Position - at;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange || !CanFight(table, s, taken)) continue;
            defenders.Add(new CharacterId(s, next[s].Generation));
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
        return !cold.IsPlayer || (task == (byte)CharacterTask.Idle || cold.Guarding && task == (byte)CharacterTask.Follow) && (cold.Orders & CharacterOrders.Passive) == 0;
    }
}
