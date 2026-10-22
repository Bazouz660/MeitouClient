using System.Numerics;

namespace Meitou.Data.World;

public sealed partial class FloorSurfaces
{
    /// <summary>
    /// The height of the highest floor or stairs triangle straight below (<paramref name="x"/>, <paramref name="z"/>) at or under <paramref name="top"/>, or null when
    /// there is none: what a ray cast down from <paramref name="top"/> hits on this building (docs/audio.md "Surface", the footstep surface).
    /// </summary>
    public float? HighestBelow(float x, float z, float top)
    {
        float best = float.NegativeInfinity;
        var q = new Vector2(x, z);
        foreach (var s in surfaces)
        {
            if (q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMin > top) continue;
            for (int i = 0; i + 2 < s.Corners.Length; i += 3)
                if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y <= top && y > best) best = y;
        }
        return float.IsNegativeInfinity(best) ? null : best;
    }

    /// <summary>
    /// The height of the highest floor or stairs triangle at (<paramref name="x"/>, <paramref name="z"/>) on floors up to <paramref name="floor"/>, or null:
    /// the building's ground for the strategy camera (docs/formats/camera.md "Height").
    /// </summary>
    public float? HighestUpTo(float x, float z, int floor)
    {
        float best = float.NegativeInfinity;
        var q = new Vector2(x, z);
        foreach (var s in surfaces)
        {
            if (s.Floor > floor || q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMax <= best) continue;
            for (int i = 0; i + 2 < s.Corners.Length; i += 3)
                if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y > best) best = y;
        }
        return float.IsNegativeInfinity(best) ? null : best;
    }

    /// <summary>
    /// The height of the lowest floor, stairs or unwalkable roof triangle straight above (<paramref name="x"/>, <paramref name="z"/>) at or over <paramref name="bottom"/>,
    /// or null when there is none: what covers a point inside the building (<see cref="InteriorVolume.Covered"/>).
    /// </summary>
    public float? LowestAbove(float x, float z, float bottom)
    {
        float best = float.PositiveInfinity;
        var q = new Vector2(x, z);
        foreach (var list in (ReadOnlySpan<List<Surface>>)[surfaces, roofs])
            foreach (var s in list)
            {
                if (q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMax < bottom) continue;
                for (int i = 0; i + 2 < s.Corners.Length; i += 3)
                    if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y >= bottom && y < best) best = y;
            }
        return float.IsPositiveInfinity(best) ? null : best;
    }
}
