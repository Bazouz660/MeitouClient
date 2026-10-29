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
    /// The repertoire of a character, the first of: caged (<see cref="Imprisoned"/>), crawling (<see cref="Crippled"/>: <see cref="Bodies.MedicalState.Crawling"/>, Q5, <b>Observed</b>), restrained
    /// (<see cref="Chained"/>), a human (<see cref="Human"/>), else <see cref="Animal"/>. The platoon repertoire is not modelled.
    /// </summary>
    public static int Of(World world, CharacterCold cold)
    {
        int confined = Confinement(world, cold);
        if (confined == Imprisoned) return Imprisoned;
        if (cold.Medical is { Crawling: true }) return Crippled;
        if (confined == Chained) return Chained;
        return SquadRegistry.IsAnimal(cold) ? Animal : Human;
    }
}

/// <summary>The one place a start asks whether a character may run a task row at all (docs/simulation.md "Task eligibility").</summary>
public static class TaskStart
{
    /// <summary>
    /// Whether the character's <see cref="Repertoire"/> registers the row (<c>Category &amp; repertoire != 0</c>). A type with no TaskData row (the 37 named-only types) has no
    /// registration to test and is allowed. Every start has a row: Meitou's <see cref="CharacterTask"/> is the act-out, the row the <see cref="TaskType"/> (a pick-up is 225 for the order, 148 for the rescue job).
    /// </summary>
    public static bool Eligible(World world, CharacterCold cold, TaskType row) =>
        TaskDataTable.Of(row) is not { } data || (data.Category & Repertoire.Of(world, cold)) != 0;
}
