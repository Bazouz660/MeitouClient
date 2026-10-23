namespace Meitou.Rendering;

/// <summary>
/// Parallax occlusion on the sand layers (docs/render-terrain.md, "Sand parallax"), a Meitou look the game does not have: each sand layer gets a height
/// map integrated from its normal map (<see cref="NormalHeight"/>), and the terrain shader marches the view ray through it, with a self-shadow toward
/// the sun, on the patches near the eye. Other layers get a flat height (no offset).
/// </summary>
public static class SandParallax
{
    /// <summary>Read when the terrain textures are created (the height array is made then or not at all).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether the patches march the height (read every frame; the viewer's K and Tab checkbox toggle it to compare).</summary>
    public static bool Active { get; set; } = true;

    /// <summary>Depth of the full height range in world units (1 unit = 10 cm); the Tab panel's slider sets it.</summary>
    public static float Depth { get; set; } = 1.2f;

    /// <summary>A sand layer by its normal map's name: "sand" in it, not sandstone or rock.</summary>
    public static bool IsSand(string? name)
    {
        if (name is null) return false;
        var file = Path.GetFileName(name.Replace('\\', '/')).ToLowerInvariant();
        return file.Contains("sand") && !file.Contains("stone") && !file.Contains("rock");
    }
}
