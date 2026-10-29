using System.Collections;
using System.Numerics;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation;

/// <summary>
/// An order command (docs/simulation.md "Order queue"): the characters it names get it as an <see cref="OrderEntry"/> in their <see cref="CharacterCold.OrderQueue"/>. The paused interface applies
/// every order command (<see cref="World.ApplyPausedInterfaceCommands"/>).
/// </summary>
public abstract record OrderCommand : SimCommand
{
    /// <summary>Added after the characters' queued orders (Shift in the game) instead of replacing them (<c>Character::addOrder</c> <c>FUN_1405c8da0</c> clears the queue without it).</summary>
    public bool Queued { get; init; }
}

/// <summary>What an order's check found (<see cref="IOrderOwner.CanRun"/>).</summary>
public enum OrderVerdict : byte
{
    /// <summary>It can start now.</summary>
    Run,
    /// <summary>It cannot start now (the original's plan fails): it stays queued, its failure text is shown, and the character falls through to its jobs and goals.</summary>
    Blocked,
    /// <summary>Its target or site is gone or no longer fits the verb (the original's desire x finder is 0): it leaves the queue.</summary>
    Gone,
}

/// <summary>An order's check: the verdict and, when blocked, the failure text (null: none), barked over the character or, <see cref="Posted"/>, posted as a message.</summary>
public readonly record struct OrderCheck(OrderVerdict Verdict, string? Why = null, bool Posted = false)
{
    public static OrderCheck Run => new(OrderVerdict.Run);
    public static OrderCheck Gone => new(OrderVerdict.Gone);
    public static OrderCheck Blocked(string? why = null, bool posted = false) => new(OrderVerdict.Blocked, why, posted);
    public bool CanRun => Verdict == OrderVerdict.Run;
}

/// <summary>
/// The system an order's row belongs to: it checks an <see cref="OrderEntry"/> of the character (<see cref="CanRun"/>, a pure read: the row's preconditions as far as Meitou models them; the
/// queue adds the repertoire and the threat test, <see cref="PlayerOrders.CanRun"/>) and starts it (<see cref="Start"/>, serial phases only, after a <see cref="OrderVerdict.Run"/>).
/// </summary>
public interface IOrderOwner
{
    OrderCheck CanRun(World world, int slot, OrderEntry order);
    void Start(World world, int slot, OrderEntry order);
}

/// <summary>
/// One order of a character's queue (the original's <c>+0x40</c> deque entry): its row, owner, target, site, point and the owner's <see cref="Detail"/>. <see cref="Started"/> once its start
/// ran; it then stands for the character's task while that task's provenance is an order's (<see cref="PlayerOrders.Runs"/>). Runtime only.
/// </summary>
public sealed class OrderEntry(TaskType row, IOrderOwner owner)
{
    public TaskType Row { get; } = row;
    public IOrderOwner Owner { get; } = owner;
    public CharacterId Target { get; init; } = CharacterId.None;
    public string? Site { get; init; }
    public Vector3 Point { get; init; }
    public object? Detail { get; init; }
    public bool Started { get; internal set; }
    /// <summary>Its check ran at the front once (its failure text was shown); a front not tried yet is tried at once.</summary>
    public bool Tried { get; internal set; }

    public bool IsMove => Row == TaskType.MOVE_CUS_ORDERED;
}

/// <summary>
/// A character's order queue (<see cref="CharacterCold.OrderQueue"/>, the original's <c>+0x40</c> deque, count <c>+0x60</c>): the front is the order in hand, the rest wait (Shift). Changed by the
/// character's own partition (a move's arrival) or a serial phase. Not saved (docs/simulation.md "Order queue").
/// </summary>
public sealed class OrderQueue : IReadOnlyList<OrderEntry>
{
    readonly List<OrderEntry> entries = [];

    public int Count => entries.Count;
    public OrderEntry this[int index] => entries[index];
    public OrderEntry? Front => entries.Count > 0 ? entries[0] : null;
    public void Clear() => entries.Clear();
    internal void Add(OrderEntry entry) => entries.Add(entry);
    internal void PopFront() => entries.RemoveAt(0);
    public IEnumerator<OrderEntry> GetEnumerator() => entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Adds the queue to the state hash as the move-only queue did (the moves after the one in hand) while it holds only what that queue could: nothing, a started order alone, or started moves.
    /// Anything else (an order waiting, a queued order other than a move) is added under its own mark, so worlds without them hash as before.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        bool legacy = entries.Count == 0 || entries[0].Started && (entries.Count == 1 || entries.TrueForAll(e => e.IsMove));
        hasher.Add(Math.Max(entries.Count - 1, 0));
        for (int k = 1; k < entries.Count; k++) if (entries[k].IsMove) hasher.Add(new Vector2(entries[k].Point.X, entries[k].Point.Z));
        if (legacy) return;
        hasher.Add(0x4F717565);
        foreach (var e in entries)
        {
            hasher.Add((int)e.Row);
            hasher.Add(e.Started);
            hasher.Add(e.Tried);
            hasher.Add(e.Target);
            hasher.Add(e.Site is { } site ? Rng.StableHash(site) : 0UL);
            hasher.Add(e.Point);
        }
    }
}

/// <summary>
/// The order queue's rules (docs/simulation.md "Order queue"; the original's <c>checkOrders</c> <c>FUN_140510920</c> and <c>Character::addOrder</c> <c>FUN_1405c8da0</c>, docs/game/ai-tasks.md
/// "Precedence"): an order command clears the queue (unless Shift) and queues the order; the front starts through its owner when it can run, else shows its failure text and stays queued
/// while the character's jobs, goals and reactions go on; a front whose task ended leaves the queue and the next starts. <b>Engine</b> until the decision step: the front is tried again every
/// <see cref="Production.ProductionSystem.JobCheckTicks"/> (<see cref="Step"/>).
/// </summary>
public static class PlayerOrders
{
    /// <summary>
    /// Gives a character an order (serial phases only): without <paramref name="queued"/> the queue is cleared first; the order is queued and, at the front, started when it can run, else
    /// its failure text is shown (<paramref name="show"/>). The character's task is left alone while the order cannot run. Returns the check of the front.
    /// </summary>
    public static OrderCheck Give(World world, int slot, OrderEntry order, bool queued, bool show = true)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (!queued) q.Clear();
        else Settle(world, slot);
        q.Add(order);
        return q.Count == 1 ? TryFront(world, slot, show) : OrderCheck.Run;
    }

    /// <summary>
    /// Whether the order can start for the character now (a pure read; the M6 <c>World.CanOrder</c> rests on it): the row is in the character's repertoire (<see cref="TaskStart.Eligible"/>),
    /// its owner's check, and a row needing no melee threat (<see cref="NeedsNoThreat"/>) has none (<see cref="Threatened"/>).
    /// </summary>
    public static OrderCheck CanRun(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (!TaskStart.Eligible(world, cold, order.Row)) return OrderCheck.Blocked();
        var own = order.Owner.CanRun(world, slot, order);
        return own.CanRun && Threatened(world, slot, order.Row) ? OrderCheck.Blocked() : own;
    }

    /// <summary>
    /// Whether the row's list B has <c>!158</c> <c>iAmBeingMeleeAttacked_immediate</c>, which no row provides, so the plan fails while the character has a melee threat (docs/game/ai-tasks.md
    /// "Precedence"): of the player's orders only 146 Turret gunner.
    /// </summary>
    public static bool NeedsNoThreat(TaskType row) =>
        TaskDataTable.Of(row) is { } data && Array.Exists(data.Needs, c => c is { State: StateType.iAmBeingMeleeAttacked_immediate, Holds: false });

    /// <summary>Whether the row needs no melee threat and the character has one within 40 (state 158, <see cref="CharacterFacts.ImmediateThreat"/>).</summary>
    static bool Threatened(World world, int slot, TaskType row) =>
        NeedsNoThreat(row) && world.SystemOf<Combat.CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot);

    /// <summary>Whether the front order started and its task still runs: the task's provenance is an order's for it (<see cref="RunningTask.Runs"/>; a move's walk is the order's move) and its handler has more to do.</summary>
    public static bool Runs(World world, int slot, OrderEntry order)
    {
        if (!order.Started) return false;
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var task = (CharacterTask)table.Next[slot].Task;
        if (cold.Running is not { Source: TaskSource.Order } run || !run.Runs(task) || order.IsMove && !cold.OrderMove) return false;
        return world.Tasks.Of(cold, task) is not { } handler || !handler.Done(world, slot, task);
    }

    /// <summary>Drops a front whose task ended: a move's, with the moves queued after it, as an interrupted walk always dropped them; any other order's alone.</summary>
    static void Settle(World world, int slot)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { Started: true } front || Runs(world, slot, front)) return;
        if (front.IsMove) q.Clear();
        else q.PopFront();
    }

    /// <summary>Tries the front: gone ones leave, a blocked one stays (its text shown with <paramref name="show"/>), one that can run starts; one whose start leaves nothing running leaves.</summary>
    static OrderCheck TryFront(World world, int slot, bool show)
    {
        var q = world.Characters.Cold(slot)!.OrderQueue;
        while (q.Front is { } front)
        {
            var check = CanRun(world, slot, front);
            front.Tried = true;
            if (check.Verdict == OrderVerdict.Gone)
            {
                q.PopFront();
                continue;
            }
            if (check.Verdict == OrderVerdict.Blocked)
            {
                if (show && check.Why is { } why)
                {
                    if (check.Posted) world.Post(why);
                    else world.Bark(world.Characters.IdOf(slot), why);
                }
                return check;
            }
            front.Started = true;
            front.Owner.Start(world, slot, front);
            if (q.Front != front || Runs(world, slot, front)) return check;
            q.PopFront();
        }
        return OrderCheck.Gone;
    }

    /// <summary>
    /// A move's walk reached its point (the character's own partition): the move leaves the queue; a queued move after it starts at once, as the move-only queue did (the path is asked for next
    /// tick). False when the walk was no queued order's move.
    /// </summary>
    internal static bool Arrived(CharacterCold cold, ref CharacterHot n)
    {
        var q = cold.OrderQueue;
        if (!cold.OrderMove || q.Front is not { IsMove: true, Started: true }) return false;
        q.PopFront();
        if (q.Front is not { IsMove: true } next) return false;
        next.Started = next.Tried = true;
        n.Goal = new Vector2(next.Point.X, next.Point.Z);
        n.Flags |= (ushort)MoveFlags.NeedPath;
        return true;
    }

    /// <summary>
    /// <see cref="TaskStart.Start"/>'s hook, before the new start: a start from another source than an order replaces the running front order, which leaves the queue as <see cref="Settle"/>
    /// drops it (a step of a follow row keeps its provenance and is no replacement).
    /// </summary>
    internal static void Replacing(World world, int slot, in TaskRequest request, bool step)
    {
        if (request.Source == TaskSource.Order || step) return;
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front || !Runs(world, slot, front)) return;
        if (front.IsMove) q.Clear();
        else q.PopFront();
    }

    /// <summary>
    /// <see cref="TaskStart.End"/>'s hook, before the end: the running front order's task is done (<see cref="TaskEnd.Done"/>: it leaves the queue, the next starts at the next <see cref="Step"/>)
    /// or stopped from outside (<see cref="TaskEnd.Stopped"/>: the queue is dropped, as a stop always dropped the queued moves).
    /// </summary>
    internal static void Ending(World world, int slot, TaskEnd reason)
    {
        if (reason == TaskEnd.Replaced) return;
        var q = world.Characters.Cold(slot)!.OrderQueue;
        if (q.Front is not { } front || !Runs(world, slot, front)) return;
        if (reason == TaskEnd.Stopped || front.IsMove) q.Clear();
        else q.PopFront();
    }

    /// <summary>
    /// Each tick after the inputs (serial): a front whose task ended leaves the queue and the next starts (the original's "plan finished" replan). <b>Engine</b> stand-ins for the replan triggers
    /// (until the decision step, M5): a front not tried yet is tried at once; every <see cref="Production.ProductionSystem.JobCheckTicks"/> a waiting one is tried again unless the character fights (a plan in progress; its end
    /// replans), silently; and on the same beat a running front whose row needs no melee threat (<see cref="NeedsNoThreat"/>) ends when one comes (the original replans on the blow's stagger
    /// and the plan fails), so the character falls through to Self preservation and takes the order up again once the threat is gone (S3, the turret gunner).
    /// </summary>
    internal static void Step(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { OrderQueue.Count: > 0 } cold) continue;
            var front = cold.OrderQueue.Front!;
            bool due = (world.Tick + i) % Production.ProductionSystem.JobCheckTicks == 0;
            if (front.Started)
            {
                if (!Runs(world, i, front))
                {
                    Settle(world, i);
                    if (cold.OrderQueue.Count > 0) TryFront(world, i, show: true);
                }
                else if (due && Threatened(world, i, front.Row))
                {
                    front.Started = false;   // waits in the queue: its end below is not the order's
                    TaskStart.End(world, i, TaskEnd.Stopped);
                }
                continue;
            }
            if (!front.Tried || due && !cold.InCombat) TryFront(world, i, show: !front.Tried);
        }
    }
}
