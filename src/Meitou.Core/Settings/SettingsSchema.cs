using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Settings;

/// <summary>
/// The settings a game release describes for the launcher: <c>settings-schema.json</c> beside its executable, written by the game
/// (<c>--settings-schema</c>) from the same names it saves under, so the launcher never repeats them (docs/launcher.md "Graphics").
/// </summary>
public sealed class SettingsSchema
{
    public const string FileName = "settings-schema.json";
    /// <summary>The layout of this file. Adding properties keeps it; anything a reader would misread raises it, and the launcher then shows nothing.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    /// <summary>The <c>meitou.user.json</c> layout this release reads and writes (its <c>version</c> key).</summary>
    public int SettingsVersion { get; set; }
    /// <summary>The frame renderers this release contains; an older schema without the key is Vulkan-only.</summary>
    public List<string> Renderers { get; set; } = ["vulkan"];
    public List<SettingsSection> Sections { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, SettingsSchemaJson.Default.SettingsSchema);

    /// <summary>The file's schema, or null when it is missing, unreadable or of a newer format.</summary>
    public static SettingsSchema? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var schema = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsSchemaJson.Default.SettingsSchema);
            return schema is { Format: <= CurrentFormat } ? schema : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

public sealed class SettingsSection
{
    public string Name { get; set; } = "";
    public List<SettingDescription> Settings { get; set; } = [];
}

public enum SettingKind { Toggle, Slider }

/// <summary>One setting: a top-level key of <c>meitou.user.json</c>, or (<see cref="Graphics"/>) a Tab panel slider under <c>graphics</c> by its label.</summary>
public sealed record SettingDescription
{
    public string Key { get; set; } = "";
    public bool Graphics { get; set; }
    public SettingKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The value when the key is missing (a toggle: 1 on, 0 off); null when it depends on the mode, and the slider then sits at <see cref="Shown"/>.</summary>
    public float? Default { get; set; }
    public float Shown { get; set; }
    /// <summary>A toggle's two states.</summary>
    public string On { get; set; } = "On";
    public string Off { get; set; } = "Off";
    public float Min { get; set; }
    public float Max { get; set; } = 1;
    public float Step { get; set; } = 1;
    public bool Logarithmic { get; set; }
    /// <summary>A slider whose stored value falls as the setting improves (texture quality's level 0 is Maximum): drawn mirrored, so
    /// right is always more; the value stored stays the same.</summary>
    public bool Reversed { get; set; }
    /// <summary>How a slider value reads: <see cref="Labels"/>[rounded value] when given, else value x <see cref="Scale"/> in <see cref="Format"/>
    /// plus <see cref="Suffix"/>; below <see cref="OffBelow"/> it reads <see cref="OffText"/>.</summary>
    public string[]? Labels { get; set; }
    public string Format { get; set; } = "0";
    public float Scale { get; set; } = 1;
    public string Suffix { get; set; } = "";
    public float? OffBelow { get; set; }
    public string OffText { get; set; } = "Off";

    public string Text(float value)
    {
        if (OffBelow is { } off && value < off) return OffText;
        if (Labels is { Length: > 0 } labels) return labels[Math.Clamp((int)MathF.Round(value - Min), 0, labels.Length - 1)];
        return (value * Scale).ToString(Format, System.Globalization.CultureInfo.InvariantCulture) + Suffix;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsSchema))]
sealed partial class SettingsSchemaJson : JsonSerializerContext;
