using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The HUD's text panels as the original fills them (docs/game/ui-screens.md section 4): the name bar, the state lines, the stats lines, the medical header and
/// rows. The original builds a list of lines inside each empty text widget, a label from 3 % of its width and a value right-aligned to 96 %, one row per line.
/// </summary>
sealed partial class PlayerInterface
{
    // The palette of data/gui/colours/kenshi_colours.xml (Verified): Main, Secondary (the text default), Title, Bad, Good, Alert; a mod's file replaces it (MyGuiColours.LoadPalette).
    internal static string MainColour => MyGuiColours.Named("Main", "afa68b");
    internal static string TitleColour => MyGuiColours.Named("Title", "492620");
    internal static string BadColour => MyGuiColours.Named("Bad", "59231a");
    internal static string GoodColour => MyGuiColours.Named("Good", "a8b774");
    internal static string GreyColour => MyGuiColours.Named("Greyed", "444444");
    /// <summary>The palette's BadBright: the editor's race stats below 1 (<c>FUN_1405ee0c0</c>, <c>#bf4834</c>).</summary>
    internal static string BadBrightColour => MyGuiColours.Named("BadBright", "bf4834");
    internal static string AlertColour => MyGuiColours.Named("Alert", "000000");

    /// <summary>The stats panel's labels (MARTIAL ARTS, STRENGTH, ...): the grey of the orders buttons' text, not Main (docs/game/ui-screens.md).</summary>
    internal static readonly string StatLabelColour = "0.4 0.380392 0.356863";

    /// <summary>Resolves a faction (a position in the world's faction list) to its name; the host sets it.</summary>
    public Func<int, string?>? FactionName { get; set; }

    /// <summary>
    /// A line of a text panel: label and value, each with its colour (null: the text default). With <paramref name="Action"/> the value is a <c>Kenshi_Button2</c> button (the
    /// DataPanel's button lines): over the right half of the row, or across the whole row when there is no label.
    /// </summary>
    internal readonly record struct HudLine(string Label, string Value, string? LabelColour = null, string? ValueColour = null, string? Tooltip = null, string? Action = null);

    /// <summary>A medical panel row: its name, the green, yellow and red layers' fractions, the number, the arrows, the robot layer and the row's tooltip (the <c>LifeBarNTooltip</c> widget).</summary>
    internal readonly record struct MedicalRow(string Name, float Green, float Yellow, float Red, string Value, string Arrows, bool Robot, string? Tooltip = null);

    /// <summary>
    /// The status panel's prison line (<c>FUN_1405d2da0</c>, docs/game/economy.md 11.5, <b>Observed</b>): the hours left rounded up, "FOREVER" for a sentence of 2000 hours or more,
    /// "Waiting to be released" once there is nothing left.
    /// </summary>
    internal static string PrisonText(PrisonView prison) =>
        prison.Sentence >= 2000 ? $"{Loc.Tr("Imprisoned, sentence time remaining:")} {Loc.Tr("FOREVER")}"
        : prison.Sentence > 0 && prison.HoursLeft > 0 ? $"{Loc.Tr("Imprisoned, sentence time remaining:")} {Loc.Plural("{1,num} hr", "{1,num} hrs", (long)Math.Ceiling(prison.HoursLeft), (long)Math.Ceiling(prison.HoursLeft))}"
        : Loc.Tr("Waiting to be released");

    /// <summary>The goal line for a character the AI is not running (the player's orders, a fight, a job): what its movement task is, else Aimless.</summary>
    static string TaskText(CharacterTask task) => task switch
    {
        CharacterTask.GoTo => Loc.Tr("Moving"),
        CharacterTask.Follow => Loc.Tr("Following"),
        CharacterTask.Attack => Loc.Tr("Attacking"),
        CharacterTask.Treat => Loc.Tr("Giving first aid"),
        CharacterTask.Rest => Loc.Tr("Resting"),
        CharacterTask.PickUp => Loc.Tr("Picking up"),
        CharacterTask.PutDown => Loc.Tr("Putting down"),
        CharacterTask.Loot => Loc.Tr("Looting"),
        CharacterTask.StealthKo => Loc.Tr("Sneak attack"),
        CharacterTask.Build => Loc.Tr("Building"),
        CharacterTask.Dismantle => Loc.Tr("Dismantling"),
        CharacterTask.DeliverMaterials => Loc.Tr("Delivering materials"),
        CharacterTask.FetchMaterials => Loc.Tr("Collecting resource"),
        CharacterTask.Operate => Loc.Tr("Operating machine"),
        CharacterTask.Haul => Loc.Tr("Hauling"),
        CharacterTask.Prospect => Loc.Tr("Prospecting"),
        CharacterTask.Sleep => Loc.Tr("Sleeping"),
        _ => Loc.Tr("Aimless"),
    };

    /// <summary>
    /// The weather notice of a hurt player character (the medical object's short and long status strings, <c>FUN_1406472c0</c> and its siblings, docs/formats/weather.md): the short
    /// word for the State line and the long sentence for its tooltip. A protection <paramref name="p"/> below 0.02 is none, from 0.99 total; the percentage is rounded (<b>engine choice</b>).
    /// </summary>
    internal static (string Short, string Long) WeatherText(WeatherNotice notice, float p)
    {
        string percent = Loc.Tr("{1,num}%", MathF.Round(Math.Clamp(p, 0, 1) * 100));
        switch (notice)
        {
            case WeatherNotice.AcidWater: return (Loc.Tr("Acid!"), Loc.Tr("You are currently submersed in acid, get out of there as soon as you can."));
            case WeatherNotice.AcidFeet: return (Loc.Tr("Feet Burning!"), Loc.Tr("The ground here is acidic and burns your feet, find some footwear!"));
        }
        string head = notice switch
        {
            WeatherNotice.AcidRain => Loc.Tr("Acid Rain ({1} protection)", percent),
            WeatherNotice.Dust => Loc.Tr("Dust Storm ({1} protection)", percent),
            WeatherNotice.Burning => Loc.Tr("Burning ({1} protection)", percent),
            _ => Loc.Tr("Poison Gas ({1} protection)", percent),
        };
        string text = notice switch
        {
            WeatherNotice.AcidRain => p < 0.02f ? Loc.Tr("It is currently raining acid, and you aren't wearing anything to protect your skin.  Best to find shelter soon.")
                : p < 0.99f ? Loc.Tr("It is currently raining acid, your gear is providing you with {1} protection.", percent) : Loc.Tr("It is currently raining acid, but your gear is providing you with total protection."),
            WeatherNotice.Dust => p < 0.02f ? Loc.Tr("You are in a dust storm, it affects your accuracy in combat.")
                : p < 0.99f ? Loc.Tr("You are in a dust storm, your gear is providing you with {1} protection.", percent) : Loc.Tr("Your gear is protecting you from the storm."),
            WeatherNotice.Burning => p < 0.02f ? Loc.Tr("You are currently on fire.")
                : p < 0.99f ? Loc.Tr("You are currently on fire, your gear is providing you with {1} protection.", percent) : Loc.Tr("Your gear is protecting you from being horribly burnt."),
            _ => p < 0.02f ? Loc.Tr("You are currently breathing in poison gas.")
                : p < 0.99f ? Loc.Tr("You are currently breathing in poison gas, your gear is providing you with {1} protection.", percent) : Loc.Tr("Your gear is protecting you from breathing in any poisons."),
        };
        return (head, text);
    }

    /// <summary>The state lines (State, Goal, Encumbrance) of the selected character.</summary>
    static HudLine[] StateLines(CharacterSnapshot lead)
    {
        var body = lead.Body;
        string state = body is { Dead: true } ? Loc.Tr("Dead") : body is { Unconscious: true } ? Loc.Tr("Unconscious") : (lead.Orders & CharacterOrders.Block) != 0 ? Loc.Tr("Defensive") : Loc.Tr("Normal");
        string? colour = body is { Dead: true } or { Unconscious: true } ? BadColour : null;
        if (body is not { Dead: true } && lead.Prison is { } prison)
        {
            state = PrisonText(prison);
            colour = BadColour;
        }
        string? tooltip = null;
        if (body is not { Dead: true } && lead.Prison is null && lead.WeatherNotice != WeatherNotice.None)
        {
            (state, tooltip) = WeatherText(lead.WeatherNotice, lead.WeatherNoticeProtection);
            colour = lead.WeatherNoticeProtection > 0.9f && lead.WeatherNotice is not (WeatherNotice.AcidWater or WeatherNotice.AcidFeet) ? GoodColour : BadColour;
        }
        int encumbrance = (int)((1 - Math.Clamp(lead.Hud?.Encumbrance ?? 1, 0, 1)) * 100);
        return [new(Loc.Tr("State:"), state, ValueColour: colour, Tooltip: tooltip), new(Loc.Tr("Goal:"), lead.Activity ?? TaskText(lead.Task)),
            new(Loc.Tr("Encumbrance:"), encumbrance.ToString(CultureInfo.InvariantCulture) + "%", ValueColour: encumbrance >= 50 ? BadColour : null)];
    }

    /// <summary>
    /// The stats lines: the value the character fights with, then its change from the trained value in brackets, the whole value Bad when it is lower and Good
    /// when higher. The labels show upper case and in Main on the panel's dark side (<b>Observed</b> in the original; where the case comes from is <b>Unknown</b>).
    /// </summary>
    internal static HudLine[] StatsLines(CharacterSnapshot lead) => lead.Hud is not { } hud ? [] : [.. hud.Stats.Select(s =>
    {
        int value = (int)s.Value, change = value - (int)s.Base;
        string text = change == 0 ? value.ToString(CultureInfo.InvariantCulture) : change < 0 ? $"{value} ({change})" : $"{value} (+{change})";
        return new HudLine(Loc.Tr(s.Label).ToUpperInvariant(), text, StatLabelColour, change < 0 ? BadColour : change > 0 ? GoodColour : null);
    })];

    /// <summary>The unit of the run speed in the information panel (Interface settings; the game shows mph).</summary>
    public SpeedUnit SpeedUnit { get; set; }

    /// <summary>
    /// The extended lines (shown when the information panel is expanded; <c>FUN_140895350</c>, docs/game/ui-screens.md section 4): the XP rates, the KO point, the run
    /// speed in mph (units per second x 0.22369362; km/h x 0.36) with its change from the unhurt run value in brackets, and the hunger rate.
    /// </summary>
    internal static HudLine[] ExtendedLines(CharacterSnapshot lead, SpeedUnit unit = SpeedUnit.Mph)
    {
        if (lead.Hud?.Extended is not { } e) return [];
        static string Percent(float f) => ((int)(f * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        float factor = unit == SpeedUnit.Kmh ? 0.36f : 0.22369362f;
        string suffix = unit == SpeedUnit.Kmh ? "km/h" : "mph";
        int speed = (int)MathF.Round(e.RunSpeed * factor), change = (int)MathF.Round((e.RunSpeed - e.RunValue) * factor);
        string run = change == 0 ? $"{speed} {suffix}" : change < 0 ? $"{speed} ({change}) {suffix}" : $"{speed} (+{change}) {suffix}";
        return [new(Loc.Tr("Athletics XP:"), Percent(e.AthleticsXp)), new(Loc.Tr("Strength XP:"), Percent(e.StrengthXp)),
            new(Loc.Tr("Toughness XP:"), ((int)e.ToughnessXp).ToString(CultureInfo.InvariantCulture) + "x"),
            new(Loc.Tr("KO point:"), ((int)e.KoPoint).ToString(CultureInfo.InvariantCulture), Tooltip: KoPointTip(e.KoPoint)),
            new(Loc.Tr("Run speed:"), run, ValueColour: change < 0 ? BadColour : change > 0 ? GoodColour : null),
            new(Loc.Tr("Hunger Rate:"), Percent(e.HungerRate))];
    }

    /// <summary>
    /// The medical header's word (the original's list is longer; Meitou shows the states it models): Dead, Unconscious (N) with N the KO timer as (int) while it runs, Starving below 1 hunger, then by the worst
    /// part h: Wounded below 0.7, Hungry below 2 hunger, Slightly Injured below 0.9, else Normal.
    /// </summary>
    internal static (string Text, string? Colour) HealthWord(BodyStatus body) =>
        body.Dead ? (Loc.Tr("Dead"), (string?)BadColour) : body.Unconscious ? (body.KoTimer > 0 ? $"{Loc.Tr("Unconscious")} ({(int)body.KoTimer})" : Loc.Tr("Unconscious"), BadColour) : body.Hunger < 1 ? (Loc.Tr("Starving"), BadColour)
        : body.WorstPart < 0.7f ? (Loc.Tr("Wounded"), BadColour) : body.Hunger < 2 ? (Loc.Tr("Hungry"), BadColour) : body.WorstPart < 0.9f ? (Loc.Tr("Slightly Injured"), BadColour) : (Loc.Tr("Normal"), null);

    /// <summary>Adds a text panel's lines over the arranged widget <paramref name="panel"/>: one row per line, the label from 3 % of the width, the value right-aligned to 96 %.</summary>
    /// <summary>One row per line, the label left and the value right; the value takes <paramref name="valueFont"/> at <paramref name="valuePixels"/> when given.</summary>
    static void AddLines(List<UiControl> controls, string panel, IReadOnlyList<HudLine> lines, float rowHeight, float? fontPixels = null, string font = "Kenshi_StandardFont_Medium",
        string? valueFont = null, float? valuePixels = null)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;
        var area = host.Bounds;
        var labelProperties = Face(font, fontPixels);
        var valueProperties = valueFont is null ? labelProperties : Face(valueFont, valuePixels);
        for (int i = 0; i < lines.Count; i++)
        {
            var row = new UiRect(area.X + area.Width * 0.03f, area.Y + i * rowHeight, area.Width * 0.93f, rowHeight);
            if (lines[i].Action is { } action)
            {
                // The button holds the value; the row's label (if any) stays text on its left.
                var button = lines[i].Label.Length == 0 ? row : row with { X = row.X + row.Width * 0.5f, Width = row.Width * 0.5f };
                var buttonProperties = new Dictionary<string, string>(valueProperties) { ["TextAlign"] = "Center", ["TextColour"] = lines[i].ValueColour ?? MyGuiColours.InkText };
                if (lines[i].Tooltip is { Length: > 0 } buttonTip) foreach (var pair in UiTooltips.TextProperties(Loc.Tr(buttonTip), "fixed")) buttonProperties[pair.Key] = pair.Value;
                // Kenshi_Button2 is a template (kenshi_templates.xml) whose root widget has the Kenshi_Button1Skin skin.
                controls.Add(new($"{panel}-button-{i}", "Button", button with { Y = button.Y + 1, Height = button.Height - 2 }, Loc.Tr(lines[i].Value), action, "Kenshi_Button1Skin", true,
                    buttonProperties, Vector4.Zero, Clip: host.Clip));
                if (lines[i].Label.Length == 0) continue;
            }
            foreach (var (part, text, colour, align, properties) in new[] { ("label", lines[i].Label, lines[i].LabelColour, "Left Top", labelProperties),
                ("value", lines[i].Action is null ? lines[i].Value : "", lines[i].ValueColour, "Right Top", valueProperties) })
                if (text.Length > 0)
                {
                    var textProperties = new Dictionary<string, string>(properties) { ["TextAlign"] = align, ["TextColour"] = colour ?? MyGuiColours.InkText };
                    // A line with a tooltip is a mouse target (the HUD's own tooltip, anchored on its panel).
                    if (lines[i].Tooltip is { Length: > 0 } tip) foreach (var pair in UiTooltips.TextProperties(Loc.Tr(tip), "fixed")) textProperties[pair.Key] = pair.Value;
                    // A value beside a label keeps out of the label's column: a long one (a French weather notice) is cut short instead of running over the label.
                    // A label beside a button keeps to the left half: added after the button, a full-row label with a tooltip would take its clicks.
                    var box = part == "value" && lines[i].Label.Length > 0 ? row with { X = row.X + row.Width * 0.35f, Width = row.Width * 0.65f }
                        : lines[i].Action is not null ? row with { Width = row.Width * 0.5f } : row;
                    controls.Add(new($"{panel}-{part}-{i}", "TextBox", box, Loc.Tr(text), "", "Kenshi_TextboxStandardText", true,
                        textProperties, Vector4.Zero, Pickable: lines[i].Tooltip is { Length: > 0 }, Clip: host.Clip));
                }
        }

        static Dictionary<string, string> Face(string name, float? pixels)
        {
            var properties = new Dictionary<string, string> { ["FontName"] = name };
            if (pixels is { } p) properties["FontPixels"] = Math.Max(8, (int)MathF.Round(p)).ToString(CultureInfo.InvariantCulture);
            return properties;
        }
    }

    /// <summary>The information panel's and the medical rows' text (Faithful HUD; called after the layout is arranged).</summary>
    /// <summary>The original's standard text at 1080 p: <c>Kenshi_StandardFont_Medium</c>, 20 pt at 50 dpi (docs/game/ui-screens.md).</summary>
    const float StandardTextPixels = 20 * 50 / 72f;

    /// <summary>
    /// The standard text's em pixels at the window's height: the Faithful mode's as the original sizes its font at that scale (<see cref="UiPainter.StandardTextPixels"/>,
    /// smaller than linear above 1080 lines), Meitou's the 1080-line size scaled.
    /// </summary>
    float StandardPixels => UiMode == UiMode.Faithful && uiPainter is { } painter ? painter.StandardTextPixels(Height) : StandardTextPixels * Height / 1080f;

    void AddHudText(List<UiControl> controls)
    {
        float row = 20 * Height / 1080f, sidePixels = StandardPixels;
        if (BuildingFocus() is { } focus)
        {
            var (name, state, stats, _, _) = BuildingLines(focus);
            AddLines(controls, "NameText", [name], row, sidePixels);
            AddLines(controls, "StateText", state, row, sidePixels);
            AddLines(controls, "StatsText", stats, row, sidePixels);
            AddMedicalText(controls, sidePixels);
            return;
        }
        if (Lead() is not { } lead) return;
        string faction = FactionName?.Invoke(lead.Faction) ?? "";
        AddLines(controls, "NameText", [new(lead.Name, faction, TitleColour, TitleColour)], row, sidePixels);
        AddLines(controls, "StateText", StateLines(lead), row, sidePixels);
        if (lead.Bounties.Count > 0 || lead.Slave is not null) AddLines(controls, "BountyText", [BountyLine(lead.Bounties)], row, sidePixels);
        // The base game draws the stats labels in the painted face (Observed: glyph shapes and widths match Sentencia at its resized 22 pt); the values
        // take the standard face, like the medical column's numbers.
        AddLines(controls, "StatsText", StatsLines(lead), row, font: "Kenshi_PaintedTextFont_Medium", valueFont: "Kenshi_StandardFont_Medium", valuePixels: sidePixels);
        if (infoExpanded) AddLines(controls, "ExtendedStatsText", ExtendedLines(lead, SpeedUnit).Select(line => line with
            { Label = line.Label.ToUpperInvariant(), LabelColour = MainColour }).ToArray(), row, sidePixels);
        AddMedicalText(controls, sidePixels);
    }

    /// <summary>The medical rows' names (centred on the bar), arrows and numbers.</summary>
    void AddMedicalText(List<UiControl> controls, float sidePixels)
    {
        foreach (var (bar, name, arrows) in medicalRows)
        {
            if (controls.FirstOrDefault(c => c.Name == bar + "Datapanel") is not { } host) continue;
            var fullBar = controls.FirstOrDefault(c => c.Name == bar)?.Bounds ?? host.Bounds;
            var textBounds = new UiRect(host.Bounds.X, fullBar.Y, host.Bounds.Width, fullBar.Height);
            string pixels = Math.Max(8, (int)MathF.Round(sidePixels)).ToString(CultureInfo.InvariantCulture);
            controls.Add(new($"{bar}-name", "TextBox", textBounds, Loc.Tr(name), "", "", true,
                new Dictionary<string, string> { ["FontPixels"] = pixels, ["TextAlign"] = "Center", ["TextColour"] = MyGuiColours.InkText }, Vector4.Zero, Pickable: false, Clip: host.Clip));
            if (arrows.Length > 0)
                controls.Add(new($"{bar}-arrows", "TextBox", textBounds, arrows, "", "", true,
                    new Dictionary<string, string> { ["FontPixels"] = pixels, ["TextAlign"] = "Right VCenter", ["TextColour"] = AlertColour }, Vector4.Zero, Pickable: false, Clip: host.Clip));
            int valueIndex = controls.FindIndex(c => c.Name == bar + "Value");
            if (valueIndex >= 0)
            {
                var value = controls[valueIndex];
                float height = Math.Max(value.Bounds.Height, 20 * Height / 1080f);
                controls[valueIndex] = value with { Bounds = value.Bounds with { Y = value.Bounds.Y + (value.Bounds.Height - height) / 2, Height = height },
                    Properties = new Dictionary<string, string>(value.Properties) { ["FontPixels"] = pixels } };
            }
        }
    }

    /// <summary>The medical rows bound this frame: the bar widget, the row's name and its arrows (<see cref="BindMedicalBars"/>).</summary>
    readonly List<(string Bar, string Name, string Arrows)> medicalRows = [];

    /// <summary>
    /// The medical panel (docs/game/ui-screens.md section 4): Blood first, the body parts, Hunger last, each a name centred on its bar, arrows at its right end and
    /// its number in the value column. A part's bar is green to its health less stun, red behind it to its maximum less stun (the lost part shows red); blood is
    /// green to its fraction of capacity; hunger (0..3) green to h - 2, yellow to h - 1, red to h.
    /// </summary>
    void BindMedicalBars(UiBindings b, CharacterSnapshot? lead, IReadOnlyList<MedicalRow>? buildingRows = null)
    {
        medicalRows.Clear();
        var rows = new List<MedicalRow>(buildingRows ?? []);
        if (buildingRows is null && lead?.Body is { } body)
        {
            var hud = lead.Hud;
            float capacity = Math.Max(hud?.BloodCapacity ?? 1, 1e-3f), blood = hud?.Blood ?? body.Blood * capacity;
            // A run of '<', one per 0.05 of bleeding (at most 100).
            int bleed = Math.Min(100, (int)MathF.Ceiling(Math.Abs(hud?.Bleeding ?? 0) / 0.05f));
            rows.Add(new(Loc.Tr("Blood"), blood / capacity, 0, 0, ((int)MathF.Floor(blood + 0.1f)).ToString(CultureInfo.InvariantCulture), new string('<', bleed), false, BloodTip(hud?.BleedRate ?? 1)));
            foreach (var part in lead.HealthParts)
            {
                float max = Math.Max(part.Maximum, 1e-3f), health = part.Flesh + part.Rig - part.Stun;
                string arrows = health < -max ? "!!!   " : health < 0 ? "<<   " : "";
                rows.Add(new(part.Name, health / max, (health + part.Bandage) / max, (max - part.Stun) / max, ((int)MathF.Floor(health + 0.1f)).ToString(CultureInfo.InvariantCulture), arrows, part.Robotic,
                    PartTip(part, hud?.Extended?.KoPoint, body.Unconscious)));
            }
            float hunger = body.Hunger;
            rows.Add(new(Loc.Tr("Hunger"), hunger - 2, hunger - 1, hunger, ((int)(hunger * 100)).ToString(CultureInfo.InvariantCulture), hunger < 2 ? "<!   " : "<   ", false,
                HungerTip(hunger, body.Unconscious, ActiveSquadMembers().Count)));
        }
        for (int i = 1; i <= 9; i++)
        {
            string name = $"LifeBar{i}";
            b.Hidden.Add(name + "White"); b.Hidden.Add(name + "Crushed");
            if (i > rows.Count) { b.Hidden.Add(name); b.Hidden.Add(name + "Value"); b.Hidden.Add(name + "Tooltip"); continue; }
            var r = rows[i - 1];
            // The row's tooltip sits on its LifeBarNTooltip widget (FUN_1407228a0 sets a type 0 text there).
            if (r.Tooltip is { Length: > 0 } tip) b.Properties[name + "Tooltip"] = UiTooltips.TextProperties(tip, "fixed");
            else b.Hidden.Add(name + "Tooltip");
            b.Progress[name + "Green"] = Math.Max(r.Green, 0);
            b.Progress[name + "Yellow"] = Math.Max(r.Yellow, 0);
            b.Progress[name + "Red"] = Math.Max(r.Red, 0);
            if (r.Robot) b.Progress[name + "Robot"] = Math.Max(r.Green, 0); else b.Hidden.Add(name + "Robot");
            b.Text[name + "Datapanel"] = "";
            b.Text[name + "Value"] = r.Value;
            b.Properties[name + "Value"] = new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = MyGuiColours.InkText };
            medicalRows.Add((name, r.Name, r.Arrows));
        }
        if (buildingRows is null && lead?.Body is { } status)
        {
            var (word, colour) = HealthWord(status);
            b.Text["HealthText"] = word;
            b.Properties["HealthText"] = new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = colour ?? MyGuiColours.InkText };
        }
    }
}
