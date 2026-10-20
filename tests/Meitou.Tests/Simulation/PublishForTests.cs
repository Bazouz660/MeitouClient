using System.Collections;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Engine;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Per-viewer snapshots and replica worlds (docs/simulation.md "Per-viewer snapshots"): each player sees their own details, and publishing a view changes nothing.</summary>
public class PublishForTests
{
    static FcsReference Ref(string id) => new(id, 0, 0, 0);

    /// <summary>
    /// The start squad (player 0's: its first two members), a second squad of player 1's holding the rest, and a villager with a plain event-1 dialogue standing next to them; no town
    /// in reach. Bodies run 300 times fast, so a bandaged wound heals within seconds.
    /// </summary>
    sealed record Game(SimWorld World, Squad Ours, Squad Theirs, CharacterId Villager) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    static Game Start(bool publish = true)
    {
        var db = SyntheticTown.Database(anatomy: true, extra:
        [
            Rec("villager-pf", FcsRecordType.CHARACTER, r => { r.Name = "Villager"; r.References["race"] = [new FcsReference("1-t", 100, 0, 0)]; r.References["dialogue"] = [Ref("hello-pf")]; }),
            Rec("hello-pf", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("hello-l-pf")]),
            Rec("hello-l-pf", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Hello."),
        ]);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body | StandardParts.Dialogue,
            new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 }, bodyTimeScale: 300);
        var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = publish }, walk, systems.Systems);
        var ours = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.Player.Join(1, Guid.NewGuid(), "Guest");
        var at = world.Characters.Previous[ours.Leader.Slot].Position;
        var theirs = SquadManagement.NewSquad(world, db, at, "Nameless", owner: 1);
        foreach (var id in ours.Members.Skip(2).ToList())
        {
            ours.Members.Remove(id);
            theirs.Members.Add(id);
            world.Characters.Cold(id.Slot)!.SquadId = theirs.Id;
        }
        theirs.Leader = theirs.Members[0];
        int folk = data.FactionIndex("20-t");
        var home = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = folk, HomeCentre = new Vector2(at.X + 100, at.Z), HomeRadius = 10 };
        world.Squads.Add(home);
        var cold = new Meitou.Tests.Combat.Combatant { Name = "Villager" }.MakeCold();
        cold.Faction = folk;
        cold.RecordId = "villager-pf";
        cold.SquadId = home.Id;
        var villager = world.Characters.Spawn(new CharacterHot { Position = at + new Vector3(100, 0, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
        home.Members.Add(villager);
        home.Leader = villager;
        return new Game(world, ours, theirs, villager);
    }

    [Fact]
    public void Player_ones_view_holds_only_their_selection_and_squads()
    {
        using var g = Start();
        var (mine, guest) = (g.Ours.Members[0], g.Theirs.Members[0]);
        g.World.Commands.Enqueue(new SelectCommand([mine]) { Tick = 0 });
        g.World.Commands.Enqueue(new SelectCommand([guest]) { Tick = 0, Player = 1 });
        g.World.RunTick();

        var view = g.World.PublishFor(1);
        Assert.Equal(1, view.Viewer);
        Assert.Equal([guest], view.Characters.Where(c => c.Selected).Select(c => c.Id));
        Assert.Equal([guest], view.Characters.Where(c => c.Hud is not null || c.ItemGrid is not null || c.Skills is not null || c.HealthParts.Count > 0).Select(c => c.Id));
        Assert.Equal(g.Theirs.Id, view.ActiveSquad);
        Assert.Equal([g.Theirs.Id], view.PlayerSquads.Select(s => s.Id));

        // Player 0's snapshot is theirs alone, and a player without a session sees no one's details and gets no session.
        var own = g.World.Snapshot;
        Assert.Equal(0, own.Viewer);
        Assert.Equal([mine], own.Characters.Where(c => c.Selected).Select(c => c.Id));
        Assert.Equal([g.Ours.Id], own.PlayerSquads.Select(s => s.Id));
        var stranger = g.World.PublishFor(2);
        Assert.DoesNotContain(stranger.Characters, c => c.Selected || c.Hud is not null);
        Assert.Empty(stranger.PlayerSquads);
        Assert.Null(g.World.Player.Find(2));
    }

    [Fact]
    public void Owner_is_the_squad_owner_on_player_characters_and_none_on_npcs()
    {
        using var g = Start();
        g.World.RunTick();
        foreach (var view in new[] { g.World.Snapshot, g.World.PublishFor(1) })
        {
            Assert.All(g.Ours.Members, id => Assert.Equal(0, view.Characters.Single(c => c.Id == id).Owner));
            Assert.All(g.Theirs.Members, id => Assert.Equal(1, view.Characters.Single(c => c.Id == id).Owner));
            Assert.All(view.Characters.Where(c => !c.IsPlayer), c => Assert.Equal(-1, c.Owner));
            Assert.Contains(view.Characters, c => c.Id == g.Villager);
        }
    }

    [Fact]
    public void Player_zeros_view_equals_the_published_snapshot_but_for_talkable()
    {
        using var g = Start();
        g.World.Commands.Enqueue(new SelectCommand([g.Ours.Members[0]]) { Tick = 0 });
        g.World.Commands.Enqueue(new InspectCommand(g.Villager) { Tick = 0 });
        g.World.RunTicks(2);

        var snapshot = g.World.Snapshot;
        var view = g.World.PublishFor(0);
        Assert.Empty(snapshot.Talkable);
        Assert.Equal([g.Villager], view.Talkable);
        Assert.Contains(snapshot.Characters, c => c.Inspected && c.Id == g.Villager);
        AssertSame(snapshot, view with { Talkable = snapshot.Talkable }, "snapshot", 0);
    }

    [Fact]
    public void Publishing_views_queues_no_heal_floaters_and_leaves_the_state_hash_alone()
    {
        // The same game twice: one publishes every tick (the heal floaters ride on that), the other publishes nothing but both players' views every tick.
        using var published = Start(publish: true);
        using var viewed = Start(publish: false);
        var (publishedHeals, viewedHeals) = (new System.Collections.Concurrent.ConcurrentQueue<Floater>(), new System.Collections.Concurrent.ConcurrentQueue<Floater>());
        published.World.Floaters = publishedHeals;
        viewed.World.Floaters = viewedHeals;
        foreach (var g in new[] { published, viewed })
        {
            g.World.Commands.Enqueue(new SelectCommand([g.Ours.Members[0]]) { Tick = 0 });
            g.World.Commands.Enqueue(new SelectCommand([g.Theirs.Members[0]]) { Tick = 0, Player = 1 });
        }
        for (int tick = 0; tick < 400; tick++)
        {
            if (tick == 5)
                foreach (var g in new[] { published, viewed })
                {
                    var cold = g.World.Characters.Cold(g.Theirs.Members[0].Slot)!;
                    var ctx = new MedicalContext(GameConstants.Default, BodyOptions.Default, cold.Race!) { Toughness = 50, Strength = 50, Seed = 4, CharacterKey = Rng.Key(g.Theirs.Members[0]) };
                    cold.Medical!.ApplyHit(1, new HitDamage(30, 0, 0), ctx);
                    cold.Medical.Bandage(100, false, cold.Race!);   // a bandaged wound heals
                }
            published.World.RunTick();
            viewed.World.RunTick();
            viewed.World.PublishFor(0);
            viewed.World.PublishFor(1);
            Assert.Equal(published.World.StateHash(), viewed.World.StateHash());
        }
        Assert.Contains(publishedHeals, f => f.Kind == FloaterKind.Heal);
        Assert.DoesNotContain(viewedHeals, f => f.Kind == FloaterKind.Heal);
    }

    [Fact]
    public void A_replica_never_ticks_and_shows_the_hosts_snapshots()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        Assert.Throws<ArgumentException>(() => session.ReplaceWorld([new PlayerSystem()], w => w.MakeReplica(9), SessionMode.Replica));
        Assert.Throws<ArgumentException>(() => session.ReplaceWorld([], mode: SessionMode.Replica));
        Assert.Equal(SessionMode.Local, session.Mode);
        Assert.Throws<InvalidOperationException>(() => session.Present(WorldSnapshot.Empty, WorldSnapshot.Empty, 0, 0));

        var replica = session.ReplaceWorld([], w => w.MakeReplica(9), SessionMode.Replica);
        Assert.True(replica.IsReplica);
        Assert.Equal(9UL, replica.Seed);
        Assert.Equal(SessionMode.Replica, session.Mode);
        Assert.Throws<InvalidOperationException>(() => replica.RunTick());
        Assert.Throws<InvalidOperationException>(() => replica.ApplyPausedInterfaceCommands());
        Assert.Throws<InvalidOperationException>(() => session.AdvanceSimulation(1));
        Assert.True(replica.PreparePopulation(Vector3.Zero));
        session.Advance(0.5);   // control ticks only
        Assert.Equal(0, replica.Tick);

        var (hero, villager, stranger) = (new CharacterId(0, 1), new CharacterId(1, 1), new CharacterId(2, 1));
        CharacterSnapshot At(CharacterId id) => new(id, null, Vector3.Zero, 0, []);
        var current = new WorldSnapshot(42, [At(hero) with { Selected = true, IsPlayer = true, Owner = 3 }, At(villager) with { Inspected = true }, At(stranger)])
        {
            Viewer = 3, PlayerFaction = 2, Money = 1234, ActiveSquad = 7, InCreation = true, CreationRaces = ["race"], Talkable = new HashSet<CharacterId> { villager },
        };
        session.Present(WorldSnapshot.Empty, current, 0.25f, 1.5f);
        Assert.Equal((42L, 2, 1234L), (replica.Tick, replica.Player.Faction, replica.Player.Money));
        var local = replica.Player.Local;
        Assert.Equal([hero], local.Selection);
        Assert.Equal((villager, 7, true), (local.Inspected, local.Squad, local.InCreation));
        Assert.Equal(["race"], local.CreationRaces);
        Assert.True(replica.CanTalk(villager, hero));
        Assert.False(replica.CanTalk(stranger, hero));
        Assert.Equal((0.25f, 1.5f), (session.SimulationAlpha, session.MotionAlpha));
        Assert.Same(current, session.CurrentSnapshot);

        // Back to a local world: it ticks again and the alphas are the clock's.
        Assert.Throws<ArgumentException>(() => session.ReplaceWorld([], w => w.MakeReplica(1)));
        var local2 = session.ReplaceWorld([]);
        Assert.Equal(SessionMode.Local, session.Mode);
        Assert.False(local2.IsReplica);
        local2.RunTick();
        Assert.Throws<InvalidOperationException>(() => local2.MakeReplica(1));
    }

    /// <summary>Structural equality for snapshots: equal values, or sequences of the same items, or objects whose public properties are the same (<paramref name="path"/> names the place in a failure).</summary>
    static void AssertSame(object? expected, object? actual, string path, int depth)
    {
        if (Equals(expected, actual)) return;
        Assert.True(expected is not null && actual is not null && expected.GetType() == actual.GetType() && depth < 8, $"{path}: {expected} != {actual}");
        if (expected is IEnumerable a && actual is IEnumerable b && expected is not string)
        {
            var (left, right) = (a.Cast<object?>().ToList(), b.Cast<object?>().ToList());
            Assert.True(left.Count == right.Count, $"{path}: {left.Count} items != {right.Count}");
            for (int k = 0; k < left.Count; k++) AssertSame(left[k], right[k], $"{path}[{k}]", depth + 1);
            return;
        }
        var properties = expected!.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToList();
        Assert.True(properties.Count > 0, $"{path}: {expected} != {actual}");
        foreach (var p in properties) AssertSame(p.GetValue(expected), p.GetValue(actual), $"{path}.{p.Name}", depth + 1);
    }
}
