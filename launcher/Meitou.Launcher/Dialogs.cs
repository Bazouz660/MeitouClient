using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Meitou.Launcher;

// Yes / no questions as a plate over the dimmed window (SetupHost), placed so its Yes button is under the pointer that asked.
public sealed partial class MainWindow
{
    Point? pointer;   // the last pointer position over the window, in window coordinates

    void TrackPointer() => AddHandler(PointerMovedEvent, (_, e) => pointer = e.GetPosition(this), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

    /// <summary>Asks; true for <paramref name="yes"/>. No, Escape and closing the question answer false.</summary>
    Task<bool> AskAsync(string title, string text, string yes, string no)
    {
        var answer = new TaskCompletionSource<bool>();
        var yesButton = new Button { Classes = { "paper" }, Content = new TextBlock { Text = yes, Foreground = (Avalonia.Media.IBrush)this.FindResource("Secondary")! } };
        var noButton = new Button { Content = no, Margin = new(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0), Children = { noButton, yesButton } };
        var plate = new Border
        {
            Classes = { "plate" }, Width = 520, Padding = new(20, 16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Classes = { "header" }, Margin = new(0, 0, 0, 10), Child = new TextBlock { Text = title } },
                    new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.Medium },
                    buttons,
                },
            },
        };
        void Close(bool result)
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
            answer.TrySetResult(result);
        }
        yesButton.Click += (_, _) => Close(true);
        noButton.Click += (_, _) => Close(false);
        plate.KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
        // Once laid out: move the plate so Yes is centred on the pointer, kept inside the host; then show it. Without a pointer (keyboard) it
        // sits bottom right, over the footer.
        void Place(object? sender, EventArgs e)
        {
            if (plate.Bounds.Width <= 0) return;
            plate.LayoutUpdated -= Place;
            var host = SetupHost.Bounds.Size;
            var yesCentre = yesButton.TranslatePoint(new Point(yesButton.Bounds.Width / 2, yesButton.Bounds.Height / 2), plate) ?? default;
            var target = pointer is { } p && this.TranslatePoint(p, SetupHost) is { } at ? at - yesCentre
                : new Point(host.Width - plate.Bounds.Width - 14, host.Height - plate.Bounds.Height - 12);
            plate.Margin = new(Math.Clamp(target.X, 0, Math.Max(0, host.Width - plate.Bounds.Width)), Math.Clamp(target.Y, 0, Math.Max(0, host.Height - plate.Bounds.Height)), 0, 0);
            plate.Opacity = 1;
        }
        plate.LayoutUpdated += Place;
        LauncherText.TranslateTree(plate, UiLanguage, originalUiText);
        SetupHost.Child = plate;
        SetupHost.IsVisible = true;
        noButton.Focus();
        return answer.Task;
    }
}
