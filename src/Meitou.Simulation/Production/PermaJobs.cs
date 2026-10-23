using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;

namespace Meitou.Simulation.Production;

/// <summary>
/// One entry of a character's job list (Perma-Jobs, docs/game/ai-tasks.md "Perma-jobs"): a task type and its target, a player building (<see cref="Construction.BuildSite.Id"/>) or, for a
/// job read from a save whose target Meitou cannot name (a character), the saved handle, kept so the job is written back as it was.
/// </summary>
public sealed record PermaJob(TaskType Type, string? Site, Hand Saved)
{
    public PermaJob(TaskType type, string site) : this(type, site, Hand.Null) { }

    /// <summary>A job without a target (the medic and rescue jobs).</summary>
    public static PermaJob Untargeted(TaskType type) => new(type, null, Hand.Null);
}

/// <summary>
/// The rules of adding a perma-job (<b>Verified</b>, the add function <c>FUN_1405086d0</c> and the TaskData rows of <c>FUN_140319a60</c>; docs/game/ai-tasks.md "Perma-jobs"): the ordered
/// type becomes its job type, a second job of a type is refused (or only for the same target when the type allows several), a companion job follows, and the jobs switch turns on.
/// </summary>
public static class PermaJobs
{
    /// <summary>A TaskData row's job fields: +4 <paramref name="Kind"/> (2: also runs before urgent tasks), +8 <paramref name="PerTarget"/>, +0xc <paramref name="Job"/>, +0x10 <paramref name="Companion"/> (0: none).</summary>
    readonly record struct Rule(int Kind, bool PerTarget, TaskType Job, TaskType Companion);

    static readonly Dictionary<TaskType, Rule> Rules = new()
    {
        [TaskType.OPERATE_MACHINERY] = new(1, true, 0, 0),
        [TaskType.PRETEND_TO_OPERATE_MACHINERY] = new(1, true, 0, 0),
        [TaskType.OPERATE_AUTOMATIC_MACHINERY] = new(1, true, TaskType.OPERATE_AUTOMATIC_MACHINERY, TaskType.OPERATE_STORAGE),
        [TaskType.OPERATE_STORAGE] = new(1, true, 0, 0),
        [TaskType.REPAIR] = new(1, false, TaskType.JOB_BUILDER, 0),
        [TaskType.JOB_BUILDER] = new(1, false, 0, 0),
        [TaskType.BUILD] = new(1, false, TaskType.JOB_BUILDER, 0),
        [TaskType.FIRST_AID_ORDER] = new(1, false, TaskType.JOB_MEDIC, 0),
        [TaskType.SPLINT_ORDER] = new(1, false, TaskType.SPLINT_JOB, 0),
        [TaskType.FIRST_AID_ROBOT] = new(1, false, TaskType.JOB_REPAIR_ROBOT, 0),
        [TaskType.JOB_MEDIC] = new(1, false, 0, 0),
        [TaskType.SPLINT_JOB] = new(1, false, 0, 0),
        [TaskType.JOB_REPAIR_ROBOT] = new(1, false, 0, 0),
        [TaskType.LOOT_TARGET] = new(1, false, TaskType.LOOT_ANIMALS_JOB, 0),
        [TaskType.LOOT_ANIMALS_JOB] = new(1, false, 0, 0),
        [TaskType.MELEE_ATTACK] = new(1, false, TaskType.ATTACK_ENEMIES, 0),
        [TaskType.USE_TURRET] = new(2, false, TaskType.MAN_A_TURRET_PLAYER_JOB, 0),
        [TaskType.MAN_A_TURRET_PLAYER_JOB] = new(2, true, 0, 0),
        [TaskType.ATTACK_ENEMIES] = new(1, false, 0, 0),
        [TaskType.BODYGUARD] = new(1, true, TaskType.BODYGUARD, TaskType.STAY_CLOSE_TO_TARGET),
        [TaskType.STAY_CLOSE_TO_TARGET] = new(1, true, 0, 0),
        [TaskType.Unnamed289] = new(1, true, 0, 0),
        [TaskType._SLAVE_OBEDIENCE] = new(1, false, 0, 0),
        [TaskType.FOCUSED_MELEE_ATTACK] = new(1, false, TaskType.ATTACK_ENEMIES, 0),
        [TaskType.UNPROVOKED_FOCUSED_MELEE_ATTACK] = new(1, false, TaskType.ATTACK_ENEMIES, 0),
        [TaskType.USE_TRAINING_DUMMY] = new(1, true, 0, 0),
        [TaskType.USE_BED] = new(1, false, TaskType.GO_HOME_AND_GO_TO_BED, 0),
        [TaskType.SLEEP_ON_FLOOR] = new(1, false, TaskType.GO_HOME_AND_GO_TO_BED, 0),
        [TaskType.SLEEP_ON_FLOOR_FAKE_AMBUSH] = new(1, false, TaskType.GO_HOME_AND_GO_TO_BED, 0),
        [TaskType.FIND_AND_RESCUE_IF_THERES_BEDS] = new(1, false, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, TaskType.FIND_BED_AND_PUT_IN),
        [TaskType.FIND_BED_AND_PUT_IN] = new(1, false, 0, 0),
        [TaskType.FEED_CORPSE_INTO_MACHINE] = new(1, true, TaskType.DEAD_GUYS_GO_IN_THE_POT, TaskType.FIND_A_DEAD_GUY),
        [TaskType.FIND_A_DEAD_GUY] = new(1, false, 0, 0),
        [TaskType.DEAD_GUYS_GO_IN_THE_POT] = new(1, true, TaskType.DEAD_GUYS_GO_IN_THE_POT, TaskType.FIND_A_DEAD_GUY),
        [TaskType.PICKUP_INTRUDERS] = new(1, false, TaskType.PICKUP_INTRUDERS, TaskType.TAKE_INTRUDER_OUTSIDE),
        [TaskType.PICKUP_INTRUDERS_TOWN] = new(1, false, TaskType.PICKUP_INTRUDERS_TOWN, TaskType.TAKE_INTRUDER_OUTSIDE_TOWN),
    };

    /// <summary>
    /// The types the production pick carries out from a list (<see cref="ProductionSystem"/>; the engineer job through the construction system). Others run elsewhere: medic 58,
    /// robotics 57 and splinting 250 in <see cref="FirstAid.FirstAidSystem"/>, rescue 148 and foraging 238 in <see cref="Carrying.CarrySystem"/>, attacking enemies 16 in
    /// <see cref="AggroSystem"/>. The rest are kept, shown and saved but never started.
    /// </summary>
    public static bool Runs(TaskType type) => type is TaskType.OPERATE_MACHINERY or TaskType.OPERATE_STORAGE or TaskType.JOB_BUILDER or TaskType.MAN_A_TURRET_PLAYER_JOB;

    /// <summary>
    /// Whether the job at <paramref name="k"/> of the list may start for a character on <paramref name="task"/> (the original's keys N+8, N+7, ... in list order, highest first;
    /// docs/game/ai-tasks.md "Perma-jobs"): it is free (no task of its own, wandering or following), on an implicit job, or on a list job further down; and the jobs switch is on
    /// or the type ignores it. False for k below 0 (no such job).
    /// </summary>
    public static bool FreeFor(CharacterCold cold, CharacterTask task, int k) =>
        k >= 0 && (task is CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow || cold.Implicit is not null || cold.JobIndex > k)
        && ((cold.Orders & CharacterOrders.Jobs) != 0 || IgnoresSwitch(cold.Jobs[k].Type));

    /// <summary>The index of the first job of <paramref name="type"/> in the list, -1 when none.</summary>
    public static int IndexOf(CharacterCold cold, TaskType type) => cold.Jobs.FindIndex(j => j.Type == type);

    /// <summary>
    /// The job an order of <paramref name="ordered"/> becomes (<b>Verified</b>: the row's +0xc when it has one), or null when it cannot be a job and the order runs once: the type has no row,
    /// or the type it becomes has none (beds become 153, which is no job).
    /// </summary>
    public static TaskType? JobOf(TaskType ordered)
    {
        if (!Rules.TryGetValue(ordered, out var rule)) return null;
        var type = rule.Job != 0 ? rule.Job : ordered;
        return Rules.ContainsKey(type) ? type : null;
    }

    /// <summary>
    /// Adds the job an order of <paramref name="ordered"/> on <paramref name="target"/> becomes to the end of <paramref name="jobs"/>, then its companion (unchecked, as the original), and turns
    /// the jobs switch on. False when the type is no job or the list refuses it: a job of the type is there and the type allows one (+8 clear), or the same job on the same target is.
    /// </summary>
    public static bool Add(CharacterCold cold, TaskType ordered, PermaJob target)
    {
        if (JobOf(ordered) is not { } type) return false;
        var rule = Rules[type];
        foreach (var job in cold.Jobs)
            if (job.Type == type && (!rule.PerTarget || SameTarget(job, target))) return false;
        cold.Jobs.Add(target with { Type = type });
        // The companion's own row decides nothing here; 31 becomes 289 under a character condition (vfunc +0x248) that is Unknown, so Meitou keeps 31.
        if (rule.Companion != 0) cold.Jobs.Add(target with { Type = rule.Companion });
        cold.Orders |= CharacterOrders.Jobs;
        return true;
    }

    static bool SameTarget(PermaJob a, PermaJob b) => a.Site is not null || b.Site is not null ? a.Site == b.Site : a.Saved == b.Saved;

    /// <summary>The types that run with the jobs switch off (<b>Verified</b>, <c>FUN_14050c1c0</c>): attacking enemies, staying close, bodyguard, robot repair, medic, splinting.</summary>
    public static bool IgnoresSwitch(TaskType type) =>
        type is TaskType.ATTACK_ENEMIES or TaskType.STAY_CLOSE_TO_TARGET or TaskType.BODYGUARD or TaskType.JOB_REPAIR_ROBOT or TaskType.JOB_MEDIC or TaskType.SPLINT_JOB;

    /// <summary>The row's display text (TaskData +0x138, <c>FUN_140319a60</c>) for the jobs list; null when Meitou does not know it.</summary>
    public static string? Text(TaskType type) => type switch
    {
        TaskType.OPERATE_MACHINERY or TaskType.PRETEND_TO_OPERATE_MACHINERY => "Operating machine",
        TaskType.OPERATE_AUTOMATIC_MACHINERY => "Operating automatic machine",
        TaskType.OPERATE_STORAGE => "Hauling to",
        TaskType.JOB_BUILDER => "Engineer",
        TaskType.JOB_MEDIC => "Medic",
        TaskType.SPLINT_JOB => "Splint Rigging",
        TaskType.JOB_REPAIR_ROBOT => "Robotics",
        TaskType.LOOT_ANIMALS_JOB => "Foraging animals",
        TaskType.MAN_A_TURRET_PLAYER_JOB => "Turret guard",
        TaskType.ATTACK_ENEMIES => "Attacking enemies",
        TaskType.BODYGUARD => "Bodyguard",
        TaskType.STAY_CLOSE_TO_TARGET or TaskType.Unnamed289 => "Staying close",
        TaskType._SLAVE_OBEDIENCE => "Obedient Slave",
        TaskType.USE_TRAINING_DUMMY => "Training",
        TaskType.FIND_AND_RESCUE_IF_THERES_BEDS => "Find and rescue",
        TaskType.FIND_BED_AND_PUT_IN => "Find and put in bed",
        TaskType.FIND_A_DEAD_GUY => "Find corpses",
        TaskType.DEAD_GUYS_GO_IN_THE_POT => "Corpse disposal",
        TaskType.PICKUP_INTRUDERS => "Remove Home Intruder",
        TaskType.PICKUP_INTRUDERS_TOWN => "Remove Intruder",
        _ => null,
    };
}
