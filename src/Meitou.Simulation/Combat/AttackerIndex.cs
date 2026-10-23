namespace Meitou.Simulation.Combat;

/// <summary>
/// Who is after whom, from the combat state of the last tick (<see cref="CombatSystem"/>'s read buffer): per slot the characters whose target it is, and those yielding to it.
/// It stands in for the grid searches round a target that only looked for its own attackers: a check walks the target's list and keeps the ones whose grid cell the search
/// would have covered (<see cref="SpatialGrid.Range"/>), so it sees the same characters, and in the same order (the grid's: cell column, row, then slot). Only characters the
/// grid holds (alive in <see cref="CharacterTable.Previous"/>) are listed. Built in a serial phase, read from any thread.
/// </summary>
sealed class AttackerIndex
{
    (int Target, int X, int Z, int Slot)[] attackers = [];
    (int Target, int Slot)[] yielders = [];
    int attackerCount, yielderCount, size;
    // Per slot: where its attackers and its yielders start in the sorted arrays (the next slot's start ends them).
    int[] attackersAt = [], yieldersAt = [];
    (int X, int Z)[] cells = [];

    public void Build(CombatSlot[] read, CharacterTable table, SpatialGrid grid)
    {
        int n = table.HighWater;
        var prev = table.Previous;
        if (cells.Length < n) cells = new (int, int)[Math.Max(n, cells.Length * 2)];
        if (attackers.Length < n) { attackers = new (int, int, int, int)[cells.Length]; yielders = new (int, int)[cells.Length]; }
        if (attackersAt.Length < n + 1) { attackersAt = new int[cells.Length + 1]; yieldersAt = new int[cells.Length + 1]; }
        attackerCount = yielderCount = 0;
        size = n;
        for (int o = 0; o < n && o < read.Length; o++)
        {
            if (!prev[o].Alive) continue;
            var cell = cells[o] = grid.Cell(prev[o].Position);
            ref readonly var s = ref read[o];
            if ((uint)s.TargetSlot < (uint)n) attackers[attackerCount++] = (s.TargetSlot, cell.X, cell.Z, o);
            if ((uint)s.YieldTo < (uint)n) yielders[yielderCount++] = (s.YieldTo, o);
        }
        // The tuples are unique (the slot is in each), so the sort's order is total.
        Array.Sort(attackers, 0, attackerCount);
        Array.Sort(yielders, 0, yielderCount);
        Starts(attackersAt, n, attackerCount, k => attackers[k].Target);
        Starts(yieldersAt, n, yielderCount, k => yielders[k].Target);
    }

    static void Starts(int[] at, int n, int count, Func<int, int> targetOf)
    {
        int k = 0;
        for (int t = 0; t <= n; t++)
        {
            while (k < count && targetOf(k) < t) k++;
            at[t] = k;
        }
    }

    /// <summary>The characters whose target is <paramref name="t"/>, in the grid's order, with their cells.</summary>
    public ReadOnlySpan<(int Target, int X, int Z, int Slot)> AttackersOf(int t) =>
        (uint)t < (uint)size ? attackers.AsSpan(attackersAt[t], attackersAt[t + 1] - attackersAt[t]) : [];

    /// <summary>The characters yielding to <paramref name="t"/>, by slot.</summary>
    public ReadOnlySpan<(int Target, int Slot)> YieldingTo(int t) =>
        (uint)t < (uint)size ? yielders.AsSpan(yieldersAt[t], yieldersAt[t + 1] - yieldersAt[t]) : [];

    /// <summary>The grid cell of a listed character as of the build.</summary>
    public (int X, int Z) CellOf(int slot) => cells[slot];
}
