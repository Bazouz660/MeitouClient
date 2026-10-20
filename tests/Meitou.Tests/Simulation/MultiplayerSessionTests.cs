using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Per-player sessions (docs/simulation.md "Multiplayer (host-authoritative co-op)"): each player's selection is their own and orders fall back on the issuer's.</summary>
public class MultiplayerSessionTests
{
    /// <summary>The start squad (player 0's: its first two members) and a second squad of player 1's holding the rest; no town in reach.</summary>
    sealed record Game(SimWorld World, Squad Ours, Squad Theirs) : IDisposable
    {
        public void Dispose() => World.Dispose();
        public PlayerSession Guest => World.Player.Find(1)!;
        public Vector3 Where(CharacterId id) => World.Characters.Previous[id.Slot].Position;
    }

    static Game Start()
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, walk, systems.Systems);
        var ours = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.Player.Join(1, Guid.NewGuid(), "Guest");
        var theirs = SquadManagement.NewSquad(world, db, world.Characters.Previous[ours.Leader.Slot].Position, "Nameless", owner: 1);
        foreach (var id in ours.Members.Skip(2).ToList())
        {
            ours.Members.Remove(id);
            theirs.Members.Add(id);
            world.Characters.Cold(id.Slot)!.SquadId = theirs.Id;
        }
        theirs.Leader = theirs.Members[0];
        return new Game(world, ours, theirs);
    }

    [Fact]
    public void Two_players_select_their_own_characters_independently()
    {
        using var g = Start();
        var (ours, theirs) = (g.Ours.Members, g.Theirs.Members);
        g.World.Commands.Enqueue(new SelectCommand([ours[1]]) { Tick = 0 });
        g.World.Commands.Enqueue(new SelectCommand([theirs[0]]) { Tick = 0, Player = 1 });
        g.World.RunTick();
        Assert.Equal([ours[1]], g.World.Player.Local.Selection);
        Assert.Equal([theirs[0]], g.Guest.Selection);
        Assert.Equal(g.Theirs.Id, g.Guest.Squad);
        // Another player's character is left out of a select, and one player's select does not touch the other's selection.
        g.World.Commands.Enqueue(new SelectCommand([ours[0], theirs[1]]) { Tick = g.World.Tick, Player = 1 });
        g.World.RunTick();
        Assert.Equal([theirs[1]], g.Guest.Selection);
        Assert.Equal([ours[1]], g.World.Player.Local.Selection);
        Assert.Equal(g.Ours.Id, g.World.Player.Local.Squad);
    }

    [Fact]
    public void An_empty_move_order_moves_the_issuers_selection_only()
    {
        using var g = Start();
        var (mine, guest) = (g.Ours.Members[0], g.Theirs.Members[0]);
        var target = g.Where(guest) + new Vector3(300, 0, 0);
        g.World.Commands.Enqueue(new SelectCommand([mine]) { Tick = 0 });
        // The select and the order in the same tick: the order takes the selection the select just made.
        g.World.Commands.Enqueue(new SelectCommand([guest]) { Tick = 0, Player = 1 });
        g.World.Commands.Enqueue(new MoveOrder([], target) { Tick = 0, Player = 1 });
        g.World.RunTick();
        var state = g.World.Characters.Previous;
        Assert.Equal((byte)CharacterTask.GoTo, state[guest.Slot].Task);
        Assert.Equal(Flat(target), state[guest.Slot].Goal);
        Assert.NotEqual((byte)CharacterTask.GoTo, state[mine.Slot].Task);
        Assert.Equal([mine], g.World.Player.Local.Selection);
    }

    [Fact]
    public void A_players_dead_selection_is_replaced_by_their_own_character_never_another_players()
    {
        using var g = Start();
        var (ours, theirs) = (g.Ours.Members, g.Theirs.Members);
        g.World.Commands.Enqueue(new SelectCommand([theirs[0]]) { Tick = 0, Player = 1 });
        g.World.RunTicks(2);
        // Player 0's characters come first in the slots, so a pick of the first living player character would take one of theirs.
        Assert.True(ours.Max(c => c.Slot) < theirs[1].Slot);
        g.World.Characters.Cold(theirs[0].Slot)!.DiedTick = g.World.Tick;
        g.World.RunTicks(2);
        Assert.Equal([theirs[1]], g.Guest.Selection);
        foreach (var id in theirs) g.World.Characters.Cold(id.Slot)!.DiedTick = g.World.Tick;
        g.World.RunTicks(2);
        Assert.All(g.Guest.Selection, id => Assert.Equal(1, g.World.OwnerOf(id)));
        Assert.DoesNotContain(g.Guest.Selection, ours.Contains);
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
