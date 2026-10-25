using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>A line of the Mods page.</summary>
public sealed class ModRow(ModInfo mod, bool enabled, string language) : INotifyPropertyChanged
{
    string uiLanguage = language;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ModInfo Mod { get; } = mod;
    public string Name => Mod.Name;
    public string Detail => LauncherText.Translate(Mod.Source == ModSource.Workshop ? "Steam workshop" : "Local mod", uiLanguage)
        + (Mod.Header is { Author.Length: > 0 } h ? $" · {h.Author}" : "");
    public string Notes => Mod.HasNativePlugin ? LauncherText.Translate("RE_Kenshi plugin: its DLLs are ignored in Meitou", uiLanguage) : "";
    public bool HasNotes => Mod.HasNativePlugin;

    public bool Enabled
    {
        get;
        set { if (field != value) { field = value; Changed(); } }
    } = enabled;

    public void SetLanguage(string language)
    {
        if (uiLanguage == language) return;
        uiLanguage = language;
        Changed(nameof(Detail));
        Changed(nameof(Notes));
    }

    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A line of the Versions page.</summary>
public sealed class ReleaseRow(Release release, bool installed, bool played, string language)
{
    public Release Release { get; } = release;
    public bool Installed { get; } = installed;
    public string Title => Release.Title;
    public string Detail => string.Join(" · ", new[]
    {
        Release.Published.ToString("yyyy-MM-dd"),
        Release.Prerelease ? LauncherText.Translate("nightly", language) : null,
        Release.Asset is null ? LauncherText.Translate($"no {Releases.Rid} build", language) : null,
    }.OfType<string>());
    /// <summary>The version Play starts.</summary>
    public bool Played { get; } = played;
    public string State => Played ? LauncherText.Translate("Installed · Play", language) : Installed ? LauncherText.Translate("Installed", language) : "";
}
