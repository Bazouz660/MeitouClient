using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Meitou.Localisation;
using Meitou.Settings;

namespace Meitou.Launcher;

// The Graphics page: the settings the version Play starts describes in its settings-schema.json, written to the game's
// meitou.user.json (GameSettings). Nothing about the settings is known here: renamed or new ones come with the release.
public sealed partial class MainWindow
{
    GameSettings game = GameSettings.Load();
    Process? running;
    DispatcherTimer? graphicsSave;
    int schemaVersion;

    // The schema of the version Play starts, and why there is none.
    (SettingsSchema? Schema, string? Problem) PlaySchema()
    {
        if (PlayVersion is not { } version) return (null, "Install a version first (Versions): its settings are shown here.");
        if (Releases.FindGame(Releases.VersionDirectory(version)) is not { } exe) return (null, $"Meitou {version} is not installed any more.");
        var path = Path.Combine(Path.GetDirectoryName(exe)!, SettingsSchema.FileName);
        if (SettingsSchema.Read(path) is { } schema) return (schema, null);
        return (null, File.Exists(path)
            ? $"Meitou {version} describes its settings in a newer way than this launcher reads: update the launcher (Settings)."
            : $"Meitou {version} is older than the Graphics page: change its settings in the game's Tab panel, or play a newer version.");
    }

    void ShowGraphics()
    {
        if (graphicsSave?.IsEnabled == true) SaveGame();
        game = GameSettings.Load();
        GraphicsPanel.Children.Clear();
        bool found = game.Version is not null;
        GraphicsFileText.Text = found ? GameSettings.FileName : Loc.Format(Tr("No {1} found"), GameSettings.FileName);
        ToolTip.SetTip(GraphicsFileText, GameSettings.FilePath);
        if (!found)
            GraphicsPanel.Children.Add(new TextBlock
            {
                Classes = { "desc" }, Margin = new(0, 0, 0, 8),
                Text = $"No {GameSettings.FileName} in {LauncherConfig.DataDirectory} yet: the game writes it when it first saves its settings "
                    + "(started with Play), and a change here creates it. Until then the game uses its defaults.",
            });
        var (schema, missing) = PlaySchema();
        if (schema is null)
        {
            GraphicsPanel.Children.Add(new TextBlock { Classes = { "desc" }, Text = missing });
            LauncherText.TranslateTree(GraphicsPanel, originalUiText);
            return;
        }
        schemaVersion = schema.SettingsVersion;
        var notes = new List<string>();
        string? locked = game.Problem is { } problem ? problem + " Start the game once to write it again; nothing is changed here until then."
            // The keys of another layout may be gone or mean something else; the game converts the file to its own when it next starts.
            : game.Version is { } v && v > schema.SettingsVersion ? $"The settings were saved by a newer Meitou than v{PlayVersion}: play that version, or change them in the game."
            : game.Version is { } w && w < schema.SettingsVersion ? $"The settings are from an older Meitou: start v{PlayVersion} once to update them, then change them here."
            : null;
        if (locked is not null) notes.Add(locked);
        if (running is { HasExited: false }) notes.Add("The game is running: it saves its settings when it closes, over changes made here.");
        if (SplitArguments(config.LaunchArguments).Contains("--low-end")) notes.Add("The launch arguments have --low-end: the game ignores the graphics sliders while it is set.");
        foreach (var note in notes) GraphicsPanel.Children.Add(new TextBlock { Classes = { "warn" }, Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });

        var rows = new StackPanel { IsEnabled = locked is null };
        foreach (var section in schema.Sections)
        {
            rows.Children.Add(Section(section.Name));
            foreach (var s in section.Settings)
                if (s.Kind == SettingKind.Toggle && !s.Graphics) rows.Children.Add(ToggleRow(s));
                else if (s.Kind == SettingKind.Slider && s.Max > s.Min) rows.Children.Add(SliderRow(s));
        }
        var reset = new Button { Content = "Reset", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) =>
        {
            // Two clicks: the sliders tuned in the game's Tab panel are not undone by a stray one.
            if (reset.Tag is null) { reset.Tag = true; reset.Content = Tr("Click again"); return; }
            game.ResetGraphics();
            SaveGame();
            ShowGraphics();
        };
        rows.Children.Add(Section("Reset"));
        rows.Children.Add(Row("Reset graphics", "Every graphics slider and Faithful / Meitou switch back to the game's defaults, also those only in the game's Tab panel.", reset, 150));
        GraphicsPanel.Children.Add(rows);
        LauncherText.TranslateTree(GraphicsPanel, originalUiText);
    }

    TextBlock Section(string text) => new() { Classes = { "caps" }, Text = Tr(text).ToUpperInvariant(), Margin = new(0, 14, 0, 2) };

    static Border Row(string name, string description, Control control, int width)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{width}") };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Classes = { "name" }, Text = name }, new TextBlock { Classes = { "desc" }, Text = description } },
        });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Border { Classes = { "row" }, Child = grid };
    }

    Border ToggleRow(SettingDescription s)
    {
        bool value = game.GetBool(s.Key) ?? s.Default >= 0.5f;
        var toggle = new ToggleButton { IsChecked = value, Content = Tr(value ? s.On : s.Off), HorizontalAlignment = HorizontalAlignment.Stretch };
        toggle.Click += (_, _) =>
        {
            bool v = toggle.IsChecked == true;
            toggle.Content = Tr(v ? s.On : s.Off);
            game.Set(s.Key, v);
            SaveGame();
        };
        return Row(s.Name, s.Description, toggle, 150);
    }

    /// <summary>A slider with its value and a Default button: a graphics slider's key is removed (the game's default for the mode),
    /// a top-level one gets the schema's default.</summary>
    Border SliderRow(SettingDescription s)
    {
        bool log = s.Logarithmic && s.Min > 0;
        float step = s.Step > 0 ? s.Step : 1;
        // A reversed setting's value is mirrored onto the slider (Min + Max - value), so right is always the better end.
        float Mirror(float v) => s.Reversed ? s.Min + s.Max - v : v;
        float ToSlider(float v) => log ? MathF.Log(Mirror(v)) : Mirror(v);
        float FromSlider(double x) => Math.Clamp(Mirror(MathF.Round((log ? MathF.Exp((float)x) : (float)x) / step) * step), s.Min, s.Max);
        float? Get() => s.Graphics ? game.GetGraphics(s.Key) : game.GetNumber(s.Key) ?? s.Default;
        // The range itself is not mirrored: Min..Max either way.
        var slider = new Slider { Minimum = log ? MathF.Log(s.Min) : s.Min, Maximum = log ? MathF.Log(s.Max) : s.Max, Margin = new(0, 0, 10, 0) };
        var value = new TextBlock { Classes = { "desc" }, Width = 120, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        var reset = new Button { Content = Tr("Default"), Margin = new(6, 0, 0, 0), IsEnabled = s.Graphics || s.Default is not null };
        bool loading = false;
        void Show()
        {
            loading = true;
            var v = Get();
            slider.Value = ToSlider(Math.Clamp(v ?? s.Shown, s.Min, s.Max));
            value.Text = Tr(v is { } x ? s.Text(x) : "Game default");
            loading = false;
        }
        slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            float v = FromSlider(e.NewValue);
            value.Text = Tr(s.Text(v));
            if (s.Graphics) game.SetGraphics(s.Key, v);
            else game.Set(s.Key, v);
            SaveGameSoon();
        };
        reset.Click += (_, _) =>
        {
            if (s.Graphics) game.SetGraphics(s.Key, null);
            else if (s.Default is { } d) game.Set(s.Key, d);
            SaveGame();
            Show();
        };
        Show();
        var dock = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(reset);
        dock.Children.Add(value);
        dock.Children.Add(slider);
        return Row(s.Name, s.Description, dock, 460);
    }

    // Dragging a slider writes the file once it rests, not on every step.
    void SaveGameSoon()
    {
        graphicsSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => SaveGame());
        graphicsSave.Stop();
        graphicsSave.Start();
    }

    void SaveGame()
    {
        graphicsSave?.Stop();
        try
        {
            if (game.Save(schemaVersion)) Status("Graphics settings saved; the game reads them when it starts.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status($"Could not write {GameSettings.FileName}: {e.Message}");
        }
    }
}
