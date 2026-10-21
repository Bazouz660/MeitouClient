namespace Meitou.Simulation.Carrying;

/// <summary>
/// Orders one of the carriers (the selection when the list is empty) to pick <see cref="Target"/> up: the nearest one that can carry walks to it and lifts it onto its left shoulder
/// (the original's <c>LIFT_PERSON_PLAYER_ORDER</c>, context menu verb "Pick Up"). The target must be knocked out or dead, of a <c>carriable</c> race and not carried already.
/// </summary>
public sealed record PickUpOrder(IReadOnlyList<CharacterId> Carriers, CharacterId Target) : SimCommand;

/// <summary>Orders the carriers (the selection when the list is empty) that carry someone to set it down where they stand (the original's <c>PUT_DOWN_OBJECT</c>, "Put Down").</summary>
public sealed record PutDownOrder(IReadOnlyList<CharacterId> Carriers) : SimCommand;

/// <summary>
/// Orders one of the looters (the selection when the list is empty) to loot <see cref="Target"/>, a knocked-out or dead character: the nearest walks to it and takes everything from
/// its inventory and backpack that fits its own (the loot window's "take all"; worn gear stays). Looting someone of another faction in sight of its members is a theft.
/// </summary>
public sealed record LootOrder(IReadOnlyList<CharacterId> Looters, CharacterId Target) : SimCommand;

/// <summary>
/// As <see cref="LootOrder"/>, but in reach the looter opens <see cref="Target"/>'s inventory in a window (<see cref="PlayerState.OpenLoot"/>) instead of taking all: items are then taken one at a
/// time (<see cref="TakeLootItem"/>). Closed with <see cref="Items.CloseContainer"/>, or when the looter walks off, goes down, or the body comes round or is lifted.
/// </summary>
public sealed record OpenLootOrder(IReadOnlyList<CharacterId> Looters, CharacterId Target) : SimCommand;

/// <summary>
/// Takes the item at <see cref="Index"/> of the open loot's inventory (<see cref="Items.Inventory.Items"/> order) into the looter's (as <see cref="Items.Inventory.Add"/>; what does not fit stays);
/// a theft as a loot is (<see cref="Crime.CrimeSystem.IsLootTheft"/>).
/// </summary>
public sealed record TakeLootItem(int Index) : SimCommand;

/// <summary>Puts the looter's (<see cref="Character"/>, the one who opened it) <see cref="Item"/> into the open loot's main grid at cell (<see cref="X"/>, <see cref="Y"/>), which must be free; worn and bag items are refused (as a container's put).</summary>
public sealed record PutLootItem(CharacterId Character, Items.InventoryItemToken Item, int X, int Y) : SimCommand;

/// <summary>
/// Orders one of the attackers (the selection when the list is empty) to knock <see cref="Target"/> out from behind (the original's order 228, verb "Stealth KO", <c>Task_StealthKO</c>):
/// the nearest sneaking one that can act creeps up behind it and swings; the roll (<see cref="CarrySystem.StealthKoChance"/>) decides between a knock-out and a fight.
/// </summary>
public sealed record StealthKoOrder(IReadOnlyList<CharacterId> Attackers, CharacterId Target) : SimCommand;

/// <summary>A stealth knock-out swing that was rolled: the target was knocked out (<see cref="Success"/>) or turned on the attacker.</summary>
public readonly record struct StealthKoEntry(long Tick, CharacterId Attacker, CharacterId Target, float Chance, bool Success);

/// <summary>
/// The Rescue button for the characters (the selection when the list is empty), the original's order 148 <c>FIND_AND_RESCUE_IF_THERES_BEDS</c> (NUM8; docs/game/ai-tasks.md
/// "Perma-jobs"): with <see cref="Job"/> (Shift) it is added to their job lists (with its companion 106), where an idle character picks up the nearest downed member of
/// its faction by itself; else each looks for one once.
/// </summary>
public sealed record RescueJobCommand(IReadOnlyList<CharacterId> Characters, bool Job = true) : SimCommand;

/// <summary>The numbers of <see cref="CarrySystem"/>: the clip fractions are the original's (<b>Observed</b>), the rest engine choices (docs/game/carry-loot.md, docs/simulation.md "Carrying").</summary>
public sealed class CarrySettings
{
    /// <summary>The carrier lifts or loots from this far (X/Z units); the walk there ends here (the first aid reach).</summary>
    public float Reach { get; init; } = 14;
    /// <summary>Ticks between the rescuers' looks for a downed character (the retaliation and first aid rhythm).</summary>
    public int AutoEveryTicks { get; init; } = 15;
    /// <summary>NPC squads pick up their knocked-out leader (the original's <c>FIND_AND_RESCUE_LEADER</c>, in 7 AI packages): a stand-in until the AI packages exist.</summary>
    public bool RescueNpcLeaders { get; init; } = true;
    /// <summary>The daylight sight R before the race's <c>vision range mult</c>: how far a rescuer looks for a downed character (the original searches its sensed-character list, whose size is <b>Unknown</b>) and how far a witness sees a looter.</summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>A body that is put down is placed this far beside its carrier (engine choice).</summary>
    public float PutDownSide { get; init; } = 3;

    /// <summary>The fraction of the lift clip at which the body is attached (<b>Observed</b>, <c>Task_LiftSomething</c> update: progress above 0.4).</summary>
    public const float AttachAt = 0.4f;
    /// <summary>The fraction at which the lift is finished (<b>Observed</b>: 0.95).</summary>
    public const float DoneAt = 0.95f;
    /// <summary>Putting down plays the clip backwards and lets go below this fraction (<b>Observed</b>, <c>Task_PutDownObject</c>: 0.2).</summary>
    public const float ReleaseBelow = 0.2f;
}

/// <summary>What one loot took: the items moved into the looter's inventory, the ones that did not fit and stay, and whether a member of the victim's faction saw it.</summary>
public readonly record struct LootEntry(long Tick, CharacterId Looter, CharacterId Victim, int Taken, int Left, bool Witnessed);

/// <summary>
/// Where a carried body is drawn and posed: it has its carrier's position and facing, and hangs this far from it, up to the shoulder and to the left (engine stand-in: the
/// original hangs it from the carrier's attach bone; the numbers are a guess). The host draws it there and the ragdoll's pinned bones follow it there.
/// </summary>
public static class CarryPose
{
    public const float Height = 14, Side = 2;

    /// <summary>The offset from the carrier's position for a body facing <paramref name="yaw"/>.</summary>
    public static System.Numerics.Vector3 Offset(float yaw) => new(MathF.Cos(yaw) * Side, Height, -MathF.Sin(yaw) * Side);
}
