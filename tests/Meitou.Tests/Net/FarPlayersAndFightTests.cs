using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Engine;
using Meitou.Engine.Time;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Tests.Combat;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Net;

/// <summary>
/// Two players a world apart and a fight both share, host and guests in one process (docs/net.md "Interest, delta, replicator and mirror", "Events";
/// docs/simulation.md "Multiplayer (host-authoritative co-op)", "Zones and campaigns").
/// </summary>
public class FarPlayersAndFightTests
{
    /// <summary>A second town of the synthetic faction, 21 000 units east of the first: four zones away, far outside each other's activation and interest.</summary>
    static readonly Vector3 FarTown = SyntheticTown.Centre + new Vector3(21000, 0, 0);

    static ViewState Above(Vector3 at) => new(at + new Vector3(0, 1500, 0), at, 8000);

    static bool Near(Vector3 a, Vector3 b, float range) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) < range;

    static SessionHarness FarTownsHost()
    {
        var far = SyntheticTown.Rec("32-t", FcsRecordType.TOWN, "Far end");
        far.Ints["type"] = 2;
        far.Floats["size radius"] = 300;
        far.Floats["town radius mult"] = 1;
        far.References["faction"] = [SyntheticTown.Ref("20-t")];
        far.References["residents"] = [SyntheticTown.Ref("10-t", 2, 0)];
        var db = SyntheticTown.Database(extra: [far]);
        return SessionHarness.SyntheticTownHost(database: db,
            towns: [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null), new TownPlacement("inst3", "32-t", FarTown, null)]);
    }

    [Fact]
    public void A_far_player_activates_its_own_zones_sees_its_own_surroundings_only_and_still_moves()
    {
        using var h = FarTownsHost();
        var world = h.HostSession.World;
        var population = h.Population!;
        h.Run(5);
        // The guest's squad stands by the far town when it signs in (a player coming back to a game where they left it there): the host already
        // knows their number and squad, so the join makes nothing new and the guest's camera starts there.
        var spot = FarTown + new Vector3(250, 0, 250);
        var guestId = Guid.NewGuid();
        world.Player.Join(1, guestId, "Far");
        population.AddPlayer(world, 1, NewGameStart.From(h.Database.Find("40-t")!), spot);
        var guest = h.Connect("Far", guestId);
        guest.View = Above(spot);
        h.RunUntil(() => guest.Client.HasPresented && guest.World.Snapshot.Characters.Any(c => c.Owner == 1));
        Assert.Equal(1, guest.Player);
        Assert.True(Near(guest.Reset!.Focus, spot, 200), $"the join focus {guest.Reset.Focus}");
        var mine = h.OwnedBy(1);
        var hosts = h.OwnedBy(0);
        Assert.NotEmpty(mine);
        Assert.NotEmpty(hosts);
        Assert.Single(SquadManagement.PlayerSquads(world, 1));
        h.Run(120);

        var home = h.HostPosition(hosts[0]);
        var there = h.HostPosition(mine[0]);
        Assert.True(Near(there, spot, 300), $"the guest's character at {there}");
        // The population keeps both places active and both towns made.
        Assert.Contains(WorldLayout.ZoneOf(home.X, home.Z), population.ActiveZones);
        Assert.Contains(WorldLayout.ZoneOf(there.X, there.Z), population.ActiveZones);
        Assert.NotEqual(WorldLayout.ZoneOf(home.X, home.Z), WorldLayout.ZoneOf(there.X, there.Z));
        Assert.Equal(SiteStatus.Loaded, population.StatusOf(0));
        Assert.Equal(SiteStatus.Loaded, population.StatusOf(1));

        List<CharacterSnapshot> Npcs(WorldSnapshot s, Vector3 at) => [.. s.Characters.Where(c => !c.IsPlayer && Near(c.Position, at, 3000))];
        var farNpcs = Npcs(world.Snapshot, FarTown);
        var homeNpcs = Npcs(world.Snapshot, SyntheticTown.Centre);
        Assert.NotEmpty(farNpcs);
        Assert.NotEmpty(homeNpcs);

        // The far town's residents live: over 20 s some of them walk.
        var before = farNpcs.ToDictionary(c => c.Id, c => c.Position);
        h.Run(600);
        int moved = before.Count(p => world.Characters.TryResolveNext(p.Key, out int slot) && Vector3.Distance(world.Characters.Previous[slot].Position, p.Value) > 20);
        Assert.True(moved > 0, $"none of {before.Count} residents of the far town moved");

        // Interest: the far guest holds the NPCs round its own characters and none of the host's; the host's own view still has its town.
        farNpcs = Npcs(world.Snapshot, FarTown);
        homeNpcs = Npcs(world.Snapshot, SyntheticTown.Centre);
        var seen = guest.World.Snapshot.Characters.Select(c => c.Id).ToHashSet();
        Assert.All(farNpcs, c => Assert.Contains(c.Id, seen));
        Assert.All(homeNpcs, c => Assert.DoesNotContain(c.Id, seen));
        Assert.All(hosts.Concat(mine), id => Assert.Contains(id, seen));   // every player character stays in interest
        var hostSeen = h.HostSession.World.Snapshot.Characters.Select(c => c.Id).ToHashSet();
        Assert.All(homeNpcs, c => Assert.Contains(c.Id, hostSeen));
        // The replica shows the far residents where the host has them.
        foreach (var c in farNpcs)
        {
            var shown = guest.World.Snapshot.Characters.Single(r => r.Id == c.Id);
            Assert.True(Vector3.Distance(shown.Position, c.Position) < 60, $"{c.Id} at {shown.Position}, host {c.Position}");
        }

        // The guest's order still moves its far character.
        var start = h.HostPosition(mine[0]);
        var target = start + new Vector3(0, 0, 400);
        guest.Say(new MoveOrder([mine[0]], target) { Tick = 0 });
        h.Run(150);
        Assert.True(Vector3.Distance(h.HostPosition(mine[0]), target) < Vector3.Distance(start, target) - 200, $"{start} -> {h.HostPosition(mine[0])}");
        var mineShown = guest.World.Snapshot.Characters.Single(c => c.Id == mine[0]);
        Assert.True(Vector3.Distance(mineShown.Position, h.HostPosition(mine[0])) < 60);
        Assert.Empty(guest.Errors);
    }

    sealed class Queues
    {
        public readonly ConcurrentQueue<SoundEvent> Sounds = new();
        public readonly ConcurrentQueue<BloodMark> Blood = new();
        public readonly ConcurrentQueue<Floater> Floaters = new();

        public void Install(Meitou.Simulation.World world)
        {
            world.Sounds = Sounds;
            world.BloodMarks = Blood;
            world.Floaters = Floaters;
        }
    }

    [Fact]
    public void A_fight_beside_a_guest_reaches_it_as_body_combat_state_and_events_and_a_far_guest_gets_none_of_the_npcs_events()
    {
        var data = MultiplayerWindowsTests.Data();
        const float height = 150;
        var ground = new OpenGroundWalkability((_, _) => height);
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
            new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true });
        var session = new WorldSession(Vector3.Zero, (-30000, -30000, 30000, 30000), (_, _) => height,
            simulation: new WorldSettings { Seed = 9, PublishSnapshots = true },
            systems: [new PlayerSystem(data), new PursuitSystem(combat), new MovementSystem(new PathService(ground, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat)],
            walkability: ground);
        var world = session.World;
        world.Player.Faction = data.PlayerFaction;
        Squad NewSquad(int faction, int owner)
        {
            var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "p", Faction = faction, HomeRadius = 10, Owner = owner };
            world.Squads.Add(squad);
            return squad;
        }
        CharacterId Spawn(Combatant who, Vector2 at, Squad squad, bool player)
        {
            var cold = who.MakeCold();
            cold.Faction = squad.Faction;
            cold.IsPlayer = player;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, height, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            return id;
        }
        var hiro = Spawn(new Combatant { Name = "Hiro" }, new(-60, 0), NewSquad(data.PlayerFaction, 0), true);
        var kai = Spawn(new Combatant { Name = "Kai", Defence = 5, Dodge = 5 }, new(0, -60), NewSquad(data.PlayerFaction, 1), true);   // the first guest
        var farAway = new Vector2(20000, 0);
        var ren = Spawn(new Combatant { Name = "Ren" }, farAway, NewSquad(data.PlayerFaction, 2), true);   // the second guest, far off
        var bandit = Spawn(new Combatant { Name = "Bandit", Attack = 90, WeaponSkill = 90 }, new(0, 60), NewSquad(data.FactionIndex("f-w"), 0), false);
        world.Commands.Enqueue(new FocusCommand(Vector3.Zero) { Tick = 0 });

        using var h = new SessionHarness(session, data.Db);
        var hostHeard = new Queues();
        hostHeard.Install(world);   // the host's own consumers: the tap hands them every event
        var near = h.Join("Near");
        var far = h.Join("Far");
        Assert.Equal((1, 2), (near.Player, far.Player));
        near.View = Above(Vector3.Zero with { Y = height });
        far.View = Above(new Vector3(farAway.X, height, farAway.Y));
        var nearHeard = new Queues();
        var farHeard = new Queues();
        nearHeard.Install(near.World);
        farHeard.Install(far.World);
        h.RunUntil(() => near.World.Snapshot.Characters.Any(c => c.Id == bandit) && far.World.Snapshot.Characters.Any(c => c.Id == ren));
        var kaiBefore = near.World.Snapshot.Characters.Single(c => c.Id == kai).Body!.Value;

        // The near guest starts it; the host's character joins in.
        near.Say(new AttackOrder([kai], bandit) { Tick = 0 });
        h.Run(5);
        world.Commands.Enqueue(new AttackOrder([hiro], bandit) { Tick = world.Tick });
        bool sawCombat = false;
        h.RunUntil(() =>
        {
            var shown = near.World.Snapshot.Characters.SingleOrDefault(c => c.Id == kai);
            sawCombat |= shown is { InCombat: true, WeaponDrawn: true };
            return sawCombat && shown?.Body is { } b && (b.Blood < kaiBefore.Blood || b.WorstPart < kaiBefore.WorstPart)
                && combat.Log.Any(e => e.Defender == bandit.Slot && e.Cut + e.Blunt + e.Pierce > 0);
        }, maxFrames: 3000);
        // Paused, nothing more happens; a second later every event the host sent is released to the replicas.
        h.HostSession.RequestClock(ClockRequest.TogglePause);
        h.Run(60);

        // The near guest's replica shows Kai hurt as the host has him, and the bandit hurt too.
        var hostKai = world.Snapshot.Characters.Single(c => c.Id == kai).Body!.Value;
        var shownKai = near.World.Snapshot.Characters.Single(c => c.Id == kai).Body!.Value;
        Assert.True(Math.Abs(hostKai.Blood - shownKai.Blood) < 0.02f && Math.Abs(hostKai.WorstPart - shownKai.WorstPart) < 0.02f, $"host {hostKai}, shown {shownKai}");
        var shownBandit = near.World.Snapshot.Characters.Single(c => c.Id == bandit);
        Assert.True(shownBandit.Body!.Value.WorstPart < 1, $"{shownBandit.Body}");

        // Every display event of the fight reached the near guest (all of it is within its event radius) in the host's order.
        Assert.Contains(hostHeard.Sounds, s => s.Kind == SoundKind.Hit && s.Subject == kai);
        Assert.Contains(hostHeard.Floaters, f => f.Target == kai);
        Assert.Contains(hostHeard.Floaters, f => f.Target == bandit);
        Assert.Contains(hostHeard.Blood, b => b.Who == bandit || b.Who == kai);
        Assert.Equal(hostHeard.Sounds.Select(s => (s.Kind, s.Subject, s.Other)), nearHeard.Sounds.Select(s => (s.Kind, s.Subject, s.Other)));
        Assert.Equal(hostHeard.Floaters.Select(f => (f.Target, f.Part)), nearHeard.Floaters.Select(f => (f.Target, f.Part)));
        Assert.Equal(hostHeard.Blood.Select(b => (b.Kind, b.Who)), nearHeard.Blood.Select(b => (b.Kind, b.Who)));
        Assert.All(nearHeard.Floaters, f => Assert.True(Vector3.Distance(f.Position, hostHeard.Floaters.First(x => x.Target == f.Target).Position) < 200));

        // The far guest is 20 000 units off: nothing about the NPC alone reaches it (player characters are in every guest's interest, so their events may).
        Assert.DoesNotContain(farHeard.Floaters, f => f.Target == bandit);
        Assert.DoesNotContain(farHeard.Blood, b => b.Who == bandit);
        Assert.DoesNotContain(farHeard.Sounds, s => s.Subject == bandit && s.Other != kai && s.Other != hiro);
        Assert.DoesNotContain(far.World.Snapshot.Characters, c => c.Id == bandit);
        Assert.Empty(near.Errors);
        Assert.Empty(far.Errors);
    }
}
