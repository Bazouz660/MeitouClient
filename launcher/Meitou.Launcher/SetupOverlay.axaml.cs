using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The first-start dialog over the main window: the install folder and the Kenshi folder, prefilled with the current (or detected)
/// ones.
/// </summary>
public sealed partial class SetupOverlay : UserControl
{
    readonly LauncherConfig config = new();
    GameInstall? install;

    /// <summary>Raised once Continue saved the folders, with the Kenshi install chosen (null when none was).</summary>
    public event Action<GameInstall?>? Confirmed;

    public SetupOverlay() => InitializeComponent();

    public SetupOverlay(LauncherConfig config, GameInstall? found)
    {
        this.config = config;
        InitializeComponent();
        InstallBox.Text = LauncherConfig.InstallDirectory;
        ShowKenshi(found, found?.Root ?? config.KenshiPath);
    }

    void ShowKenshi(GameInstall? found, string? text)
    {
        install = found;
        KenshiBox.Text = text ?? "";
        KenshiStatus.Text = found is not null ? "The folder with data/gamedata.base."
            : string.IsNullOrWhiteSpace(text) ? "Not found: browse to the folder with data/gamedata.base."
            : "Not a Kenshi folder: data/gamedata.base not found.";
    }

    void SetKenshi(string path)
    {
        path = path.Trim().Trim('"');
        ShowKenshi(GameInstall.IsValid(path) ? GameInstall.Open(path) : null, path);
    }

    void FindKenshi_Click(object? sender, RoutedEventArgs e)
    {
        var found = GameInstall.Locate();
        ShowKenshi(found, found?.Root ?? KenshiBox.Text);
    }

    async void BrowseKenshi_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Kenshi folder") is { } path) SetKenshi(path);
    }

    void KenshiBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (KenshiBox.Text is { } path && path != install?.Root) SetKenshi(path);
    }

    async void BrowseInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Install folder") is { } path) InstallBox.Text = path;
    }

    async Task<string?> PickFolder(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders is [var folder] ? folder.TryGetLocalPath() : null;
    }

    async void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (KenshiBox.Text is { } typed && typed != install?.Root) SetKenshi(typed);
        var folder = (InstallBox.Text ?? "").Trim().Trim('"');
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) { Problem.Text = "Choose a full path for the install folder."; return; }
        var previous = LauncherConfig.InstallDirectory;
        IsEnabled = false;
        try
        {
            Directory.CreateDirectory(folder);
            Problem.Text = "Moving installed versions…";
            await Task.Run(() => Releases.MoveInstalled(previous, folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem.Text = $"Could not use {folder}: {ex.Message}";
            IsEnabled = true;
            return;
        }
        config.InstallFolder = Path.GetFullPath(folder) == Path.GetFullPath(LauncherConfig.DataDirectory) ? null : folder;
        if (install is not null) config.KenshiPath = install.Root;
        config.FoldersConfirmed = true;
        config.Save();
        Confirmed?.Invoke(install);
    }
}
