namespace Meitou.Rendering;

/// <summary>
/// The ground contact (docs/render-terrain.md, "Ground contact"), a Meitou look the game does not have: a TERRAIN-mode mesh's surface within
/// <see cref="Band"/> of the ground under it takes the ground's normal, slope layers and biomes, so a rock meets the terrain without a hard edge.
/// The <c>contact</c> switch (Enhancements).
/// </summary>
public static class GroundContact
{
    /// <summary>Read by every TERRAIN-mode mesh draw (TerrainRenderer's constants); false: the meshes as the game draws them.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The height above the ground (world units, 1 unit = 10 cm) over which a mesh blends from the ground's material to its own.</summary>
    public static float Band { get; set; } = 20;
}
