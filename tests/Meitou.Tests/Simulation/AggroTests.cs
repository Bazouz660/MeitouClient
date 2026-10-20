using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Faction relations driving fights: the relation table's rules, the auto-aggro, the leash, the player's self-defence and deeds.</summary>
public class AggroTests
{
    // Factions: 0 raiders, 1 villagers (raiders hate them), 2 nomads (raiders hate them but coexist), 3 friends (allied to the raiders),
    // 4 hermits (the raiders' own list says hostile, the hermits list the raiders as coexisting), 5 the player (Nameless, no table of its own),
    // 6 wildlife (not real), 7 townsfolk (neutral to everyone, the player's victims).
    const int Raiders = 0, Villagers = 1, Nomads = 2, Friends = 3, Hermits = 4, Player = 5, Wildlife = 6, Townsfolk = 7;

    static FactionRelations Relations()
    {
        FactionData F(string id, int def = 0, (string, int)[]? rel = null, string[]? coexist = null, bool notReal = false) => new()
        {
            Id = id, Name = id, DefaultRelation = def, NotReal = notReal,
            Relations = (rel ?? []).ToDictionary(r => r.Item1, r => r.Item2),
            Coexistence = coexist ?? [],
        };
        var table = FactionRelations.Build([
            F("raiders", 0, [("villagers", -80), ("nomads", -80), ("friends", 70), ("hermits", -80), ("Nameless", -60)], ["nomads"]),
            F("villagers"),
            F("nomads"),
            F("friends", 0, [("raiders", 70)]),
            F("hermits", 0, null, ["raiders"]),
            F("Nameless", 10, [("raiders", 40)]),
            F("wildlife", -100, notReal: true),
            F("townsfolk", 10),
        ]);
        table.PlayerFaction = Player;
        return table;
    }

    // ------------------------------------------------------------------ worlds

    sealed class Arena(SimWorld world, CombatSystem combat, AggroSystem aggro, FactionRelations relations) : IDisposable
    {
        public SimWorld World { get; } = world;
        public CombatSystem Combat { get; } = combat;
        public AggroSystem Aggro { get; } = aggro;
        public FactionRelations Relations { get; } = relations;
        public void Dispose() => World.Dispose();

        public Squad Squad(int faction, Vector2 home, params Vector2[] at) => FacingSquad(faction, home, null, at);

        /// <summary>A squad whose members face <paramref name="face"/> (null: yaw 0, along +Z); the original's NPCs see only ahead (the view cone) and hear only movers.</summary>
        public Squad FacingSquad(int faction, Vector2 home, Vector2? face, params Vector2[] at) =>
            SquadOf(faction, home, false, [.. at.Select(p => (p, face is { } f ? MathF.Atan2(f.X - p.X, f.Y - p.Y) : 0f))]);

        /// <summary>A squad with each member's position and yaw; <paramref name="idle"/> makes them stand (no wandering, so they make no noise).</summary>
        public Squad SquadOf(int faction, Vector2 home, bool idle, params (Vector2 At, float Yaw)[] members)
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = home, HomeRadius = 30 };
            World.Squads.Add(squad);
            foreach (var (p, yaw) in members)
            {
                var cold = new Combatant { Name = $"f{faction}-{squad.Members.Count}" }.MakeCold();
                cold.Faction = faction;
                cold.SquadId = squad.Id;
                bool leader = squad.Members.Count == 0;
                var id = World.Characters.Spawn(new CharacterHot
                {
                    Position = new Vector3(p.X, 150, p.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Yaw = yaw,
                    Task = (byte)(idle ? CharacterTask.Idle : leader ? CharacterTask.Wander : CharacterTask.Follow), TaskTime = 1,
                }, cold, 0);
                squad.Members.Add(id);
                if (leader) squad.Leader = id;
            }
            return squad;
        }

        public CharacterId PlayerCharacter(Vector2 at)
        {
            var cold = new Combatant { Name = "player" }.MakeCold();
            cold.Faction = Player;
            cold.IsPlayer = true;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        }

        public bool Fighting(CharacterId id) => World.Characters.Cold(id.Slot)!.InCombat;
    }

    static Arena Make(ulong seed, int threads, FactionRelations? relations = null, AggroSettings? settings = null, int minPartition = 1)
    {
        relations ??= Relations();
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true })
        {
            Relations = relations,
        };
        var aggro = new AggroSystem(relations, settings ?? new AggroSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat), aggro]);
        return new Arena(world, combat, aggro, relations);
    }

    /// <summary>
    /// Raiders between villagers (hostile), nomads (coexisting), hermits (coexisting from their side) and friends (allied), each a squad 150 units off.
    /// Returns the squads in that order.
    /// </summary>
    static (Arena Arena, Squad Raiders, Squad Villagers, Squad Nomads, Squad Hermits, Squad Friends) Crossroads(ulong seed, int threads, int minPartition = 1)
    {
        var a = Make(seed, threads, minPartition: minPartition);
        var o = new Vector2(2000, 2000);
        var raiders = a.Squad(Raiders, o, o, o + new Vector2(0, 12));
        var villagers = a.Squad(Villagers, o + new Vector2(150, 0), o + new Vector2(150, 0), o + new Vector2(160, 10));
        var nomads = a.Squad(Nomads, o + new Vector2(-150, 0), o + new Vector2(-150, 0), o + new Vector2(-160, 10));
        var hermits = a.Squad(Hermits, o + new Vector2(0, 150), o + new Vector2(0, 150));
        var friends = a.Squad(Friends, o + new Vector2(0, -150), o + new Vector2(0, -150));
        return (a, raiders, villagers, nomads, hermits, friends);
    }

    [Fact]
    public void Hostile_squads_in_sight_fight_and_nobody_else_does()
    {
        var (a, raiders, villagers, nomads, hermits, friends) = Crossroads(3, 1);
        using var _ = a;
        a.World.RunTicks(60);
        // The raiders started it: every logged fight is a raider attacking a villager, and both raiders took part (the second one joined).
        Assert.NotEmpty(a.Aggro.Log);
        Assert.All(a.Aggro.Log, e => { Assert.Equal(Raiders, e.AttackerFaction); Assert.Equal(Villagers, e.TargetFaction); });
        Assert.All(raiders.Members, m => Assert.True(a.Fighting(m), "both raiders fight"));
        // The villagers answer (retaliation), and nobody touches the nomads, hermits or friends.
        a.World.RunTicks(60);
        Assert.Contains(villagers.Members, a.Fighting);
        foreach (var m in nomads.Members.Concat(hermits.Members).Concat(friends.Members)) Assert.False(a.Fighting(m));
        foreach (var e in a.Combat.Log)
        {
            int fa = a.World.Characters.Cold(e.Attacker)!.Faction, fd = a.World.Characters.Cold(e.Defender)!.Faction;
            Assert.True((fa, fd) is (Raiders, Villagers) or (Villagers, Raiders), $"a blow between factions {fa} and {fd}");
        }
    }

    [Fact]
    public void The_players_assaults_turn_a_faction_hostile_with_the_games_message_and_its_people_then_attack()
    {
        using var a = Make(7, 1);
        var o = new Vector2(2000, 2000);
        var town = a.Squad(Townsfolk, o, o, o + new Vector2(30, 0));
        var watch = a.Squad(Townsfolk, o + new Vector2(200, 0), o + new Vector2(200, 0));
        var me = a.PlayerCharacter(o + new Vector2(60, 60));
        a.World.RunTicks(20);
        Assert.Empty(a.Aggro.Log);   // neutral: 10
        a.World.Commands.Enqueue(new AttackOrder([me], town.Members[0]) { Tick = a.World.Tick });
        a.World.RunTick();
        Assert.Equal(-10, a.Relations.Get(Townsfolk, Player));
        // Ordering the same attack again is the same assault.
        a.World.Commands.Enqueue(new AttackOrder([me], town.Members[0]) { Tick = a.World.Tick });
        a.World.RunTick();
        Assert.Equal(-10, a.Relations.Get(Townsfolk, Player));
        Assert.Empty(a.World.TakeMessages());
        // A second victim: -30, hostile, and the game's message.
        a.World.Commands.Enqueue(new AttackOrder([me], town.Members[1]) { Tick = a.World.Tick });
        a.World.RunTick();
        Assert.Equal(-30, a.Relations.Get(Townsfolk, Player));
        Assert.Equal(["townsfolk are now hostile towards you"], a.World.TakeMessages().Select(m => m.Text));
        // Now another squad of theirs, which nobody attacked (the victims' mates join them anyway), picks the fight with the player by itself.
        a.World.RunTicks(30);
        Assert.Contains(a.Aggro.Log, e => e.Attacker == watch.Members[0] && e.Target == me);
    }

    internal static List<ulong> Hashes(int threads, int minPartition = 1) => Hashes(threads, out _, minPartition);

    internal static List<ulong> Hashes(int threads, out (long RelationChanges, long Started, int Blows) seen, int minPartition = 1)
    {
        var (a, _, _, _, _, _) = Crossroads(11, threads, minPartition);
        using var _a = a;
        // A player character close to the raiders, who hate the player: it is attacked and defends itself; at tick 120 it is ordered onto a villager, an assault on the villagers.
        var me = a.PlayerCharacter(new Vector2(2000, 2080));
        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 30, 120, 400, 900 })
        {
            a.World.RunTicks(target - (int)a.World.Tick);
            if (target == 120) a.World.Commands.Enqueue(new AttackOrder([me], a.World.Squads.All.First(s => s.Faction == Villagers).Members[0]) { Tick = a.World.Tick });
            hashes.Add(a.World.StateHash());
        }
        seen = (a.Relations.Changes, a.Aggro.Started, a.Combat.Log.Count);
        return hashes;
    }

    [Fact]
    public void The_aggro_scenario_hashes_the_same_at_1_4_and_16_threads()
    {
        var one = Hashes(1, out var seen);
        // The scenario covers what it is for: fights started by hostility, blows, and a relation changed by the player's assault (so the table is hashed).
        Assert.True(seen.RelationChanges > 0 && seen.Started > 0 && seen.Blows > 0, $"{seen}");
        Assert.Equal(one, Hashes(4));
        Assert.Equal(one, Hashes(16));
        Assert.NotEqual(one[^1], one[^2]);
    }

    // ------------------------------------------------------------------ the AI's stances (docs/simulation.md "AI", Combat goals)

    static void Stance(Arena a, Squad squad, Meitou.Simulation.Ai.AiStance stance)
    {
        foreach (var m in squad.Members) a.World.Characters.Cold(m.Slot)!.Ai = new Meitou.Simulation.Ai.AiMind { Squad = squad, Stance = stance };
    }

    [Fact]
    public void A_territorial_stance_fights_enemies_inside_its_home()
    {
        var (a, raiders, _, _, _, _) = Crossroads(3, 1);
        using var _ = a;
        Stance(a, raiders, Meitou.Simulation.Ai.AiStance.Territorial);
        raiders.HomeRadius = 400;
        a.World.RunTicks(90);
        Assert.NotEmpty(a.Aggro.Log);
        Assert.All(a.Aggro.Log, e => Assert.Equal(Villagers, e.TargetFaction));
    }
}
