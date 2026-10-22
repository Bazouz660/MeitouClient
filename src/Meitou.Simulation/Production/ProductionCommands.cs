using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>Sends characters (the selection when empty) to work a complete player building: operate a well, harvest a farm, research at a bench.</summary>
public sealed record OperateOrder(IReadOnlyList<CharacterId> Characters, string Site) : SimCommand;

/// <summary>
/// Gives characters (the selection when empty) the job an order of <paramref name="Type"/> on a player building becomes, at the end of their job lists (the original's Shift with an order;
/// <see cref="PermaJobs.Add"/>).
/// </summary>
public sealed record AddJob(IReadOnlyList<CharacterId> Characters, Meitou.Data.Gameplay.Ai.TaskType Type, string Site) : SimCommand;

/// <summary>
/// The original's order 146 USE_TURRET on a complete turret of the player's (function 12): the nearest of the characters (the selection when empty) that can act walks to its gunner spot
/// (<see cref="Ai.AiSystem.TurretSpot"/>). Turrets do not fire yet (docs/simulation.md "Production").
/// </summary>
public sealed record UseTurretOrder(IReadOnlyList<CharacterId> Characters, string Site) : SimCommand;

/// <summary>Takes the job at <paramref name="Index"/> off a character's job list (the list's X button).</summary>
public sealed record RemoveJob(CharacterId Character, int Index) : SimCommand;

/// <summary>Moves a character's job from <paramref name="From"/> to <paramref name="To"/> in its list (the list's rows are dragged to reorder, the original's tutorial).</summary>
public sealed record MoveJob(CharacterId Character, int From, int To) : SimCommand;

/// <summary>
/// A character puts an item of its own into a machine: <paramref name="Slot"/> k into recipe input k (as many units as the input still takes), -1 into the storage grid at
/// (<paramref name="X"/>, <paramref name="Y"/>), or with X below 0 where it first fits (stacks of it first, <see cref="Inventory.Add"/>). <paramref name="Count"/> above 0 moves at most that
/// many units (a right click moves one), 0 the whole stack. The character must stand within <see cref="ProductionSettings.TransferReach"/> of the building.
/// </summary>
public sealed record PutInMachine(CharacterId Character, InventoryItemToken Item, string Site, int Slot, int X = 0, int Y = 0, int Count = 0) : SimCommand;

/// <summary>
/// A character takes from a machine: <paramref name="Slot"/> -2 the output's items, k the items of input k, -1 the storage item at <paramref name="Index"/>; into its own <c>main</c> grid
/// (or <paramref name="Section"/> "backpack_content") at the first free cells. <paramref name="Count"/> above 0 takes at most that many units, 0 all.
/// </summary>
public sealed record TakeFromMachine(CharacterId Character, string Site, int Slot, int Index = 0, string Section = "main", int Count = 0) : SimCommand;

/// <summary>Queues a research (paying its cost from the research benches when it was never paid).</summary>
public sealed record QueueResearch(string Research) : SimCommand;

/// <summary>Takes a research off the queue; its progress is kept for when it is queued again.</summary>
public sealed record CancelResearch(string Research) : SimCommand;

/// <summary>Queues a craft at a crafting bench: an option's key (<see cref="Meitou.Data.Gameplay.Construction.CraftOption.Key"/>), <paramref name="Count"/> times.</summary>
public sealed record QueueCraft(string Site, string Option, int Count = 1) : SimCommand;

/// <summary>Takes the craft at <paramref name="Index"/> off a bench's queue (its progress is lost, as the original).</summary>
public sealed record CancelCraft(string Site, int Index) : SimCommand;

/// <summary>Sets a bench's repeat flag: a finished craft goes back to the end of the queue.</summary>
public sealed record RepeatCrafts(string Site, bool Repeat) : SimCommand;
