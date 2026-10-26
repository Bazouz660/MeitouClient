using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// NPC squads running away from a lost fight (docs/simulation.md "Fleeing"; the original's facts in docs/game/perception.md "Fleeing"). All work is serial
/// (<c>SlowWorld</c>), in slot and squad-id order, so it is the same at any thread count.
/// <list type="bullet">
/// <item><b>When</b> (the original's squad-intact test, <c>FUN_14079acf0</c>; docs/game/perception.md "Fleeing"): every <see cref="CheckEveryTicks"/> ticks an NPC squad in a fight breaks
/// when, with fewer than <see cref="MinSquadForRatio"/> members, its leader is down, or, with more, those still able to fight are fewer than <see cref="IntactRatio"/> of the squad
/// (<see cref="LeaderDownRatio"/> with the leader down). The FACTION run-away ratios are loaded by the original but never read, and no enemy is counted.</item>
/// <item><b>How</b> (the original's <c>Task_Runaway</c>, Observed): each member still standing stops fighting and runs at full speed to a point
/// <see cref="FirstRun"/> + up to <see cref="RunSpread"/> units from the centre of the enemies it can see, on the far side of it; when it gets there (or within
/// <see cref="Arrived"/> of it, or its path fails) it runs <see cref="Hop"/> + up to <see cref="RunSpread"/> units in a random direction.</item>
/// <item><b>Until</b> (the task's end conditions, <c>isNoEnemiesInVicinity</c> and not <c>isAggressionTowardsMe</c>): no enemy standing within its sight range and
/// nobody attacking it. It then goes back to wandering. A fleeing character neither looks for fights (<see cref="AggroSystem"/>) nor answers attacks
/// (<see cref="RetaliationSystem"/>). The player's characters never flee by themselves.</item>
/// </list>
/// </summary>
public sealed class FleeSystem(FactionRelations relations, CombatSystem combat, float sightRange = AggroSystem.OutdoorSight) : ITickSystem, IStateHashed
{
    /// <summary>How often squads in a fight are checked, and fleeing characters tested for safety (engine choice, as retaliation's).</summary>
    public const int CheckEveryTicks = 15;
    /// <summary><c>Task_Runaway</c>'s first run: 2000 + 400 x a random number in [0, 1) from the threats' centre.</summary>
    public const float FirstRun = 2000, RunSpread = 400;
    /// <summary>Its later hops: 1000 + 400 x random, in a random direction, when the last one ended or is within 200 units of its end.</summary>
    public const float Hop = 1000, Arrived = 200;
    /// <summary>The squad-intact test's figures (<c>FUN_14079acf0</c>, <b>Verified</b> literals): squads of at least 4 compare those able to fight with 0.2 of the squad (0.4 with the leader down).</summary>
    public const int MinSquadForRatio = 4;
    public const float IntactRatio = 0.2f, LeaderDownRatio = 0.4f;

    public FactionRelations Relations { get; } = relations;
    /// <summary>The sight range R before the race multiplier, for the enemies counted and the safety test (the <see cref="AggroSettings.SightRange"/> of the game).</summary>
    public float SightRange { get; } = sightRange;
    /// <summary>The squads that broke so far.</summary>
    public long Broken { get; private set; }
    /// <summary>Characters that started running so far.</summary>
    public long Fled { get; private set; }

    readonly List<Squad> squads = [];
    readonly HashSet<int> attacked = [];
    readonly HashSet<int> seen = [];
    readonly List<int> standing = [];
    // The characters standing this check by faction (in slot order) and, per faction asked, the standing enemies of it: a battle of one faction, or of
    // factions at peace, needs no scans, and a few enemies in a big crowd are checked directly rather than by a grid scan over the whole crowd.
    readonly Dictionary<int, List<int>> standingBy = [];
    readonly List<int> standingFactions = [];
    readonly Dictionary<int, List<int>> enemiesOf = [];
    readonly Stack<List<int>> spare = [];
    /// <summary>Up to this many enemies standing, a scan tests each of them instead of querying the grid.</summary>
    const int DirectEnemies = 256;

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        bool check = world.Tick % CheckEveryTicks == 0;
        if (check) { Attacked(table); StandingFactions(table); }
        // Characters already running: the next hop, or the end when it is safe.
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive || table.Cold(i) is not { Fleeing: true } cold) continue;
            if (cold.Medical is { Incapacitated: true } || cold.IsPlayer) { cold.Fleeing = false; continue; }
            if (check && Safe(world, table, i))
            {
                cold.Fleeing = false;
                MovementSystem.Stop(table, i);
                n.Task = (byte)CharacterTask.Wander;
                n.TaskTime = 0;
                continue;
            }
            bool running = n.Task == (byte)CharacterTask.GoTo;
            if (running && Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), n.Goal) > Arrived * Arrived) continue;
            if (n.Task == (byte)CharacterTask.Attack) world.Defer(new StopCommand([new CharacterId(i, n.Generation)]) { Tick = world.Tick + 1 });
            Run(world, table, i, RandomDirection(world, i, 1), Hop);
        }
        if (!check) return;
        // Squads in a fight that break.
        squads.Clear();
        squads.AddRange(world.Squads.All);
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var squad in squads) Consider(world, table, squad);
    }

    void Consider(World world, CharacterTable table, Squad squad)
    {
        if (!Relations.Contains(squad.Faction)) return;
        int size = 0;
        bool fighting = false, leaderDown = true, held = false;
        standing.Clear();
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } c) continue;
            if (c.IsPlayer || c.Fleeing) return;
            size++;
            held |= c.IsImprisoned;
            bool down = c.Medical is { Incapacitated: true };
            if (m == squad.Leader) leaderDown = down;
            if (down || c.Fighter is null) continue;
            standing.Add(s);
            if (c.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack) fighting = true;
        }
        // A member in a cage or restraint keeps the squad intact (FUN_14079ab40, Verified; its other case, a member carried off by a non-ally, is not modelled).
        if (!fighting || standing.Count == 0 || held) return;
        // The squad-intact test (FUN_14079acf0, docs/game/perception.md "Fleeing"): under MinSquadForRatio members it holds while the leader stands; from that many,
        // while those able to fight are at least IntactRatio of the squad (LeaderDownRatio with the leader down). No enemy is counted.
        bool breaks = size < MinSquadForRatio ? leaderDown : standing.Count < size * (leaderDown ? LeaderDownRatio : IntactRatio);
        if (!breaks) return;
        Broken++;
        foreach (int s in standing)
        {
            var centre = Vector3.Zero;
            int k = 0;
            Enemies(world, table, s, null, (_, p) => { centre += p; k++; });
            var here = table.Next[s].Position;
            Vector2 away;
            if (k == 0) away = RandomDirection(world, s, 0);
            else
            {
                centre /= k;
                away = new Vector2(here.X - centre.X, here.Z - centre.Z);
                away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : RandomDirection(world, s, 0);
            }
            var from = k == 0 ? new Vector2(here.X, here.Z) : new Vector2(centre.X, centre.Z);
            ref var n = ref table.Next[s];
            table.Cold(s)!.Fleeing = true;
            if (n.Task == (byte)CharacterTask.Attack || table.Cold(s)!.InCombat) world.Defer(new StopCommand([new CharacterId(s, n.Generation)]) { Tick = world.Tick + 1 });
            Run(world, table, s, away, FirstRun, from);
            Fled++;
        }
    }

    struct Scan
    {
        public CharacterTable Table;
        public FactionRelations Relations;
        public int Self, Faction;
        public Vector3 Eye;
        public float Range, Limit;
        public HashSet<int>? Into;
        public Action<int, Vector3>? Each;
        public bool Any;
    }

    /// <summary>The enemies standing within slot <paramref name="s"/>'s sight range (no view cone: the original's "in the vicinity" is what the senses hold).</summary>
    bool Enemies(World world, CharacterTable table, int s, HashSet<int>? into, Action<int, Vector3>? each, float limit = float.MaxValue)
    {
        var cold = table.Cold(s)!;
        var scan = new Scan
        {
            Table = table, Relations = Relations, Self = s, Faction = cold.Faction, Eye = table.Next[s].Position,
            Range = AggroSystem.RangeOf(cold, SightRange), Limit = limit, Into = into, Each = each,
        };
        var enemies = EnemiesOf(cold.Faction);
        if (enemies.Count <= DirectEnemies) foreach (int e in enemies) Visit(e, ref scan);
        else world.Grid.Query(scan.Eye.X, scan.Eye.Z, scan.Range, ref scan, Visit);
        return scan.Any;
    }

    static void Visit(int slot, ref Scan s)
    {
        if (slot == s.Self || s.Into?.Count > s.Limit || !s.Table.Next[slot].Alive) return;
        var p = s.Table.Next[slot].Position;
        if (Vector3.DistanceSquared(p, s.Eye) > s.Range * s.Range) return;
        if (s.Table.Cold(slot) is not { Medical.Incapacitated: false } c || !AggroSystem.IsEnemy(s.Relations, s.Faction, c.Faction)) return;
        s.Any = true;
        s.Into?.Add(slot);
        s.Each?.Invoke(slot, p);
    }

    bool Safe(World world, CharacterTable table, int s) => !attacked.Contains(s) && (!AnyEnemy(table.Cold(s)!.Faction) || !Enemies(world, table, s, null, null));

    /// <summary>Whether a character standing anywhere this check (<see cref="StandingFactions"/>) is an enemy of <paramref name="faction"/>: when none is, no scan can find one.</summary>
    bool AnyEnemy(int faction) => EnemiesOf(faction).Count > 0;

    /// <summary>The characters standing this check whose faction is an enemy of <paramref name="faction"/>, by faction in order of first appearance, then slot.</summary>
    List<int> EnemiesOf(int faction)
    {
        if (enemiesOf.TryGetValue(faction, out var list)) return list;
        list = spare.Count > 0 ? spare.Pop() : [];
        foreach (int other in standingFactions)
            if (AggroSystem.IsEnemy(Relations, faction, other)) list.AddRange(standingBy[other]);
        enemiesOf[faction] = list;
        return list;
    }

    /// <summary>The characters standing (alive, not down) by faction, for <see cref="EnemiesOf"/>; once per check.</summary>
    void StandingFactions(CharacterTable table)
    {
        foreach (var l in standingBy.Values) { l.Clear(); spare.Push(l); }
        foreach (var l in enemiesOf.Values) { l.Clear(); spare.Push(l); }
        standingBy.Clear();
        enemiesOf.Clear();
        standingFactions.Clear();
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } c) continue;
            if (!standingBy.TryGetValue(c.Faction, out var l))
            {
                standingBy[c.Faction] = l = spare.Count > 0 ? spare.Pop() : [];
                standingFactions.Add(c.Faction);
            }
            l.Add(i);
        }
    }

    /// <summary>The slots some living character is fighting (the combat state as of this tick).</summary>
    void Attacked(CharacterTable table)
    {
        attacked.Clear();
        for (int i = 0; i < table.HighWater; i++)
        {
            ref readonly var n = ref table.Next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Attack) continue;
            var st = combat.StateOf(new CharacterId(i, n.Generation));
            if (st.TargetSlot >= 0) attacked.Add(st.TargetSlot);
        }
    }

    Vector2 RandomDirection(World world, int s, ulong salt)
    {
        var id = new CharacterId(s, world.Characters.Next[s].Generation);
        ulong roll = Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Flee, ((ulong)world.Tick << 2) | salt);
        // Task_Runaway draws x and z uniformly in [-1, 1] and normalises; a draw at the origin is replaced by +X.
        float x = Rng.Float(roll) * 2 - 1, z = Rng.Float(Rng.Mix(roll)) * 2 - 1;
        var v = new Vector2(x, z);
        return v.LengthSquared() > 1e-8f ? Vector2.Normalize(v) : Vector2.UnitX;
    }

    void Run(World world, CharacterTable table, int s, Vector2 direction, float distance, Vector2? from = null)
    {
        ref var n = ref table.Next[s];
        var id = new CharacterId(s, n.Generation);
        float r = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Flee, ((ulong)world.Tick << 2) | 2));
        var start = from ?? new Vector2(n.Position.X, n.Position.Z);
        MovementSystem.GoTo(table, s, start + direction * (distance + RunSpread * r), SpeedMode.Free);
    }

    public void Hash(ref StateHasher hasher)
    {
        // Only once something fled, so worlds without fleeing hash as before.
        if (Fled == 0) return;
        hasher.Add(Broken);
        hasher.Add(Fled);
    }
}
