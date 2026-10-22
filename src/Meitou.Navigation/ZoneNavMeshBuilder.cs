using System.Diagnostics;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace Meitou.Navigation;

/// <summary>
/// Builds a zone's navmesh from its gathered geometry with Recast (DotRecast): per tile rasterise the triangles with their areas, cut carvers,
/// paint doors, partition, contour and triangulate; then stitch the tiles, and keep only the regions near a seed
/// (docs/game/pathfinding.md, "Region pruning by seeds"). Radius 0: clearance is a query-time check.
/// </summary>
internal static partial class ZoneNavMeshBuilder
{
    /// <summary>Cells of the neighbouring tiles rasterised round each tile (Recast's borderSize; engine choice: its usual walkable radius + 3, radius 0 here).</summary>
    const int TileBorderCells = 3;

    public static ZoneNavMesh Build(ZoneGeometry g, NavBuildSettings s, out NavBuildTimes times)
    {
        var total = Stopwatch.StartNew();
        times = new NavBuildTimes();
        float zoneSize = g.ZoneMax.X - g.ZoneMin.X;
        float pad = TileBorderCells * s.CellSize;
        int cellsPerSide = (int)MathF.Round(zoneSize / s.CellSize);
        if (cellsPerSide % s.TileCells != 0) throw new ArgumentException($"{cellsPerSide} cells per zone side is not a multiple of {s.TileCells}");
        int tilesPerSide = cellsPerSide / s.TileCells;
        float tileWorld = s.TileCells * s.CellSize;
        var verts = g.Vertices.ToArray();
        var indices = g.Indices.ToArray();
        var areas = g.Areas.Select(a => (int)a).ToArray();

        // Bucket the triangles and the volumes by tile.
        var triBuckets = new List<int>[tilesPerSide * tilesPerSide];
        var carvers = new List<int>[tilesPerSide * tilesPerSide];
        var painters = new List<int>[tilesPerSide * tilesPerSide];
        var solids = new List<int>[tilesPerSide * tilesPerSide];
        for (int i = 0; i < triBuckets.Length; i++) { triBuckets[i] = []; carvers[i] = []; painters[i] = []; solids[i] = []; }
        void Tiles(float x0, float z0, float x1, float z1, List<int>[] buckets, int id)
        {
            // A tile also takes what lies in its border (BuildTile).
            x0 -= pad; z0 -= pad; x1 += pad; z1 += pad;
            int tx0 = Math.Clamp((int)MathF.Floor((x0 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1), tx1 = Math.Clamp((int)MathF.Floor((x1 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1);
            int tz0 = Math.Clamp((int)MathF.Floor((z0 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1), tz1 = Math.Clamp((int)MathF.Floor((z1 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1);
            if (x1 < g.ZoneMin.X || x0 > g.ZoneMax.X || z1 < g.ZoneMin.Y || z0 > g.ZoneMax.Y) return;
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++) buckets[tz * tilesPerSide + tx].Add(id);
        }
        for (int t = 0; t < indices.Length; t += 3)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int k = 0; k < 3; k++)
            {
                float x = verts[indices[t + k] * 3], z = verts[indices[t + k] * 3 + 2];
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            Tiles(x0, z0, x1, z1, triBuckets, t / 3);
        }
        for (int c = 0; c < g.Carvers.Count; c++)
        {
            var (a, b) = Bounds(g.Carvers[c]);
            Tiles(a.X, a.Y, b.X, b.Y, carvers, c);
        }
        for (int c = 0; c < g.Painters.Count; c++)
        {
            var (a, b) = Bounds(g.Painters[c]);
            Tiles(a.X, a.Y, b.X, b.Y, painters, c);
        }
        for (int c = 0; c < g.Solids.Count; c++) Tiles(g.Solids[c].Min.X, g.Solids[c].Min.Y, g.Solids[c].Max.X, g.Solids[c].Max.Y, solids, c);

        var ticks = new TileTicks();
        var meshes = new RcPolyMesh?[tilesPerSide * tilesPerSide];
        var workers = s.Threads > 0 ? s.Threads : Math.Max(1, Environment.ProcessorCount - 1);
        var watch = Stopwatch.StartNew();
        Parallel.For(0, meshes.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, tile =>
        {
            if (triBuckets[tile].Count == 0) return;
            int tx = tile % tilesPerSide, tz = tile / tilesPerSide;
            meshes[tile] = BuildTile(g, s, verts, indices, areas, triBuckets[tile], carvers[tile], painters[tile], solids[tile], ticks,
                g.ZoneMin.X + tx * tileWorld, g.ZoneMin.Y + tz * tileWorld, tileWorld);
        });
        times.Tiles = watch.Elapsed.TotalMilliseconds;
        times.TileCount = meshes.Count(m => m is not null);
        double ms = 1000.0 / Stopwatch.Frequency;
        times.CpuRaster = ticks.Raster * ms; times.CpuCompact = ticks.Compact * ms; times.CpuRegions = ticks.Regions * ms; times.CpuContours = ticks.Contours * ms; times.CpuMesh = ticks.Mesh * ms;

        watch.Restart();
        var mesh = Stitch(g, s, meshes, tilesPerSide, tileWorld);
        AssignDoors(mesh, g, s);
        times.Stitch = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        SeedPruner.Prune(mesh, g, s);
        times.Prune = watch.Elapsed.TotalMilliseconds;
        times.Total = total.Elapsed.TotalMilliseconds;
        times.Polygons = mesh.PolygonCount;
        times.KeptPolygons = mesh.KeptCount;
        times.Vertices = mesh.Vertices.Length;
        return mesh;
    }

    /// <summary>Stopwatch ticks the tiles of one build spent per phase; the tiles add to it from several threads.</summary>
    sealed class TileTicks
    {
        public long Raster, Compact, Regions, Contours, Mesh;
    }

    static (Vector2 Min, Vector2 Max) Bounds(NavVolume v)
    {
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var p in v.Polygon) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
        return (lo, hi);
    }

    static RcPolyMesh? BuildTile(ZoneGeometry g, NavBuildSettings s, float[] verts, int[] indices, int[] areas, List<int> tris, List<int> carvers, List<int> painters, List<int> solids, TileTicks ticks,
        float x0, float z0, float tileWorld)
    {
        var ctx = new RcContext();
        long t0 = Stopwatch.GetTimestamp();
        int walkableHeight = (int)MathF.Ceiling(s.AgentHeight / s.CellHeight);
        int walkableClimb = (int)MathF.Floor(s.MaxClimb / s.CellHeight);

        // Tile bounds in Y from the triangles that touch it.
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (int t in tris)
            for (int k = 0; k < 3; k++)
            {
                float y = verts[indices[t * 3 + k] * 3 + 1];
                yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y);
            }
        float ch = s.CellHeight;
        if ((yMax - yMin) / ch > 8000) ch = (yMax - yMin) / 8000f;
        // A border of cells from the neighbouring tiles: Recast then keeps the contour vertices on the tile's edge and marks those edges as portals, so a
        // narrow strip along the edge is not simplified away from it (docs/game/pathfinding.md, "Own builder", tile seams).
        int border = TileBorderCells;
        float pad = border * s.CellSize;
        var bmin = new RcVec3f(x0 - pad, yMin - 1, z0 - pad);
        var bmax = new RcVec3f(x0 + tileWorld + pad, yMax + 1, z0 + tileWorld + pad);
        int cells = s.TileCells + 2 * border;
        var hf = new RcHeightfield(cells, cells, bmin, bmax, s.CellSize, ch, border);

        var tileTris = new int[tris.Count * 3];
        var tileAreas = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            int t = tris[i];
            tileTris[i * 3] = indices[t * 3]; tileTris[i * 3 + 1] = indices[t * 3 + 1]; tileTris[i * 3 + 2] = indices[t * 3 + 2];
            tileAreas[i] = areas[t];
        }
        RcRasterizations.RasterizeTriangles(ctx, verts, tileTris, tileAreas, tris.Count, hf, walkableClimb);
        RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, hf);
        RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);
        long t1 = Stopwatch.GetTimestamp();
        var chf = RcCompacts.BuildCompactHeightfield(ctx, walkableHeight, walkableClimb, hf);

        foreach (int c in carvers) Mark(ctx, g.Carvers[c], NavArea.Null, chf);
        foreach (int c in solids) RemoveInside(g.Solids[c], chf);
        foreach (int c in painters) Mark(ctx, g.Painters[c], NavArea.Door, chf, s.CellSize * s.DoorInflateCells);

        long t2 = Stopwatch.GetTimestamp();
        RcRegions.BuildRegionsMonotone(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        long t3 = Stopwatch.GetTimestamp();
        var cset = RcContours.BuildContours(ctx, chf, s.MaxSimplificationError, s.MaxEdgeLength, 1);
        long t4 = Stopwatch.GetTimestamp();
        var result = cset.conts.Count == 0 ? null : RcMeshs.BuildPolyMesh(ctx, cset, ZoneNavMesh.MaxVerticesPerPolygon);
        long t5 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref ticks.Raster, t1 - t0);
        Interlocked.Add(ref ticks.Compact, t2 - t1);
        Interlocked.Add(ref ticks.Regions, t3 - t2);
        Interlocked.Add(ref ticks.Contours, t4 - t3);
        Interlocked.Add(ref ticks.Mesh, t5 - t4);
        return result;
    }

    /// <summary>
    /// How far below an upward face of a solid a span's floor has to be to count as inside (engine choice: one height cell). Recast rounds a span's top up to the
    /// highest point of its surface over the cell, so a span on the solid's own top is on or above that face and stays.
    /// </summary>
    internal const float SolidTopInset = 1;

    /// <summary>Removes the walkable spans whose floor, at the middle of their cell, lies inside the solid (docs/game/pathfinding.md, "Solids").</summary>
    static void RemoveInside(NavSolid solid, RcCompactHeightfield chf)
    {
        float cs = chf.cs, ch = chf.ch;
        int x0 = Math.Max(0, (int)MathF.Floor((solid.Min.X - chf.bmin.X) / cs)), x1 = Math.Min(chf.width - 1, (int)MathF.Floor((solid.Max.X - chf.bmin.X) / cs));
        int z0 = Math.Max(0, (int)MathF.Floor((solid.Min.Y - chf.bmin.Z) / cs)), z1 = Math.Min(chf.height - 1, (int)MathF.Floor((solid.Max.Y - chf.bmin.Z) / cs));
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;
                    var p = new Vector3(chf.bmin.X + (x + 0.5f) * cs, chf.bmin.Y + chf.spans[i].y * ch, chf.bmin.Z + (z + 0.5f) * cs);
                    bool inside = true;
                    foreach (var plane in solid.Planes)
                        if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W >= (plane.Y > 0 ? -SolidTopInset : 0)) { inside = false; break; }
                    if (inside) chf.areas[i] = NavArea.Null;
                }
            }
    }

    static void Mark(RcContext ctx, NavVolume v, byte area, RcCompactHeightfield chf, float inflate = 0)
    {
        var outline = inflate > 0 ? Inflate(v.Polygon, inflate) : v.Polygon;
        var poly = new float[outline.Length * 3];
        for (int i = 0; i < outline.Length; i++)
        {
            poly[i * 3] = outline[i].X;
            poly[i * 3 + 1] = 0;
            poly[i * 3 + 2] = outline[i].Y;
        }
        RcAreas.MarkConvexPolyArea(ctx, poly, v.YMin, v.YMax, new RcAreaModification(area), chf);
    }
}
