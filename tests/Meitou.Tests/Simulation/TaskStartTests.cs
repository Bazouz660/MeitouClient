using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The one task start path (docs/simulation.md "Task starts"): what a start does to the character's fight.</summary>
public class TaskStartTests
{
    /// <summary>The fixture's Greenlander, carriable like every base race but the Leviathan.</summary>
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers, Anatomy = Human.Anatomy, Carriable = true };

    const string Well = "60-t", Shed = "61-t";

    /// <summary>The first aid kits, a well (an operable mine) and a shed (4 Building Materials).</summary>
    static GameDatabase Database()
    {
        var db = FirstAidTests.KitDatabase();
        static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };
        var water = Rec("50-t", FcsRecordType.ITEM, "Water");
        water.Floats["weight kg"] = 1;
        var wellFn = Rec("80-t", FcsRecordType.BUILDING_FUNCTIONALITY, "80-t");
        wellFn.Ints["function"] = MachineData.MineFunction;
        wellFn.Ints["stat used"] = (int)StatsEnumerated.Labouring;
        wellFn.References["produces"] = [new FcsReference("50-t", 10, 0, 0)];
        var well = Rec(Well, FcsRecordType.BUILDING, "Test Well");
        well.References["functionality"] = [new FcsReference("80-t", 0, 0, 0)];
        var shed = Rec(Shed, FcsRecordType.BUILDING, "Test Shed");
        shed.Ints["build materials"] = 4;
        var file = new FcsFile();
        file.Records.AddRange([water, wellFn, well, shed]);
        db.Apply(file, "buildings.mod");
        return db;
    }

    /// <summary>
    /// A player's fighter (attacking a far enemy, so no blow lands) with a medkit and a splint kit, sneaking, and with <paramref name="carrying"/> a downed mate on its shoulder; a hurt mate,
    /// a downed NPC, an awake NPC and a second enemy; the player's well and an unfinished shed; the movement, first aid, body, carry, construction, production and combat systems in the
    /// game's order.
    /// </summary>
    sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public CombatSystem Combat { get; }
        public CharacterId Fighter, Enemy, OtherEnemy, Mate, Carried, Downed, Awake;
        public string WellSite = "", ShedSite = "";
        readonly OpenGroundWalkability walk = new(SyntheticTown.Ground);
        readonly ItemFactory items;

        public Rig(bool carrying)
        {
            var db = Database();
            items = new ItemFactory(db, BaseGame);
            Combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false });
            var systems = new List<ITickSystem>
            {
                new PlayerSystem(), new MovementSystem(new PathService(walk, true)), new FirstAidSystem(BaseGame, BodyOptions.Default, items, new FirstAidSettings { AutoNpcs = false }),
                new BodySystem(BaseGame, BodyOptions.Default), new CarrySystem(BaseGame, BodyOptions.Default),
                new ConstructionSystem(db, BaseGame, BodyOptions.Default, items, [], ""), new ProductionSystem(db, BaseGame, BodyOptions.Default, items, null), Combat,
            };
            World = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk, systems);
            World.Player.Faction = 1;
            string Site(string record, float x, float z, bool complete)
            {
                var p = new Vector3(x, 300, z);
                string id = World.Buildings.NewId(WorldLayout.ZoneOf(p.X, p.Z));
                World.Buildings.Add(new BuildSite { Id = id, Record = record, Position = p, Owner = 1, Materials = [], Complete = complete });
                return id;
            }
            WellSite = Site(Well, 700, 2000, complete: true);
            ShedSite = Site(Shed, 700, 2300, complete: false);
            Fighter = Add(1000, 2000, player: true, "72-t", "74-t");
            Mate = Add(1040, 2000, player: true);
            Carried = Add(1000, 2000, player: true);
            Downed = Add(1060, 2040, player: false);
            Awake = Add(1080, 2060, player: false);
            Enemy = Add(1500, 2000, player: false);
            OtherEnemy = Add(1500, 2100, player: false);
            Cold(Mate).Medical!.Parts[RLeg].Flesh = Cold(Mate).Medical!.Parts[RLeg].MaxHp * 0.2f;
            Cold(Carried).Medical!.KnockOut(1000);
            Cold(Downed).Medical!.KnockOut(1000);
            if (carrying) (Cold(Fighter).Carrying, Cold(Carried).CarriedBy) = (Carried, Fighter);
            Cold(Fighter).Orders |= CharacterOrders.Sneak;
            Say(new AttackOrder([Fighter], Enemy) { Tick = 0, Picked = true });
        }

        CharacterId Add(float x, float z, bool player, params string[] kits)
        {
            var cold = new Combatant { Name = "C" + World.Characters.HighWater }.MakeCold();
            cold.Race = Person;
            cold.Stats![StatsEnumerated.Medic] = 50;
            cold.Inventory = new Inventory();
            foreach (var k in kits) cold.Inventory.Add(items.Create(k)!);
            cold.IsPlayer = player;
            cold.Faction = player ? 1 : 2;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15 }, cold, 0);
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public CharacterTask Task(CharacterId id) => (CharacterTask)World.Characters.Previous[id.Slot].Task;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public void Dispose() => World.Dispose();
    }

    /// <summary>The converted order starts given to a fighting character, by name, with the act-out each starts (M3b and M3c add theirs).</summary>
    public static TheoryData<string> Orders => ["move", "follow", "bodyguard", "stop", "first aid", "splint", "rest", "medic", "pick up", "kidnap", "loot", "open inventory", "stealth ko", "put down",
        "operate", "prospect", "build", "dismantle", "attack"];

    static (SimCommand Order, CharacterTask Task) Order(Rig rig, string name, long tick)
    {
        var f = rig.Fighter;
        SimCommand order = name switch
        {
            "move" => new MoveOrder([f], new Vector3(900, 0, 1900)) { Tick = tick },
            "follow" => new FollowOrder([f], rig.Mate) { Tick = tick },
            "bodyguard" => new FollowOrder([f], rig.Mate) { Tick = tick, Guard = true },
            "stop" => new StopCommand([f]) { Tick = tick },
            "first aid" => new TreatOrder([f], rig.Mate) { Tick = tick },
            "splint" => new SplintOrder([f], rig.Mate) { Tick = tick },
            "rest" => new RestOrder([f]) { Tick = tick },
            "medic" => new MedicJobCommand([f], Job: false) { Tick = tick },
            "pick up" => new PickUpOrder([f], rig.Downed) { Tick = tick },
            "kidnap" => new PickUpOrder([f], rig.Downed) { Tick = tick, Kidnap = true },
            "loot" => new LootOrder([f], rig.Downed) { Tick = tick },
            "open inventory" => new OpenLootOrder([f], rig.Downed) { Tick = tick },
            "stealth ko" => new StealthKoOrder([f], rig.Awake) { Tick = tick },
            "put down" => new PutDownOrder([f]) { Tick = tick },
            "operate" => new OperateOrder([f], rig.WellSite) { Tick = tick },
            "prospect" => new ProspectOrder(f) { Tick = tick },
            "build" => new BuildOrder([f], rig.ShedSite) { Tick = tick },
            "dismantle" => new DismantleOrder([f], rig.WellSite) { Tick = tick },
            "attack" => new AttackOrder([f], rig.OtherEnemy) { Tick = tick, Picked = true },
            _ => throw new ArgumentException(name),
        };
        var task = name switch
        {
            "move" => CharacterTask.GoTo,
            "follow" or "bodyguard" => CharacterTask.Follow,
            "stop" => CharacterTask.Idle,
            "first aid" or "splint" or "medic" => CharacterTask.Treat,
            "rest" => CharacterTask.Rest,
            "pick up" or "kidnap" => CharacterTask.PickUp,
            "loot" or "open inventory" => CharacterTask.Loot,
            "stealth ko" => CharacterTask.StealthKo,
            "put down" => CharacterTask.PutDown,
            "operate" => CharacterTask.Operate,
            "prospect" => CharacterTask.Prospect,
            "build" => CharacterTask.Build,
            "dismantle" => CharacterTask.Dismantle,
            _ => CharacterTask.Attack,
        };
        return (order, task);
    }

    /// <summary>
    /// A new task ends the fight task (docs/game/combat.md "Fight lifecycle and orders"): every order given mid-fight starts its act-out and leaves the character with no fight flag
    /// and no fight target, unless the order is a fight, which keeps it fighting the new target. Catches an order refused mid-fight, or one that overwrites the task and leaves the
    /// fight running under it (docs/simulation.md "Task starts", a fight under another task).
    /// </summary>
    [Theory, MemberData(nameof(Orders))]
    public void An_order_given_mid_fight_ends_the_fight_unless_it_is_one(string name)
    {
        using var rig = new Rig(carrying: name == "put down");
        rig.World.RunTicks(3);
        var f = rig.Fighter;
        Assert.True(rig.Cold(f).InCombat && rig.Combat.StateOf(f).TargetSlot == rig.Enemy.Slot, "fighting before the order");
        var (order, task) = Order(rig, name, rig.World.Tick);
        rig.Say(order);
        rig.World.RunTick();
        Assert.Equal(task, rig.Task(f));
        if (task == CharacterTask.Attack)
        {
            Assert.True(rig.Cold(f).InCombat);
            Assert.Equal(rig.OtherEnemy.Slot, rig.Combat.StateOf(f).TargetSlot);
            return;
        }
        Assert.False(rig.Cold(f).InCombat, "the fight flag is off");
        Assert.True(rig.Combat.StateOf(f).TargetSlot < 0, "no fight target");
    }

    const int H = Repertoire.Human, I = Repertoire.Imprisoned, Ch = Repertoire.Chained, A = Repertoire.Animal, Cr = Repertoire.Crippled;

    /// <summary>The row of each start (docs/simulation.md "Task eligibility") and the repertoires the original registers it for (docs/game/ai-tasks.md "TaskData registry").</summary>
    public static TheoryData<TaskType, int> StartRows => new()
    {
        { TaskType.MOVE_CUS_ORDERED, H | A | Cr }, { TaskType.STAY_CLOSE_TO_TARGET, H | A | Cr }, { TaskType.BODYGUARD, H | A | Cr }, { TaskType.Unnamed289, A },
        { TaskType.FOCUSED_MELEE_ATTACK, H | A | Cr }, { TaskType.ATTACK_ATTACKERS_OF, H | A | Cr }, { TaskType.ATTACK_ENEMIES, H | A | Cr },
        { TaskType.FIRST_AID_ORDER, H | I | Cr }, { TaskType.SPLINT_ORDER, H | I | Cr }, { TaskType.SPLINT_JOB, H | I }, { TaskType.JOB_MEDIC, H | I | Cr }, { TaskType.JOB_REPAIR_ROBOT, H | I | Cr },
        { TaskType.LOOT_TARGET, H | I | Cr }, { TaskType.LOOT_THE_DEAD, H | I | Cr }, { TaskType.NOT_BE_UNARMED, H | I | Cr },
        { TaskType.LIFT_PERSON_PLAYER_ORDER, H }, { TaskType.KIDNAP_ORDER, H }, { TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, H }, { TaskType.LOOT_ANIMALS_JOB, H },
        { TaskType.DEAD_GUYS_GO_IN_THE_POT, H }, { TaskType.PICKUP_INTRUDERS, H }, { TaskType.PICKUP_INTRUDERS_TOWN, H }, { TaskType.PUT_DOWN_OBJECT, H }, { TaskType.STEALTH_KNOCKOUT, H },
        { TaskType.OPERATE_MACHINERY, H }, { TaskType.USE_TRAINING_DUMMY, H }, { TaskType.OPERATE_STORAGE, H }, { TaskType.USE_TURRET, H }, { TaskType.PROSPECTING, H | Cr },
        { TaskType.BUILD, H }, { TaskType.REPAIR, H }, { TaskType.DISMANTLE, H }, { TaskType.USE_BED, H | Cr }, { TaskType.PUT_SOMEONE_IN_BED, H }, { TaskType.FIND_BED_AND_PUT_IN, H },
    };

    /// <summary>
    /// Each start's row admits exactly the repertoires the original registers it for (<c>FUN_14060e840</c>, <c>Category &amp; repertoire</c>): catches a start moved to a row of another
    /// registration or a row's flags changed in the table. The platoon repertoire (2) is not modelled.
    /// </summary>
    [Theory, MemberData(nameof(StartRows))]
    public void Each_start_row_admits_the_repertoires_the_original_registers_it_for(TaskType row, int admitted)
    {
        var data = TaskDataTable.Of(row);
        Assert.NotNull(data);
        foreach (int repertoire in new[] { H, I, Ch, A, Cr })
            Assert.True(((data.Category & repertoire) != 0) == ((admitted & repertoire) != 0), $"{row} and repertoire 0x{repertoire:X}");
    }

    /// <summary>
    /// <see cref="Repertoire.Of"/> outside a cage: human 1, an animal 0x20, a crawler or one staying low 0x40 (the posture setter <c>FUN_1405c7e20</c>), before the animal test; a type with no
    /// row (Rest, 54) is open to all. Caged and chained characters are covered by the cage tests' starts.
    /// </summary>
    [Fact]
    public void The_repertoire_is_crippled_for_a_crawler_or_one_staying_low_else_human_or_animal()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground));
        CharacterCold Make(bool animal = false, bool low = false, bool stump = false)
        {
            var cold = new Combatant().MakeCold();
            if (animal) cold.Fighter = new Fighter(null) { IsAnimal = true };
            cold.Medical!.StayingLow = low;
            if (stump) { cold.Medical.Parts[RLeg].Limb = LimbState.Stump; cold.Medical.Tick(1e-4f, Context()); }
            return cold;
        }
        Assert.Equal(H, Repertoire.Of(world, Make()));
        Assert.Equal(A, Repertoire.Of(world, Make(animal: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(stump: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(low: true)));
        Assert.Equal(Cr, Repertoire.Of(world, Make(animal: true, low: true)));
        Assert.False(TaskStart.Eligible(world, Make(stump: true), TaskType.BUILD));
        Assert.False(TaskStart.Eligible(world, Make(animal: true), TaskType.LIFT_PERSON_PLAYER_ORDER));
        Assert.True(TaskStart.Eligible(world, Make(animal: true), TaskType.REST));
    }
}
