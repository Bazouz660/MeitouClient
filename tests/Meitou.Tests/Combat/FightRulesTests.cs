using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.DuelFixture;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>Rules of the fight loop beyond the formulas: reach, staggering, orders, the animation stance, and the downed.</summary>
public class FightRulesTests
{
    [Fact]
    public void A_knocked_out_fighter_cannot_react_and_the_fight_ends()
    {
        using var world = NewWorld(6, 1, out var combat);
        var (a, b) = Pair(world, new Combatant { Attack = 100, WeaponSkill = 100, Dexterity = 100, Strength = 100, Quality = 1 }, new Combatant { Defence = 0, Toughness = 1, Attack = 1, WeaponSkill = 1 });
        RunToEnd(world, a, b);
        Assert.True(Down(world, b));
        Assert.False(Down(world, a));
        int blows = combat.Log.Count;
        world.RunTicks(120);
        Assert.Equal(blows, combat.Log.Count);                       // no more blows at the downed
        Assert.False(combat.StateOf(a).Fighting);
        Assert.False(combat.StateOf(b).Fighting);
        // An order against the downed does nothing.
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = world.Tick });
        world.RunTicks(60);
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat);
    }

    /// <summary>
    /// The stumble break-out timer (<c>FUN_1406085e0</c>, +0x124, <b>Verified</b>): 1.2 s from the stumble's start it is due and then rolls on the first tick a swing is in the window
    /// (a failed roll starts it again), not only on a tick that is an exact multiple of 1.2 s after the start.
    /// </summary>
    [Fact]
    public void A_stumbling_fighter_breaks_out_on_the_first_tick_a_swing_is_in_the_window_once_the_timer_is_due()
    {
        var techniques = CombatFixtures.KatanaTechniques().Append(CombatFixtures.Dodge("fly") with { IsStumbleDodge = true }).ToArray();
        int every = 0, offGrid = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            using var world = NewWorld(seed, 1, out var combat, new CombatOptions { RecordLog = true, TickMedical = false }, techniques);
            combat.StumbleChooser = (_, _, _, _, _) => (0, 6f);
            var (a, b) = Pair(world, new Combatant { Attack = 100, WeaponSkill = 100, Dexterity = 100, Strength = 100, Quality = 1 },
                new Combatant { Defence = 0, Attack = 1, Toughness = 100, Martial = 100, Armour = [CombatFixtures.BodyPlate, CombatFixtures.Helmet] });
            every = (int)MathF.Round(CombatTuning.BreakOutSeconds / world.TickSeconds);
            var before = combat.StateOf(b);
            for (int k = 0; k < 1500 && !Down(world, b); k++)
            {
                world.RunTick();
                var now = combat.StateOf(b);
                if (before.StumbleClip >= 0 && now.StumbleClip < 0 && now.StumbleStart == before.StumbleStart && (world.Tick - 1 - before.StumbleStart) % every != 0) offGrid++;
                before = now;
            }
        }
        Assert.True(offGrid > 0, "a break-out roll that is due waits for a swing in the window instead of being lost");
    }

    /// <summary>A, ordered onto B (its task target), with C attacking it from the other side; each of another faction, standing where it is put.</summary>
    static (CharacterId A, CharacterId B, CharacterId C) Three(SimWorld world, float far)
    {
        CharacterId Put(int faction, float x)
        {
            var cold = new Combatant().MakeCold();
            cold.Faction = faction;
            return world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000 + x, 150, 1000), Health = 100 }, cold, 0);
        }
        CharacterId a = Put(0, 0), b = Put(1, far), c = Put(2, -14);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = world.Tick });
        world.Commands.Enqueue(new AttackOrder([c], a) { Tick = world.Tick });
        return (a, b, c);
    }

    /// <summary>
    /// The fight's own target down (docs/game/combat.md "Leaving a fight", CombatClassAI update FUN_14060d6a0): the fight ends at once, though another still attacks the fighter.
    /// </summary>
    [Fact]
    public void A_fighter_whose_task_target_drops_ends_its_fight_at_once()
    {
        using var world = NewWorld(6, 1, out var combat, new CombatOptions { TickMedical = false });
        var (a, b, c) = Three(world, 14);
        world.RunTicks(10);
        Assert.Equal(b.Slot, combat.StateOf(a).TargetSlot);
        world.Characters.Cold(b.Slot)!.Medical!.KnockOut(600);
        world.RunTicks(1);
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat, "the task target is down: the fight ends");
        Assert.Equal(a.Slot, combat.StateOf(c).TargetSlot);
    }

    /// <summary>
    /// Another target down (docs/game/combat.md "Leaving a fight", Task_MeleeAttack update FUN_14034ca40): the fighter turned on its attacker C fights on without a target while its task
    /// target B, standing and of no hostile faction, is in its senses, and leaves the fight once B's entry has decayed (30 sense units, 60 s, after B was last sensed).
    /// </summary>
    [Fact]
    public void A_fighter_that_loses_another_target_fights_on_without_one_until_nothing_is_left()
    {
        using var world = NewWorld(6, 1, out var combat, new CombatOptions { TickMedical = false });
        var (a, b, c) = Three(world, 300);
        for (int k = 0; k < 300 && combat.StateOf(a).TargetSlot != c.Slot; k++) world.RunTick();
        Assert.Equal(c.Slot, combat.StateOf(a).TargetSlot);   // its attacker, at its first choice
        world.Characters.Cold(c.Slot)!.Medical!.KnockOut(600);
        world.Characters.Place(b.Slot, new Vector3(1000, 150, 9000), 0);   // out of sight and hearing from now
        world.RunTicks((int)(50 / world.TickSeconds));
        Assert.True(world.Characters.Cold(a.Slot)!.InCombat, "B still in its senses: the fight goes on");
        Assert.True(combat.StateOf(a).TargetSlot < 0);
        world.RunTicks((int)(15 / world.TickSeconds));
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat, "B's entry decayed: nothing is left");
    }
}
