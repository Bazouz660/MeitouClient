using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The original generator keeps only the regions within 4 units of a seed point (regionPruningSettings, minRegionArea 1e8 makes every region "too small" otherwise),
/// and the largest region when none qualifies. This is that pass over the polygon graph: roofs, closed yards and cliff tops without a seed are dropped
/// (docs/game/pathfinding.md, "Region pruning by seeds").
/// </summary>
internal static class SeedPruner
{
    /// <summary>Marks the polygons not connected to any seed as not kept. Returns how many seeds found a polygon.</summary>
    public static int Prune(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        int n = mesh.PolygonCount;
        if (n == 0) return 0;
        var keep = new bool[n];
        var queue = new Queue<int>();
        int matched = Match(mesh, g.Seeds, s, keep, queue);
        int border = g.TerrainHeight is { } terrain ? MatchBorder(mesh, g, terrain, keep, queue) : 0;

        if (matched == 0 && border == 0 && (g.ClippedAtBorder || g.InteriorDestroyed))
        {
            // Part of an interior whose doors are across a zone border: the neighbour's kept border edges reach it (ZoneKeptClosure).
            // A destroyed building's interior: its ruin floor is the exterior's, its largest region often a floor or wall tops up in the air.
            for (int p = 0; p < n; p++) mesh.Kept[p] = false;
            return 0;
        }
        if (matched == 0 && border == 0)
        {
            // "All regions are below the area threshold and too far from a seed point. Keeping the largest region."
            var component = Components(mesh);
            int best = component.GroupBy(c => c).OrderByDescending(c => c.Count()).First().Key;
            for (int p = 0; p < n; p++) if (component[p] == best) { keep[p] = true; }
            for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p];
            return 0;
        }

        Spread(mesh, keep, queue);
        for (int p = 0; p < n; p++) mesh.Kept[p] = keep[p];
        return matched;
    }

    /// <summary>Also keeps the regions of <paramref name="mesh"/> that <paramref name="seeds"/> reach (no fallback); true when a polygon was added.</summary>
    public static bool Extend(ZoneNavMesh mesh, IReadOnlyList<Vector3> seeds, NavBuildSettings s)
    {
        if (mesh.PolygonCount == 0 || seeds.Count == 0) return false;
        var keep = (bool[])mesh.Kept.Clone();
        var queue = new Queue<int>();
        Match(mesh, seeds, s, keep, queue);
        if (queue.Count == 0) return false;
        Spread(mesh, keep, queue);
        for (int p = 0; p < keep.Length; p++) mesh.Kept[p] = keep[p];
        return true;
    }

    /// <summary>Marks the polygons each seed claims (queued when newly kept). Returns how many seeds found a polygon, kept already or not.</summary>
    static int Match(ZoneNavMesh mesh, IReadOnlyList<Vector3> seeds, NavBuildSettings s, bool[] keep, Queue<int> queue)
    {
        // The mesh's own point index (independent of which polygons are kept).
        var ix = mesh.Index;
        int matched = 0;
        foreach (var seed in seeds)
        {
            bool found = false;
            // Engine choice: a seed that finds nothing within the slack is retried with three times the slack (a seed's Y is where it was authored).
            foreach (float slack in new[] { s.SeedHeightSlack, 3 * s.SeedHeightSlack })
            {
                float reach = s.SeedDistance;
                int cx0 = ix.Col(seed.X - reach - mesh.BoundsMin.X), cx1 = ix.Col(seed.X + reach - mesh.BoundsMin.X);
                int cz0 = ix.Row(seed.Z - reach - mesh.BoundsMin.Y), cz1 = ix.Row(seed.Z + reach - mesh.BoundsMin.Y);
                for (int z = cz0; z <= cz1; z++)
                    for (int x = cx0; x <= cx1; x++)
                        foreach (int p in ix.Cells[z * ix.Columns + x])
                        {
                            if (keep[p]) { found = true; continue; }
                            if (seed.X < ix.Min[p].X - reach || seed.X > ix.Max[p].X + reach || seed.Z < ix.Min[p].Y - reach || seed.Z > ix.Max[p].Y + reach) continue;
                            if (mesh.DistanceXZ(p, seed.X, seed.Z) > reach) continue;
                            float y = mesh.HeightAt(p, Math.Clamp(seed.X, ix.Min[p].X, ix.Max[p].X), Math.Clamp(seed.Z, ix.Min[p].Y, ix.Max[p].Y));
                            if (Math.Abs(y - seed.Y) > slack) continue;
                            keep[p] = true;
                            queue.Enqueue(p);
                            found = true;
                        }
                if (found) break;
            }
            if (found) matched++;
        }
        return matched;
    }

    /// <summary>How far (units) a border edge's middle may be from the ground (the terrain, or the water's height over it) to count as ground level (engine choice).</summary>
    public const float BorderGroundSlack = 30;

    /// <summary>
    /// Marks the polygons with an open edge on the zone's border line at ground level (queued when newly kept). The original seeds each zone from its built
    /// neighbours' open border edges and ships every tile built, so a region that goes on into the next zone is kept wherever its seed is; pruned by its own
    /// seeds alone, a zone with few of them (Fog Islands 14.26: 1) kept 3 % of the original's area. Roofs and walkways crossing the border stay pruned
    /// (docs/game/pathfinding.md, "Zone borders"). Returns how many polygons it marked.
    /// </summary>
    static int MatchBorder(ZoneNavMesh mesh, ZoneGeometry g, Func<float, float, float> terrain, bool[] keep, Queue<int> queue)
    {
        const float onLine = 0.5f;
        int marked = 0;
        for (int p = 0; p < mesh.PolygonCount; p++)
        {
            if (keep[p]) continue;
            var poly = mesh.Polygons[p];
            for (int k = 0; k < poly.Length; k++)
            {
                if (mesh.Neighbours[p][k] >= 0) continue;
                var a = mesh.Vertices[poly[k]];
                var b = mesh.Vertices[poly[(k + 1) % poly.Length]];
                bool onBorder = MathF.Abs(a.X - g.ZoneMin.X) < onLine && MathF.Abs(b.X - g.ZoneMin.X) < onLine
                    || MathF.Abs(a.X - g.ZoneMax.X) < onLine && MathF.Abs(b.X - g.ZoneMax.X) < onLine
                    || MathF.Abs(a.Z - g.ZoneMin.Y) < onLine && MathF.Abs(b.Z - g.ZoneMin.Y) < onLine
                    || MathF.Abs(a.Z - g.ZoneMax.Y) < onLine && MathF.Abs(b.Z - g.ZoneMax.Y) < onLine;
                if (!onBorder) continue;
                var mid = (a + b) / 2;
                if (MathF.Abs(mid.Y - MathF.Max(terrain(mid.X, mid.Z), WorldWater.Height)) > BorderGroundSlack) continue;
                keep[p] = true;
                queue.Enqueue(p);
                marked++;
                break;
            }
        }
        return marked;
    }

    static void Spread(ZoneNavMesh mesh, bool[] keep, Queue<int> queue)
    {
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int q in mesh.NeighboursOf(p))
                if (!keep[q]) { keep[q] = true; queue.Enqueue(q); }
        }
    }

    static int[] Components(ZoneNavMesh mesh)
    {
        var id = new int[mesh.PolygonCount];
        Array.Fill(id, -1);
        int next = 0;
        var stack = new Stack<int>();
        for (int p = 0; p < id.Length; p++)
        {
            if (id[p] >= 0) continue;
            id[p] = next;
            stack.Push(p);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                foreach (int q in mesh.NeighboursOf(c)) if (id[q] < 0) { id[q] = next; stack.Push(q); }
            }
            next++;
        }
        return id;
    }
}
