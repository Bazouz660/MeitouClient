namespace Meitou.Rendering;

/// <summary>
/// The ground contact (docs/render-terrain.md, "Ground contact"), a Meitou look the game does not have: a TERRAIN-mode mesh's surface near the
/// ground under it takes the ground's normal, slope layers and biomes, so a rock meets the terrain without a hard edge. The band is
/// <see cref="Share"/> of the placed mesh's height, at most <see cref="MaxBand"/>, so a small rock keeps its own look. The <c>contact</c> switch (Enhancements).
/// </summary>
public static class GroundContact
{
    /// <summary>Read by every TERRAIN-mode mesh draw (TerrainRenderer's constants); false: the meshes as the game draws them.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The band as a share of the placed mesh's height.</summary>
    public static float Share { get; set; } = 0.15f;

    /// <summary>The most height above the ground (world units, 1 unit = 10 cm) a mesh blends from the ground's material to its own over.</summary>
    public static float MaxBand { get; set; } = 60;
}
