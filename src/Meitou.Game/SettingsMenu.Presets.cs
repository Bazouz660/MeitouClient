using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>The Graphics page's quality presets and its all-Faithful / all-Meitou switch (docs/native-ui.md "Settings").</summary>
sealed partial class SettingsMenu
{
    static readonly string[] PresetNames = ["Low", "Medium", "High", "Ultra", "Custom"];
    const int HighPreset = 2, CustomPreset = 4;

    /// <summary>
    /// A preset's slider: its value at Low, Medium and Ultra (High is the start default), either fixed or a multiple of the start default.
    /// <see cref="Mode"/> names the switch whose side picks the base instead (the slider's Meitou or Faithful mark), so High reads High in both looks.
    /// </summary>
    sealed record PresetSlider(string Label, float Low, float Medium, float Ultra, bool Scale = false, string? Mode = null, float? High = null);

    static readonly PresetSlider[] PresetSliders =
    [
        new(WorldFrame.ObjectDistanceSlider, 0.25f, 0.6f, 2, Mode: "Draw distances"),
        new("Landmark distance (Meitou)", 0.25f, 0.6f, 1.5f, Scale: true),
        new("Distant towns (zones)", 3, 6, float.NaN),
        new("Object LOD distance x", 0.25f, 0.6f, 2),
        new("Terrain detail: error px (less = finer)", 2, 1.5f, 0.6f, Mode: "Draw distances"),
        new("Foliage draw distance x", 0.25f, 0.5f, 2, Scale: true),
        new("Foliage LOD distance x (lod Meitou)", 0.5f, 0.75f, 2),
        new("Large foliage range (F6 Meitou)", 0.1f, 0.5f, 1.5f, Scale: true),
        new("Medium foliage range", 0.1f, 0.5f, 1.5f, Scale: true),
        new("Small foliage range", 0.1f, 0.5f, 1.5f, Scale: true),
        new("Impostor distance (F7 Meitou)", 0.3f, 0.6f, 1.5f, Scale: true),
        new("Large impostor distance (F7 Meitou)", 0.1f, 0.5f, 1.5f, Scale: true),
        new("Grass draw distance x", 0.25f, 0.5f, 2, Scale: true),
        new(WorldFrame.GrassDensitySlider, 0.2f, 0.5f, float.NaN),
        new(WorldFrame.WaterReflectionSlider, 0, 1, 4),
        new("Reflection range x (game 0.6)", 0.6f, 1, 6),
        // Low's shadow distance is the slider's left end: shadows off.
        new(WorldFrame.ShadowDistanceSlider, float.NegativeInfinity, 0.6f, 1.5f, Mode: "Shadows"),
        new(WorldFrame.ShadowMapSlider, 0, 1, 2),
        new("Shadow filter 0-2 (Meitou; 0 low-end)", 0, 1, 2),
        new(WorldFrame.ParticleSliders[1], 0.25f, 0.5f, float.NaN),
        new(WorldFrame.FilteringSlider, 1, 2, 4, High: 4),
        // The start default is the saved level (applied before the menu exists), so High names the full size.
        new(WorldFrame.TextureQualitySlider, 3, 1, 0, High: 0),
    ];

    /// <summary>The costliest Meitou effects, set Faithful by Low (as <c>--low-end</c> does); no preset turns a switch on.</summary>
    static readonly string[] LowOffSwitches = ["Ambient occlusion", "Indirect light", "Light shafts", "Clouds", "Cloud shadows", "Water", "Shadows"];
    const string FifthCascade = "Shadow cascade 5 (Meitou)";

    /// <summary>The Faithful / Meitou switches whose reading stays put when set (Indirect light without ray queries); the all switch ignores them.</summary>
    readonly HashSet<string> stuckSwitches = [];

    Entry PresetEntry => new("Quality preset", "Sets draw distances, foliage, shadow, water, particle and texture settings together; High is the default. "
        + "Low also turns the costliest Meitou effects off (indirect light, light shafts, clouds, ambient occlusion, Meitou shadows and water). Anti-aliasing and render scale stay as they are.",
        Choice: new(() => PresetNames, CurrentPreset, ApplyPreset));

    float PresetTarget(PresetSlider p, Slider slider, int preset)
    {
        float start = 0;
        for (int i = 0; i < values.Sliders.Count; i++) if (ReferenceEquals(values.Sliders[i], slider)) start = defaults[i];
        if (p.Mode is { } mode && values.Toggles.FirstOrDefault(t => t.Label == mode) is { } toggle && (toggle.Get() ? slider.Meitou : slider.Faithful) is { } mark)
            start = mark;
        float factor = preset switch { 0 => p.Low, 1 => p.Medium, 3 => p.Ultra, _ => float.NaN };
        float value = preset == HighPreset ? p.High ?? start
            : float.IsNaN(factor) ? p.High ?? start
            : float.IsNegativeInfinity(factor) ? slider.Min
            : p.Scale || p.Mode is not null ? start * factor : factor;
        // A Faithful shadow distance stays within the game's slider.
        if (p.Mode == "Shadows" && !values.Toggles.Any(t => t.Label == "Shadows" && t.Get()) && value > slider.Min)
            value = Math.Min(value, global::Meitou.Data.World.KenshiShadows.MaxRange);
        return Math.Clamp(value, slider.Min, slider.Max);
    }

    IEnumerable<(PresetSlider Preset, Slider Slider)> PresetPairs() =>
        PresetSliders.Select(p => (p, values.Sliders.FirstOrDefault(s => s.Label == p.Label))).Where(x => x.Item2 is not null)!;

    bool CascadeFor(int preset) => preset switch { 0 or 1 => false, 3 => true, _ => toggleDefaults[values.Toggles.Select(t => t.Label).ToList().IndexOf(FifthCascade)] };

    /// <summary>The preset every preset slider (and the fifth cascade) sits at, else <see cref="CustomPreset"/>; the switches do not count.</summary>
    int CurrentPreset()
    {
        var cascade = values.Toggles.FirstOrDefault(t => t.Label == FifthCascade);
        for (int preset = 0; preset < CustomPreset; preset++)
            if (PresetPairs().All(x => MathF.Abs(x.Slider.FractionOf(x.Slider.Get()) - x.Slider.FractionOf(PresetTarget(x.Preset, x.Slider, preset))) <= 0.005f)
                && (cascade is null || cascade.Get() == CascadeFor(preset)))
                return preset;
        return CustomPreset;
    }

    void ApplyPreset(int preset)
    {
        if (preset is < 0 or >= CustomPreset) return;
        // The switches first: they move the distances that sit at their mode's default, and the mode decides the distances' base.
        if (preset == 0)
        {
            foreach (var toggle in values.Toggles.Where(t => t.Remaster && LowOffSwitches.Contains(t.Label) && t.Get())) toggle.Set(false);
            if (values.Sliders.FirstOrDefault(s => s.Label == WorldFrame.ParticleSliders[0]) is { } particles) particles.Set(1);   // a quarter of the render size
        }
        foreach (var (p, slider) in PresetPairs()) slider.Set(PresetTarget(p, slider, preset));
        if (values.Toggles.FirstOrDefault(t => t.Label == FifthCascade) is { } cascade && cascade.Get() != CascadeFor(preset)) cascade.Set(CascadeFor(preset));
        notice = $"{PresetNames[preset]} quality applied.";
    }

    /// <summary>
    /// A page's all-Faithful / all-Meitou switch over its Faithful / Meitou <paramref name="toggles"/> and the <paramref name="sliders"/> with both marks;
    /// it reads 1 when all are Meitou, 0 when all are Faithful, -1 when they are mixed.
    /// </summary>
    Entry SidesEntry(string label, string hint, string what, IReadOnlyList<Toggle> toggles, IReadOnlyList<Slider> sliders) => new(label, hint, Segmented: true,
        Choice: new(() => ["Faithful", "Meitou"], () =>
        {
            var live = toggles.Where(t => !stuckSwitches.Contains(t.Label)).ToList();
            bool AtMark(Slider s, float? mark) => mark is { } m && MathF.Abs(s.Get() - m) <= 0.002f * (s.Max - s.Min);
            return live.All(t => t.Get()) && sliders.All(s => AtMark(s, s.Meitou)) ? 1 : live.All(t => !t.Get()) && sliders.All(s => AtMark(s, s.Faithful)) ? 0 : -1;
        }, side =>
        {
            bool meitou = side == 1;
            foreach (var toggle in toggles.Where(t => t.Get() != meitou))
            {
                toggle.Set(meitou);
                if (toggle.Get() != meitou) stuckSwitches.Add(toggle.Label);
            }
            foreach (var slider in sliders) slider.Set((meitou ? slider.Meitou : slider.Faithful)!.Value);
            notice = meitou ? $"Every {what} is Meitou." : $"Every {what} is as the game ships it.";
        }));

    Entry LookEntry => SidesEntry("All graphics features", "Every graphics feature as the game ships it, or every Meitou improvement. The quality settings stay as they are.",
        "graphics feature", values.Toggles.Where(t => t.Remaster).ToList(), []);

    /// <summary>The Advanced or Fixes page's all switch, over that page's Faithful / Meitou switches (not the plain on/off ones such as luckyren Mode) and sliders with both marks.</summary>
    Entry PageSidesEntry(List<Entry> entries, string label, string hint, string what) => SidesEntry(label, hint, what,
        entries.Select(e => e.Toggle).OfType<Toggle>().Where(t => t.Remaster).ToList(),
        entries.Select(e => e.Slider).OfType<Slider>().Where(s => s.Faithful is not null && s.Meitou is not null).ToList());
}
