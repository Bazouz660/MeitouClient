using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The decision step's precedence for the player's characters (docs/simulation.md "Decision step"; the original's chooser, docs/game/ai-tasks.md "Precedence"): one scenario per source
/// pair, on the task start rig with self-defence and feeding.
/// </summary>
public class DecisionTests
{
    /// <summary>The rig after its opening fight is stopped: the fighter stands idle, no longer sneaking, with the decision trace on.</summary>
    static TaskStartTests.Rig Quiet()
    {
        var rig = new TaskStartTests.Rig(carrying: false, decide: true);
        rig.World.RunTicks(3);
        rig.Say(new StopCommand([rig.Fighter]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        rig.Cold(rig.Fighter).Orders &= ~CharacterOrders.Sneak;
        rig.World.Decisions.Tracing = true;
        return rig;
    }

    /// <summary>An NPC of the enemy's faction right beside the fighter, attacking it.</summary>
    static CharacterId Attacker(TaskStartTests.Rig rig)
    {
        var at = rig.World.Characters.Next[rig.Fighter.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Say(new Meitou.Simulation.Combat.AttackOrder([foe], rig.Fighter) { Tick = rig.World.Tick });
        return foe;
    }

    static void RunUntil(TaskStartTests.Rig rig, Func<bool> done, int max, string what)
    {
        for (int t = 0; t < max && !done(); t++) rig.World.RunTick();
        Assert.True(done(), $"{what} within {max} ticks; last decision: {(rig.Cold(rig.Fighter).Decision is { } d ? $"{d.Winner} | {string.Join(" | ", d.Lines)}" : "none")}");
    }

    static bool Hurt(CharacterCold cold) => cold.Medical!.Parts.Any(p => p.Flesh < p.MaxHp);

    /// <summary>
    /// Orders beat Self preservation: a resting character attacked from beside it keeps resting; the blows that land stagger it, and the step asked again picks the order. Catches a
    /// character on an order that turns to fight its attacker.
    /// </summary>
    [Fact]
    public void An_order_beats_self_defence_and_a_landed_blow_asks_again()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new RestOrder([f]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Attacker(rig);
        RunUntil(rig, () => Hurt(rig.Cold(f)), 600, "a blow landing");
        long hit = rig.World.Tick;
        RunUntil(rig, () => rig.Cold(f).Decision is { } d && d.Tick >= hit - 1, 60, "a decision after the blow");
        Assert.Equal("Orders", rig.Cold(f).Decision!.Winner);
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Assert.False(rig.Cold(f).InCombat);
    }

    /// <summary>Self preservation beats a job: a character operating the well on its job fights the attacker at its side. Catches a job's task that keeps its character from defending.</summary>
    [Fact]
    public void Self_defence_beats_a_jobs_task()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 600, "operating the well");
        var foe = Attacker(rig);
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Attack, 120, "fighting back");
        Assert.Equal(foe.Slot, rig.Combat.StateOf(f).TargetSlot);
        Assert.Equal("32 Self preservation", rig.Cold(f).Decision!.Winner);
    }

    /// <summary>
    /// An order that cannot run falls through (the original's chooser goes on): a First Aid order without a medkit stays queued with its text while its character defends itself. Catches a
    /// blocked order that keeps its character from its goals, or one dropped by the fight.
    /// </summary>
    [Fact]
    public void An_order_that_cannot_run_falls_through_to_self_defence()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        var inventory = rig.Cold(f).Inventory!;
        Assert.True(inventory.Remove(inventory.All().First(i => i.ItemFunction == ItemInfo.FirstAidKitFunction)));
        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();
        Assert.False(rig.Cold(f).OrderQueue[0].Started);
        var foe = Attacker(rig);
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Attack, 120, "fighting back");
        Assert.Equal(foe.Slot, rig.Combat.StateOf(f).TargetSlot);
        Assert.Equal([TaskType.FIRST_AID_ORDER], rig.Cold(f).OrderQueue.Select(e => e.Row));
        Assert.Contains(rig.Cold(f).Decision!.Lines, l => l.StartsWith("1 Orders: ") && !l.EndsWith("no order"));
    }

    /// <summary>
    /// Getting food (243) beats a job: a hungry character operating the well on its job walks to its outpost's store for food. Catches a job's task that keeps its character from the goals
    /// the original tries before the jobs.
    /// </summary>
    [Fact]
    public void Getting_food_beats_a_jobs_task()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 600, "operating the well");
        rig.World.Machines.Find(rig.StoreSite)!.Storage!.Add(new ItemInstance { Record = "bread", Name = "bread", ItemFunction = FeedSystem.FoodFunction, IsFood = true, Quantity = 1 });
        rig.Cold(f).Medical!.Hunger = 2;
        RunUntil(rig, () => rig.Cold(f).Running is { Row: TaskType.AQUIRE_FOOD_AT_HOMEBASE }, 60, "going for food");
        Assert.Equal(CharacterTask.GoTo, rig.Task(f));
        Assert.Equal("243 Getting food", rig.Cold(f).Decision!.Winner);
    }

    /// <summary>
    /// The job list in order (<c>FUN_14050e5b0</c>): a character treating its mate on the medic job (second in its list) is taken over by the operate job above it as soon as the well can be
    /// worked, at the next decision. Catches a running medic job that a higher job cannot take over (the gap of the old job passes).
    /// </summary>
    [Fact]
    public void A_higher_list_job_takes_over_a_lower_running_one()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        var well = rig.World.Buildings.Find(rig.WellSite)!;
        well.Complete = false;
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new MedicJobCommand([f]) { Tick = rig.World.Tick + 1 });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Treat && rig.Cold(f).Running is { Row: TaskType.JOB_MEDIC, Job: 1 }, 120, "treating on the medic job");
        well.Complete = true;
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 2 * ProductionSystem.JobCheckTicks + 2, "the operate job taking over");
        Assert.Equal((TaskType.OPERATE_MACHINERY, 0), (rig.Cold(f).Running!.Row, rig.Cold(f).Running!.Job));
        Assert.Equal("Jobs", rig.Cold(f).Decision!.Winner);
    }

    /// <summary>
    /// An order beats a job and the job waits while it runs: a character operating on its job rests at once when ordered and is not taken back to the well at the re-asks. Catches a job that
    /// takes a character off its order.
    /// </summary>
    [Fact]
    public void An_order_beats_a_job()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 600, "operating the well");
        rig.Say(new RestOrder([f]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        rig.World.RunTicks(3 * ProductionSystem.JobCheckTicks);
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Assert.Equal(TaskSource.Order, rig.Cold(f).Running!.Source);
    }

    /// <summary>
    /// The jobs switch (<c>FUN_14050c1c0</c>): turned off, it stops the operate job (87) the character runs and lets the medic job (58, one of 16, 31, 45, 57, 58, 250) below it run.
    /// Catches a switch that stops every job, or none.
    /// </summary>
    [Fact]
    public void The_jobs_switch_off_stops_jobs_but_the_listed_ones()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OPERATE_MACHINERY, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new MedicJobCommand([f]) { Tick = rig.World.Tick + 1 });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate && rig.Cold(f).Jobs.Count == 2, 600, "operating the well (the higher job)");
        rig.Say(new OrdersCommand([f], CharacterOrders.Jobs, On: false) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Treat, 2 * ProductionSystem.JobCheckTicks + 2, "the medic job running with the switch off");
        Assert.Equal((TaskType.JOB_MEDIC, 1), (rig.Cold(f).Running!.Row, rig.Cold(f).Running!.Job));
        Assert.Contains($"5 Jobs [0] {(int)TaskType.OPERATE_MACHINERY}: jobs switch off", rig.Cold(f).Decision!.Lines);
    }

    /// <summary>
    /// The urgent job pass (step 3, before Self preservation): a gunner on the Turret guard job (234) that senses an enemy keeps manning its turret while that enemy closes in to attack it, and
    /// fights it only once it is within 40 (146 needs !158). Catches a turret job that gives way to Self preservation as a plain job, or that never does.
    /// </summary>
    [Fact]
    public void An_urgent_turret_job_beats_self_defence_until_a_melee_threat()
    {
        using var rig = new OrderQueueTests.TurretRig(aggro: true);
        var (gunner, enemy) = (rig.Gunner, rig.Enemy);
        rig.World.Decisions.Tracing = true;
        rig.Say(new AddJob([gunner], TaskType.MAN_A_TURRET_PLAYER_JOB, rig.Turret) { Tick = 0 });
        rig.RunUntil(() => rig.Task(gunner) == CharacterTask.Operate && rig.Cold(gunner).OperateSite == rig.Turret, 600, "manning the turret");
        rig.Say(new Meitou.Simulation.Combat.AttackOrder([enemy], gunner) { Tick = rig.World.Tick, Picked = true });
        bool held = false;
        rig.RunUntil(() =>
        {
            var gap = rig.World.Characters.Next[enemy.Slot].Position - rig.World.Characters.Next[gunner.Slot].Position;
            float d = MathF.Sqrt(gap.X * gap.X + gap.Z * gap.Z);
            if (d > 50 && d < 150 && rig.Cold(gunner).Decision is { Winner: "Urgent jobs" } && rig.Task(gunner) == CharacterTask.Operate) held = true;
            return rig.Task(gunner) == CharacterTask.Attack;
        }, 900, "the gunner fighting back");
        Assert.True(held, "the gunner kept its turret on the urgent job while the attacker was beyond 40");
        Assert.Equal(enemy.Slot, rig.World.SystemOf<Meitou.Simulation.Combat.CombatSystem>()!.StateOf(gunner).TargetSlot);
    }
}
