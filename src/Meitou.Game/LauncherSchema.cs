using System.Text.Json;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Settings;

namespace Meitou.Game;

/// <summary>
/// The settings the launcher's Graphics page shows (docs/launcher.md "Graphics"), written by <c>--settings-schema &lt;file&gt;</c> into each
/// release (tools/scripts/package-release.ps1). The keys are the ones this release saves under, so a renamed setting renames its launcher row.
/// </summary>
static class LauncherSchema
{
    static string Key(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    static SettingDescription Graphics(string label, float min, float max, float step, float shown) => new()
    {
        Key = label, Graphics = true, Kind = SettingKind.Slider, Name = SettingsMenu.Label(label), Description = SettingsMenu.SliderHint(label),
        Min = min, Max = max, Step = step, Shown = shown,
    };

    public static SettingsSchema Create()
    {
        var defaults = new UserConfig();
        return new SettingsSchema
        {
            SettingsVersion = UserConfig.CurrentVersion,
            Sections =
            [
                new SettingsSection
                {
                    Name = "Display",
                    Settings =
                    [
                        new() { Key = Key(nameof(UserConfig.FullScreen)), Kind = SettingKind.Toggle, Name = "Full screen", Description = SettingsMenu.SliderHint("Full screen"),
                            Default = defaults.FullScreen ? 1 : 0, On = "Borderless", Off = "Window" },
                        new() { Key = Key(nameof(UserConfig.VSync)), Kind = SettingKind.Toggle, Name = "VSync", Description = SettingsMenu.SliderHint("VSync"),
                            Default = defaults.VSync ? 1 : 0 },
                        new() { Key = Key(nameof(UserConfig.FpsLimit)), Kind = SettingKind.Slider, Name = "Frame limit", Description = "Frames per second when VSync is off.",
                            Default = defaults.FpsLimit, Shown = defaults.FpsLimit, Min = 0, Max = 360, Step = 5, Suffix = " fps", OffBelow = 1, OffText = "Unlimited" },
                    ],
                },
                new SettingsSection
                {
                    Name = "Graphics",
                    // Null defaults: the game's value depends on the Faithful / Meitou mode until the slider is saved.
                    Settings =
                    [
                        Graphics(WorldFrame.TextureQualitySlider, 0, Meitou.Data.Textures.TextureQuality.Maximum, 1, Meitou.Data.Textures.TextureQuality.Default) with
                            { Labels = [.. Meitou.Data.Textures.TextureQuality.Labels] },
                        Graphics(WorldFrame.ObjectDistanceSlider, 1000, 400000, 100, Enhancements.MeitouObjectDistance) with { Logarithmic = true },
                        Graphics(WorldFrame.ShadowDistanceSlider, KenshiShadows.MinRange * 0.9f, 200000, 100, Enhancements.MeitouShadowRange) with
                            { Logarithmic = true, OffBelow = KenshiShadows.MinRange },
                        Graphics(WorldFrame.ShadowMapSlider, 0, 2, 1, 2) with { Labels = [.. KenshiShadows.QualitySizes.Select(s => $"{s}²")] },
                        Graphics(WorldFrame.UpscalerSliders[0], 0, 5, 1, 3) with { Name = "Anti-aliasing", Labels = ["Off", "FXAA", "SMAA", "TAA", "FSR", "DLSS"] },
                        Graphics(WorldFrame.UpscalerSliders[1], 0.33f, 1, 0.01f, 1) with { Scale = 100, Suffix = " %" },
                        Graphics(WorldFrame.FilteringSlider, 0, 4, 1, 4) with { Labels = ["1x", "2x", "4x", "8x", "16x"] },
                        Graphics(WorldFrame.WaterReflectionSlider, 0, 4, 1, 2) with { Labels = ["Off", "Landscape", "Characters", "Buildings", "Everything"] },
                        Graphics(WorldFrame.GrassDensitySlider, 0.1f, 2, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
                        Graphics(WorldFrame.ParticleSliders[0], 0, 4, 1, 3) with { Name = "Weather particles", Labels = ["Off", "Quarter size", "Half size", "Auto", "Full size"] },
                        Graphics(WorldFrame.ParticleSliders[1], 0.1f, 1, 0.05f, 1) with { Format = "0.00", Suffix = "x" },
                    ],
                },
            ],
        };
    }

    /// <summary><c>--settings-schema &lt;file&gt;</c>: writes the schema and exits, before anything else starts.</summary>
    public static int Write(string path)
    {
        File.WriteAllText(path, Create().ToJson());
        Console.WriteLine($"schema    {path}");
        return 0;
    }
}
