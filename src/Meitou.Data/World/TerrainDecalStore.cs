using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.World;

/// <summary>
/// The terrain's blood decals by cell (the game's <c>TerrainDecalsManager</c>, docs/game/blood-decals.md): adding a decal, the growing pool under a bleeding
/// character that stands still, and the ageing. The renderer draws the cells near the camera from <see cref="In"/>; <see cref="CellVersion"/> tells it
/// when a cell changed. Not thread-safe: the host owns it on one thread.
/// </summary>
public sealed class TerrainDecalStore
{
    /// <summary>The life a decal is made with, in seconds of game time.</summary>
    public const float NewLife = 99;
    /// <summary>The largest a bleeding pool grows.</summary>
    public const float PoolMax = 2;

    /// <summary>A bleeding character's pool: where it lies and how big it has grown (the game's <c>BleedToken</c>).</summary>
    record struct BleedToken(Vector2 At, float Size);

    readonly (int Width, int Height)[][] sizes;
    readonly Dictionary<uint, List<TerrainDecal>> cells = [];
    readonly Dictionary<uint, int> versions = [], rewrites = [];
    // Per cell, the decals placed or grown since its last rewrite, in that order.
    readonly Dictionary<uint, List<TerrainDecal>> appended = [];
    readonly Dictionary<long, BleedToken> bleeds = [];
    readonly List<uint> touched = [];
    readonly List<uint> emptied = [];
    int nextVersion;

    /// <param name="sizes">For each <see cref="TerrainDecalType"/>, the sizes in texels of its folder's textures (the variants); a type without any makes no decals.</param>
    public TerrainDecalStore(IReadOnlyList<IReadOnlyList<(int Width, int Height)>> sizes)
    {
        this.sizes = new (int, int)[4][];
        for (int t = 0; t < 4; t++) this.sizes[t] = t < sizes.Count ? [.. sizes[t]] : [];
    }

    /// <summary>Changes with every change of any cell.</summary>
    public int Version { get; private set; }

    /// <summary>The cells that hold decals.</summary>
    public IEnumerable<uint> Cells => cells.Keys;

    public int Count { get; private set; }

    /// <summary>The decals of a cell (empty when it has none).</summary>
    public ReadOnlySpan<TerrainDecal> In(uint key) => cells.TryGetValue(key, out var list) ? CollectionsMarshal.AsSpan(list) : default;

    /// <summary>A number that changes whenever <paramref name="key"/>'s decals change (0 for a cell that never had any).</summary>
    public int CellVersion(uint key) => versions.GetValueOrDefault(key);

    /// <summary>
    /// A number that changes when decals leave <paramref name="key"/> (ageing, a new world): a texture of the cell must be drawn again from <see cref="In"/>. While it
    /// stays, the cell's changes are only additions: <see cref="Appended"/>, in order, drawn over what the texture holds (the original likely draws a cell's new decals onto
    /// its texture too: it clears it only for a new cell, docs/game/blood-decals.md).
    /// </summary>
    public int CellRewrite(uint key) => rewrites.GetValueOrDefault(key);

    /// <summary>The decals placed in <paramref name="key"/> or grown there since its last rewrite (<see cref="CellRewrite"/>), oldest first.</summary>
    public ReadOnlySpan<TerrainDecal> Appended(uint key) => appended.TryGetValue(key, out var log) ? CollectionsMarshal.AsSpan(log) : default;

    /// <summary>Every decal and pool gone (a new world).</summary>
    public void Clear()
    {
        foreach (var key in cells.Keys) Rewrite(key);
        cells.Clear();
        appended.Clear();
        bleeds.Clear();
        Count = 0;
    }

    /// <summary>Every cell's copy of every decal, cell by cell (what a save holds).</summary>
    public IEnumerable<TerrainDecal> All => cells.Values.SelectMany(list => list);

    /// <summary>Decals as a save holds them, each into its own cell as it is (no placing again); one of no known type is skipped.</summary>
    public void Load(IEnumerable<TerrainDecal> decals)
    {
        foreach (var d in decals)
        {
            if ((int)d.Type >= sizes.Length) continue;
            if (!cells.TryGetValue(d.Key, out var list)) cells[d.Key] = list = [];
            list.Add(d);
            Count++;
            Append(d.Key, d);
        }
    }

    /// <summary>
    /// A new decal (<c>FUN_1408dce50</c>, <c>FUN_1408dc6b0</c>): a random texture of <paramref name="type"/>'s, turned so its width runs along
    /// <paramref name="direction"/> (X/Z), its width and height <paramref name="size"/> × the texture's texels × the cell's 288 units / 2048, stored in each cell its
    /// corners fall in. Nothing when the type has no textures or <paramref name="life"/> is not positive.
    /// </summary>
    public void Add(TerrainDecalType type, Vector3 at, Vector2 direction, float size, uint abgr, float life, Random random)
    {
        var variants = sizes[(int)type];
        if (variants.Length == 0 || !(life > 0)) return;
        float angle = MathF.Atan2(direction.X, direction.Y);
        Place(new TerrainDecal(0, type, (byte)random.Next(variants.Length), 0, 0, 0, 0, (short)(int)(angle * 1000), abgr, life), new Vector2(at.X, at.Z), size);
    }

    /// <summary>
    /// A bleeding character standing still (<c>FUN_1408dcfa0</c>): <paramref name="bleeding"/> × <paramref name="dt"/> × 0.2 grows the pool at its feet, more slowly
    /// the bigger it is (by that over the larger of 1 and ten times the size), up to <see cref="PoolMax"/>; moved more than √10 units away, it starts a new pool. The
    /// pool's decal is replaced when its size in the cell changes.
    /// </summary>
    public void Bleed(long who, Vector3 at, float bleeding, float dt, uint abgr, Random random)
    {
        float grow = bleeding * dt * 0.2f;
        var here = new Vector2(at.X, at.Z);
        if (!bleeds.TryGetValue(who, out var token)) token = new BleedToken(here, grow);
        else if (Vector2.DistanceSquared(token.At, here) <= 10) token.Size = MathF.Min(token.Size + grow / MathF.Max(1, token.Size * 10), PoolMax);
        else token = new BleedToken(here, grow);
        bleeds[who] = token;

        uint key = TerrainDecalCells.Key(token.At.X, token.At.Y);
        var (lx, lz) = Local(key, token.At);
        if (cells.TryGetValue(key, out var list))
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d.Type != TerrainDecalType.Pool || d.LocalX != lx || d.LocalZ != lz) continue;
                var (w, h) = Texels(d);
                int half = (short)(int)(w * token.Size * 10);
                if (half == d.HalfWidth && (short)(int)(h * token.Size * 10) == d.HalfHeight) return;
                // Meitou: grown in steps of a 25th of its size (and at the cap), not at every texel, so a battle's pools do not redraw their cells every tick.
                if (token.Size < PoolMax && Math.Abs(half - d.HalfWidth) * 25 < d.HalfWidth) return;
                Regrow(d, token.At, token.Size);
                return;
            }
        Add(TerrainDecalType.Pool, new Vector3(token.At.X, at.Y, token.At.Y), new Vector2(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f), token.Size, abgr, NewLife, random);
    }

    /// <summary>The character has stopped bleeding or gone: its next pool starts anew.</summary>
    public void Stanch(long who) => bleeds.Remove(who);

    /// <summary>
    /// Ages every decal by <paramref name="dt"/> seconds of game time (the scaled frame time: nothing while paused) and removes those whose life ran out, except
    /// in the cells <paramref name="drawn"/> says are on a texture now (the game keeps what it has drawn until the cell's texture is given to another; <c>FUN_1408dd860</c>).
    /// </summary>
    public void Age(float dt, Func<uint, bool> drawn)
    {
        if (dt <= 0 || Count == 0) return;
        emptied.Clear();
        foreach (var (key, list) in cells)
        {
            bool keep = drawn(key);
            int before = list.Count;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var d = list[i];
                d.Life -= dt;
                if (d.Life > 0 || keep) list[i] = d;
                else list.RemoveAt(i);
            }
            if (list.Count != before)
            {
                Count -= before - list.Count;
                touched.Add(key);
            }
            if (list.Count == 0) emptied.Add(key);
        }
        foreach (var key in emptied) { cells.Remove(key); appended.Remove(key); }
        foreach (var key in touched) Rewrite(key);
        touched.Clear();
    }

    (int Width, int Height) Texels(in TerrainDecal d)
    {
        var variants = sizes[(int)d.Type];
        return d.Variant < variants.Length ? variants[d.Variant] : (0, 0);
    }

    static (short X, short Z) Local(uint key, Vector2 at)
    {
        var (x0, z0) = TerrainDecalCells.Origin(key);
        return ((short)(int)((at.X - x0) / TerrainDecalCells.Size * 1000), (short)(int)((at.Y - z0) / TerrainDecalCells.Size * 1000));
    }

    /// <summary>The four corners of a decal of <paramref name="size"/> centred on <paramref name="at"/> (<c>FUN_1408dc6b0</c>'s quad).</summary>
    void Corners(in TerrainDecal d, Vector2 at, float size, Span<Vector2> corners)
    {
        var (w, h) = Texels(d);
        float ex = size * w * TerrainDecalCells.Size / 4096, ez = size * h * TerrainDecalCells.Size / 4096;
        float angle = d.Rotation / 1000f, s = MathF.Sin(angle), c = MathF.Cos(angle);
        var width = new Vector2(s, c) * ex;
        var height = new Vector2(c, -s) * ez;
        corners[0] = at + width + height;
        corners[1] = at - width + height;
        corners[2] = at + width - height;
        corners[3] = at - width - height;
    }

    void Place(TerrainDecal d, Vector2 at, float size)
    {
        var (w, h) = Texels(d);
        d.HalfWidth = (short)(int)(w * size * 10);
        d.HalfHeight = (short)(int)(h * size * 10);
        Span<Vector2> corners = stackalloc Vector2[4];
        Corners(d, at, size, corners);
        Span<uint> keys = stackalloc uint[4];
        int n = 0;
        foreach (var corner in corners)
        {
            uint key = TerrainDecalCells.Key(corner.X, corner.Y);
            if (keys[..n].Contains(key)) continue;
            keys[n++] = key;
            var (lx, lz) = Local(key, at);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            var copy = d with { Key = key, LocalX = lx, LocalZ = lz };
            list.Add(copy);
            Count++;
            Append(key, copy);
        }
    }

    /// <summary>
    /// A pool <paramref name="d"/> (a cell's copy, centred on <paramref name="at"/>) grown to <paramref name="size"/>: each cell's copy is replaced where it lies in
    /// the list, and the cells its larger corners now reach get one. A pool only grows, so no cell loses it, and the grown copy is drawn over the old one (<see cref="Appended"/>).
    /// </summary>
    void Regrow(TerrainDecal d, Vector2 at, float size)
    {
        var (w, h) = Texels(d);
        var old = d;
        d.HalfWidth = (short)(int)(w * size * 10);
        d.HalfHeight = (short)(int)(h * size * 10);
        Span<Vector2> corners = stackalloc Vector2[4];
        Corners(d, at, size, corners);
        Span<uint> keys = stackalloc uint[4];
        int n = 0;
        foreach (var corner in corners)
        {
            uint key = TerrainDecalCells.Key(corner.X, corner.Y);
            if (keys[..n].Contains(key)) continue;
            keys[n++] = key;
            var (lx, lz) = Local(key, at);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            var copy = d with { Key = key, LocalX = lx, LocalZ = lz };
            int at0 = list.FindIndex(x => x.Type == old.Type && x.Variant == old.Variant && x.Rotation == old.Rotation && x.LocalX == lx && x.LocalZ == lz);
            if (at0 >= 0) list[at0] = copy = copy with { Life = list[at0].Life };
            else { list.Add(copy); Count++; }
            Append(key, copy);
        }
    }

    void Touch(uint key)
    {
        versions[key] = ++nextVersion;
        Version = nextVersion;
    }

    /// <summary>A decal added to (or grown in) <paramref name="key"/>'s list: drawn over what the cell's texture holds. A log much longer than the cell's list is dropped for a redraw.</summary>
    void Append(uint key, in TerrainDecal d)
    {
        if (!appended.TryGetValue(key, out var log)) appended[key] = log = [];
        log.Add(d);
        if (log.Count > 2 * (cells.TryGetValue(key, out var list) ? list.Count : 0) + 64) Rewrite(key);
        else Touch(key);
    }

    /// <summary>Decals left <paramref name="key"/> (or its log grew too long): its texture is drawn again from the list.</summary>
    void Rewrite(uint key)
    {
        rewrites[key] = ++nextVersion;
        if (appended.TryGetValue(key, out var log)) log.Clear();
        Touch(key);
    }
}
