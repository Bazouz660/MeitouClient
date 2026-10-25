using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The ground contact (docs/render-terrain.md, "Ground contact"), a Meitou look the game does not have: a TERRAIN-mode mesh's surface near the
/// ground under it takes the ground's normal, slope layers and biomes, so a rock meets the terrain without a hard edge. The band is
/// <see cref="Share"/> of how far the placement stands above the ground (<see cref="VisibleHeight"/>), at most <see cref="MaxBand"/>, so a rock
/// that only just shows keeps its own look. The <c>contact</c> switch (Enhancements).
/// </summary>
public static class GroundContact
{
    /// <summary>Read by every TERRAIN-mode mesh draw (TerrainRenderer's constants); false: the meshes as the game draws them.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The band as a share of the placement's <see cref="VisibleHeight"/>.</summary>
    public static float Share { get; set; } = 0.15f;

    /// <summary>The most height above the ground (world units, 1 unit = 10 cm) a mesh blends from the ground's material to its own over.</summary>
    public static float MaxBand { get; set; } = 60;

    /// <summary>The 26 directions of <see cref="Support"/>: the axes, the edge diagonals and the corner diagonals.</summary>
    static readonly Vector3[] Directions = [.. Enumerable.Range(0, 27).Where(i => i != 13).Select(i => new Vector3(i % 3 - 1, i / 3 % 3 - 1, i / 9 - 1))];

    /// <summary>
    /// A mesh's vertices furthest along each of 26 directions (repeats removed): a few points of its real surface whose highest, placed, stands
    /// for its highest vertex (<see cref="VisibleHeight"/>). Empty for a mesh without vertices.
    /// </summary>
    internal static Vector3[] Support(Model model)
    {
        var best = new float[Directions.Length];
        var points = new Vector3[Directions.Length];
        Array.Fill(best, float.MinValue);
        bool any = false;
        foreach (var part in model.Parts)
            foreach (ref readonly var v in part.Vertices.AsSpan())
            {
                any = true;
                for (int d = 0; d < Directions.Length; d++)
                {
                    float s = Vector3.Dot(v.Position, Directions[d]);
                    if (s > best[d]) (best[d], points[d]) = (s, v.Position);
                }
            }
        return any ? [.. points.Distinct()] : [];
    }

    /// <summary>
    /// How far a placed mesh stands above the ground: its highest <paramref name="support"/> point placed, over the ground under that point. A rock
    /// is often mostly buried and tilted, so its bounding box says little about what shows. 0 without support points.
    /// </summary>
    internal static float VisibleHeight(in Matrix4x4 placement, Vector3[] support, in HeightSnapshot ground)
    {
        if (support.Length == 0) return 0;
        var top = new Vector3(0, float.MinValue, 0);
        foreach (var p in support)
        {
            var w = Vector3.Transform(p, placement);
            if (w.Y > top.Y) top = w;
        }
        return Math.Max(top.Y - ground.HeightAt(top.X, top.Z), 0);
    }
}
