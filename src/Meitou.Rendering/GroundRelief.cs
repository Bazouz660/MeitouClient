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

    /// <summary>Depth of the full height range in world units (1 unit = 10 cm), for both the parallax and the displacement; the Tab panel's slider sets it.</summary>
    public static float Depth { get; set; } = 1.2f;

    /// <summary>Whether the ground near the eye is raised for real (tessellated patches, where the device has tessellation shaders); the Tab panel toggles it.</summary>
    public static bool DisplaceActive { get; set; } = true;

    /// <summary>Patches within this horizontal distance of the eye are drawn tessellated: beyond TerrainShaders' DisplaceEnd (250), the end of the raising.</summary>
    public const float TessellatedRadius = 260;
}
