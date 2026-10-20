using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The stealth knock-out (<see cref="CharacterTask.StealthKo"/>, the original's <c>Task_StealthKO</c>; docs/game/combat.md "Stealth knock-out"): a sneaking character creeps to a point
/// behind its target, swings the <c>knockout</c> clip at x1.2 and at half of it rolls 0-100 against <see cref="StealthKoChance"/>. A hit knocks the target out for
/// <see cref="MedicalState.PutDownKoMinutes"/> with the attacker's assassination / 100; a miss turns the target on the attacker. Either trains assassination (a miss three times as much).
/// The walk breaks off when the target has spotted the attacker (its <see cref="AggroSystem"/> spot meter is full) or is fighting.
/// </summary>
public sealed partial class CarrySystem
{
    /// <summary>The attacker's clip and the rate it plays at (<c>Task_StealthKO</c>: <c>knockout</c> at 1.2, <b>Verified</b> literals).</summary>
    public const string KnockoutClip = "knockout";
    public const float KnockoutPlaySpeed = 1.2f;
    /// <summary>The fractions of the clip at which the roll is made and the task ends (0.5 and 0.85, <b>Verified</b> literals).</summary>
    public const float KnockoutStrikeAt = 0.5f, KnockoutDoneAt = 0.85f;
    /// <summary>The swing starts within this distance of the point behind the target (14, <b>Verified</b> literal).</summary>
    public const float KnockoutStartRange = 14;
    /// <summary>How far behind the target that point is (<b>engine choice</b>: the original subtracts a vector of the target's, +0xd0 of its body object, <b>Unknown</b>).</summary>
    public const float KnockoutBehind = 8;

    /// <summary>The length of the <c>knockout</c> clip in seconds (1 when the clip lengths are not known); the swing lasts this / <see cref="KnockoutPlaySpeed"/>.</summary>
    public float KnockoutSeconds { get; set; } = 1;
    /// <summary>The senses the walk checks (a target that spotted the attacker turns round); null: never spotted.</summary>
    public AggroSystem? Aggro { get; set; }

    readonly List<StealthKoEntry> knockouts = [];
    XpService? xp;

    /// <summary>The swings rolled since the last <see cref="TakeKnockouts"/> (the host shows them as "*Thunk*" or "Failed!").</summary>
    public IReadOnlyList<StealthKoEntry> Knockouts => knockouts;

    public StealthKoEntry[] TakeKnockouts()
    {
        StealthKoEntry[] all = [.. knockouts];
        knockouts.Clear();
        return all;
    }

    /// <summary>A character that may be sent on a stealth knock-out: sneaking, awake, not fighting, carrying or carried.</summary>
    public static bool CanKnockOut(CharacterCold cold) =>
        cold is { Medical.Incapacitated: false, Race: not null, Stats: not null, InCombat: false } && (cold.Orders & CharacterOrders.Sneak) != 0 && cold.Carrying.IsNone && cold.CarriedBy.IsNone;

    /// <summary>A character that may be knocked out from behind: awake, with a body, not carried.</summary>
    public static bool CanBeKnockedOut(CharacterCold cold) => cold is { Medical.Incapacitated: false, Race: not null } && cold.CarriedBy.IsNone;

    /// <summary>
    /// The chance in per cent that a stealth knock-out lands (<c>FUN_1408852f0</c>, <b>Verified</b> literals; docs/game/combat.md "Stealth knock-out"). X = the attacker's assassination minus
    /// the target's best of: assassination, half its thievery, half its stealth term x 100, half its melee defence, its toughness (each skill times its injury multiplier); then
    /// 100 x (X + K) / 2K with K = 30 for X >= 0 and 15 below, unclamped (50 at X = 0, 100 at +30, 0 at -15). 0 when the target has spotted the attacker.
    /// </summary>
    public static float StealthKoChance(CharacterCold attacker, CharacterCold target, bool spotted = false)
    {
        if (spotted || attacker.Stats is not { } a || target.Stats is not { } t) return 0;
        float Skill(CharacterCold c, CharacterStats s, StatsEnumerated stat) => s[stat] * (c.Medical?.StatMultiplier(stat) ?? 1);
        float defence = Skill(target, t, StatsEnumerated.Assassination);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Thieving) * 0.5f);
        defence = MathF.Max(defence, Orders.Stealth(target) * 100 * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.MeleeDefence) * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Toughness));
        float x = Skill(attacker, a, StatsEnumerated.Assassination) - defence;
        float k = x < 0 ? 15 : 30;
        return (x + k) / (2 * k) * 100;
    }

    void BeginKnockOut(World world, StealthKoOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int target) || !CanBeKnockedOut(table.Cold(target)!)) return;
        // The first that can go, in the order given (the selection's order), so the chance the interface showed is the one rolled.
        foreach (var id in world.Actors(order, order.Attackers))
            if (table.TryResolveNext(id, out int attacker) && attacker != target && CanKnockOut(table.Cold(attacker)!))
            {
                Begin(table, attacker, order.Target, CharacterTask.StealthKo, CharacterTask.Idle);
                return;
            }
    }

    /// <summary>The point behind the target the attacker creeps to.</summary>
    static Vector2 Behind(in CharacterHot target) =>
        new(target.Position.X - MathF.Sin(target.Yaw) * KnockoutBehind, target.Position.Z - MathF.Cos(target.Yaw) * KnockoutBehind);

    /// <summary>The walk of a stealth knock-out (Schedule, serial): to the point behind the target, re-aimed as it moves; within reach the swing starts.</summary>
    void ScheduleKnockOuts(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.StealthKo) continue;
            var cold = table.Cold(i)!;
            if (cold.LiftTime >= 0) continue;   // swinging: SlowWorld has it
            if (cold.Medical is not { Incapacitated: false } || cold.InCombat || !table.TryResolveNext(cold.CarryTarget, out int t) || table.Cold(t) is not { } target
                || !CanBeKnockedOut(target) || target.InCombat || Aggro?.Spotted(t, table.IdOf(i)) == true)
            {
                End(table, i);
                continue;
            }
            var goal = Behind(next[t]);
            if (Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal) < KnockoutStartRange)
            {
                MovementSystem.Stop(table, i);
                cold.PathRequest = 0;
                n.Velocity = Vector3.Zero;
                cold.LiftTime = 0;
                Face(ref n, next[t]);
                continue;
            }
            n.TaskTime -= dt;
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    static void Face(ref CharacterHot n, in CharacterHot target)
    {
        float dx = target.Position.X - n.Position.X, dz = target.Position.Z - n.Position.Z;
        if (dx * dx + dz * dz > 1e-4f) n.Yaw = MathF.Atan2(dx, dz);
    }

    /// <summary>The swing (SlowWorld, serial): the roll at <see cref="KnockoutStrikeAt"/> of the clip, the end at <see cref="KnockoutDoneAt"/>.</summary>
    void Swing(World world, CharacterTable table, int i, float dt)
    {
        var cold = table.Cold(i)!;
        if (cold.LiftTime < 0) return;   // still creeping
        float length = KnockoutSeconds / KnockoutPlaySpeed;
        float before = cold.LiftTime;
        cold.LiftTime += dt;
        if (cold.Medical is not { Incapacitated: false } || !table.TryResolveNext(cold.CarryTarget, out int t) || table.Cold(t) is not { } target)
        {
            End(table, i);
            return;
        }
        Face(ref table.Next[i], table.Next[t]);
        if (before < KnockoutStrikeAt * length && cold.LiftTime >= KnockoutStrikeAt * length)
        {
            if (!CanBeKnockedOut(target) || Gap(table.Next[i], table.Next[t]) > KnockoutStartRange + KnockoutBehind)
            {
                End(table, i);   // it went down or walked off mid-swing
                return;
            }
            Strike(world, table, i, t);
        }
        if (cold.LiftTime >= KnockoutDoneAt * length) End(table, i);
    }

    void Strike(World world, CharacterTable table, int i, int t)
    {
        var cold = table.Cold(i)!;
        var target = table.Cold(t)!;
        var attackerId = table.IdOf(i);
        var targetId = table.IdOf(t);
        float chance = StealthKoChance(cold, target, Aggro?.Spotted(t, attackerId) == true);
        float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(attackerId), RngPurpose.StealthKo, (ulong)world.Tick)) * 100;
        bool success = chance > roll;
        // XP: assassination through the generic entry with dt 1, 100 on a hit and 300 on a miss, halved on a sleeping target (Verified literals; the original's 0.07 case and
        // its "(No XP)" condition are Unknown).
        if (cold.Stats is { } stats && cold.Race is { } race)
        {
            float f = table.Next[t].Task == (byte)CharacterTask.Rest || target.Ai is { Behaviour: Ai.AiBehaviour.Sleep } ? 0.5f : 1;
            (xp ??= new XpService(Constants, bodyOptions)).Continuous(stats, race, StatsEnumerated.Assassination, 1, f * (success ? 100 : 300));
        }
        if (success)
        {
            if (target.Medical is { } medical && target.Race is { } targetRace)
            {
                var ctx = new MedicalContext(Constants, bodyOptions, targetRace) { Toughness = target.Stats?.Toughness ?? 0, Strength = target.Stats?.Strength ?? 50, HasStats = target.Stats is not null };
                float assassination = (cold.Stats?[StatsEnumerated.Assassination] ?? 0) * (cold.Medical?.StatMultiplier(StatsEnumerated.Assassination) ?? 1);
                medical.KnockOut(medical.PutDownKoMinutes(ctx, assassination * 0.01f));
                MovementSystem.Stop(table, t);
                table.Next[t].Velocity = Vector3.Zero;
            }
        }
        else world.Defer(new Combat.AttackOrder([targetId], attackerId) { Tick = world.Tick + 1 });
        // Hit or miss, the swing is an assault on the victim's faction if someone sees it (engine choice: the original's crime call for this task is not traced).
        Crimes?.Assault(world, i, t);
        knockouts.Add(new StealthKoEntry(world.Tick, attackerId, targetId, chance, success));
    }
}
