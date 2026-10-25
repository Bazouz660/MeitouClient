using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Meitou.Launcher;

// Play reports for the roadmap: "Works for me" / "Broken" on one roadmap system, with the version, platform and whether mods are on, and
// optionally the end of that version's log.txt. Sent to the reports service (services/Meitou.Reports); docs/play-reports.md.
public sealed partial class MainWindow
{
    /// <summary>The reports service; empty hides the Report button.</summary>
    const string ReportsUrl = "https://gear-pastry-chorus.cloudovna.app";
    const string RoadmapUrl = "https://meitou.martinvlcek.sk/roadmap.json";
    const int LogTail = 512 * 1024;

    void ShowReportButton() => ReportButton.IsVisible = ReportsUrl.Length > 0;

    async void Report_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        List<(string Id, string Name)> rows;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var file = JsonNode.Parse(await Http.GetStringAsync(RoadmapUrl, cts.Token));
            rows = (file?["rows"] as JsonArray ?? []).OfType<JsonObject>().Select(r => ((string)r["id"]!, (string)r["system"]!)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status($"Could not read the roadmap: {ex.Message}");
            return;
        }
        if (rows.Count == 0) { Status("The roadmap lists no systems."); return; }

        var secondary = (IBrush)this.FindResource("Secondary")!;
        var system = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320, PlaceholderText = "Roadmap system" };
        foreach (var (id, name) in rows) system.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        var works = new RadioButton { Content = "Works for me", GroupName = "verdict", IsChecked = true, Margin = new(0, 0, 16, 0) };
        var broken = new RadioButton { Content = "Broken", GroupName = "verdict" };
        var version = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in installedVersions) version.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        version.SelectedItem = version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == PlayVersion) ?? version.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var note = new TextBox { PlaceholderText = "What you did or saw (needed for Broken)", MaxLength = 300 };
        var attach = new CheckBox { Content = "Attach the end of this version's log.txt (it can show folder names, such as your user name)" };
        var problem = new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap };
        var send = new Button { Classes = { "paper" }, Content = new TextBlock { Text = "Send report", Foreground = secondary } };
        var cancel = new Button { Content = "Cancel", Margin = new(0, 0, 8, 0) };

        static Control Labelled(string label, Control control) =>
            new StackPanel { Spacing = 4, Children = { new TextBlock { Classes = { "desc" }, Text = label }, control } };
        var plate = new Border
        {
            Classes = { "plate" }, Width = 560, Padding = new(20, 16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Border { Classes = { "header" }, Child = new TextBlock { Text = "Report to the roadmap" } },
                    new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap, Text = "Tell the roadmap how one of Kenshi's systems plays in Meitou. Bugs with steps are best as a GitHub issue." },
                    Labelled("System", system),
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { works, broken } },
                    Labelled("Meitou version", version),
                    note,
                    attach,
                    problem,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, send } },
                },
            },
        };
        void Close()
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
        }
        cancel.Click += (_, _) => Close();
        plate.KeyDown += (_, k) => { if (k.Key == Key.Escape) Close(); };
        send.Click += async (_, _) =>
        {
            if (system.SelectedItem is not ComboBoxItem { Tag: string row }) { problem.Text = LauncherText.Translate("Pick a system."); return; }
            bool isBroken = broken.IsChecked == true;
            if (isBroken && string.IsNullOrWhiteSpace(note.Text)) { problem.Text = LauncherText.Translate("Say what happened."); return; }
            string? build = (version.SelectedItem as ComboBoxItem)?.Tag as string;
            var body = new JsonObject
            {
                ["row"] = row, ["verdict"] = isBroken ? "broken" : "works", ["build"] = build ?? "unknown", ["platform"] = Platform(),
                ["modded"] = mods.Any(m => m.Enabled), ["note"] = note.Text?.Trim(), ["source"] = "launcher",
            };
            if (attach.IsChecked == true && build is not null && ReadLogTail(build) is { } log) body["log"] = log;
            send.IsEnabled = false;
            problem.Text = LauncherText.Translate("Sending…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await Http.PostAsync($"{ReportsUrl}/reports", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string? error = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))?["error"];
                    throw new HttpRequestException((int)response.StatusCode == 429 ? "too many reports from here, try later" : error ?? $"HTTP {(int)response.StatusCode}");
                }
                Close();
                Status("Thanks: your report is on its way to the roadmap.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                problem.Text = $"{LauncherText.Translate("Not sent")}: {ex.Message}";
                send.IsEnabled = true;
            }
        };
        LauncherText.TranslateTree(plate, originalUiText);
        SetupHost.Child = plate;
        SetupHost.IsVisible = true;
        Dispatcher.UIThread.Post(() => system.Focus());
    }

    /// <summary>win, linux or mac; a Windows build under Wine or Proton (its registry has a Software\Wine key) is proton.</summary>
    static string Platform()
    {
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "mac";
        if (OperatingSystem.IsWindows())
        {
            using var wine = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine");
            if (wine is not null) return "proton";
        }
        return "win";
    }

    /// <summary>The last <see cref="LogTail"/> bytes of <paramref name="version"/>'s log.txt, from a whole line; null without one.</summary>
    static string? ReadLogTail(string version)
    {
        try
        {
            var path = Path.Combine(Releases.VersionDirectory(version), "log.txt");
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, file.Length - LogTail);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[file.Length - start];
            file.ReadExactly(bytes);
            int from = start > 0 ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
            return Encoding.UTF8.GetString(bytes, from, bytes.Length - from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
