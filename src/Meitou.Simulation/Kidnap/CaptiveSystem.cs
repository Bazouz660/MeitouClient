using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Kidnap;

/// <summary>Settings of the <see cref="CaptiveSystem"/>: engine choices unless a doc label says otherwise.</summary>
public sealed record CaptiveSettings
{
    /// <summary>The sweep for new jobs runs every this many ticks, starts at most <see cref="MaxNewJobs"/> and keeps at most <see cref="MaxJobs"/> running.</summary>
    public int SweepEveryTicks { get; init; } = 30;
    public int MaxJobs { get; init; } = 32;
    public int MaxNewJobs { get; init; } = 8;
    /// <summary>A worker is at its cage within the cage's <c>use range</c> plus this; a walk that ended within <see cref="Slack"/> counts too (a cage inside a building).</summary>
    public float Reach { get; init; } = 15;
    public float Slack { get; init; } = 200;
    /// <summary>Walks that end away from the cage before the job is given up.</summary>
    public int Walks { get; init; } = 3;
    /// <summary>Game seconds a job may last before it is given up.</summary>
    public float GiveUpSeconds { get; init; } = 600;
    /// <summary>The length of the kill clip <c>chop down static</c> (the system builder passes the data's), and the share of it at which its blow lands (<b>Observed</b>: 0.55).</summary>
    public float ChopSeconds { get; init; } = 1.5f;
    public float BlowAt { get; init; } = 0.55f;
    /// <summary>The slave processing kneels this long before the captive is stripped and enslaved (<b>Observed</b>: 3 s, <c>Task_ProcessNewSlave</c>).</summary>
    public float ProcessSeconds { get; init; } = 3;
    /// <summary>Watching slaves (183) looks at the cages within this distance of the watcher (<b>Observed</b>: 180, <c>FUN_1408cd630</c>).</summary>
    public float WatchRange { get; init; } = 180;
    /// <summary>The eater and its meal say their lines at most this often (game seconds); the original fires them every frame and its dialogue throttles them.</summary>
    public float BarkSeconds { get; init; } = 10;
    /// <summary>Keep a <see cref="CaptiveSystem.Log"/> (tests).</summary>
    public bool RecordLog { get; init; }
}

/// <summary>What a <see cref="CaptiveJob"/> does to its captive.</summary>
public enum CaptiveJobKind : byte
{
    /// <summary>Goals 135 / 282 by way of 134 (<c>Task_KillPrisoner</c>): blows until the captive dies.</summary>
    Kill,
    /// <summary>Goals 139 / 282 by way of 220 (<c>Task_EatPrisoner</c>): eaten part by part.</summary>
    Eat,
    /// <summary>Goals 183 / 260 by way of 182 (<c>Task_ProcessNewSlave</c>): stripped, dressed in slave clothing and enslaved.</summary>
    Process,
    /// <summary>Goals 173 / 217 / 245 by way of 174 (<c>Task_GetSlave</c>): taken out of the cage as the squad's slave.</summary>
    Gather,
    /// <summary>Goals 194 / 218: a delivered slave is put into a cage of the receiving town.</summary>
    Deliver,
    /// <summary>The slave goal 172 (<c>Task_UseCage</c>): a slave walks into a free cage of its owner.</summary>
    CageSelf,
}

public enum CaptivePhase : byte { Walk, Work }

/// <summary>One worker's job on one captive (serial state of the <see cref="CaptiveSystem"/>).</summary>
public sealed class CaptiveJob
{
    public required CaptiveJobKind Kind { get; init; }
    public required CharacterId Worker { get; init; }
    public required CharacterId Captive { get; init; }
    /// <summary>The cage the captive is in (or, for <see cref="CaptiveJobKind.Deliver"/> and <see cref="CaptiveJobKind.CageSelf"/>, the cage it goes into).</summary>
    public required string Cage { get; init; }
    public required int Faction { get; init; }
    /// <summary>The town the job is in (its kill timer), -1: none.</summary>
    public int Site { get; init; } = -1;
    public CaptivePhase Phase { get; set; }
    /// <summary>Game seconds in the phase and of the whole job.</summary>
    public float Time { get; set; }
    public float Age { get; set; }
    public int Walks { get; set; }
    /// <summary>Blows struck (kill); the game second of the next line (eat).</summary>
    public int Blows { get; set; }
    public float NextBark { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add((int)Kind); h.Add(Worker); h.Add(Captive); h.Add(Rng.StableHash(Cage)); h.Add(Faction); h.Add(Site);
        h.Add((int)Phase); h.Add(Time); h.Add(Age); h.Add(Walks); h.Add(Blows); h.Add(NextBark);
    }
}

public enum CaptiveEventKind { Started, Killed, Eaten, Enslaved, Gathered, Delivered, Caged, GaveUp }

public readonly record struct CaptiveEvent(long Tick, CaptiveEventKind Kind, CaptiveJobKind Job, CharacterId Worker, CharacterId Captive);

/// <summary>
/// What captors do with their captives (docs/simulation.md "Captives"; facts in docs/game/ai-tasks.md "Prisoner fate" and "Slave rows"). The original runs these as package goals planned
/// down to their tasks; Meitou runs each as a job of one worker, started by a sweep for every member of an AI squad whose current role list holds the goal in its hours and who is
/// free (the AI's, awake, not fighting, not in another job), worked serially in <c>SlowWorld</c> while the AI leaves the worker alone (<see cref="CharacterCold.Engaged"/>). The cages
/// a worker uses are those of the town its squad belongs to.
/// <list type="bullet">
/// <item><b>Kill</b> (135 "Kill random prisoner"; 282 "Dinner time" eats instead): wanted when the town's kill timer has run out or all the faction's cages there are full
/// (<b>Observed</b>, AI slot 20 <c>FUN_14032e9b0</c>; the timer is set to now + uniform[1, 15) game hours when the town is first asked about and whenever a captive dies of a
/// kill or a meal, <c>FUN_14034a810</c>). The target is the best occupied cage of the worker's faction (the cage finder's weights, <c>FUN_1408d0770</c>), its occupant alive and not a robot;
/// the occupant says event 10 MARKED_FOR_DEATH to the worker. Needs a weapon (B list hasWeaponEquipped). At the cage it plays <c>chop down static</c> and strikes once per clip at
/// 0.55 of it (<see cref="CombatSystem.Execute"/>), the occupant crying event 11 SCREAMING_TORTURE each time, until it dies.</item>
/// <item><b>Eat</b> (139 "Eat random prisoner", wanted always; 282 at the kill rate): the same target; the eater sheathes its weapon (B list !hasWeaponEquipped), plays <c>eat</c>,
/// says event 52 EATING_SOMETHING_SOUNDS while the captive says 11, and the captive is eaten (<see cref="MedicalState.BeingEaten"/>) until it dies.</item>
/// <item><b>Process</b> (260 "Watching for new slaves": an awake occupant of a cage of the town, not a slave, <c>FUN_1408cc110</c>; 183 "Watching slaves": an occupant within 180 that
/// is no slave of its own side, <c>FUN_1408cd630</c>; both only in cages of non-player factions): the worker kneels 3 s (<c>medic</c>), then strips the captive's weapons and armour
/// into its own inventory (locked shackles stay), dresses it in its faction's <c>slave clothing</c> where the slot is free, says event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE for
/// one that was a slave before) and makes it its faction's slave (<c>Task_ProcessNewSlave</c>, <b>Observed</b>).</item>
/// </list>
/// The jobs and the kill timers are not saved (the original keeps the timers in a global map it does not save, <b>Observed</b>); hashed only while any exist.
/// </summary>
public sealed partial class CaptiveSystem(PopulationData data, CombatSystem? combat, CaptiveSettings? settings = null) : ITickSystem, IStateHashed
{
    readonly List<CaptiveJob> jobs = [];
    readonly List<CaptiveEvent> log = [];
    // The kill timer of each town: the game hour from which a kill is wanted again (FUN_14034a810's map), by site.
    readonly SortedDictionary<int, double> killAt = [];
    readonly CharacterAssembly assembly = new(data);
    int timerRolls;

    public CaptiveSettings Settings { get; } = settings ?? new CaptiveSettings();
    /// <summary>The length of the kill clip in game seconds (the system builder sets the data's <c>chop down static</c>; <see cref="CaptiveSettings.ChopSeconds"/> without animations).</summary>
    public float ChopSeconds { get; set; } = (settings ?? new CaptiveSettings()).ChopSeconds;
    public IReadOnlyList<CaptiveJob> Jobs => jobs;
    public IReadOnlyList<CaptiveEvent> Log => log;
    /// <summary>The game hour from which the town <paramref name="site"/> wants a kill again (null: not asked yet).</summary>
    public double? KillTimer(int site) => killAt.TryGetValue(site, out double at) ? at : null;

    /// <summary>The cage finder's distance scale (the arrest system's, <see cref="FixtureRegistry.CageSearchScale"/>).</summary>
    const float CageScale = FixtureRegistry.CageSearchScale;
    const ulong TimerSalt = 0x4B696C6C54696D72;

    public void SlowWorld(World world)
    {
        for (int k = 0; k < jobs.Count;)
        {
            if (Step(world, jobs[k])) k++;
            else jobs.RemoveAt(k);
        }
        if (world.Tick % Math.Max(1, Settings.SweepEveryTicks) == 0) Sweep(world);
    }

    // ------------------------------------------------------------------ the sweep

    bool Busy(CharacterId id)
    {
        foreach (var j in jobs) if (j.Worker == id || j.Captive == id) return true;
        return false;
    }

    bool Taken(string cage)
    {
        foreach (var j in jobs) if (j.Cage == cage) return true;
        return false;
    }

    /// <summary>A worker the AI holds and nothing else: awake (not asleep or sitting), not fighting, in no job.</summary>
    bool Free(World world, int slot)
    {
        var table = world.Characters;
        return table.Next[slot].Alive && table.Cold(slot) is { } c && AiSystem.Holds(table.Next[slot], c) && table.Next[slot].Task != (byte)CharacterTask.Rest && !Busy(table.IdOf(slot));
    }

    void Sweep(World world)
    {
        var table = world.Characters;
        int hour = world.Hour, started = 0;
        foreach (var squad in world.Squads.All)
        {
            if (jobs.Count >= Settings.MaxJobs || started >= Settings.MaxNewJobs) return;
            if (squad.Ai is not { } board || squad.Members.Count == 0) continue;
            foreach (var member in squad.Members)
            {
                if (jobs.Count >= Settings.MaxJobs || started >= Settings.MaxNewJobs) return;
                if (!table.TryResolveNext(member, out int slot) || !Free(world, slot)) continue;
                var cold = table.Cold(slot)!;
                var roles = board.Roles[(int)AiSystem.RoleOf(squad, member, cold)];
                for (int g = 0; g < roles.Count; g++)
                {
                    if (!roles[g].InWindow(hour)) continue;
                    if (TryStart(world, squad, board, slot, cold, roles[g].Task.Type) is { } job)
                    {
                        jobs.Add(job);
                        cold.Engaged = true;
                        Note(world, CaptiveEventKind.Started, job);
                        started++;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>A job for goal <paramref name="type"/> of the member in <paramref name="slot"/>, when the goal is one of the captive goals and it finds a target (null otherwise).</summary>
    CaptiveJob? TryStart(World world, Squad squad, Blackboard board, int slot, CharacterCold cold, TaskType type)
    {
        int site = board.Town;
        switch (type)
        {
            case TaskType.KILL_A_RANDOM_CAGE_OCCUPANT:
                if (combat is null || cold.Fighter is not { Unarmed: false } || site < 0 || !KillDue(world, site, cold.Faction)) return null;
                return FateJob(world, slot, cold, board, site, CaptiveJobKind.Kill);
            case TaskType.EAT_A_RANDOM_CAGE_OCCUPANT:
                return site < 0 ? null : FateJob(world, slot, cold, board, site, CaptiveJobKind.Eat);
            case TaskType.EAT_A_RANDOM_CAGE_OCCUPANT_MEASURED_RATE:
                return site < 0 || !KillDue(world, site, cold.Faction) ? null : FateJob(world, slot, cold, board, site, CaptiveJobKind.Eat);
            case TaskType.NEW_SLAVE_PROCESSING:
            case TaskType.SLAVE_WATCHING:
                if (site < 0 || cold.Carrying is { IsNone: false }) return null;
                return ProcessTarget(world, slot, cold, site, watching: type == TaskType.SLAVE_WATCHING) is { } found
                    ? new CaptiveJob { Kind = CaptiveJobKind.Process, Worker = world.Characters.IdOf(slot), Captive = found.Occupant, Cage = found.Id, Faction = cold.Faction, Site = site }
                    : null;
            default:
                return TryStartLogistics(world, squad, board, slot, cold, type);
        }
    }

    CaptiveJob? FateJob(World world, int slot, CharacterCold cold, Blackboard board, int site, CaptiveJobKind kind)
    {
        if (FateTarget(world, slot, cold.Faction, site, board.OwnHome?.Key) is not { } cage) return null;
        var table = world.Characters;
        var worker = table.IdOf(slot);
        // The finder has the occupant say "marked for death" to the one who picked it (event 10, FUN_1408d0d90).
        world.Defer(new Dialogue.DialogueEvent(cage.Occupant, worker, MarkedForDeath) { Tick = world.Tick + 1 });
        if (kind == CaptiveJobKind.Eat) cold.DrawnWeapon = WeaponKinds.None;   // 220's B list: !hasWeaponEquipped
        return new CaptiveJob { Kind = kind, Worker = worker, Captive = cage.Occupant, Cage = cage.Id, Faction = cold.Faction, Site = site };
    }

    /// <summary>The ANIMATION records the jobs hold (<c>Task_KillPrisoner</c>, <c>Task_EatPrisoner</c>, <c>Task_ProcessNewSlave</c>, <b>Observed</b>).</summary>
    public const string ChopClip = "chop down static", EatClip = "eat", ProcessClip = "medic";

    /// <summary>EventTriggerEnum values the captive jobs fire (docs/game/dialogue.md "Enums").</summary>
    public const int MarkedForDeath = 10, ScreamingTorture = 11, IntroducingNewSlave = 35, RecapturedSlave = 37, SlaveDelivery = 39, EatingSounds = 52;

    // ------------------------------------------------------------------ finders

    /// <summary>The town's cages (registering its fixtures when nobody has yet).</summary>
    IEnumerable<Fixture> CagesOf(World world, int site) => CagesOf(world, data, site);

    static IEnumerable<Fixture> CagesOf(World world, PopulationData data, int site)
    {
        if (!world.Fixtures.IsRegistered(site)) world.Fixtures.RegisterSite(world, data, site);
        foreach (var f in world.Fixtures.All)
            if (f.Kind == FixtureKind.Cage && f.Site == site) yield return f;
    }

    /// <summary>The live character held in a cage, or -1.</summary>
    static int OccupantSlot(World world, Fixture f)
    {
        var table = world.Characters;
        if (FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { Medical: { Dead: false } }) return -1;
        return s;
    }

    /// <summary>
    /// The cage whose occupant a killer or eater takes (<c>FUN_1408d0d90</c> over the cage finder <c>FUN_1408d0770</c> in its occupied mode, <b>Observed</b>): of the occupied cages of
    /// <paramref name="faction"/> in the town (engine: the original also takes another faction's cage that a town test passes, <b>Unknown</b>), the one with the highest weight
    /// clamp(1 - d^2 / 4608^2, 1e-5, 1), x100 in the worker's home building; not one whose occupant is a robot (RACE <c>robot</c>), nor one another job works.
    /// </summary>
    Fixture? FateTarget(World world, int slot, int faction, int site, string? home)
    {
        var at = world.Characters.Next[slot].Position;
        Fixture? best = null;
        float bestWeight = 0;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner != faction || Taken(f.Id) || OccupantSlot(world, f) is not (>= 0 and var s) || world.Characters.Cold(s)!.Race is { IsRobot: true } || Busy(world.Characters.IdOf(s))) continue;
            float d2 = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            float w = Math.Clamp(1 - d2 / (CageScale * CageScale), 1e-5f, 1) * 100;
            if (home is not null && f.Building == home) w *= 100;
            if (w > bestWeight) (best, bestWeight) = (f, w);
        }
        return best;
    }

    /// <summary>All the cages of <paramref name="faction"/> in the town are held by live characters, and there is one (<c>FUN_1405a1ab0</c>, the state CAGES_ARE_ALL_FULL).</summary>
    public bool CagesFull(World world, int site, int faction) => CagesFull(world, data, site, faction);

    public static bool CagesFull(World world, PopulationData data, int site, int faction)
    {
        bool any = false;
        foreach (var f in CagesOf(world, data, site))
        {
            if (f.Owner != faction) continue;
            if (OccupantSlot(world, f) < 0) return false;
            any = true;
        }
        return any;
    }

    /// <summary>The kill rate (AI slot 20, <c>FUN_14032e9b0</c>): the town's timer has run out, or all the faction's cages there are full. The first ask sets the timer.</summary>
    bool KillDue(World world, int site, int faction)
    {
        if (!killAt.TryGetValue(site, out double at)) killAt[site] = at = NextKill(world, site);
        return world.GameHours >= at || CagesFull(world, site, faction);
    }

    /// <summary>now + uniform[1, 15) game hours (<c>FUN_14034a810</c>).</summary>
    double NextKill(World world, int site) =>
        world.GameHours + 1 + 14 * Rng.Float(Rng.Hash(world.Seed, (ulong)(uint)site, RngPurpose.Slow, TimerSalt + (ulong)timerRolls++));

    /// <summary>
    /// A captive a slaver processes (<c>FUN_1408cc110</c> for 260, <c>FUN_1408cd630</c> for 183, through <c>BuildingFinder_SlavesToProcess</c>, <b>Observed</b>): a cage of the town whose
    /// owner is not the player's faction and is the worker's or an ally of it (engine reading of the finder's town test), holding a live character no other job works. 260 takes an
    /// awake occupant that is no slave; 183 one within <see cref="CaptiveSettings.WatchRange"/> of the worker that is not already a slave of the worker's side (the original's
    /// <c>FUN_1408ca3d0</c> also re-processes a slave that does not look like one, by its clothing: not modelled).
    /// </summary>
    Fixture? ProcessTarget(World world, int slot, CharacterCold cold, int site, bool watching)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner < 0 || f.Owner == world.Player.Faction || f.Owner != cold.Faction && !Allied(cold.Faction, f.Owner) || Taken(f.Id)) continue;
            if (OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s))) continue;
            var c = table.Cold(s)!;
            if (watching)
            {
                if (Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z)) > Settings.WatchRange * Settings.WatchRange) continue;
                if (c.SlaveState == 1 && (c.SlaveOwner == cold.Faction || Allied(cold.Faction, c.SlaveOwner))) continue;
            }
            else if (c.SlaveState != 0 || c.Medical is { Incapacitated: true }) continue;
            return f;
        }
        return null;
    }

    bool Allied(int a, int b) => a >= 0 && b >= 0 && (a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b));

    // ------------------------------------------------------------------ one job

    /// <summary>One tick of a job; false when it is over.</summary>
    bool Step(World world, CaptiveJob j)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        j.Time += dt;
        j.Age += dt;
        if (!table.TryResolveNext(j.Worker, out int w) || table.Cold(w) is not { } wc || wc.Medical is { Incapacitated: true } || wc.InCombat || wc.Fleeing || !wc.CarriedBy.IsNone)
            return End(world, j, CaptiveEventKind.GaveUp);
        if (j.Age > Settings.GiveUpSeconds) return End(world, j, CaptiveEventKind.GaveUp);
        if (j.Kind is CaptiveJobKind.Gather or CaptiveJobKind.Deliver or CaptiveJobKind.CageSelf) return StepLogistics(world, j, w, wc);
        var cage = world.Fixtures.Find(j.Cage);
        if (cage is null || !table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { } cc || cc.InWhat != cage.Id || !cc.IsImprisoned || cc.Medical is not { Dead: false })
            return Over(world, j, table.TryResolveNext(j.Captive, out int gone) && table.Cold(gone)?.Medical is { Dead: true });
        if (j.Phase == CaptivePhase.Walk)
        {
            if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
            if (!arrived) return true;
            MovementSystem.Stop(table, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.ActionClip = j.Kind switch { CaptiveJobKind.Kill => ChopClip, CaptiveJobKind.Eat => EatClip, _ => ProcessClip };
        }
        // Facing the captive while it works.
        var to = table.Next[c].Position - table.Next[w].Position;
        if (to.X * to.X + to.Z * to.Z > 1e-4f) table.Next[w].Yaw = MathF.Atan2(to.X, to.Z);
        switch (j.Kind)
        {
            case CaptiveJobKind.Kill:
            {
                if (combat is null) return End(world, j, CaptiveEventKind.GaveUp);
                // One blow per chop clip, at 0.55 of it.
                if (j.Time < (j.Blows + Settings.BlowAt) * MathF.Max(ChopSeconds, 0.1f)) return true;
                j.Blows++;
                world.Defer(new Dialogue.DialogueEvent(j.Captive, j.Worker, ScreamingTorture) { Tick = world.Tick + 1 });
                bool died = combat.Execute(world, w, c, j.Blows);
                if (cc.Medical.Incapacitated) MovementSystem.Stop(table, c);
                return died ? Over(world, j, true) : true;
            }
            case CaptiveJobKind.Eat:
            {
                if (cc.Race is null || cc.Race.IsRobot) return End(world, j, CaptiveEventKind.GaveUp);
                if (j.Time >= j.NextBark)
                {
                    j.NextBark = j.Time + Settings.BarkSeconds;
                    world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, EatingSounds) { Tick = world.Tick + 1 });
                    world.Defer(new Dialogue.DialogueEvent(j.Captive, j.Worker, ScreamingTorture) { Tick = world.Tick + 1 });
                }
                return Eat(world, j, c, cc, dt) ? Over(world, j, true) : true;
            }
            case CaptiveJobKind.Process:
            {
                if (j.Time < Settings.ProcessSeconds) return true;
                Process(world, w, wc, c, cc);
                Note(world, CaptiveEventKind.Enslaved, j);
                return End(world, j, null);
            }
        }
        return true;
    }

    /// <summary>
    /// The walk to the cage: within its <c>use range</c> + <see cref="CaptiveSettings.Reach"/> it has arrived, or when a walk ended within <see cref="CaptiveSettings.Slack"/>; a walk that ended
    /// farther counts against <see cref="CaptiveSettings.Walks"/>. Its own faction's shut doors on the way are unlocked first (the lock rounds' "keys", engine choice). False: give up.
    /// </summary>
    bool Walk(World world, CaptiveJob j, int w, Fixture cage, out bool arrived)
    {
        var table = world.Characters;
        ref var n = ref table.Next[w];
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(cage.Position.X, cage.Position.Z));
        bool walking = n.Task == (byte)CharacterTask.GoTo;
        arrived = d <= cage.UseRange + Settings.Reach || !walking && j.Walks > 0 && d <= Settings.Slack;
        if (arrived || walking) return true;
        if (++j.Walks > Settings.Walks)
        {
            world.Fixtures.MarkUnreachable(world, cage, j.Faction);
            return false;
        }
        if (world.Fixtures.IsShut(cage.Building) && world.Fixtures.DoorOwner(cage.Building) == j.Faction) world.Fixtures.SetDoorsLocked(cage.Building, false);
        MovementSystem.GoTo(table, w, new Vector2(cage.Position.X, cage.Position.Z), SpeedMode.Walk);
        return true;
    }

    /// <summary>One update of a meal (<see cref="MedicalState.BeingEaten"/>, at the captive's own rate); true when the captive died of it or otherwise.</summary>
    bool Eat(World world, CaptiveJob j, int c, CharacterCold cc, float dt)
    {
        var table = world.Characters;
        var medical = cc.Medical!;
        var ctx = new MedicalContext(data.Bodies.Constants, data.BodyOptions, cc.Race!)
        {
            Toughness = cc.Stats?.Toughness ?? 0,
            Strength = cc.Stats?.Strength ?? 50,
            HasStats = cc.Stats is not null,
            Seed = world.Seed,
            CharacterKey = Rng.Key(table.IdOf(c)),
        };
        var events = new List<MedicalEvent>();
        float rate = MedicalState.EatRate(cc.Stats?.Toughness ?? 0, cc.Stats?.Scale ?? 1);
        bool eaten = medical.BeingEaten(rate, dt, ctx, data.BodyOptions.Dismemberment != Dismemberment.Never, events);
        foreach (var e in events)
        {
            if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) MovementSystem.Stop(table, c);
            else if (e.Kind == MedicalEventKind.PartSevered)
            {
                table.LimbsChanged.Enqueue((c, medical.Parts[e.Part].Template.LimbSlot));
                world.Defer(new Dialogue.DialogueEvent(j.Captive, CharacterId.None, medical.Parts[e.Part].Template.LimbSlot is 2 or 3 ? 70 : 71) { Tick = world.Tick + 1 });
            }
        }
        // "{1} has been eaten alive." for the player's own (FUN_140856bc0: of the player's faction, or of a squad that is).
        if (eaten && (cc.IsPlayer || world.Player.Faction >= 0 && cc.Faction == world.Player.Faction)) world.Post(Loc.Tr("{1} has been eaten alive.", cc.Name));
        return medical.Dead;
    }

    /// <summary>
    /// The processing of a new slave once the kneel is over (<c>Task_ProcessNewSlave</c>, <b>Observed</b>): the captive's weapons and armour go to the worker (its inventory, else the ground;
    /// engine choice for the original's storage), locked shackles stay on; its faction's <c>slave clothing</c> is put on where the slot is free; event 35, or 37 for one that was a
    /// slave before, from the worker to it; it becomes the worker's faction's slave.
    /// </summary>
    void Process(World world, int w, CharacterCold wc, int c, CharacterCold cc)
    {
        var table = world.Characters;
        bool before = cc.SlaveOwner >= 0 || cc.SlaveState != 0;
        if (cc.Inventory is { } inv)
        {
            foreach (var item in inv.Items.ToList())
            {
                if (item.LockedOn || item.Section is not ("back" or "hip" or "shirt" or "head" or "legs" or "boots" or "armour" or "belt")) continue;
                inv.Items.Remove(item);
                item.Section = "main";
                if (wc.Inventory is not { } into || !into.Add(item)) world.GroundItems.Drop(item, table.Next[c].Position, 0);
            }
            if (data.Factions is { } factions && (uint)wc.Faction < (uint)factions.Count && data.Db.Find(factions[wc.Faction].Id) is { } faction)
                foreach (var link in faction.GetReferences("slave clothing"))
                    if (data.Items.Create(link.TargetStringId, 1) is { } clothing) Items.InventoryCommands.TryEquip(inv, clothing);
            assembly.RefreshEquipment(cc);
        }
        assembly.RefreshEquipment(wc);
        world.Defer(new Dialogue.DialogueEvent(table.IdOf(w), table.IdOf(c), before ? RecapturedSlave : IntroducingNewSlave) { Tick = world.Tick + 1 });
        FixtureRegistry.Enslave(world, cc, wc.Faction);
    }

    /// <summary>The captive died or is gone: a death resets the town's kill timer (<c>FUN_140353990</c>, the end of both the kill and the meal).</summary>
    bool Over(World world, CaptiveJob j, bool died)
    {
        if (died && j.Site >= 0) killAt[j.Site] = NextKill(world, j.Site);
        return End(world, j, died ? j.Kind == CaptiveJobKind.Eat ? CaptiveEventKind.Eaten : CaptiveEventKind.Killed : CaptiveEventKind.GaveUp);
    }

    /// <summary>Ends a job: the worker goes back to the AI.</summary>
    bool End(World world, CaptiveJob j, CaptiveEventKind? kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(j.Worker, out int w) && table.Cold(w) is { } wc)
        {
            wc.Engaged = false;
            wc.ActionClip = null;
            if (table.Next[w].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, w);
        }
        if (kind is { } k) Note(world, k, j);
        return false;
    }

    void Note(World world, CaptiveEventKind kind, CaptiveJob j)
    {
        if (Settings.RecordLog) log.Add(new CaptiveEvent(world.Tick, kind, j.Kind, j.Worker, j.Captive));
    }

    public void Hash(ref StateHasher hasher)
    {
        if (jobs.Count == 0 && killAt.Count == 0) return;   // only once in use, so worlds without captives hash as before
        hasher.Add(0x43617074);
        hasher.Add(jobs.Count);
        foreach (var j in jobs) j.Hash(ref hasher);
        hasher.Add(killAt.Count);
        foreach (var (site, at) in killAt) { hasher.Add(site); hasher.Add(at); }
        hasher.Add(timerRolls);
    }
}
