using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    internal NewGameSettings NewGameOptions { get; private set; } = new();
    bool advancedOpen;
    NewGameHelpCatalog advancedHelp = new();

    // Both panels belong to the same modal screen, so the lower panel cannot block Begin, Cancel or the toggle above it.
    static MyGuiLayout WithAdvancedPanel(MyGuiLayout start, MyGuiLayout advanced)
    {
        static MyGuiWidget Prefix(MyGuiWidget w) => w with { Name = "advanced-" + w.Name, Children = w.Children.Select(Prefix).ToArray() };
        var panel = Prefix(advanced.Roots[0]) with { Type = "Widget" };
        return start with { Roots = [.. start.Roots, panel] };
    }

    void BindNewGamePanels(UiBindings b)
    {
        // The start panel keeps the layout's size, centred; expanding raises it by half the options panel.
        var root = faithfulScreens.Layout("new-game").Roots[0];
        var size = root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition);
        float scale = Height / 1080f, width = Math.Min(size.Width, Width - 24), mainHeight = Math.Min(size.Height, Height - 24);
        float extra = advancedOpen ? Math.Max(0, Math.Min(Height * .43f, Height - 24 - mainHeight)) : 0;
        float x = (Width - width) / 2, y = (Height - mainHeight - extra) / 2;
        b.Positions["Root"] = new(x, y, width, mainHeight);
        b.Positions["advanced-Root"] = new(x, y + mainHeight, width, extra);
        if (!advancedOpen) b.Hidden.Add("advanced-Root");
        b.Positions["advanced-Datapanel"] = new(x + 12 * scale, y + mainHeight + 42 * scale, width - 24 * scale, extra - 42 * scale - 80 * scale);
        b.Positions["advanced-TooltipPanel"] = new(x + 12 * scale, y + mainHeight + extra - 74 * scale, width - 24 * scale, 62 * scale);
    }

    bool AdvancedAction(MyGuiEvent e)
    {
        var parts = e.Action.Split(':');
        if (parts.Length < 2 || !parts[0].StartsWith("advanced-", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out int i) || i < 0 || i >= NewGameSettings.Options.Length) return false;
        var option = NewGameSettings.Options[i];
        float value = parts[0] switch
        {
            "advanced-slider" => option.Min + Math.Clamp(e.Value, 0, 1) * (option.Max - option.Min),
            "advanced-less" => NewGameOptions[i] - .05f,
            "advanced-more" => NewGameOptions[i] + .05f,
            _ => NewGameOptions[i],
        };
        NewGameOptions = NewGameOptions.Set(i, MathF.Round(value * 20) / 20);
        return true;
    }

    void AddAdvancedContent(List<UiControl> controls)
    {
        if (!advancedOpen || controls.FirstOrDefault(c => c.Name == "advanced-Datapanel") is not { } panel) return;
        float s = Height / 1080f;
        var area = panel.Bounds;
        Dictionary<string, string> Text(string align = "Left VCenter") => new()
        {
            ["FontName"] = "Kenshi_StandardFont_Medium", ["FontPixels"] = Math.Max(8, (int)(14 * s)).ToString(CultureInfo.InvariantCulture),
            ["TextColour"] = MyGuiColours.InkText, ["TextAlign"] = align,
        };
        float row = area.Height / 7, labelWidth = area.Width * .46f, arrow = 24 * s, valueWidth = 48 * s;
        string help = "";
        for (int i = 0; i < NewGameSettings.Options.Length; i++)
        {
            var option = NewGameSettings.Options[i];
            float y = area.Y + i * row;
            if (new UiRect(area.X, y, area.Width, row).Contains(mouse.X, mouse.Y)) help = advancedHelp.TextAt(option.HelpLine);
            var label = Text();
            controls.Add(new($"advanced-label-{i}", "TextBox", new(area.X, y + 4 * s, labelWidth - 6 * s, row - 8 * s), Loc.Tr(option.Label), "", "Kenshi_GenericTextBoxFlatSkin", true, label, Vector4.Zero, Pickable: false));
            float left = area.X + labelWidth, right = area.X + area.Width - valueWidth - arrow;
            controls.Add(new($"advanced-less-{i}", "Button", new(left, y + (row - arrow) / 2, arrow, arrow), "", $"advanced-less:{i}", "Kenshi_SpeedArrowLeftSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            controls.Add(new($"advanced-more-{i}", "Button", new(right, y + (row - arrow) / 2, arrow, arrow), "", $"advanced-more:{i}", "Kenshi_SpeedArrowRightSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            var track = new UiRect(left + arrow + 6 * s, y + (row - 18 * s) / 2, right - left - arrow - 12 * s, 18 * s);
            controls.Add(new($"advanced-track-{i}", "Widget", track, "", $"advanced-slider:{i}", "Kenshi_SliderSkin", true, new Dictionary<string, string> { ["Slider"] = "true" }, Vector4.Zero, Pickable: true));
            float t = (NewGameOptions[i] - option.Min) / (option.Max - option.Min);
            controls.Add(new($"advanced-knob-{i}", "Button", new(track.X + t * track.Width - 6 * s, y + (row - 28 * s) / 2, 12 * s, 28 * s), "", "", "Kenshi_SliderButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
            controls.Add(new($"advanced-value-{i}", "TextBox", new(right + arrow, y + 4 * s, valueWidth, row - 8 * s), NewGameOptions[i].ToString("0.##", CultureInfo.InvariantCulture), "", "Kenshi_SpeedTextboxSkin", true, Text("Center"), Vector4.Zero, Pickable: false));
        }
        if (controls.FirstOrDefault(c => c.Name == "advanced-TooltipPanel") is { } footer)
        {
            var text = Text("Left Top"); text["WordWrap"] = "true";
            controls.Add(new("advanced-help", "TextBox", new(footer.Bounds.X + 8 * s, footer.Bounds.Y + 8 * s,
                footer.Bounds.Width - 16 * s, footer.Bounds.Height - 16 * s), help, "", "", true, text, Vector4.Zero,
                Pickable: false, Clip: footer.Bounds));
        }
    }
}
