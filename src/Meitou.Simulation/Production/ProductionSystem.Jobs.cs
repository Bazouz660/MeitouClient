using Meitou.Data.Gameplay.Ai;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Job lists (Perma-Jobs; docs/simulation.md "Production", the original's rules in docs/game/ai-tasks.md "Perma-jobs"): a player character whose task is idle or came from its list takes the
// first job of its list that is not blocked, so a higher job takes over as soon as it can run and a blocked one passes to the next. Meitou runs two types: Operating machine (87, the
// operator below) and Hauling to (124: a machine's output carried to the job's storage, CharacterTask.Haul). Blocked uses one test for starting and for going on.
public sealed partial class ProductionSystem
{
    /// <summary>How often (ticks) a character's job list is looked at, staggered by slot (engine choice; the original re-plans when its task ends or its commitment runs out).</summary>
    public const int JobCheckTicks = 10;

    void AddJobs(World world, AddJob add)
    {
        if (Site(world, add.Site) is null) return;
        var table = world.Characters;
        foreach (var id in Who(world, add.Characters))
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold) PermaJobs.Add(cold, add.Type, new PermaJob(add.Type, add.Site));
    }

    void AddOrderJobs(World world, AddOrderJob add)
    {
        var table = world.Characters;
        var ordered = add.Ordered;
        // A first aid on a robot is the original's robot repair order (60), whose job is Robotics.
        if (ordered == TaskType.FIRST_AID_ORDER && table.TryResolveNext(add.Target, out int t) && table.Cold(t)?.Race is { IsRobot: true }) ordered = TaskType.FIRST_AID_ROBOT;
        foreach (var id in Who(world, add.Characters))
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold) PermaJobs.Add(cold, ordered, PermaJob.Untargeted(ordered));
    }

    void RemoveJobAt(World world, RemoveJob remove)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(remove.Character, out int slot) || table.Cold(slot) is not { } cold || (uint)remove.Index >= (uint)cold.Jobs.Count) return;
        cold.Jobs.RemoveAt(remove.Index);
        if (cold.JobIndex == remove.Index) End(table, slot);
        else if (cold.JobIndex > remove.Index) cold.JobIndex--;
    }

    void MoveJobTo(World world, MoveJob move)
    {
        if (!world.Characters.TryResolveNext(move.Character, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        int count = cold.Jobs.Count;
        if ((uint)move.From >= (uint)count || (uint)move.To >= (uint)count || move.From == move.To) return;
        var job = cold.Jobs[move.From];
        cold.Jobs.RemoveAt(move.From);
        cold.Jobs.Insert(move.To, job);
        // The job the character is on keeps pointing at it.
        int k = cold.JobIndex;
        if (k == move.From) cold.JobIndex = move.To;
        else if (move.From < k && k <= move.To) cold.JobIndex--;
        else if (move.To <= k && k < move.From) cold.JobIndex++;
    }

    /// <summary>
    /// Once a tick (serial): characters due a look at their lists take the first job that can run, unless it is the one they are on; with none, an implicit job of the squad AI
    /// options (<see cref="ImplicitPick"/>).
    /// </summary>
    void PickJobs(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            var task = (CharacterTask)n.Task;
            // An order replaced the job's task.
            if ((cold.JobIndex >= 0 || cold.Implicit is not null) && task is not (CharacterTask.Operate or CharacterTask.Haul))
            {
                cold.JobIndex = -1;
                cold.Implicit = null;
                cold.HaulTo = null;
            }
            if (!cold.IsPlayer || (world.Tick + i) % JobCheckTicks != 0) continue;
            bool onJob = cold.JobIndex >= 0 || cold.Implicit is not null;
            // Idle as the medic job counts it: no task, or following or wandering.
            if (!onJob && task is not (CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) continue;
            if (!Able(cold)) continue;
            int pick = -1;
            for (int k = 0; k < cold.Jobs.Count && pick < 0; k++)
                if (Runnable(world, cold, k)) pick = k;
            if (pick >= 0)
            {
                if (pick != cold.JobIndex) StartJob(world, i, pick);
                continue;
            }
            if (ImplicitPick(world, i, onJob ? cold.ResumeTask : task) is { } chosen)
            {
                string? current = chosen.Type == TaskType.DITCH_ALL_RESOURCES ? cold.HaulTo : cold.OperateSite;
                if (chosen.Type != cold.Implicit || chosen.Site != current) StartImplicit(world, i, chosen.Type, chosen.Site);
                continue;
            }
            if (onJob) End(table, i);
        }
    }

    /// <summary>How far (X/Z) from where it idles a character sits down on a chair (engine choice: the original's sitter looks in its home town only).</summary>
    public const float SeatRange = 600;

    /// <summary>
    /// The implicit job of a character whose list has none to run (docs/game/ai-tasks.md "Perma-jobs": the original's lowest keys, in this order), with the jobs switch on and not a
    /// slave. <see cref="SquadAi.Ditch"/>: carried items a storage takes go there (242; only with no queued orders). <see cref="SquadAi.Sleep"/>: an injured character lies in a free
    /// bed until healed (153, then 257). <see cref="SquadAi.Sit"/>: a free chair (275). The one it is on is kept while it holds. Distances are from where it idles, its squad
    /// leader when it follows, so a follower gets up when the leader walks off.
    /// </summary>
    (TaskType Type, string Site)? ImplicitPick(World world, int slot, CharacterTask idle)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var options = world.Player.SquadAi;
        if ((cold.Orders & CharacterOrders.Jobs) == 0 || cold.SlaveState != 0 || options == SquadAi.None) return null;
        var at = table.Next[slot].Position;
        if (idle == CharacterTask.Follow && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId)?.Leader is { IsNone: false } leader && leader.Slot != slot && table.IsAlive(leader))
            at = table.Next[leader.Slot].Position;
        if ((options & SquadAi.Ditch) != 0)
        {
            if (cold.Implicit == TaskType.DITCH_ALL_RESOURCES && cold.HaulTo is { } keep) return (TaskType.DITCH_ALL_RESOURCES, keep);
            if (cold.OrderQueue.Count == 0 && cold.Inventory is { } inv
                && NearestSite(world, at, Settings.HaulRange, (_, m) => m.Data.IsStorage && m.Storage is not null && inv.All().Any(item => Takes(world, m, item))) is { } store)
                return (TaskType.DITCH_ALL_RESOURCES, store);
        }
        if ((options & SquadAi.Sleep) != 0 && Injured(cold))
        {
            if (cold.Implicit == TaskType.GO_HOME_AND_GO_TO_BED && cold.OperateSite is { } bed && Site(world, bed) is { } kept && Gap(kept.Position, at) <= Settings.HaulRange)
                return (TaskType.GO_HOME_AND_GO_TO_BED, bed);
            if (NearestSite(world, at, Settings.HaulRange, (s, m) => IsBed(m) && Free(world, cold, s, m)) is { } free) return (TaskType.GO_HOME_AND_GO_TO_BED, free);
        }
        if ((options & SquadAi.Sit) != 0)
        {
            if (cold.Implicit == TaskType.SIT_AROUND && cold.OperateSite is { } seat && Site(world, seat) is { } kept && Gap(kept.Position, at) <= SeatRange)
                return (TaskType.SIT_AROUND, seat);
            if (NearestSite(world, at, SeatRange, (s, m) => IsSeat(m) && Free(world, cold, s, m)) is { } free) return (TaskType.SIT_AROUND, free);
        }
        return null;
    }

    /// <summary>BF_BED and the skeleton bed (6, 25).</summary>
    static bool IsBed(Machine m) => m.Data.Function is TownBuildings.BedFunction or TownBuildings.SkeletonBedFunction;

    /// <summary>BF_CHAIR and BF_THRONE (17, 24; docs/game/ai-tasks.md "Town goals").</summary>
    static bool IsSeat(Machine m) => m.Data.Function is TownBuildings.ChairFunction or ThroneFunction;

    const int ThroneFunction = 24;

    /// <summary>Nobody else lies or sits on it.</summary>
    static bool Free(World world, CharacterCold cold, BuildSite site, Machine m) => OperatorsOf(world, site.Id) - (cold.OperateSite == site.Id ? 1 : 0) < m.Data.MaxOperators;

    /// <summary>A body part below full health (<b>engine choice</b>: the original's injury test is Unknown; its 257 is "get out of bed once healed").</summary>
    static bool Injured(CharacterCold cold) => cold.Medical is { Dead: false } medical && medical.Parts.Any(p => p.Fraction is >= 0 and < 1);

    /// <summary>The player's complete building nearest <paramref name="at"/> within <paramref name="range"/> whose machine passes <paramref name="ok"/> (ties: the lower site id).</summary>
    string? NearestSite(World world, Vector3 at, float range, Func<BuildSite, Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (Site(world, m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, at);
            if (gap > range || gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best) > 0) || !ok(site, m)) continue;
            best = m.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartImplicit(World world, int slot, TaskType type, string siteId)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var site = Site(world, siteId)!;
        var m = Ensure(world, site)!;
        var resume = cold.JobIndex >= 0 || cold.Implicit is not null ? cold.ResumeTask : (CharacterTask)table.Next[slot].Task;
        if (type == TaskType.DITCH_ALL_RESOURCES) StartHaulTrip(world, slot, site, m);
        else
        {
            Begin(world, slot, site, m);
            // A bed names no clip: the sleeper lies as on the floor (engine choice).
            if (IsBed(m)) cold.OperateClip = AnimationLibrary.AnimalLyingRecord;
        }
        cold.JobIndex = -1;
        cold.Implicit = type;
        cold.ResumeTask = resume;
    }

    /// <summary>A sitter or sleeper at its chair or bed: on the seat's origin facing its way, as the original's <c>Task_OperateMachine</c> (docs/game/ai-tasks.md "Town goals").</summary>
    static void Settle(ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m)
    {
        n.Position = site.Position;
        n.Yaw = TownBuildings.YawOf(site.Rotation);
        n.Velocity = default;
        m.Operators++;
        cold.Operating = true;
    }

    /// <summary>A job of the list that may run now: Meitou runs its type, the jobs switch is on (or the type ignores it), and it is not blocked.</summary>
    bool Runnable(World world, CharacterCold cold, int k)
    {
        var job = cold.Jobs[k];
        return PermaJobs.Runs(job.Type) && ((cold.Orders & CharacterOrders.Jobs) != 0 || PermaJobs.IgnoresSwitch(job.Type)) && !Blocked(world, cold, job);
    }

    /// <summary>
    /// Whether a job cannot run (the original skips such a job: an unmet start condition the planner cannot meet, or the target finder found nothing; docs/game/ai-tasks.md "Perma-jobs").
    /// Operating: the building is gone or not an operable machine, its operator places are taken by others, or it has no work (<see cref="HasWork"/>). Hauling to: the storage is gone, and
    /// the character carries nothing it takes and no machine within <see cref="ProductionSettings.HaulRange"/> has an output it takes.
    /// </summary>
    bool Blocked(World world, CharacterCold cold, PermaJob job)
    {
        if (job.Site is not { } id || Site(world, id) is not { Complete: true } site || Ensure(world, site) is not { } m) return true;
        switch (job.Type)
        {
            case TaskType.OPERATE_MACHINERY:
                if (!m.Data.Operable) return true;
                if (OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators) return true;
                return !HasWork(world, site, m, cold);
            case TaskType.OPERATE_STORAGE:
                if (!m.Data.IsStorage || m.Storage is null) return true;
                if (cold.Inventory is { } inv && inv.All().Any(i => Takes(world, m, i))) return false;
                return HaulSource(world, site, m) is null;
            default:
                return true;
        }
    }

    /// <summary>
    /// A machine an operator can work (the original's start conditions of task 87: output not full, inputs not empty, power; docs/game/ai-tasks.md): it has power, and a bench a queued
    /// research or craft; a farm a dead crop to clear or a ripe one to harvest; inputs that hold something or that the operator can fill (it carries the item or, with
    /// <see cref="ProductionSettings.AutoHaul"/>, a storage holds it); and room in the output or, with AutoHaul, a storage that takes it.
    /// </summary>
    bool HasWork(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        if (WorkFactor(world, m) <= 0) return false;
        if (m.Data.IsResearch) return world.Research.Queue.Count > 0;
        if (m.Farm is { } f)
        {
            if (f.Death >= m.Data.Farm!.DeathThreshold) return true;
            return f.Growth >= 1 && f.Harvested < f.Plants.Length && Room(world, site, m);
        }
        if (m.Craft is { Queue.Count: 0 }) return false;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0 || input.Level + input.Count > 0) continue;
            if (cold.Inventory is { } inv && Carried(inv, input.Item).Any()) continue;
            if (Settings.AutoHaul && NearestStorage(world, site, s => s.Storage!.Items.Any(i => i.Record == input.Item && i.Quantity > 0 && i.Section == "main")) is not null) continue;
            return false;
        }
        return m.Craft is not null || Room(world, site, m);
    }

    /// <summary>The output has room, or with AutoHaul a storage takes it.</summary>
    bool Room(World world, BuildSite site, Machine m) =>
        m.Output is { } o && (!o.Full || Settings.AutoHaul && items.Create(o.Recipe.Item, 1) is { } one && NearestStorage(world, site, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null);

    /// <summary>A carried item a "Hauling to" job takes to its storage: a loose stack of something a player machine makes (not the character's own kit), that the storage takes and has room for.</summary>
    static bool Takes(World world, Machine store, Items.ItemInstance item) =>
        item.Section is "main" or "backpack_content" && !item.LockedOn && item.Contents is null && store.Data.Accepts(item.Record, item.Type) && Fits(store.Storage!, item)
        && world.Machines.All.Any(s => !s.Data.IsStorage && s.Output?.Recipe.Item == item.Record);

    /// <summary>The player's complete machine (not a storage) nearest a storage within <see cref="ProductionSettings.HaulRange"/> whose output holds items the storage takes (ties: the lower site id).</summary>
    string? HaulSource(World world, BuildSite storage, Machine store)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (s.Data.IsStorage || s.Output is not { Count: > 0 } output || Site(world, s.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, storage.Position);
            if (gap > Settings.HaulRange || gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0)) continue;
            if (items.Create(output.Recipe.Item, 1) is not { } one || !store.Data.Accepts(one.Record, one.Type) || !Fits(store.Storage!, one)) continue;
            best = s.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartJob(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var job = cold.Jobs[k];
        var site = Site(world, job.Site!)!;
        var m = Ensure(world, site)!;
        if (cold.JobIndex < 0) cold.ResumeTask = (CharacterTask)table.Next[slot].Task;
        if (job.Type == TaskType.OPERATE_MACHINERY)
        {
            if (cold.OperateSite != site.Id) Begin(world, slot, site, m);
        }
        else StartHaulTrip(world, slot, site, m);
        cold.JobIndex = k;
    }

    /// <summary>A <see cref="CharacterTask.Haul"/> trip to a storage: what the character already carries for it goes there first; else it fetches a machine's output.</summary>
    void StartHaulTrip(World world, int slot, BuildSite storage, Machine store)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        End(table, slot);
        ref var n = ref table.Next[slot];
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Haul;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        bool carries = cold.Inventory is { } inv && inv.All().Any(i => Takes(world, store, i));
        cold.HaulSite = carries ? storage.Id : HaulSource(world, storage, store);
        cold.HaulTo = storage.Id;
        sweep = true;
    }

    /// <summary>A hauler at the end of a leg (serial): at the source it takes the output, then makes for the storage; at the storage it puts in what the storage takes and the job's trip ends.</summary>
    void HaulStep(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (cold.HaulTo is not { } storeId || cold.HaulSite is not { } at
            || Site(world, storeId) is not { Complete: true } || world.Machines.Find(storeId) is not { Storage: not null } store
            || Site(world, at) is not { Complete: true } atSite || world.Machines.Find(at) is not { } there || !Able(cold) || cold.Inventory is not { } inv)
        {
            End(table, slot);
            return;
        }
        if (!AtSite(n, atSite, there)) return;
        if (at != storeId)
        {
            if (there.Output is { Count: > 0 } output && items.Create(output.Recipe.Item, output.Count) is { } made && store.Data.Accepts(made.Record, made.Type))
            {
                int moved = Placed(inv, made);
                output.Count -= moved;
                world.Machines.Touch();
            }
            if (inv.All().Any(i => Takes(world, store, i))) cold.HaulSite = storeId;
            else End(table, slot);
            return;
        }
        foreach (var item in inv.All().Where(i => Takes(world, store, i)).ToList())
        {
            inv.Remove(item);
            if (store.Storage!.Add(item)) continue;
            if (!inv.Add(item)) world.GroundItems.Drop(item, store.Position, 0);
            break;
        }
        world.Machines.Touch();
        End(table, slot);
    }
}
