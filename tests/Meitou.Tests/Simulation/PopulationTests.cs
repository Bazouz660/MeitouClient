using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class PopulationTests
{
    [Fact]
    public void Residents_appear_when_the_focus_is_near_and_leave_after_the_grace_period()
    {
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        Assert.Equal(SiteStatus.Loaded, population.StatusOf(0));
        // 3 squads of 10 (patrol), 1 faction squad of 3; the gated one is not made.
        Assert.Equal(4, world.Squads.Count);
        var table = world.Characters;
        Assert.Equal(world.Squads.All.Sum(s => s.Members.Count), table.Count);
        var centre = new Vector2(SyntheticTown.Centre.X, SyntheticTown.Centre.Z);
        foreach (var s in world.Squads.All)
        {
            Assert.InRange(Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), centre), 0, 300 * 0.6f + 0.01f);
            Assert.False(s.Leader.IsNone);
        }
        var patrols = world.Squads.All.Where(s => s.TemplateId == "10-t").ToList();
        Assert.Equal(3, patrols.Count);
        Assert.All(patrols, s => Assert.InRange(s.Members.Count, 1 + 2 + 1 + 1, 1 + 4 + 1 + 1));
        int before = table.Count;

        // The focus moves far away: nothing is removed during the grace period, then everything is.
        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(150);   // 5 s
        Assert.Equal(before, table.Count);
        world.RunTicks(200);   // past 10 s
        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, world.Squads.Count);

        // Coming back loads them again (slots are reused with new generations).
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal(before, table.Count);
        Assert.Contains(table.Previous.ToArray(), c => c.Alive && c.Generation > 0);
    }

    [Fact]
    public void A_town_stays_loaded_while_one_of_its_squads_stands_in_an_active_zone()
    {
        // Issue #57: a squad chasing the player away from home vanished with its site's zone.
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        var table = world.Characters;
        var chaser = world.Squads.All.First().Members[0];
        var far = new Vector3(-50000, 0, -50000);
        world.Commands.Enqueue(new FocusCommand(far) { Tick = world.Tick });
        world.RunTick();
        table.Place(chaser.Slot, far, 0);
        world.RunTicks(350);   // past 10 s
        Assert.Equal(SiteStatus.Loaded, population.StatusOf(0));
        Assert.True(table.IsAlive(chaser));

        // Back home, out of the active zones: the town goes after its grace.
        table.Place(chaser.Slot, SyntheticTown.Centre, 0);
        world.RunTicks(350);
        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void A_housed_squads_prisoners_are_made_caged_in_its_home_held_without_a_sentence_and_trimmed_to_its_free_cages()
    {
        var function = SyntheticTown.Rec("53-t", Meitou.Data.Fcs.FcsRecordType.BUILDING_FUNCTIONALITY, "Cage");
        function.Ints["function"] = TownBuildings.CageFunction;
        var cage = SyntheticTown.Rec("54-t", Meitou.Data.Fcs.FcsRecordType.BUILDING, "Prisoner Cage");
        cage.References["functionality"] = [SyntheticTown.Ref("53-t")];
        var house = SyntheticTown.Rec("52-t", Meitou.Data.Fcs.FcsRecordType.BUILDING, "House");
        var captives = SyntheticTown.Rec("14-t", Meitou.Data.Fcs.FcsRecordType.SQUAD_TEMPLATE, "Captives");
        captives.References["squad"] = [SyntheticTown.Ref("2-t", 3)];
        // One patrol, housed by its level-data template in a house with two cages; its template makes a captive squad of three.
        var db = SyntheticTown.Database(residentSquads: 1, extra: [function, cage, house, captives], changePatrol: p => p.References["prisoners"] = [SyntheticTown.Ref("14-t")]);
        var at = SyntheticTown.Centre + new Vector3(0, 0, 100);
        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        string key = BuildingKey.Of(zone, "house");
        var home = new TownBuilding("house", "52-t", "house", at, 0, false, false, [])
        {
            Zone = zone, CanHouse = true, ResidentsTemplate = "10-t",
            Fixtures = [new TownFixture($"{key}/0", "54-t", true, at + new Vector3(10, 0, 0), 0), new TownFixture($"{key}/1", "54-t", true, at + new Vector3(-10, 0, 0), 0)],
        };
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)], buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [home] });
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1 }).Systems);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        world.RunTicks(2);

        var patrol = world.Squads.All.Single(s => s.TemplateId == "10-t");
        Assert.Same(home, patrol.HomeBuilding);
        var held = world.Squads.All.Single(s => s.TemplateId == "14-t");
        Assert.Equal(2, held.Members.Count);   // three made, two free cages
        Assert.Equal(patrol.Faction, held.Faction);
        var table = world.Characters;
        var cages = held.Members.Select(m => table.Cold(m.Slot)!).Select(c => (c.IsImprisoned, c.Kidnapped, c.SentenceLength, c.InWhat)).ToList();
        Assert.All(cages, c => Assert.Equal((true, true, 0f), (c.IsImprisoned, c.Kidnapped, c.SentenceLength)));
        Assert.Equal([$"{key}/0", $"{key}/1"], cages.Select(c => c.InWhat).Order());
        Assert.All(cages, c => Assert.True(world.Fixtures.Find(c.InWhat!)!.Lock!.Locked));
    }

    [Fact]
    public void Background_building_gives_the_same_town_as_building_in_the_tick()
    {
        using var sync = SyntheticTown.World(11, 1, 3);
        sync.RunTick();
        var settings = new PopulationSettings { Background = true };
        using var async = SyntheticTown.World(11, 1, 3, settings);
        Assert.True(TestWaits.TickUntil(async, () => async.Characters.Count > 0), "the background build never finished");
        Assert.Equal(sync.Characters.Count, async.Characters.Count);
        var a = sync.Squads.All.Select(s => (s.TemplateId, s.Position.X, s.Position.Z, s.Members.Count)).ToList();
        var b = async.Squads.All.Select(s => (s.TemplateId, s.Position.X, s.Position.Z, s.Members.Count)).ToList();
        Assert.Equal(a, b);
    }
}
