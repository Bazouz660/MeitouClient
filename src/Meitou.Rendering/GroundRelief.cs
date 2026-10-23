namespace Meitou.Rendering;

/// <summary>
/// Relief on the ground's base layers (docs/render-terrain.md, "Ground relief"), a Meitou look the game does not have: each layer gets a height map
/// integrated from its normal map (<see cref="NormalHeight"/>). Near the eye the terrain is tessellated and raised by it (the displacement); further
/// out the terrain shader marches the view ray through it (the parallax), and toward the sun for a self-shadow everywhere it reaches.
/// </summary>
public static class GroundRelief
{
    /// <summary>Read when the terrain textures are created (the height array is made then or not at all).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether the patches march the height (read every frame; the viewer's K and Tab checkbox toggle it to compare).</summary>
    public static bool ParallaxActive { get; set; } = true;

    /// <summary>Depth of a typical layer's full height range in world units (1 unit = 10 cm; each layer scales it by LayerDepth), for both the parallax and the displacement; the Tab panel's slider sets it.</summary>
    public static float Depth { get; set; } = 2.0f;

    /// <summary>Whether the ground near the eye is raised for real (tessellated patches, where the device has tessellation shaders); the Tab panel toggles it.</summary>
    public static bool DisplaceActive { get; set; } = true;

    /// <summary>Patches within this horizontal distance of the eye are drawn tessellated: beyond TerrainShaders' DisplaceEnd (250), the end of the raising.</summary>
    public const float TessellatedRadius = 260;

    /// <summary>
    /// A layer's depth as a multiple of <see cref="Depth"/>, from its normal map's own relief (<see cref="NormalHeight.FromNormals"/>, a fraction of the tile):
    /// relative to a typical ground's (<see cref="TypicalRelief"/>), square-rooted and clamped to 0.3..1.5, so smooth sand stays shallow and gravel deep,
    /// but the game's 450-fold spread (0.0002 to 0.09; docs/render-terrain.md) is not copied: its normal maps' strengths are a look, not a measure.
    /// </summary>
    public static float LayerDepth(float relief) => Math.Clamp(MathF.Sqrt(relief / TypicalRelief), 0.3f, 1.5f);

    /// <summary>The median relief of the base layers' normal maps (<b>Observed</b>, 76 biomes, decoded at 512²).</summary>
    public const float TypicalRelief = 0.0042f;
}
