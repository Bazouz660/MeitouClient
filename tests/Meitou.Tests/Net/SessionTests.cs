using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net;
using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Session;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Net;

/// <summary>
/// A host and its clients in one process over the loopback network (docs/net.md, "Host", "Client and jitter buffer"; docs/simulation.md
/// "Multiplayer (host-authoritative co-op)"): ownership, replication, per-player isolation, the clock, reconnecting and joining while paused.
/// </summary>
public class SessionTests
{
    static CharacterWire Wire(CharacterSnapshot c) => CharacterWire.Quantize(c, _ => 0u, SessionHarness.Animations);

    [Fact]
    public void A_guest_moves_its_own_character_and_is_refused_the_hosts_with_the_gates_reason()
    {
        using var h = SessionHarness.SyntheticTownHost();
        var guest = h.Join("Guest");
        Assert.Equal(1, guest.Player);
        var mine = h.OwnedBy(1);
        Assert.NotEmpty(mine);
        var hosts = h.OwnedBy(0);
        Assert.NotEmpty(hosts);
        // The guest sees its own characters as its own.
        h.RunUntil(() => guest.World.Snapshot.Characters.Any(c => c.Owner == 1));

        var start = h.HostPosition(mine[0]);
        var target = start + new Vector3(400, 0, 0);
        guest.Say(new MoveOrder([mine[0]], target) { Tick = 0 });
        var hostStart = h.HostPosition(hosts[0]);
        guest.Say(new MoveOrder([hosts[0]], hostStart + new Vector3(0, 0, 400)) { Tick = 0 });
        h.Run(150);

        Assert.True(Vector3.Distance(h.HostPosition(mine[0]), target) < Vector3.Distance(start, target) - 200, $"{start} -> {h.HostPosition(mine[0])}");
        Assert.True(Vector3.Distance(h.HostPosition(hosts[0]), hostStart) < 1, "the host's character stayed");
        Assert.Equal([CommandGate.NotYours], guest.Errors);
        // The replica shows the move.
        var shown = guest.World.Snapshot.Characters.Single(c => c.Id == mine[0]);
        Assert.True(Vector3.Distance(shown.Position, h.HostPosition(mine[0])) < 50);
    }

    [Fact]
    public void The_guests_newest_snapshot_equals_what_the_host_published_for_it_at_that_tick()
    {
        using var h = SessionHarness.SyntheticTownHost();
        var guest = h.Join("Guest");
        guest.Say(new MoveOrder([h.OwnedBy(1)[0]], SyntheticTown.Centre + new Vector3(300, 0, 300)) { Tick = 0 });
        h.Run(90);

        var newest = guest.Client.LastAssembled;
        Assert.NotNull(newest);
        Assert.True(h.Published.TryGetValue((1, newest.Tick), out var expected), $"nothing published at tick {newest.Tick}");
        Assert.Equal(expected.Characters.Select(c => c.Id), newest.Characters.Select(c => c.Id));
        for (int i = 0; i < expected.Characters.Count; i++)
            Assert.True(Wire(expected.Characters[i]).DiffMask(Wire(newest.Characters[i])) == 0, $"{expected.Characters[i].Id} differs");
        Assert.Equal(expected.Characters.Select(c => c.Owner), newest.Characters.Select(c => c.Owner));
        Assert.Equal(expected.Characters.Select(c => c.Selected), newest.Characters.Select(c => c.Selected));
        Assert.Equal(expected.PlayerSquads.Select(s => (s.Id, s.Name)), newest.PlayerSquads.Select(s => (s.Id, s.Name)));
        Assert.Equal((expected.ActiveSquad, expected.PlayerFaction, expected.Money, expected.InCreation), (newest.ActiveSquad, newest.PlayerFaction, newest.Money, newest.InCreation));
        // The replica world shows it as the guest's own view.
        Assert.Equal(expected.ActiveSquad, guest.World.Player.Local.Squad);
    }

    [Fact]
    public void One_players_trade_conversation_and_selection_are_invisible_to_the_other()
    {
        var data = MultiplayerWindowsTests.Data();
        const float height = 150;
        var ground = new OpenGroundWalkability((_, _) => height);
        var dialogue = new DialogueSystem(data);
        var trade = new TradeSystem(data);
        var session = new WorldSession(Vector3.Zero, (-5000, -5000, 5000, 5000), (_, _) => height,
            simulation: new WorldSettings { Seed = 7, PublishSnapshots = true },
            systems: [new PlayerSystem(data), new MovementSystem(new PathService(ground, true)), dialogue, trade], walkability: ground);
        var world = session.World;
        world.Player.Faction = data.PlayerFaction;
        world.Player.Money = 1000;
        world.Fixtures.RegisterSite(world, data, 0);
        Squad NewSquad(int faction, int owner, string template)
        {
            var squad = new Squad { Id = world.Squads.NextId(), TemplateId = template, Faction = faction, Town = 0, HomeRadius = 10, Owner = owner };
            world.Squads.Add(squad);
            return squad;
        }
        CharacterId Spawn(string name, Vector2 at, Squad squad, bool player, string record)
        {
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = squad.Faction;
            cold.IsPlayer = player;
            cold.RecordId = record;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, height, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            return id;
        }
        var hiro = Spawn("Hiro", new(0, 0), NewSquad(data.PlayerFaction, 0, "p"), true, "");
        var kaiSquad = NewSquad(data.PlayerFaction, 1, "p");   // the guest will be player 1
        var kai = Spawn("Kai", new(400, 0), kaiSquad, true, "");
        var anna = Spawn("Anna", new(20, 0), NewSquad(data.FactionIndex("f-w"), 0, "t"), false, "npc-w");
        var seller = Spawn("Seller", new(400, 20), NewSquad(data.FactionIndex("f-w"), 0, "shop-w"), false, "");
        world.Commands.Enqueue(new FocusCommand(Vector3.Zero) { Tick = 0 });

        using var h = new SessionHarness(session, data.Db);
        h.Run(2);   // the shop fills
        var guest = h.Join("Guest");
        Assert.Equal(1, guest.Player);

        // The host selects Hiro and talks to Anna; the guest selects Kai and trades with the seller.
        world.Commands.Enqueue(new SelectCommand([hiro]) { Tick = world.Tick });
        world.Commands.Enqueue(new TalkOrder(hiro, anna) { Tick = world.Tick });
        guest.Say(new SelectCommand([kai]) { Tick = 0 });
        guest.Say(new OpenTrade(kai, seller) { Tick = 0 });
        h.RunUntil(() => guest.World.Snapshot.Trade is not null && h.HostSession.World.Snapshot.Conversation is not null);
        h.Run(10);

        var hostView = h.HostSession.World.Snapshot;
        var guestView = guest.World.Snapshot;
        Assert.Equal((anna, hiro), (hostView.Conversation!.Npc, hostView.Conversation.Player));
        Assert.Null(hostView.Trade);
        Assert.Equal((kai, seller), (guestView.Trade!.Customer, guestView.Trade.Trader));
        Assert.Null(guestView.Conversation);
        Assert.Equal([hiro], h.HostSession.World.Player.Local.Selection);
        Assert.Equal([kai], guest.World.Player.Local.Selection);
        Assert.Equal([hiro], hostView.Characters.Where(c => c.Selected).Select(c => c.Id));
        Assert.Equal([kai], guestView.Characters.Where(c => c.Selected).Select(c => c.Id));
        Assert.Empty(guest.Errors);
    }

    [Fact]
    public void A_guest_pauses_the_game_for_everyone_unless_only_the_host_may()
    {
        using (var h = SessionHarness.SyntheticTownHost())
        {
            var guest = h.Join("Guest");
            guest.Session.RequestClock(ClockRequest.TogglePause);
            h.RunUntil(() => guest.ClockChanges.Count > 0);
            Assert.Equal(0, h.HostSession.Simulation.Speed);
            Assert.Equal([(new ClockChange(1, ClockChangeKind.Paused, 1), "Guest")], h.HostClockChanges);
            Assert.Equal([(new ClockChange(1, ClockChangeKind.Paused, 1), "Guest")], guest.ClockChanges);
            long paused = h.HostSession.World.Tick;
            h.Run(30);
            Assert.Equal(paused, h.HostSession.World.Tick);
            Assert.True(guest.Session.Simulation.IsPaused);   // the guest's HUD shows it

            // The host resumes; the guest hears it under the host's name.
            h.HostSession.RequestClock(ClockRequest.TogglePause);
            h.RunUntil(() => guest.ClockChanges.Count > 1);
            Assert.Equal((new ClockChange(0, ClockChangeKind.Resumed, 1), "Hosty"), guest.ClockChanges[1]);
            Assert.False(guest.Session.Simulation.IsPaused);
        }

        using (var h = SessionHarness.SyntheticTownHost(new NetHostOptions { ClockPolicy = ClockPolicy.HostOnly }))
        {
            var guest = h.Join("Guest");
            guest.Session.RequestClock(ClockRequest.TogglePause);
            h.RunUntil(() => guest.Messages.Count > 0);
            Assert.Equal([NetHost.HostOnlyClock], guest.Messages);
            Assert.Equal(1, h.HostSession.Simulation.Speed);
            Assert.Empty(h.HostClockChanges);
        }
    }

    [Fact]
    public void A_guest_who_reconnects_keeps_its_number_and_squads()
    {
        using var h = SessionHarness.SyntheticTownHost();
        var guest = h.Join("Guest");
        var squads = SquadManagement.PlayerSquads(h.HostSession.World, 1).Select(s => s.Id).ToList();
        Assert.NotEmpty(squads);
        int all = h.HostSession.World.Squads.All.Count();

        guest.Client.Leave();
        h.Run(10);
        Assert.Equal(NetPlayerState.Away, h.Host.Roster.Single(e => e.Player == 1).State);
        Assert.Equal(squads, SquadManagement.PlayerSquads(h.HostSession.World, 1).Select(s => s.Id));

        var again = h.Join("Guest", guest.Id);
        h.Run(10);
        Assert.Equal(1, again.Player);
        Assert.Equal(squads, SquadManagement.PlayerSquads(h.HostSession.World, 1).Select(s => s.Id));
        Assert.Equal(all, h.HostSession.World.Squads.All.Count());
        Assert.Equal(NetPlayerState.Playing, h.Host.Roster.Single(e => e.Player == 1).State);
        // The new connection got a full snapshot: the guest's own characters are all there.
        Assert.All(h.OwnedBy(1), id => Assert.Contains(again.World.Snapshot.Characters, c => c.Id == id));
        Assert.Equal(squads[0], again.World.Player.Local.Squad);
    }

    [Fact]
    public void A_guest_joining_a_paused_game_gets_its_squad_without_the_game_running()
    {
        using var h = SessionHarness.SyntheticTownHost();
        h.Run(5);
        h.HostSession.RequestClock(ClockRequest.TogglePause);
        long tick = h.HostSession.World.Tick;
        var guest = h.Join("Guest");
        h.RunUntil(() => guest.World.Snapshot.Characters.Any(c => c.Owner == 1));

        Assert.Equal(tick, h.HostSession.World.Tick);
        Assert.NotEmpty(SquadManagement.PlayerSquads(h.HostSession.World, 1));
        Assert.True(h.HostSession.World.Player.Find(1)!.InCreation);
        Assert.True(guest.World.Snapshot.InCreation);
        Assert.True(guest.Session.Simulation.IsPaused);
    }
}
