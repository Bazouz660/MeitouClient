using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
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

    [Fact]
    public void Squad_limits_and_the_active_squad_act_per_owner()
    {
        using var g = Start();
        int max = SyntheticTown.Data(SyntheticTown.Database()).Bodies.Constants!.MaxSquads;
        var at = g.Where(g.Theirs.Members[0]);
        g.World.TakeMessages();
        for (int i = 0; i < max + 3; i++) g.World.Commands.Enqueue(new NewSquadCommand(at) { Tick = 0, Player = 1 });
        g.World.RunTick();
        // Player 1 is at `max squads`, which only counts their own; the refusal goes to them alone.
        Assert.Equal(max, SquadManagement.PlayerSquads(g.World, 1).Count);
        Assert.Contains(g.World.TakeMessages(), m => m is { Player: 1, Text: "Maximum number of squads reached." });
        g.World.Commands.Enqueue(new NewSquadCommand(at) { Tick = g.World.Tick });
        g.World.RunTick();
        Assert.Equal(2, SquadManagement.PlayerSquads(g.World, 0).Count);
        Assert.DoesNotContain(g.World.TakeMessages(), m => m.Text == "Maximum number of squads reached.");

        // The active squad is kept per owner: removing the guest's active (empty) squad moves only the guest to their first squad with members.
        var empty = SquadManagement.PlayerSquads(g.World, 1).Last();
        Assert.Empty(empty.Members);
        g.Guest.Squad = empty.Id;
        g.World.Player.Local.Squad = g.Ours.Id;
        g.World.Commands.Enqueue(new RemoveSquadCommand(empty.Id) { Tick = g.World.Tick, Player = 1 });
        g.World.RunTick();
        Assert.Null(g.World.Squads.Find(empty.Id));
        Assert.Equal(g.Theirs.Id, g.Guest.Squad);
        Assert.Equal(g.Ours.Id, g.World.Player.Local.Squad);

        // A command acts on its issuer's squads only: player 0 can neither remove nor show the guest's squad.
        g.World.Commands.Enqueue(new RemoveSquadCommand(g.Theirs.Id) { Tick = g.World.Tick });
        g.World.Commands.Enqueue(new ActiveSquadCommand(g.Theirs.Id) { Tick = g.World.Tick });
        g.World.RunTick();
        Assert.NotNull(g.World.Squads.Find(g.Theirs.Id));
        Assert.Equal(g.Ours.Id, g.World.Player.Local.Squad);
    }

    /// <summary>A fresh single-player world with the start squad and no second session.</summary>
    static SimWorld Single(out Squad ours)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, walk, systems.Systems);
        ours = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        return world;
    }

    static ulong HashAfter(Action<SimWorld, Squad>? change = null)
    {
        using var world = Single(out var ours);
        change?.Invoke(world, ours);
        world.RunTick();
        return world.StateHash();
    }

    [Fact]
    public void The_state_hash_changes_only_once_a_second_session_or_a_squad_owner_exists()
    {
        ulong alone = HashAfter();
        Assert.Equal(alone, HashAfter());
        // Player 0's roster entry and squads owned by player 0 leave the single-player hash as it was.
        Assert.Equal(alone, HashAfter((w, _) => w.Player.Join(0, Guid.NewGuid(), "Host")));
        Assert.Equal(alone, HashAfter((_, s) => s.Owner = 0));
        // A second session, or a squad of another owner, is hashed.
        Assert.NotEqual(alone, HashAfter((w, _) => w.Player.Join(1, Guid.NewGuid(), "Guest")));
        Assert.NotEqual(alone, HashAfter((_, s) => s.Owner = 1));
    }

    sealed record BailResult(bool Freed, CharacterId Near, CharacterId Far, CharacterId Payer, IReadOnlyList<PlayerMessage> Messages, long Money);

    /// <summary>
    /// A caged prisoner (bail c.2000) at cage A (+200, 0) of the arrest town, a player-0 character 10 from it and a player-1 character at <paramref name="guestAt"/>; player 1 pays the bail.
    /// </summary>
    static BailResult PayBailAsGuest(Vector2 guestAt)
    {
        var centre = new Vector3(2000, 150, 2000);
        var person = new RaceData { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers, Anatomy = Human.Anatomy, Carriable = true };
        var data = ArrestTests.Town();
        var relations = data.Relations;
        int city = data.FactionIndex("50-a");
        var crime = new CrimeSystem(relations);
        var arrest = new ArrestSystem(crime, BaseGame, new ArrestSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 31, PublishSnapshots = false, MinPartitionSize = 1 }, walk, new List<ITickSystem> { new PlayerSystem(), crime, arrest });
        world.Fixtures.RegisterSite(world, data, 0);
        world.Player.Join(1, Guid.NewGuid(), "Guest");
        var squads = new[]
        {
            new Squad { Id = 1000, TemplateId = "p", Faction = relations.PlayerFaction, Owner = 0 },
            new Squad { Id = 1001, TemplateId = "p", Faction = relations.PlayerFaction, Owner = 1 },
        };
        foreach (var s in squads) world.Squads.Add(s);
        CharacterId Hero(Vector2 where, string name, Squad squad)
        {
            var cold = new Combatant().MakeCold(name);
            cold.Race = person;
            cold.Faction = relations.PlayerFaction;
            cold.IsPlayer = true;
            cold.NpcClass = FundamentalType.Adventurer;
            cold.SquadId = squad.Id;
            var id = world.Characters.Spawn(new CharacterHot { Position = centre + new Vector3(where.X, 0, where.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
            squad.Members.Add(id);
            return id;
        }
        var near = Hero(new Vector2(190, 0), "near", squads[0]);
        var far = Hero(guestAt, "far", squads[1]);

        var prisonerCold = new Combatant().MakeCold();
        prisonerCold.Race = person;
        prisonerCold.Faction = city;
        prisonerCold.NpcClass = FundamentalType.Civilian;
        prisonerCold.Bounties = new BountyLedger();
        prisonerCold.Bounties.Get(city, "city").Amount = 700;
        var keepers = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Town = 0, HomeCentre = new Vector2(centre.X, centre.Z), HomeRadius = 500 };
        world.Squads.Add(keepers);
        prisonerCold.SquadId = keepers.Id;
        var prisoner = world.Characters.Spawn(new CharacterHot { Position = centre + new Vector3(200, 0, 30), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, prisonerCold, 0);
        keepers.Members.Add(prisoner);
        Assert.True(world.Fixtures.Imprison(world, prisoner, world.Fixtures.Find("0.0:cageA")!));

        world.Player.Money = 5000;
        world.TakeMessages();
        world.Commands.Enqueue(new PayBailOrder(prisoner) { Tick = world.Tick, Player = 1 });
        world.RunTicks(2);
        var payer = arrest.Log.FirstOrDefault(e => e.Kind == ArrestEventKind.Bailed).Guard;
        return new BailResult(!world.Characters.Cold(prisoner.Slot)!.IsImprisoned, near, far, payer, world.TakeMessages(), world.Player.Money);
    }

    [Fact]
    public void Bail_is_paid_by_the_issuers_character_not_the_nearest_one()
    {
        // Player 0's character is nearer to the cage; player 1's order is still paid by player 1's.
        var paid = PayBailAsGuest(new Vector2(200, 300));
        Assert.True(paid.Freed);
        Assert.Equal(paid.Far, paid.Payer);
        Assert.Equal(3000, paid.Money);
        Assert.Contains(paid.Messages, m => m.Player == 1 && m.Text.StartsWith("far paid a bail"));

        // With none of player 1's characters in range nothing is paid, though player 0's stands beside the cage.
        var refused = PayBailAsGuest(new Vector2(-450, 0));
        Assert.False(refused.Freed);
        Assert.Equal(5000, refused.Money);
        Assert.Contains(refused.Messages, m => m.Player == 1 && m.Text.Contains("near enough"));
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
