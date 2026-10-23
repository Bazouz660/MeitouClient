using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    // ------------------------------------------------------------------ act

    /// <summary>A stat as the combat formulas read it: times the injury and hunger multiplier and the stats' read-time scale (an animal's age, docs/game/animals.md).</summary>
    /// <summary>The guarding state: the fighter's own, or the Block order (its tooltip's +20 melee defence; docs/game/combat.md).</summary>
    static bool Guarding(CharacterCold c) => c.Fighter!.Guarding || Orders.Has(c, CharacterOrders.Block);

    static float Eff(CharacterCold c, StatsEnumerated stat) => c.Stats![stat] * c.Stats.Scale * c.Medical!.StatMultiplier(stat);

    int PauseTicks(World world, int slot, int counter)
    {
        float u = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(slot)), CombatRoll.Pause, (ulong)(uint)counter ^ ((ulong)(uint)world.Tick << 20));
        return Math.Max(1, (int)MathF.Ceiling((CombatTuning.PauseMin + u * (CombatTuning.PauseMax - CombatTuning.PauseMin)) / world.TickSeconds));
    }

    /// <summary>
    /// The ticks a technique's clip takes at <c>anim speed mult</c> x <paramref name="rate"/> clip seconds per second; the clip's length is from the fighter's skeleton (an
    /// animal's own, <see cref="AnimationLengths.For"/>: <c>attack1</c> is 0.77 s on a bonedog and 5 s on a beak thing).
    /// </summary>
    int Ticks(CombatTechnique t, float rate, float dt, CharacterCold cold)
    {
        float clip = lengths.For(cold.Fighter is { IsAnimal: true } ? cold.Race?.StringId : null).Of(t.AnimName);
        if (clip <= 0) clip = CombatTuning.DefaultClipSeconds;
        return Math.Max(2, (int)MathF.Ceiling(clip / (MathF.Max(t.AnimSpeedMult, 0.1f) * MathF.Max(rate, 0.05f)) / dt));
    }

    /// <summary>
    /// The playback rate of a technique (docs/game/combat.md "Animation speed", <b>Verified</b>): attacks lerp(t, 0.8, 1.2) x G capped at 1.2, blocks lerp(t, 0.6, 4) x G
    /// capped at 4, with t = 0.01 (0.6 dexterity + 0.4 skill) (attack, or defence with the bonuses); dodges 1. G = (0.5 + 0.5 min(1, e + 0.5) (1 - f)) x the gear's
    /// <c>combat speed mult</c>, e the encumbrance factor and f the weapon-weight-over-strength factor of the strength XP.
    /// </summary>
    float PlayRate(CharacterCold cold, CombatTechnique tech, bool attack)
    {
        if (!attack && (tech.IsDodge || !tech.IsBlock)) return 1;
        return Rate(cold, attack);
    }

    /// <summary>The attack play rate of a fighter (its <see cref="PlayRate"/> for any attack; CharStats +0x188 in the original), which the chase lead also reads.</summary>
    public float AttackRate(CharacterCold cold) => Rate(cold, true);

    float Rate(CharacterCold cold, bool attack)
    {
        var fighter = cold.Fighter!;
        float weight = fighter.Unarmed ? 0 : fighter.Weapon!.Stats.Weight;
        float f = Math.Clamp(xp.WeaponWeightStrengthFactor(weight, cold.Stats![StatsEnumerated.Strength], cold.Medical!.StatMultiplier(StatsEnumerated.Strength)), 0, 1);
        float g = (0.5f + 0.5f * MathF.Min(1, fighter.EncumbranceFactor + 0.5f) * (1 - f)) * fighter.CombatSpeed;
        float dex = Eff(cold, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float skill = attack
            ? fighter.Unarmed ? Eff(cold, StatsEnumerated.MartialArts) : Eff(cold, StatsEnumerated.MeleeAttack) + fighter.Weapon!.Stats.AttackMod
            : Eff(cold, StatsEnumerated.MeleeDefence) + fighter.DefenceBonus + (fighter.Weapon?.Stats.DefenceMod ?? 0) + (Guarding(cold) ? 20 : 0);
        float t = 0.01f * (0.6f * dex + 0.4f * skill);
        return attack ? MathF.Min(1.2f, WeaponStats.Lerp(t, 0.8f, 1.2f) * g) : MathF.Min(4f, WeaponStats.Lerp(t, 0.6f, 4f) * g);
    }

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int now = (int)world.Tick;
        float dt = world.TickSeconds;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null) continue;
            ref var s = ref write[i];
            ulong key = Rng.Key(table.IdOf(i));

            // 1. The body: bleeding, healing, waking.
            if (Settings.TickMedical)
            {
                events ??= [];
                events.Clear();
                cold.Medical.Tick(dt, MakeContext(world, i, cold), events);
            }
            // The weapon in hand follows the arms (docs/game/combat.md "Arms"): losing one puts the weapon away and the fight goes on with what the arms still allow.
            if (cold.Fighter.SetArms(cold.Medical.ArmWorks(left: false), cold.Medical.ArmWorks(left: true)) && cold.InCombat && !s.Ranged) cold.DrawnWeapon = MeleeDrawn(cold.Fighter);
            s.Down = cold.Medical.Incapacitated;
            if (s.Down)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.ReactTech = -1;
                s.TargetSlot = -1;
                s.Engaged = false;
                if (cold.InCombat) EndFight(table, i);
                continue;
            }

            // 2. Helpless in a hit reaction until its clip ends; every BreakOutSeconds of it the character may break out into a block against a blow coming at it
            // (the original's +0x124; its 10 % lower bound on the swing's progress is not modelled).
            if (now < s.StunUntil)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                int every = Math.Max(1, (int)MathF.Round(CombatTuning.BreakOutSeconds / dt));
                bool chance = s.StumbleClip >= 0 && now > s.StumbleStart && (now - s.StumbleStart) % every == 0;
                if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks) s.ReactTech = -1;
                if (chance && s.ReactTech < 0)
                {
                    React(world, i, ref s, cold, key, now, dt);
                    if (s.ReactTech >= 0 && !techniques[s.ReactTech].IsDodge) { s.StunUntil = now; s.StumbleClip = -1; }
                    else s.ReactTech = -1;
                }
                if (now < s.StunUntil) continue;
            }

            // 3. The target.
            bool fighting = s.TargetSlot >= 0;
            if (fighting && !(s.TargetSlot < prev.Length && prev[s.TargetSlot].Alive && prev[s.TargetSlot].Generation == s.TargetGeneration && !read[s.TargetSlot].Down))
            {
                fighting = false;
                s.TargetSlot = -1;
                s.AttackTech = -1;
                s.NextBlow = 0;
                EndFight(table, i);
            }

            // 3b. A crossbowman fights with the crossbow or the melee weapon; with the crossbow the ranged controller runs instead of the melee attack (a blow aimed at it is still met).
            ChooseMode(world, i, ref s, cold, fighting);
            if (s.Ranged)
            {
                if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks) s.ReactTech = -1;
                if (s.ReactTech < 0 && s.AttackTech < 0) React(world, i, ref s, cold, key, now, dt);
                TickRanged(world, i, ref s, cold, effects, now, dt);
                continue;
            }

            // 4. The reaction and the attack in progress. A block that ends leaves a short wait (a dodge goes straight back to deciding).
            if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks)
            {
                if (!techniques[s.ReactTech].IsDodge) s.ReadyTick = Math.Max(s.ReadyTick, now + (int)MathF.Ceiling(CombatTuning.AfterBlockPause / dt));
                s.ReactTech = -1;
            }
            float incoming = Incoming(world, i, now, out int yielder);
            // One that lost the initiative roll to it: it turns on that one (FUN_140666a30 sets the winner to attack the asker).
            if (yielder >= 0 && yielder != s.TargetSlot && !s.Ranged)
            {
                s.TargetSlot = yielder;
                s.TargetGeneration = prev[yielder].Generation;
                s.Engaged = false;
                fighting = true;
            }
            if (!fighting) s.Engaged = false;
            bool aborted = false;
            if (s.AttackTech >= 0)
            {
                var tech = techniques[s.AttackTech];
                // Given up to defend: still in the first half of the swing while a blow at it is past its half (docs/game/combat.md "Pacing").
                // It goes straight to the defence; one that may not defend yet (after a failed roll) keeps swinging.
                if (incoming > CombatTuning.AbortIncomingProgress && (now - s.AttackStart) < CombatTuning.AbortOwnProgress * s.AttackTicks && now >= s.DefendReadyTick)
                {
                    s.AttackTech = -1;
                    s.NextBlow = 0;
                    aborted = true;
                }
                else if (s.NextBlow > 0 && fighting && now >= s.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(s.NextBlow) * s.AttackTicks))
                {
                    effects.Emit(i, s.TargetSlot, BlowEffect, s.AttackSeq, default, s.NextBlow);
                    s.Thrown++;
                    int blow = s.NextBlow;
                    s.NextBlow = s.NextBlow < tech.Blows ? s.NextBlow + 1 : 0;
                    // Someone attacking it: the swing ends at this blow's stop frame and it decides again (FUN_14060a590).
                    if (incoming >= 0) Stop(ref s, tech, blow);
                }
            }
            if (s.AttackTech >= 0 && now >= s.AttackEnd)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                float pause = s.LastOutcome switch
                {
                    BlowOutcome.Blocked => CombatTuning.BlockedPause,
                    BlowOutcome.Dodged => CombatTuning.DodgedPause,
                    BlowOutcome.Missed => CombatTuning.MissedPause,
                    _ => CombatTuning.HitPause,
                };
                s.ReadyTick = now + Math.Max(1, (int)MathF.Ceiling(pause / dt));
            }

            // 5. Deciding tries attacking first (asking the target, StartAttack); only when that fails does a fighter defend against the blows coming at it,
            // from anyone (docs/game/combat.md "Pacing"). Being attacked cuts the pause after an attack short; an opponent that lost the initiative to it
            // may be swung at at once.
            if (s.ReactTech < 0 && s.AttackTech < 0)
            {
                // Deciding, it first asks to be one of the fighters its target lets close in (FUN_140666e20); a fighter left out circles (PursuitSystem) and asks again each tick.
                if (fighting && !s.Engaged) s.Engaged = MayJoin(world, i, s.TargetSlot);
                bool yielded = fighting && read[s.TargetSlot].YieldTo == i && now < read[s.TargetSlot].YieldUntil;
                if (!aborted && fighting && (now >= s.ReadyTick || incoming >= 0 || yielded)) StartAttack(world, i, ref s, cold, key, now, dt);
                if (s.AttackTech < 0 && now >= s.DefendReadyTick) React(world, i, ref s, cold, key, now, dt);
            }
        }
    }

    void StartAttack(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int t = s.TargetSlot;
        var fighter = cold.Fighter!;
        if (!fighter.CanAttack || Orders.Has(cold, CharacterOrders.Block)) return;   // the Block order: defends, starts no attack
        if (!s.Engaged) return;   // left out by its target: it circles (PursuitSystem)
        var tc = table.Cold(t)!;
        var medical = cold.Medical!;
        float length = fighter.WeaponLength;
        // The gap to where the target will be (a target that is not fighting is led by LeadSeconds, FUN_1402b4570).
        var aim = prev[t].Position + (tc.InCombat ? Vector3.Zero : prev[t].Velocity * CombatTuning.AimLeadSeconds);
        float e = fighter.EncumbranceFactor;
        var query = new AttackQuery
        {
            Category = fighter.TechniqueCategory,
            Skill = cold.Stats![fighter.WeaponSkill],
            UnarmedSkill = WeaponStats.Lerp(e, 0.2f, 1) * (cold.Stats[StatsEnumerated.MartialArts] + fighter.UnarmedBonus),
            Animal = fighter.IsAnimal,
            Prone = medical.Crawling,
            Encumbrance = e,
            WeaponLength = length,
            Scale = medical.ScaleK,
            Gap = CombatTuning.Gap(prev[i].Position, aim, cold, tc),
            Previous = s.LastTech,
            TargetStaggered = now < read[t].StunUntil,
            LeftArm = medical.ArmWorks(left: true),
            RightArm = medical.ArmWorks(left: false),
            LegDown = LegDown(medical),
        };
        float roll = CombatRolls.Float(world.Seed, key, CombatRoll.Technique, (ulong)(s.AttackSeq + 1));
        int pick = TechniqueChooser.ChooseAttack(techniques, query, roll, out s.Reach);
        if (pick < 0) return;
        // In reach with a technique: ask the target (and hold an attack slot).
        if (!HasSlot(world, i, t, tc, now) || !HasInitiative(world, i, ref s, t, cold, tc, now)) return;
        var tech = techniques[pick];
        s.AttackSeq++;
        s.AttackTech = pick;
        s.LastTech = pick;
        s.AttackStart = now;
        s.AttackTicks = Ticks(tech, PlayRate(cold, tech, attack: true), dt, cold);
        s.AttackEnd = now + Math.Max(1, (int)MathF.Ceiling(tech.EndProgress * s.AttackTicks));
        s.NextBlow = 1;
        s.LastOutcome = BlowOutcome.Missed;
        s.AttackReach = TechniqueChooser.ReachOf(tech, length);
        s.AttackSkill = Eff(cold, fighter.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack) + (fighter.Unarmed ? fighter.UnarmedBonus : fighter.AttackBonus + (fighter.Weapon?.Stats.AttackMod ?? 0));
    }

    /// <summary>
    /// Whether <paramref name="i"/> may swing at <paramref name="t"/> now, asking the target first (<b>Verified</b>, FUN_14060c670 / FUN_140666a30, docs/game/combat.md
    /// "Pacing"): yes when the target lost the initiative to it; not while the target is swinging at it (it defends instead); yes while the target is staggered,
    /// defending or not fighting it; otherwise an initiative roll on both attack skills (the skill roll with <c>attack chance factor</c>, 50/50 at equal skill),
    /// drawn from the pair and tick so both sides agree. The loser yields: the winner may swing at it at once (the original sets the winner to attack).
    /// </summary>
    bool HasInitiative(World world, int i, ref CombatSlot s, int t, CharacterCold cold, CharacterCold tc, int now)
    {
        ref readonly var target = ref read[t];
        var prev = world.Characters.Previous;
        bool atMe = target.TargetSlot == i && target.TargetGeneration == prev[i].Generation;
        if (target.YieldTo == i && now < target.YieldUntil) return true;
        if (target.AttackTech >= 0 && atMe) return false;   // it is swinging at me: I defend (FUN_140666a30)
        // One that would not swing back (not fighting, the Block or Passive order) cannot take the initiative.
        if (now < target.StunUntil || target.ReactTech >= 0 || target.Down || target.TargetSlot < 0 || tc.Fighter is not { CanAttack: true }
            || Orders.Has(tc, CharacterOrders.Block | CharacterOrders.Passive)) return true;
        if (s.YieldTo == t && now < s.YieldUntil) return false;
        // The target is free (deciding or in its pause): one draw for the pair and tick, the lower slot's view.
        int lo = Math.Min(i, t), hi = Math.Max(i, t);
        float skillI = Eff(cold, cold.Fighter!.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float skillT = Eff(tc, tc.Fighter.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float lowSkill = lo == i ? skillI : skillT, highSkill = lo == i ? skillT : skillI;
        float a = 1 + combat.AttackChanceFactor * MathF.Max(0, highSkill - lowSkill), b = 1 + combat.AttackChanceFactor * MathF.Max(0, lowSkill - highSkill);
        float k = b / a, lowWins = k >= 1 ? 1 - 1 / (2 * k) : k / 2;
        ulong pair = ((ulong)(uint)lo << 32) | (uint)hi;
        float roll = CombatRolls.Float(world.Seed, pair, CombatRoll.Initiative, (ulong)(uint)now);
        if ((roll < lowWins) == (i == lo)) return true;
        s.YieldTo = t;
        s.YieldUntil = now + Math.Max(1, (int)MathF.Ceiling(CombatTuning.YieldSeconds / world.TickSeconds));
        return false;
    }

    struct IncomingScan
    {
        public CombatSlot[] Read;
        public int Self, SelfGeneration, Now, Yielder;
        public float Most;
    }

    /// <summary>
    /// How far through the most advanced attack aimed at <paramref name="i"/> is (0 to 1, from the last tick's state; -1 when nobody is swinging at it), and the
    /// lowest slot that lost the initiative roll to it and still yields (<paramref name="yielder"/>, -1: none).
    /// </summary>
    float Incoming(World world, int i, int now, out int yielder)
    {
        var prev = world.Characters.Previous;
        var scan = new IncomingScan { Read = read, Self = i, SelfGeneration = prev[i].Generation, Now = now, Most = -1, Yielder = -1 };
        world.Grid.Query(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius, ref scan, static (int other, ref IncomingScan st) =>
        {
            if (other == st.Self) return;
            ref readonly var a = ref st.Read[other];
            if (a.YieldTo == st.Self && st.Now < a.YieldUntil && !a.Down && (st.Yielder < 0 || other < st.Yielder)) st.Yielder = other;
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetSlot != st.Self || a.TargetGeneration != st.SelfGeneration || a.AttackTicks <= 0) return;
            st.Most = MathF.Max(st.Most, Math.Clamp((st.Now - a.AttackStart) / (float)a.AttackTicks, 0, 1));
        });
        yielder = scan.Yielder;
        return scan.Most;
    }

    /// <summary>Any leg part is down (flesh - stun + rig &lt; 0): the original's two leg parts at medical +0x4d8 / +0x4e0 in the kick factor (<c>FUN_140886030</c>).</summary>
    static bool LegDown(MedicalState medical)
    {
        foreach (var p in medical.Parts) if (p.IsLeg && p.IsDown) return true;
        return false;
    }

    struct SlotScan
    {
        public CombatSlot[] Read;
        public CharacterTable Table;
        public CharacterCold TargetCold;
        public int Self, Target, TargetGeneration, Now, Holders, Ahead;
        public Vector3 TargetPosition;
    }

    /// <summary>
    /// Whether <paramref name="i"/> gets one of its target's attack slots for this swing (docs/game/combat.md "Attack slots and circling", <c>FUN_140666310</c> / <c>FUN_1406661d0</c>):
    /// <see cref="AttackSlots"/> plus the target race's <c>extra attack slots</c> (1 to 5), only 1 while the target swings itself. A slot is held from the grant to the swing's last
    /// blow (<c>FUN_14060a590</c> gives it back), so the holders are the fighters at the target whose swing has a blow to come. The original grants in its update order; here
    /// the other fighters that may ask on the same tick (engaged, free, within their reach) and have a lower slot go first (engine choice).
    /// </summary>
    bool HasSlot(World world, int i, int t, CharacterCold tc, int now)
    {
        int slots = read[t].AttackTech >= 0 ? 1 : Math.Clamp(AttackSlots + (tc.Race?.ExtraAttackSlots ?? 0), 1, MaxAttackSlots);
        var prev = world.Characters.Previous;
        var scan = new SlotScan { Read = read, Table = world.Characters, TargetCold = tc, Self = i, Target = t, TargetGeneration = prev[t].Generation, Now = now, TargetPosition = prev[t].Position };
        world.Grid.Query(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius, ref scan, static (int other, ref SlotScan st) =>
        {
            if (other == st.Self) return;
            ref readonly var o = ref st.Read[other];
            if (o.TargetSlot != st.Target || o.TargetGeneration != st.TargetGeneration || o.Down || o.Ranged) return;
            if (o.AttackTech >= 0)
            {
                if (o.NextBlow > 0) st.Holders++;
                return;
            }
            if (other > st.Self || !o.Engaged || o.ReactTech >= 0 || st.Now < o.StunUntil || st.Now < o.ReadyTick && !(st.Read[st.Target].YieldTo == other && st.Now < st.Read[st.Target].YieldUntil)) return;
            if (o.YieldTo == st.Target && st.Now < o.YieldUntil) return;
            var c = st.Table.Cold(other);
            if (c is null || o.Reach <= 0 || CombatTuning.Gap(st.Table.Previous[other].Position, st.TargetPosition, c, st.TargetCold) > o.Reach) return;
            st.Ahead++;
        });
        return scan.Holders + scan.Ahead < slots;
    }

    /// <summary>The most attack slots a target can have (the slot count is clamped to 1 to 5, <c>FUN_1406661d0</c>).</summary>
    public const int MaxAttackSlots = 5;
    /// <summary>The most fighters a target lets close in at once (<c>FUN_140666e20</c> adds an asker while it has at most four).</summary>
    public const int MaxEngaged = 5;

    struct JoinScan
    {
        public CombatSlot[] Read;
        public int Self, Target, TargetGeneration, Count;
    }

    /// <summary>
    /// Whether <paramref name="i"/> may join the fighters its target lets close in (<see cref="MaxEngaged"/>, <c>FUN_140666e20</c>): it joins while fewer are in. The ones that are
    /// not in yet ask on every tick; those with a lower slot ask first (engine choice for the original's update order).
    /// </summary>
    bool MayJoin(World world, int i, int t)
    {
        var prev = world.Characters.Previous;
        var scan = new JoinScan { Read = read, Self = i, Target = t, TargetGeneration = prev[t].Generation };
        world.Grid.Query(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius, ref scan, static (int other, ref JoinScan st) =>
        {
            if (other == st.Self) return;
            ref readonly var o = ref st.Read[other];
            if (o.TargetSlot != st.Target || o.TargetGeneration != st.TargetGeneration || o.Down || o.Ranged) return;
            if (o.Engaged || other < st.Self) st.Count++;
        });
        return scan.Count < MaxEngaged;
    }

    struct CountScan
    {
        public CombatSlot[] Read;
        public int Target, TargetGeneration, Count;
    }

    /// <summary>The melee fighters (engaged or circling) at <paramref name="t"/>, as of the last tick: the original's engaged count plus the askers it turned away (<c>FUN_140666e20</c>).</summary>
    public int FightersAt(World world, int t)
    {
        var prev = world.Characters.Previous;
        var scan = new CountScan { Read = read, Target = t, TargetGeneration = prev[t].Generation };
        world.Grid.Query(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius, ref scan, static (int other, ref CountScan st) =>
        {
            ref readonly var o = ref st.Read[other];
            if (o.TargetSlot == st.Target && o.TargetGeneration == st.TargetGeneration && !o.Down && !o.Ranged) st.Count++;
        });
        return scan.Count;
    }

    struct Scan
    {
        public CombatSlot[] Read;
        public CombatTechnique[] Techniques;
        public int Self, SelfGeneration, Now;
        public CombatSlot Me;
        public int Attacker, Blow, Strike;
    }

    void React(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var scan = new Scan { Read = read, Techniques = techniques, Self = i, SelfGeneration = prev[i].Generation, Now = now, Me = s, Attacker = -1, Strike = int.MaxValue };
        world.Grid.Query(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius, ref scan, static (int other, ref Scan st) =>
        {
            if (other == st.Self) return;
            ref readonly var a = ref st.Read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetSlot != st.Self || a.TargetGeneration != st.SelfGeneration) return;
            var tech = st.Techniques[a.AttackTech];
            int strike = a.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(a.NextBlow) * a.AttackTicks);
            if (strike <= st.Now) return;
            if (st.Me.ReactedSlot == other && st.Me.ReactedSeq == a.AttackSeq && st.Me.ReactedBlow == a.NextBlow) return;
            if (strike < st.Strike) { st.Strike = strike; st.Attacker = other; st.Blow = a.NextBlow; }
        });
        if (scan.Attacker < 0) return;

        var attack = read[scan.Attacker];
        var atkTech = techniques[attack.AttackTech];
        int direction = atkTech.DirectionOf(scan.Blow);
        s.ReactedSlot = scan.Attacker;
        s.ReactedSeq = attack.AttackSeq;
        s.ReactedBlow = scan.Blow;

        var fighter = cold.Fighter!;
        float meleeDefence = Eff(cold, StatsEnumerated.MeleeDefence);
        ulong counter = CombatRolls.BlowCounter(scan.Attacker, attack.AttackSeq, scan.Blow, 0);
        float chanceRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Block, counter);
        float pickRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Reaction, counter);
        float block, dodge = 0;
        if (fighter.CanBlock)
        {
            float d = DefenceFormulas.Defence(meleeDefence, fighter.Weapon!.Stats.DefenceMod, true, fighter.DefenceBonus, Guarding(cold));
            block = DefenceFormulas.BlockChance(d, attack.AttackSkill, combat);
        }
        else
        {
            block = 0;
            float effective = DefenceFormulas.EffectiveDefence(Eff(cold, StatsEnumerated.MartialArts), fighter.EncumbranceFactor, Speed.LegFactor(cold.Medical!), fighter.DefenceBonus, Guarding(cold));
            dodge = DefenceFormulas.BlockChance(effective, attack.AttackSkill, combat, 0, 95);
        }
        int pick = TechniqueChooser.ChooseReaction(techniques, fighter.TechniqueCategory, cold.Medical!.Crawling, fighter.CanBlock, direction, block, dodge, chanceRoll, pickRoll);
        if (pick < 0)
        {
            // No defence (a failed dodge roll, or nothing fits): none is tried again for a while (the original's +0x14c).
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.NoDefenceSeconds / dt);
            return;
        }
        var reaction = techniques[pick];
        int ticks = Ticks(reaction, PlayRate(cold, reaction, attack: false), dt, cold);
        // The reaction is timed so that it is at its "blocked frame" when the blow arrives; with less lead time it starts at once and is less far along,
        // With LateDefences (Meitou balance choice, docs/simulation.md "Combat") never too little for the roll it passed to count: a late one joins its clip partway.
        int strike = scan.Strike;
        int start = Math.Max(now, strike - (int)MathF.Round(reaction.StrikeProgress(1) * ticks));
        if (LateDefences)
        {
            float least = reaction.IsBlock ? HitOutcomes.BlockProgress : HitOutcomes.DodgeFrom;
            start = Math.Min(start, strike - (int)MathF.Ceiling(least * ticks) - 1);
        }
        s.ReactTech = pick;
        s.ReactStart = start;
        s.ReactTicks = ticks;
        s.LastTech = -1;   // the original's current technique (+0x150) is now a defence, which the next attack choice does not count as its last

    }
}
