using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Simulation;

/// <summary>The order queue (docs/simulation.md "Order queue"; the original's <c>checkOrders</c> and <c>Character::addOrder</c>, docs/game/ai-tasks.md "Precedence").</summary>
public class OrderQueueTests
{
    static TaskType[] Rows(TaskStartTests.Rig rig, CharacterId id) => [.. rig.Cold(id).OrderQueue.Select(e => e.Row)];

    /// <summary>
    /// <c>addOrder</c>: an order without Shift clears the queue first, one with Shift waits behind the order in hand (a move, then a non-move order). Catches Shift queueing only moves, and an
    /// order that starts without replacing the queue.
    /// </summary>
    [Fact]
    public void An_order_without_Shift_clears_the_queue_and_one_with_Shift_waits_behind()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        long t = rig.World.Tick;
        rig.Say(new MoveOrder([f], new Vector3(900, 0, 1900)) { Tick = t });
        rig.Say(new MoveOrder([f], new Vector3(900, 0, 2100)) { Tick = t, Queued = true });
        rig.Say(new RestOrder([f]) { Tick = t, Queued = true });
        rig.World.RunTick();
        Assert.Equal([TaskType.MOVE_CUS_ORDERED, TaskType.MOVE_CUS_ORDERED, TaskType.REST], Rows(rig, f));
        Assert.Equal([true, false, false], rig.Cold(f).OrderQueue.Select(e => e.Started));
        Assert.Equal(CharacterTask.GoTo, rig.Task(f));

        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();
        Assert.Equal([TaskType.FIRST_AID_ORDER], Rows(rig, f));
        Assert.True(rig.Cold(f).OrderQueue[0].Started);
        Assert.Equal(CharacterTask.Treat, rig.Task(f));
    }

    /// <summary>
    /// <c>checkOrders</c> pops the front once its task is done and the next order starts: a move, then a Shift-queued Rest. Catches a queue that only chains moves, or one whose front stays
    /// after its task ended.
    /// </summary>
    [Fact]
    public void A_done_order_leaves_the_queue_and_the_next_one_starts()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        rig.Say(new MoveOrder([f], new Vector3(1000, 0, 1960)) { Tick = rig.World.Tick });
        rig.Say(new RestOrder([f]) { Tick = rig.World.Tick, Queued = true });
        for (int i = 0; i < 400 && rig.Task(f) != CharacterTask.Rest; i++) rig.World.RunTick();
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Assert.Equal([TaskType.REST], Rows(rig, f));
        Assert.True(rig.Cold(f).OrderQueue[0].Started);
    }

    /// <summary>
    /// A player order that fails to plan stays queued and the chooser goes on (docs/game/ai-tasks.md "Precedence"): a First Aid order with no medkit waits while the character's job (operating
    /// the well) runs, and starts once a kit is back. Catches a refused order being dropped, or one that blocks the character's jobs.
    /// </summary>
    [Fact]
    public void An_order_that_cannot_run_stays_queued_while_a_job_runs_and_starts_once_it_can()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        var inventory = rig.Cold(f).Inventory!;
        var kit = inventory.All().First(i => i.ItemFunction == ItemInfo.FirstAidKitFunction);
        Assert.True(inventory.Remove(kit));
        rig.Say(new StopCommand([f]) { Tick = rig.World.Tick });
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();
        Assert.Equal([TaskType.FIRST_AID_ORDER], Rows(rig, f));
        Assert.False(rig.Cold(f).OrderQueue[0].Started);

        rig.World.RunTicks(ProductionSystem.JobCheckTicks * 2);
        Assert.Equal(CharacterTask.Operate, rig.Task(f));
        Assert.Equal([TaskType.FIRST_AID_ORDER], Rows(rig, f));

        inventory.Add(kit);
        rig.World.RunTicks(ProductionSystem.JobCheckTicks + 1);
        Assert.Equal(CharacterTask.Treat, rig.Task(f));
        Assert.True(rig.Cold(f).OrderQueue[0].Started);
    }

    /// <summary>
    /// Every order goes through the one order path while the game is paused too: each order type sets its act-out between ticks. Catches an order the paused interface drops (it applied a
    /// hand-kept list of commands, which left out pick up, loot, operate, build, beds and others).
    /// </summary>
    [Theory, MemberData(nameof(TaskStartTests.Orders), MemberType = typeof(TaskStartTests))]
    public void Every_order_applies_while_paused(string name)
    {
        using var rig = new TaskStartTests.Rig(carrying: name is "put down" or "put in bed" or "put in cage");
        rig.World.RunTicks(3);
        var (order, task) = TaskStartTests.Order(rig, name, rig.World.Tick);
        rig.Say(order);
        rig.World.ApplyPausedInterfaceCommands();
        Assert.Equal(task, rig.Task(rig.Fighter));
    }
}
