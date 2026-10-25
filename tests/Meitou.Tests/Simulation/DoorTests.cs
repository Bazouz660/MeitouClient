using System.Numerics;
using Meitou.Data;
using Meitou.Data.Physics;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Doors: leaf motion, closing to the lock, Meitou's close-behind, bashing and where the doors are (docs/game/locks.md "Doors", "Door motion"; docs/simulation.md "Doors").</summary>
public class DoorTests(ITestOutputHelper output)
{
    [Fact]
    public void The_leaf_slides_and_steps_out_by_the_original_formula()
    {
        // A collision box in PhysX's Z-up frame: 20 wide (X), 4 thick (Y), 40 tall (Z), centred at (-10, 2, 20); in the part's Y-up frame its Y is -Z and its Z is up.
        var box = DoorFrame.Of(Pose.Identity with { Translation = new Vector3(-10, 2, 20) }, new Vector3(10, 2, 20));
        // No step out: slide x dist x doa.
        var plain = new DoorLeaf(0, 30, 1, 0);
        Assert.Equal(new Vector3(15, 0, 0), plain.LocalOffset(0.5f, box));
        Assert.Equal(new Vector3(30, 0, 0), plain.LocalOffset(1, box));
        // Step out 10 along the thinnest other axis (box Y = part -Z, its centre at Z -2: away from the origin is -Z) until t = 10 / (0.25 x 28 + 10) = 10/17, then slide 28 up (box Z).
        var stepping = new DoorLeaf(2, 28, 0.5f, 10);
        var half = stepping.LocalOffset(0.5f, box);
        Assert.Equal(-8.5f, half.Z, 4);
        Assert.Equal(0, half.Y);
        Assert.Equal(new Vector3(0, 28, -10), stepping.LocalOffset(1, box));
    }

    sealed class Gate : IDoorGate
    {
        public void SetDoorsBlocked(string buildingInstance, bool blocked) { }
        public void ResetDoors() { }
        public DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => new(Vector3.Zero, Vector3.UnitZ, 15);
    }

    [Fact]
    public void A_door_that_wants_its_lock_locks_when_closed_and_a_walker_opened_door_closes_after_5_s()
    {
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 31, PublishSnapshots = false, MinPartitionSize = 1 }, walk, [new LockSystem(BaseGame, BodyOptions.Default)]);
        world.Fixtures.RegisterSite(world, ArrestTests.Town(), 0);
        world.Fixtures.Doors = new Gate();

        var door = world.Fixtures.Find("0.0:house#0")!;
        door.Motion = DoorMotion.Open;
        door.OpenAmount = 1;
        door.WantsLock = true;
        Assert.True(FixtureRegistry.CloseDoor(door));
        for (int i = 0; i < 11; i++) world.Fixtures.StepDoors(0.1f);
        Assert.Equal(DoorMotion.Closed, door.Motion);
        Assert.Equal(0, door.OpenAmount);
        Assert.True(door.Lock!.Locked);

        // Meitou: a walker opened it and nobody goes through, so it closes once 5 s have passed since the last pass (and not before).
        door.WantsLock = false;
        door.Lock.Locked = false;
        door.Motion = DoorMotion.Open;
        door.OpenAmount = 1;
        door.AutoOpened = true;
        door.LastPass = world.Tick;
        int five = (int)MathF.Ceiling(LockSystem.AutoCloseSeconds / world.TickSeconds);
        world.RunTicks(five - 10);
        Assert.Equal(DoorMotion.Open, door.Motion);
        world.RunTicks(10 + LockSystem.WalkerEveryTicks);
        Assert.Equal(DoorMotion.Closing, door.Motion);
        Assert.False(door.Lock!.Locked);
    }

    [Fact]
    public void A_bash_blow_does_clamp_2_blunt_plus_half_cut_20_to_60_an_animal_at_a_quarter_first()
    {
        Assert.Equal(20, LockSystem.BlowDamage(10, 2, animal: false));     // 9 -> 20
        Assert.Equal(35, LockSystem.BlowDamage(30, 10, animal: false));
        Assert.Equal(60, LockSystem.BlowDamage(100, 40, animal: false));   // 130 -> 60
        Assert.Equal(25, LockSystem.BlowDamage(0, 50, animal: true));      // 100 x 0.25, then the clamp
        Assert.Equal(20, LockSystem.BlowDamage(30, 10, animal: true));
        Assert.Equal(0.015f, LockSystem.ProgressPerDamage);
    }

    sealed class SpotGate : IDoorGate
    {
        public void SetDoorsBlocked(string buildingInstance, bool blocked) { }
        public void ResetDoors() { }
        public DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => new(new Vector3(0, 0, 60), Vector3.UnitZ, 15);
    }

    [Fact, Slow]
    public void A_bonedog_whose_target_hides_in_a_locked_hub_house_walks_to_its_door_bashes_it_open_and_goes_in()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        int site = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        float ground = 150;
        var walk = new OpenGroundWalkability((_, _) => ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Movement | StandardParts.Pursuit | StandardParts.Body | StandardParts.Combat | StandardParts.Crime, SynchronousPaths = true,
            Combat = new Meitou.Simulation.Combat.CombatOptions { SelfApproach = false, TickMedical = false },
        });
        using var world = new SimWorld(new WorldSettings { Seed = 3, MinPartitionSize = 1, PublishSnapshots = false }, walk, systems.Systems);
        world.Fixtures.RegisterSite(world, data, site);
        world.Fixtures.Doors = new SpotGate();   // headless: a stand-in for navigation's door spots
        var b = data.Sites[site].Buildings.First(x => world.Fixtures.OfBuilding(x.Key).Any(f => f.Kind == FixtureKind.Door));
        ground = b.Position.Y;   // the house stands at its level height: the ground under the walkers too
        var doors = world.Fixtures.OfBuilding(b.Key).Where(f => f.Kind == FixtureKind.Door).ToList();
        foreach (var d in doors) { d.Lock!.Level = Math.Max(1, d.Lock.Level); world.Fixtures.SetLocked(d, true); }
        Assert.True(world.Fixtures.IsShut(b.Key));
        var door = doors[0];
        float hardness = door.Lock!.Hardness;
        Assert.True(hardness > 0);

        var population = new PopulationSystem(data, new PopulationSettings());
        string human = db.OfType(Meitou.Data.Fcs.FcsRecordType.CHARACTER)
            .First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId)?.Name == "Greenlander") && r.GetReferences("stats").Count > 0).StringId;
        var me = population.SpawnSandbox(world, human, new Vector2(b.Position.X, b.Position.Z), player: true, faction: data.PlayerFaction);
        var spot = world.Fixtures.DoorSpotOf(door)!.Value;
        var outward = Vector2.Normalize(new Vector2(spot.Centre.X - b.Position.X, spot.Centre.Z - b.Position.Z));
        var dog = population.SpawnSandbox(world, "3979-gamedata.base", new Vector2(spot.Centre.X, spot.Centre.Z) + outward * 200, player: false);
        world.RunTicks(2);
        world.Defer(new AttackOrder([dog], me) { Tick = world.Tick + 1 });
        var table = world.Characters;

        // A bonedog's blows do the minimum (a quarter of its packet is under 20): 0.3 of the door's progress each, one every 1.5 s.
        bool bashed = false;
        Vector3 at = default;
        int ticks = (int)((MathF.Ceiling(door.DoorProgress / (20 * LockSystem.ProgressPerDamage)) + 4) * LockSystem.BashSwingSeconds / world.TickSeconds) + 900;
        for (int t = 0; t < ticks && !door.DoorBroken; t++)
        {
            world.RunTick();
            bashed |= table.Cold(dog.Slot)!.Bashing == door.Id;
            at = table.Next[dog.Slot].Position;
        }
        output.WriteLine($"{b.Name}: hardness {hardness}, broken at tick {world.Tick}");
        Assert.True(bashed);
        Assert.True(door.DoorBroken);
        Assert.False(door.Lock.Locked);
        Assert.Equal(DoorMotion.Opening, door.Motion);
        Assert.False(world.Fixtures.IsShut(b.Key));
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(spot.Centre.X, spot.Centre.Z)) < 2 * DoorPlan.DoorReach);
        // The pursuit carries on into the house.
        world.RunTicks(150);
        Assert.Null(table.Cold(dog.Slot)!.Bashing);
        var now = table.Next[dog.Slot].Position;
        Assert.True(Vector2.Distance(new Vector2(now.X, now.Z), new Vector2(b.Position.X, b.Position.Z)) < Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(b.Position.X, b.Position.Z)));
    }

    [Fact, Slow]
    public void Every_door_in_the_hub_has_a_known_position_and_its_destroyed_gates_have_none()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), buildings: TownBuildings.Build(db, levels, null, (_, _) => 0));
        int site = data.Sites.ToList().FindIndex(s => s.Town.Name == "The Hub");
        var hub = data.Sites[site];
        var geometry = new NavDoorGeometry(InstallData.Install!, db);
        int doors = 0;
        var missing = new List<string>();
        foreach (var b in hub.Buildings)
        {
            if (db.Find(b.BuildingId) is not { } record) continue;
            for (int i = 0; i < record.GetReferences("doors").Count; i++, doors++)
                if (geometry.Spot(record.StringId, i) is null) missing.Add($"{b.Name} ({record.StringId}) door {i}");
        }
        Assert.True(doors > 10, $"{doors} doors");
        Assert.Empty(missing);

        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk, []);
        world.Fixtures.RegisterSite(world, data, site);
        // The level data marks both Defensive Gate IV destroyed; a ruin has no doors (docs/game/locks.md "Doors"), the intact buildings keep theirs.
        var gates = hub.Buildings.Where(b => b.IsGate).ToList();
        Assert.Equal(2, gates.Count(b => b.Destroyed));
        Assert.All(gates.Where(b => b.Destroyed), g => Assert.DoesNotContain(world.Fixtures.OfBuilding(g.Key), f => f.Kind == FixtureKind.Door));
        Assert.Contains(hub.Buildings.Where(b => !b.Destroyed), b => world.Fixtures.OfBuilding(b.Key).Any(f => f.Kind == FixtureKind.Door));
    }
}
