using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>A line of the Mods page.</summary>
public sealed class ModRow(ModInfo mod, bool enabled) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ModInfo Mod { get; } = mod;
    public string Name => Mod.Name;
    public string Detail => (Mod.Source == ModSource.Workshop ? "Steam workshop" : "Local mod")
        + (Mod.Header is { Author.Length: > 0 } h ? $" · {h.Author}" : "");
    public string Notes => Mod.HasNativePlugin ? "RE_Kenshi plugin: its DLLs are ignored in Meitou" : "";
    public bool HasNotes => Mod.HasNativePlugin;

    public bool Enabled
    {
        get;
        set { if (field != value) { field = value; Changed(); } }
    } = enabled;

    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A line of the Versions page.</summary>
public sealed class ReleaseRow(Release release, bool installed, bool played)
{
    public Release Release { get; } = release;
    public bool Installed { get; } = installed;
    public string Title => Release.Title;
    public string Detail => string.Join(" · ", new[]
    {
        Release.Published.ToString("yyyy-MM-dd"),
        Release.Prerelease ? "nightly" : null,
        Release.Asset is null ? $"no {Releases.Rid} build" : null,
    }.OfType<string>());
    /// <summary>The version Play starts.</summary>
    public bool Played { get; } = played;
    public string State => Played ? "Installed · Play" : Installed ? "Installed" : "";
}
