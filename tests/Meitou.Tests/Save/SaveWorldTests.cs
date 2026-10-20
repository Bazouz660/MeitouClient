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
    public void A_new_games_world_is_captured_into_a_save_and_loaded_into_another_world()
    {
        using var g = NewGame();
        var clock = new SaveClock(2, 14, 30);
        g.World.Player.Local.Selection.Add(g.Squad.Leader);
        var save = SaveCapture.Capture(g.World, g.Data, clock);

        Assert.Equal((2, 14, 30), (save.Camera.Day, save.Camera.Hour, save.Camera.Minute));
        Assert.Equal(500, save.Camera.PlayerMoney);
        Assert.Equal((1, 4), (save.Camera.Squads, save.Camera.Members));
        Assert.Equal(g.Data.Factions.Count, save.Factions.Count);
        var platoon = Assert.Single(save.Platoons);
        Assert.Equal("Nameless_0", platoon.Name);
        Assert.Equal(4, platoon.Characters.Count);
        Assert.Equal(1, save.PlayerFaction!.PlatoonCounter);
        // The leader is first in the selection; its handle is the CAMERA's.
        var leader = platoon.Characters.Single(c => c.IsLeader);
        Assert.Equal(leader.Handle, save.Camera.SelectedCharacter);

        string folder = Path.Combine(Dir("newgame"), "slot");
        try
        {
            save.Write(folder);
            using var world2 = EmptyWorld();
            var loaded = SaveLoader.Load(world2, SaveGame.Load(folder), g.Data);
            Assert.Equal(clock, loaded.Clock);
            Assert.Equal(500, world2.Player.Money);
            Assert.Equal(g.Data.PlayerFaction, world2.Player.Faction);
            var squad = world2.Squads.Find(world2.Player.Local.Squad)!;
            Assert.Equal(4, squad.Members.Count);
            Assert.Equal(g.Squad.Members.Select(m => g.World.Characters.Cold(m.Slot)!.Name), squad.Members.Select(m => world2.Characters.Cold(m.Slot)!.Name));
            for (int i = 0; i < 4; i++)
            {
                var a = g.World.Characters.Previous[g.Squad.Members[i].Slot];
                var b = world2.Characters.Previous[squad.Members[i].Slot];
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.Yaw, b.Yaw, 4);
            }
            Assert.Equal(g.Squad.Leader.Slot, g.Squad.Members.First(m => m == g.Squad.Leader).Slot);
            Assert.Equal(world2.Characters.Cold(squad.Leader.Slot)!.Name, g.World.Characters.Cold(g.Squad.Leader.Slot)!.Name);
            Assert.Equal([squad.Leader], world2.Player.Local.Selection);
            Assert.All(squad.Members, m => Assert.True(world2.Characters.Cold(m.Slot)!.IsPlayer));
            Assert.All(squad.Members, m => Assert.NotNull(world2.Characters.Cold(m.Slot)!.Save));
            // Relations: the table the new save holds is the data's own.
            foreach (var f in loaded.Factions.Where(f => !f.IsPlayer))
                foreach (var r in f.Relations.Values)
                    Assert.Equal(g.Data.Relations.Get(g.Data.Factions.ToList().FindIndex(x => x.Id == f.Id), g.Data.Relations.IndexOf(r.FactionId)), r.Relation);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
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
            var squad = world2.Squads.Find(world2.Player.Local.Squad)!;
            var item = Assert.Single(world2.Characters.Cold(squad.Leader.Slot)!.Inventory!.Items);
            Assert.Equal(("70-t", "main", 2, 1, 3, 40f), (item.Record, item.Section, item.X, item.Y, item.Quantity, item.Charges));
            Assert.NotNull(item.Saved);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}
