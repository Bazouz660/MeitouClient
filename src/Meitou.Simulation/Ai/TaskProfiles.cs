using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The scoring numbers of a goal type's TaskData (docs/game/ai-tasks.md "Desire and commitment", Observed): its desire, how long a character sticks with it once
/// chosen (game hours, <see cref="CommitMin"/> plus a uniform share of <see cref="CommitRange"/>, only when <see cref="Commits"/>), and its crowding factor.
/// </summary>
/// <param name="Desire">The desire: a fixed value (0 or more), <see cref="TaskProfiles.Random"/> (0.01 + 0.4 x a fresh roll) or <see cref="TaskProfiles.Special"/>
/// (a TaskData function of its own; the engine's rule for the behaviour).</param>
public readonly record struct TaskProfile(float Desire, float CommitMin = 0, float CommitRange = 0, bool Commits = false, float Crowd = 1);

/// <summary>The <see cref="TaskProfile"/> of every goal type the engine acts out (<see cref="TaskBehaviours"/>); other types get the default (special, no commitment, crowd 1).</summary>
public static class TaskProfiles
{
    public const float Random = -1, Special = -2;

    static readonly TaskProfile Default = new(Special);

    static readonly Dictionary<TaskType, TaskProfile> Table = new()
    {
        [TaskType.IDLE] = new(0.1f),
        [TaskType.WANDERER] = new(1),
        [TaskType.PATROL] = new(0.25f),
        [TaskType.PATROL_TOWN] = new(0.25f),
        [TaskType.WANDER_TOWN] = new(Random, 0.25f, 1.5f, true),
        [TaskType.RELAX_IN_TOWN_PACKAGE] = new(1),
        [TaskType.SHOPPING] = new(Special, 0.25f, 1.5f, true),
        [TaskType.STAND_AT_SHOPKEEPER_NODE] = new(0.5f),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING] = new(0.5f, 0.1f, 0.5f, true),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY] = new(0.5f, 0.1f, 0.5f, true),
        [TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE] = new(Random, 0.1f, 1, true),
        [TaskType.STAND_AT_GENERAL_NODE] = new(0.5f, 0.03f, 0.1f, true),
        [TaskType.HOLD_POSITION] = new(1),
        [TaskType.STAY_IN_HOME] = new(Random, 0.2f, 0.8f, true),
        [TaskType.SIT_AROUND] = new(Random, 0.2f, 0.8f, true),
        [TaskType.SIT_ON_THRONE] = new(Special, 0.2f, 0.8f, true),
        // Worker: AI slot 19 (1); only the pretend row commits, 0.5 + r x 2 hours (docs/game/ai-tasks.md "Desire and commitment").
        [TaskType.AUTO_LABOURING_MINES] = new(1),
        [TaskType.AUTO_LABOURING_MINES_PRETEND] = new(1, 0.5f, 2, true),
        [TaskType.MAN_THE_GATE] = new(1),
        [TaskType.FOLLOW_SLAVEMASTER] = new(1),
        [TaskType.BODYGUARD] = new(Special, Crowd: 0.9f),
        [TaskType.GO_HOME_AND_GO_TO_BED] = new(Special, 2, 8, true),
        [TaskType.GO_HOME_AND_GO_TO_BED_SECURE] = new(Special, 2, 8, true),
        [TaskType.RUN_AWAY_HOMETOWN] = new(Special, 0.05f, 0.15f),
    };

    // Flat by type number for the think's hot loop.
    static readonly TaskProfile[] Flat = BuildFlat();

    static TaskProfile[] BuildFlat()
    {
        var a = new TaskProfile[TaskBehaviours.TypeCount];
        Array.Fill(a, Default);
        foreach (var (type, p) in Table) a[(int)type] = p;
        return a;
    }

    public static TaskProfile Of(TaskType type) => (uint)type < (uint)Flat.Length ? Flat[(int)type] : Default;
}
