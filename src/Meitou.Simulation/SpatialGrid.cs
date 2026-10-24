using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// The characters of the last tick sorted into square cells on the ground plane, for neighbour queries (separation, targets in
/// reach). Built once per tick in the serial Schedule step from <see cref="CharacterTable.Previous"/>, then only read, so phases
/// can query it from any thread. Within a cell the slots are ascending; <see cref="Query"/> visits cells in a fixed order, so the
/// order neighbours are seen in depends on the state alone, never on the thread count (float sums over neighbours rely on that).
/// </summary>
public sealed class SpatialGrid
{
    readonly float cellSize;
    (long Cell, int Slot)[] entries = [];
    int count;

    public SpatialGrid(float cellSize)
    {
        if (!(cellSize > 0)) throw new ArgumentOutOfRangeException(nameof(cellSize));
        this.cellSize = cellSize;
    }

    public float CellSize => cellSize;

    internal void Build(ReadOnlySpan<CharacterHot> state)
    {
        if (entries.Length < state.Length) entries = new (long, int)[Math.Max(state.Length, entries.Length * 2)];
        count = 0;
        for (int i = 0; i < state.Length; i++)
        {
            if (!state[i].Alive) continue;
            entries[count++] = (CellOf(state[i].Position.X, state[i].Position.Z), i);
        }
        // (cell, slot) pairs are unique, so the sort is total and its result does not depend on the algorithm.
        Array.Sort(entries, 0, count);
    }

    long CellOf(float x, float z) => Pack((int)MathF.Floor(x / cellSize), (int)MathF.Floor(z / cellSize));

    /// <summary>The cell column and row of a point (the cell <see cref="Build"/> files it in).</summary>
    internal (int X, int Z) Cell(Vector3 at) => ((int)MathF.Floor(at.X / cellSize), (int)MathF.Floor(at.Z / cellSize));

    /// <summary>The cells <see cref="Query"/> visits for a circle: a character is visited exactly when its <see cref="Cell"/> lies in this range.</summary>
    internal CellRange Range(float x, float z, float radius) =>
        new((int)MathF.Floor((x - radius) / cellSize), (int)MathF.Floor((x + radius) / cellSize), (int)MathF.Floor((z - radius) / cellSize), (int)MathF.Floor((z + radius) / cellSize));

    internal readonly record struct CellRange(int X0, int X1, int Z0, int Z1)
    {
        public bool Holds((int X, int Z) cell) => cell.X >= X0 && cell.X <= X1 && cell.Z >= Z0 && cell.Z <= Z1;
    }

    static long Pack(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    /// <summary>
    /// Calls <paramref name="visit"/> with the slot of every character whose cell is within the cells overlapping the circle of
    /// <paramref name="radius"/> round (<paramref name="x"/>, <paramref name="z"/>); the caller tests the exact distance. The
    /// order is by cell (row by row), then slot.
    /// </summary>
    public void Query<TState>(float x, float z, float radius, ref TState state, QueryVisitor<TState> visit)
    {
        int x0 = (int)MathF.Floor((x - radius) / cellSize), x1 = (int)MathF.Floor((x + radius) / cellSize);
        int z0 = (int)MathF.Floor((z - radius) / cellSize), z1 = (int)MathF.Floor((z + radius) / cellSize);
        // The cells of one column (cx) with cz ascending are adjacent in the sorted entries, so one search per run finds the first and a scan to
        // the last visits them in the same order as a search per cell would. The packed cz is unsigned, so negative rows sort after the others:
        // a column crossing cz = 0 is two runs, the negative one first.
        for (int cx = x0; cx <= x1; cx++)
        {
            if (z0 < 0 && z1 >= 0)
            {
                Run(Pack(cx, z0), Pack(cx, -1), ref state, visit);
                Run(Pack(cx, 0), Pack(cx, z1), ref state, visit);
            }
            else Run(Pack(cx, z0), Pack(cx, z1), ref state, visit);
        }
    }

    void Run<TState>(long first, long last, ref TState state, QueryVisitor<TState> visit)
    {
        for (int i = FirstOf(first); i < count && entries[i].Cell <= last; i++) visit(entries[i].Slot, ref state);
    }

    public delegate void QueryVisitor<TState>(int slot, ref TState state);

    int FirstOf(long key)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (entries[mid].Cell < key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
