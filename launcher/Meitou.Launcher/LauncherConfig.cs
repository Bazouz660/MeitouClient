using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>The launcher's settings and mod presets, in <c>launcher.json</c> in <see cref="DataDirectory"/>.</summary>
public sealed class LauncherConfig
{
    /// <summary>
    /// The launcher's own folder: <c>%LocalAppData%/Meitou</c> on Windows, <c>~/.local/share/Meitou</c> on Linux,
    /// <c>~/Library/Application Support/Meitou</c> on macOS. Holds <c>launcher.json</c> and is the game's working directory, so its
    /// <c>meitou.user.json</c> is shared by every version.
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Meitou");

    static string FilePath => Path.Combine(DataDirectory, "launcher.json");

    /// <summary>Where <c>versions/</c> and <c>downloads/</c> go: <see cref="InstallFolder"/>, else <see cref="DataDirectory"/>.</summary>
    public static string InstallDirectory { get; private set; } = DataDirectory;

    string? installFolder;
    public string? InstallFolder
    {
        get => installFolder;
        set
        {
            installFolder = string.IsNullOrWhiteSpace(value) ? null : value;
            InstallDirectory = installFolder ?? DataDirectory;
        }
    }

    /// <summary>The user confirmed the install and Kenshi folders in the first-start dialog.</summary>
    public bool FoldersConfirmed { get; set; }
    public string? KenshiPath { get; set; }
    public bool IncludePrereleases { get; set; }
    public string? SelectedVersion { get; set; }
    /// <summary>A game release the user chose not to be offered as an update; a newer one is offered again.</summary>
    public string? SkippedVersion { get; set; }
    public string LaunchArguments { get; set; } = "";
    /// <summary>Language of the launcher's interface, also written to Kenshi's settings.cfg when a matching locale or JSON catalog exists.</summary>
    public string UiLanguage { get; set; } = "en";
    /// <summary>An installed launcher applies its own update at startup and restarts into it.</summary>
    public bool AutoUpdateLauncher { get; set; } = true;
    public List<ModPreset> Presets { get; set; } = [];

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherConfig) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherConfig));
    }
}

/// <summary>A named mod list: mod names in load order, each enabled or not.</summary>
public sealed class ModPreset
{
    public string Name { get; set; } = "";
    public List<PresetMod> Mods { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class PresetMod
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>Source-generated JSON for the launcher's files, so it works trimmed.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LauncherConfig))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Releases.Cache))]
sealed partial class LauncherJson : System.Text.Json.Serialization.JsonSerializerContext;
