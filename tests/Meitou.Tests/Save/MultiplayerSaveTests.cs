using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>The multiplayer roster and squad owners in saves (docs/formats/save.md "Multiplayer roster and owners").</summary>
public class MultiplayerSaveTests
{
    static readonly Guid HostId = new("11111111-2222-3333-4444-555555555555"), GuestId = new("66666666-7777-8888-9999-aaaaaaaaaaaa");

    sealed record Game(SimWorld World, PopulationData Data, Squad Ours, Squad Theirs) : IDisposable
    {
        public void Dispose() => World.Dispose();
        public string[] Names(Squad squad) => [.. squad.Members.Select(m => World.Characters.Cold(m.Slot)!.Name)];
    }

    /// <summary>
    /// A started game on the synthetic town, its start squad split: player 0's first two members and a squad of player 1's holding the rest. <paramref name="roster"/>: the
    /// host and the guest have GUIDs; otherwise player 0 is alone (a single-player game).
    /// </summary>
    static Game Start(bool roster)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1 });
        var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        var ours = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.Player.Local.Selection.Add(ours.Leader);
        if (!roster) return new Game(world, data, ours, ours);
        world.Player.Local.Guid = HostId;
        world.Player.Local.Name = "Host";
        world.Player.Join(1, GuestId, "Guest");
        var theirs = SquadManagement.NewSquad(world, db, world.Characters.Previous[ours.Leader.Slot].Position, "Nameless", owner: 1);
        foreach (var id in ours.Members.Skip(2).ToList())
        {
            ours.Members.Remove(id);
            theirs.Members.Add(id);
            world.Characters.Cold(id.Slot)!.SquadId = theirs.Id;
        }
        theirs.Leader = theirs.Members[0];
        return new Game(world, data, ours, theirs);
    }

    static SimWorld EmptyWorld() => new(new WorldSettings { Seed = 11, PublishSnapshots = false }, new OpenGroundWalkability(SyntheticTown.Ground), [new PlayerSystem()]);

    /// <summary>Writes <paramref name="save"/> to a folder and loads it into a new world with <paramref name="local"/> as player 0.</summary>
    static SimWorld Reload(SaveGame save, PopulationData data, string name, Guid local)
    {
        string folder = Path.Combine(Temp(name), "slot");
        try
        {
            save.Write(folder);
            var world = EmptyWorld();
            SaveLoader.Load(world, SaveGame.Load(folder), data, new SaveLoadOptions { LocalPlayer = local });
            return world;
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    static Squad SquadOf(SimWorld world, string[] names) =>
        world.Squads.All.Single(s => s.Members.Count > 0 && s.Members.Select(m => world.Characters.Cold(m.Slot)!.Name).SequenceEqual(names));

    [Fact]
    public void Owners_round_trip_through_capture_and_load_with_the_roster_remapped_to_the_loading_player()
    {
        using var g = Start(roster: true);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        var camera = save.Camera.Record;
        Assert.Equal(2, camera.Ints[RosterSave.CountKey]);
        Assert.Equal((HostId.ToString(), "Host", 0), (camera.Strings["meitou player0 id"], camera.Strings["meitou player0 name"], camera.Ints["meitou player0 number"]));
        Assert.Equal((GuestId.ToString(), "Guest", 1), (camera.Strings["meitou player1 id"], camera.Strings["meitou player1 name"], camera.Ints["meitou player1 number"]));

        // The host loads it: everyone keeps their number and squads.
        using (var host = Reload(save, g.Data, "mp-host", HostId))
        {
            Assert.Equal([(0, HostId, "Host"), (1, GuestId, "Guest")], host.Player.Sessions.Select(s => (s.Number, s.Guid, s.Name)));
            var (ours, theirs) = (SquadOf(host, g.Names(g.Ours)), SquadOf(host, g.Names(g.Theirs)));
            Assert.Equal((0, 1), (ours.Owner, theirs.Owner));
            Assert.Equal((ours.Id, theirs.Id), (host.Player.Local.Squad, host.Player.Find(1)!.Squad));
            Assert.Equal([ours.Leader], host.Player.Local.Selection);
        }

        // The guest loads it as host: the guest becomes 0 and the old host takes the guest's number, with the squads; the old host's selection is not player 0's.
        using var guest = Reload(save, g.Data, "mp-guest", GuestId);
        Assert.Equal([(0, GuestId, "Guest"), (1, HostId, "Host")], guest.Player.Sessions.Select(s => (s.Number, s.Guid, s.Name)));
        var (oldOurs, oldTheirs) = (SquadOf(guest, g.Names(g.Ours)), SquadOf(guest, g.Names(g.Theirs)));
        Assert.Equal((1, 0), (oldOurs.Owner, oldTheirs.Owner));
        Assert.Equal((oldTheirs.Id, oldOurs.Id), (guest.Player.Local.Squad, guest.Player.Find(1)!.Squad));
        Assert.Empty(guest.Player.Local.Selection);
    }

    [Fact]
    public void A_single_player_save_has_no_roster_or_owner_keys()
    {
        using var g = Start(roster: false);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        var camera = save.Camera.Record;
        Assert.DoesNotContain(camera.Ints.Keys, k => k.StartsWith("meitou player", StringComparison.Ordinal));
        Assert.DoesNotContain(camera.Strings.Keys, k => k.StartsWith("meitou player", StringComparison.Ordinal));
        Assert.All(save.Platoons, p => Assert.False(p.Record.Strings.ContainsKey(RosterSave.OwnerKey)));
    }

    [Fact]
    public void A_load_without_a_local_player_gives_every_squad_to_player_0()
    {
        using var g = Start(roster: true);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0));
        using var world = Reload(save, g.Data, "mp-single", Guid.Empty);
        var local = Assert.Single(world.Player.Sessions);
        Assert.Equal((Guid.Empty, ""), (local.Guid, local.Name));
        var squads = SquadManagement.PlayerSquads(world);
        Assert.Equal(2, squads.Count);
        Assert.All(squads, s => Assert.Equal(0, s.Owner));
        Assert.Equal(SquadOf(world, g.Names(g.Ours)).Id, local.Squad);
        Assert.Equal([SquadOf(world, g.Names(g.Ours)).Leader], local.Selection);
    }
}
