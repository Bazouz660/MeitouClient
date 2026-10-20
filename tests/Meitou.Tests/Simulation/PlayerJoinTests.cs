using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Joining a running game and passing squads between players (docs/simulation.md "Multiplayer (host-authoritative co-op)", Joining).</summary>
public class PlayerJoinTests
{
    /// <summary>The host's start squad and a guest (player 1) on the roster without squads; no town in reach.</summary>
    sealed record Game(SimWorld World, PopulationSystem Population, NewGameStart Start, Squad Ours) : IDisposable
    {
        public void Dispose() => World.Dispose();
        public PlayerSession Guest => World.Player.Find(1)!;
        public Vector3 Near => World.Characters.Previous[Ours.Leader.Slot].Position + new Vector3(300, 0, 0);
    }

    static Game Start()
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, walk, systems.Systems);
        var start = NewGameStart.From(db.Find("40-t")!);
        var ours = systems.Population!.StartPlayer(world, start);
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.Player.Join(1, Guid.NewGuid(), "Guest");
        return new Game(world, systems.Population, start, ours);
    }

    [Fact]
    public void A_joiner_gets_a_squad_of_their_own_in_creation_and_the_faction_is_left_alone()
    {
        using var g = Start();
        var player = g.World.Player;
        var (faction, money, research, completed) = (player.Faction, player.Money, g.World.Research.Version, g.World.Research.Completed.ToList());
        var localSelection = player.Local.Selection.ToList();
        var squad = g.Population.AddPlayer(g.World, 1, g.Start, g.Near);
        Assert.Equal(faction, player.Faction);
        Assert.Equal(money, player.Money);
        Assert.Equal(research, g.World.Research.Version);
        Assert.Equal(completed, g.World.Research.Completed);
        Assert.Equal(1, squad.Owner);
        Assert.Equal(g.Ours.Members.Count, squad.Members.Count);
        Assert.All(squad.Members, id => Assert.Equal(1, g.World.OwnerOf(id)));
        Assert.NotEqual(g.Ours.Name, squad.Name);
        Assert.Equal([squad], SquadManagement.PlayerSquads(g.World, 1));
        Assert.True(g.Guest.InCreation);
        Assert.Equal(squad.Id, g.Guest.Squad);
        Assert.Equal([squad.Members[0]], g.Guest.Selection);
        Assert.Equal(g.Near, g.Guest.Focus);
        // The host's session keeps its squad and selection.
        Assert.Equal(g.Ours.Id, player.Local.Squad);
        Assert.Equal(localSelection, player.Local.Selection);
    }

    [Fact]
    public void A_rejoin_makes_no_new_squad_and_selects_the_existing_one()
    {
        using var g = Start();
        g.World.Commands.Enqueue(new JoinPlayer(null, g.Near) { Tick = 0, Player = 1 });
        g.World.RunTick();
        var squad = Assert.Single(SquadManagement.PlayerSquads(g.World, 1));
        g.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = g.World.Tick, Player = 1 });
        g.World.RunTick();
        int squads = g.World.Squads.All.Count();
        g.Guest.Selection.Clear();
        g.World.Commands.Enqueue(new JoinPlayer(null, g.Near + new Vector3(500, 0, 0)) { Tick = g.World.Tick, Player = 1 });
        g.World.RunTick();
        Assert.Equal(squads, g.World.Squads.All.Count());
        Assert.Equal([squad], SquadManagement.PlayerSquads(g.World, 1));
        Assert.Equal(squad.Id, g.Guest.Squad);
        Assert.Equal([squad.Members[0]], g.Guest.Selection);
        Assert.False(g.Guest.InCreation);
    }

    [Fact]
    public void A_transferred_squad_changes_owner_and_leaves_the_other_players_selections()
    {
        using var g = Start();
        var theirs = g.Population.AddPlayer(g.World, 1, g.Start, g.Near);
        var member = theirs.Members[0];
        Assert.Equal([member], g.Guest.Selection);
        g.World.Commands.Enqueue(new TransferSquad(theirs.Id, 0) { Tick = 0 });
        g.World.RunTick();
        Assert.Equal(0, theirs.Owner);
        Assert.Equal(0, g.World.OwnerOf(member));
        Assert.Empty(g.Guest.Selection);
        Assert.Equal(-1, g.Guest.Squad);
        Assert.Equal([g.Ours, theirs], SquadManagement.PlayerSquads(g.World, 0));
        Assert.Equal(g.Ours.Id, g.World.Player.Local.Squad);
        // The new owner can select the squad's members now.
        g.World.Commands.Enqueue(new SelectCommand([member]) { Tick = g.World.Tick });
        g.World.RunTick();
        Assert.Equal([member], g.World.Player.Local.Selection);
    }
}
