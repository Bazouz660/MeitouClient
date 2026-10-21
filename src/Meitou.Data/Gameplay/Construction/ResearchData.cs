using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>A RESEARCH record (docs/game/buildings-production.md "Research").</summary>
public sealed record ResearchData
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public int Level { get; init; }
    /// <summary><c>time</c>: the base hours before <c>research rate</c> and the level growth.</summary>
    public float Time { get; init; }
    public bool IsLevelUpgrade { get; init; }
    public bool BlueprintOnly { get; init; }
    /// <summary><c>requirements</c>: RESEARCH ids that must be finished, other records (buildings) that must be unlocked.</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];
    /// <summary><c>cost</c>: items and counts (v0) paid when it is queued.</summary>
    public IReadOnlyList<(string Item, int Count)> Cost { get; init; } = [];
    public IReadOnlyList<string> EnableBuildings { get; init; } = [];
    /// <summary><c>repeats</c>: how many more times it can be researched after the first (fcs.def).</summary>
    public int Repeats { get; init; }
    /// <summary><c>repeat mult</c>: the cost factor per repetition (<b>Observed</b>, <c>FUN_1408320a0</c>).</summary>
    public float RepeatMult { get; init; } = 1;
    /// <summary>The crafts it unlocks (<c>enable item</c>, <c>enable armour</c>, ...): kind, record and the repetition (v0, 0 = the first) that unlocks it.</summary>
    public IReadOnlyList<(CraftKind Kind, string Id, int Repetition)> EnableCrafts { get; init; } = [];

    /// <summary><c>improve buildings</c>: the BUILDINGs its <see cref="ProductionMult"/>, <see cref="PowerIncrease"/> and <see cref="CapacityIncrease"/> apply to (<b>Observed</b>, <c>FUN_140831a60</c>).</summary>
    public IReadOnlyList<string> ImproveBuildings { get; init; } = [];
    /// <summary><c>production mult</c>: replaces the listed buildings' multiplier (the last finished research wins).</summary>
    public float ProductionMult { get; init; } = 1;
    /// <summary><c>power increase</c> and <c>power capacity increase</c>: added to the listed buildings' <c>power output</c> and <c>power capacity</c>.</summary>
    public int PowerIncrease { get; init; }
    public int CapacityIncrease { get; init; }

    /// <summary>The <c>enable</c> list of each craft kind (fcs.def, <b>Verified</b>); weapon types and models both unlock weapons.</summary>
    public static readonly (string Field, CraftKind Kind)[] EnableFields =
    [
        ("enable item", CraftKind.Item), ("enable armour", CraftKind.Armour), ("enable weapon type", CraftKind.Weapon), ("enable weapon model", CraftKind.Weapon),
        ("enable crossbow", CraftKind.Crossbow), ("enable backpack", CraftKind.Backpack), ("enable robotics", CraftKind.Robotics),
    ];

    /// <summary>The cost of its repetition <paramref name="times"/> (0 the first): each count x <c>repeat mult</c>^times, rounded down (engine reading).</summary>
    public IEnumerable<(string Item, int Count)> CostAt(int times)
    {
        float f = MathF.Pow(RepeatMult, times);
        foreach (var (item, count) in Cost) yield return (item, Math.Max(1, (int)(count * f)));
    }

    /// <summary>
    /// The research hours (<b>Observed</b>, <c>FUN_14082ed10</c>): <c>time x research rate x level increase^(level - 1) / option</c>, the power by repeated multiplication (as the exe).
    /// </summary>
    public float Hours(GameConstants constants, float option = 1)
    {
        float hours = Time * constants.ResearchRate / option;
        for (int i = 1; i < Level; i++) hours *= constants.ResearchLevelIncreaseRate;
        return hours;
    }

    public static ResearchData From(GameRecord r) => new()
    {
        Id = r.StringId,
        Name = r.Name,
        Category = r.GetString("category"),
        Description = r.GetString("description"),
        Level = r.GetInt("level"),
        Time = r.GetInt("time"),
        IsLevelUpgrade = r.GetBool("is level upgrade"),
        BlueprintOnly = r.GetBool("blueprint only"),
        Requirements = [.. r.GetReferences("requirements").Select(l => l.TargetStringId)],
        Cost = [.. r.GetReferences("cost").Where(l => l.Values.Value0 > 0).Select(l => (l.TargetStringId, l.Values.Value0))],
        EnableBuildings = [.. r.GetReferences("enable buildings").Select(l => l.TargetStringId)],
        Repeats = Math.Max(0, r.GetInt("repeats")),
        RepeatMult = r.GetFloat("repeat mult", 1),
        ImproveBuildings = [.. r.GetReferences("improve buildings").Select(l => l.TargetStringId)],
        ProductionMult = r.GetFloat("production mult", 1),
        PowerIncrease = r.GetInt("power increase"),
        CapacityIncrease = r.GetInt("power capacity increase"),
        EnableCrafts = [.. EnableFields.SelectMany(e => r.GetReferences(e.Field).Select(l => (e.Kind, l.TargetStringId, l.Values.Value0)))],
    };

    /// <summary>Every RESEARCH record, in the database's order.</summary>
    public static List<ResearchData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.RESEARCH).Select(From)];
}
