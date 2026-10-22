using Meitou.Simulation;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.DuelFixture;

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
}
