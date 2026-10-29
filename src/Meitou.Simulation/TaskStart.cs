using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation;

/// <summary>
/// The task repertoires a character plans with (<c>FUN_14060e840</c>, <b>Verified</b>, docs/game/ai-tasks.md "Planner"): the AI's mode flags are tested in the order 4 imprisoned,
/// 0x40 crippled, 0x10 chained, 1 human, 2 platoon, else 0x20 animal. Each <see cref="TaskDataRow.Category"/> is the set of repertoires that register the row.
/// </summary>
public static class Repertoire
{
    public const int Human = 1, Platoon = 2, Imprisoned = 4, Chained = 0x10, Animal = 0x20, Crippled = 0x40;

    /// <summary>
    /// The mode flag a cage sets on entry (<c>FUN_140330600</c>): <see cref="Chained"/> when the cage's functionality restrains movement (pole, peeler), else <see cref="Imprisoned"/>; 0 outside a cage.
    /// </summary>
    public static int Confinement(World world, CharacterCold cold) =>
        cold is { IsImprisoned: true, InWhat: { } cage } ? world.Fixtures.Find(cage) is { Restrains: true } ? Chained : Imprisoned : 0;

    /// <summary>
    /// The repertoire of a character, the first of: caged (<see cref="Imprisoned"/>), prone (<see cref="Crippled"/>: the posture setter <c>FUN_1405c7e20</c> sets the bit for posture 1 staying low
    /// and 2 crippled and clears it for the others, <b>Verified</b>; <see cref="Bodies.MedicalState.StayingLow"/>, <see cref="Bodies.MedicalState.Crawling"/>), restrained (<see cref="Chained"/>),
    /// a human (<see cref="Human"/>), else <see cref="Animal"/>. The platoon repertoire is not modelled.
    /// </summary>
    public static int Of(World world, CharacterCold cold)
    {
        int confined = Confinement(world, cold);
        if (confined == Imprisoned) return Imprisoned;
        if (cold.Medical is { Crawling: true } or { StayingLow: true }) return Crippled;
        if (confined == Chained) return Chained;
        return SquadRegistry.IsAnimal(cold) ? Animal : Human;
    }
}

/// <summary>Which source of the original's chooser (<c>AITaskSytem::chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Precedence") a task start stands in for.</summary>
public enum TaskSource : byte
{
    /// <summary>A player's order (the order queue).</summary>
    Order,
    /// <summary>A goal: Self preservation's or Hold's fight, a package goal's.</summary>
    Goal,
    /// <summary>A job of the character's list (<see cref="RunningTask.Job"/> its index), or a job pass.</summary>
    Job,
    /// <summary>An engine answer to an attack (<see cref="RetaliationSystem"/>).</summary>
    Reaction,
    /// <summary>An NPC's engine start (healing, leader rescue, looting, a kidnapper's or a guard's lift).</summary>
    Npc,
}

/// <summary>The result of <see cref="TaskStart.Start"/>.</summary>
public enum StartResult : byte
{
    Started,
    /// <summary>The character's <see cref="Repertoire"/> does not register the row; nothing changed.</summary>
    NotEligible,
}

/// <summary>Why a task ends (<see cref="ITaskHandler.End"/>).</summary>
public enum TaskEnd : byte
{
    /// <summary>Another task starts: the act-out lets go of what it holds; the new start sets the movement and the task.</summary>
    Replaced,
    /// <summary>The Stop order: the act-out lets go and the character stands, idle.</summary>
    Stopped,
}

/// <summary>
/// A task start (<see cref="TaskStart.Start"/>): the act-out <see cref="Task"/>, the original's <see cref="Row"/> it runs (the row the repertoire is tested on) and its <see cref="Source"/>, with
/// the job index, target character, site and point the act-out reads, and the task it goes back to when it ends by itself (<see cref="Resume"/>; engine, until a decision step decides again).
/// </summary>
public readonly record struct TaskRequest(CharacterTask Task, TaskType Row, TaskSource Source)
{
    public int Job { get; init; } = -1;
    public CharacterId Target { get; init; } = CharacterId.None;
    public string? Site { get; init; }
    public System.Numerics.Vector3 Point { get; init; }
    public CharacterTask Resume { get; init; }
}

/// <summary>
/// The provenance of a character's task (<see cref="CharacterCold.Running"/>), recorded by <see cref="TaskStart.Start"/>: the act-out it started (<see cref="Task"/>), the row, the source, the job
/// index, the target or site, and the tick. It speaks for the task only while <see cref="Task"/> is the character's task (<see cref="Runs"/>); a follow row's (31, 45, 289) is kept through
/// the fight that is a step of it (<see cref="TaskStart.Start"/>). Not saved (tasks are not); hashed only as the markers it replaced were.
/// </summary>
public sealed record RunningTask(CharacterTask Task, TaskType Row, TaskSource Source, int Job, CharacterId Target, string? Site, long Tick)
{
    /// <summary>Whether this is the provenance of <paramref name="task"/>, the character's task now.</summary>
    public bool Runs(CharacterTask task) => Task == task;
}

/// <summary>The act-out of one <see cref="CharacterTask"/>, registered by its owning system (<see cref="ITaskOwner"/>, <see cref="World.Tasks"/>). Serial phases only.</summary>
public interface ITaskHandler
{
    /// <summary>Starts the act-out: the start is eligible, the fight and the task before it have ended and the provenance is recorded.</summary>
    void Start(World world, int slot, in TaskRequest request);
    /// <summary>Ends the act-out <paramref name="task"/> of the character.</summary>
    void End(World world, int slot, CharacterTask task, TaskEnd reason);
    /// <summary>Whether the act-out has nothing left to do (the original's "plan finished" replan trigger): by default, once its system has taken the character off it.</summary>
    bool Done(World world, int slot, CharacterTask task) => world.Characters.Next[slot].Task != (byte)task;
}

/// <summary>A system that owns act-outs registers their handlers when the world is made.</summary>
public interface ITaskOwner
{
    void RegisterTasks(TaskHandlers tasks);
}

/// <summary>
/// The act-out handlers by <see cref="CharacterTask"/> (<see cref="World.Tasks"/>): an open list each owning system fills (<see cref="ITaskOwner"/>) and later code may add to or replace. A task with
/// no handler is started by setting it.
/// </summary>
public sealed class TaskHandlers
{
    readonly ITaskHandler?[] handlers = new ITaskHandler?[256];

    public void Register(CharacterTask task, ITaskHandler handler) => handlers[(byte)task] = handler ?? throw new ArgumentNullException(nameof(handler));

    public ITaskHandler? Of(CharacterTask task) => handlers[(byte)task];
}

/// <summary>The one task start and end path (docs/simulation.md "Task eligibility", "Task starts").</summary>
public static class TaskStart
{
    /// <summary>
    /// Whether the character's <see cref="Repertoire"/> registers the row (<c>Category &amp; repertoire != 0</c>). A type with no TaskData row (the 37 named-only types) has no
    /// registration to test and is allowed. Every start has a row: Meitou's <see cref="CharacterTask"/> is the act-out, the row the <see cref="TaskType"/> (a pick-up is 225 for the order, 148 for the rescue job).
    /// </summary>
    public static bool Eligible(World world, CharacterCold cold, TaskType row) =>
        TaskDataTable.Of(row) is not { } data || (data.Category & Repertoire.Of(world, cold)) != 0;

    /// <summary>
    /// Starts a task (serial phases only): the row must be <see cref="Eligible"/>; then a fight ends (a new task drops the fight task, docs/game/combat.md "Fight lifecycle and orders"),
    /// the task before ends through its handler, the provenance is recorded (<see cref="CharacterCold.Running"/>) and the act-out starts. A fight the character did not take up by an order
    /// or a job, while it runs a follow row (31, 45, 289), is a step of that row (31 and 45 need state 16, which 13 "Attacking attackers of" gives; docs/game/ai-tasks.md "Precedence"): the
    /// row's provenance is kept, so the follow is taken up again after it.
    /// </summary>
    public static StartResult Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException("tasks start in the serial phases only");
        var cold = table.Cold(slot)!;
        if (!Eligible(world, cold, request.Row)) return StartResult.NotEligible;
        var current = (CharacterTask)table.Next[slot].Task;
        if (request.Task != CharacterTask.Attack) world.Tasks.Of(CharacterTask.Attack)?.End(world, slot, CharacterTask.Attack, TaskEnd.Replaced);
        if (current != request.Task && current != CharacterTask.Attack) world.Tasks.Of(current)?.End(world, slot, current, TaskEnd.Replaced);
        cold.Running = cold.Running is { } running && Step(running, current, request)
            ? running with { Task = request.Task, Tick = world.Tick }
            : new RunningTask(request.Task, request.Row, request.Source, request.Job, request.Target, request.Site, world.Tick);
        if (world.Tasks.Of(request.Task) is { } handler) handler.Start(world, slot, request);
        else
        {
            ref var n = ref table.Next[slot];
            n.Task = (byte)request.Task;
            n.TaskTime = 0;
        }
        return StartResult.Started;
    }

    static bool Step(RunningTask running, CharacterTask current, in TaskRequest request) =>
        request.Task == CharacterTask.Attack && request.Source is TaskSource.Goal or TaskSource.Reaction && Production.PermaJobs.Follows(running.Row)
        && running.Runs(current) && current is CharacterTask.Follow or CharacterTask.Attack;

    /// <summary>
    /// Ends the character's task from outside its act-out (serial phases only): its fight and the task end through their handlers and the provenance is dropped; <see cref="TaskEnd.Stopped"/>
    /// then stands it, idle (<see cref="MovementSystem.Stop"/>).
    /// </summary>
    public static void End(World world, int slot, TaskEnd reason)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException("tasks end in the serial phases only");
        var current = (CharacterTask)table.Next[slot].Task;
        world.Tasks.Of(CharacterTask.Attack)?.End(world, slot, CharacterTask.Attack, reason);
        if (current != CharacterTask.Attack) world.Tasks.Of(current)?.End(world, slot, current, reason);
        if (reason == TaskEnd.Stopped) MovementSystem.Stop(table, slot);
        table.Cold(slot)!.Running = null;
    }
}
