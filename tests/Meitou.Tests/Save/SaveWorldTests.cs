using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>Saves and worlds: loading a save into a <see cref="SimWorld"/> and capturing a world into a save (docs/simulation.md "Saves").</summary>
public class SaveWorldTests
{
    sealed record Game(SimWorld World, PopulationData Data, Squad Squad) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>A started game on the synthetic town: the player's squad of four, 500 cats.</summary>
    static Game NewGame(ulong seed = 11, bool items = false)
    {
        var db = SyntheticTown.Database(items: items);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1 });
        var population = systems.Population!;
        var world = new SimWorld(new WorldSettings { Seed = seed, PublishSnapshots = false }, walk, systems.Systems);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        return new Game(world, data, squad);
    }

    static SimWorld EmptyWorld(ulong seed = 11) =>
        new(new WorldSettings { Seed = seed, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), [new PlayerSystem()]);

    static string Dir(string name) => Temp(name);

    [Fact]
    public void A_dead_player_character_leaves_its_squad_for_the_hidden_dead_squad()
    {
        using var g = NewGame();
        g.World.RunTicks(1);
        var dead = g.Squad.Leader;
        var next = g.Squad.Members[1];
        g.World.Characters.Cold(dead.Slot)!.DiedTick = g.World.Tick;
        g.World.RunTicks(1);

        Assert.DoesNotContain(dead, g.Squad.Members);
        Assert.Equal(next, g.Squad.Leader);
        Assert.Equal((int)SquadRole.Leader, g.World.Characters.Cold(next.Slot)!.Role);
        var graveyard = Assert.Single(g.World.Squads.All, s => s.Dead);
        Assert.Equal([dead], graveyard.Members);
        Assert.Equal(graveyard.Id, g.World.Characters.Cold(dead.Slot)!.SquadId);
        Assert.DoesNotContain(graveyard, SquadManagement.PlayerSquads(g.World));
    }

    [Fact]
    public void An_inventory_changed_in_play_is_saved_and_loaded()
    {
        using var g = NewGame(items: true);
        var cold = g.World.Characters.Cold(g.Squad.Leader.Slot)!;
        var ration = g.Data.Items.Create("70-t", 3)!;
        (ration.X, ration.Y, ration.Charges) = (2, 1, 40);
        cold.Inventory = new Inventory();
        cold.Inventory.Items.Add(ration);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        string folder = Path.Combine(Dir("inventory"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            var squad = world2.Squads.Find(world2.Player.Squad)!;
            var item = Assert.Single(world2.Characters.Cold(squad.Leader.Slot)!.Inventory!.Items);
            Assert.Equal(("70-t", "main", 2, 1, 3, 40f), (item.Record, item.Section, item.X, item.Y, item.Quantity, item.Charges));
            Assert.NotNull(item.Saved);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}
