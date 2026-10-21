using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The orders panel's toggles: block, passive, hold, sneak and the move speed (docs/simulation.md "Orders").</summary>
public class OrdersTests
{
    /// <summary>A player character at the origin and a bandit <paramref name="distance"/> away that attacks it at tick 0; <paramref name="orders"/> set on the player first.</summary>
    static (SimWorld World, CombatSystem Combat, CharacterId Player, CharacterId Bandit) Attacked(CharacterOrders orders, float distance = 30)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk,
            [new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat)]);
        CharacterId Make(string name, Vector3 at, bool player)
        {
            var cold = new Combatant { Name = name }.MakeCold();
            cold.IsPlayer = player;
            return world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Mode = SpeedMode.Free }, cold, 0);
        }
        var player = Make("player", new Vector3(1000, 150, 1000), true);
        var bandit = Make("bandit", new Vector3(1000 + distance, 150, 1000), false);
        if (orders != CharacterOrders.None) world.Commands.Enqueue(new OrdersCommand([player], orders, true) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([bandit], player) { Tick = 0 });
        return (world, combat, player, bandit);
    }

    [Fact]
    public void Block_fights_back_without_attacking_and_passive_does_not_fight_back()
    {
        var (open, openCombat, p0, _) = Attacked(CharacterOrders.None);
        var (block, blockCombat, p1, _) = Attacked(CharacterOrders.Block);
        var (passive, passiveCombat, p2, _) = Attacked(CharacterOrders.Passive);
        using (open) using (block) using (passive)
        {
            open.RunTicks(600); block.RunTicks(600); passive.RunTicks(600);
            Assert.True(openCombat.StateOf(p0).Thrown > 0, "without orders the attacked character swings back");
            Assert.True(blockCombat.StateOf(p1).TargetSlot >= 0 || block.Characters.Cold(p1.Slot)!.Medical!.Incapacitated, "a blocker still faces its attacker");
            Assert.Equal(0, blockCombat.StateOf(p1).Thrown);
            Assert.NotEqual((byte)CharacterTask.Attack, passive.Characters.Next[p2.Slot].Task);
            Assert.Equal(0, passiveCombat.StateOf(p2).Thrown);
        }
    }

    [Fact]
    public void Orders_go_to_the_originals_state_keys_and_back()
    {
        var cold = new CharacterCold { Orders = CharacterOrders.Block | CharacterOrders.Sneak | CharacterOrders.Hold, RunSpeed = SpeedMode.Run };
        var record = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_CHARACTER };
        Orders.Write(record, cold);
        Assert.True(record.Bools["defensive mode"]);
        Assert.True(record.Bools["stealth"]);
        Assert.True(record.Bools["passive mode"]);
        Assert.False(record.Bools["non combat mode"]);
        Assert.Equal(1, record.Ints["speed mode"]);
        // The jobs switch is not here: it is the AI record's (PermaJobSave).
        Assert.False(record.Bools.ContainsKey(Orders.JobsKey));
        var back = new CharacterCold { Orders = CharacterOrders.None };
        Orders.Read(record, back);
        Assert.Equal(cold.Orders, back.Orders);
        Assert.Equal(SpeedMode.Run, back.RunSpeed);
    }
}
