using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Ui;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Trading;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The tooltip runtime (docs/game/ui-screens.md "Tooltips"). Any control can carry the original's user strings (<see cref="UiTooltips"/>); the topmost control under a still pointer shows its
/// tooltip after MyGUI's 0.5 s delay, laid out and placed as the original's <c>ToolTip</c> classes do, in the original's skins (Faithful) or on the Meitou plate.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The item records of the game data (for the item tooltip's lines); the host sets it.</summary>
    public Func<string, GameRecord?>? ItemRecordOf { get; set; }
    /// <summary>The combat tables (weapon and armour records); the host sets it.</summary>
    public Func<CombatDatabase?>? CombatOf { get; set; }

    /// <summary>The control property that makes an item cell show an item tooltip: the key of its <see cref="ItemTip"/>.</summary>
    const string ItemTipKey = "tooltip_item";

    string? tipTarget;
    Vector2 tipPoint;
    long tipSince;
    bool tipShown;
    readonly Dictionary<string, ItemTip> itemTips = [];

    /// <summary>A press hides the tooltip (MyGUI hides it while the mouse is captured) and the delay starts again.</summary>
    void HideTooltip()
    {
        tipTarget = null;
        tipShown = false;
    }

    /// <summary>
    /// The mouse-over tooltip of the control under the pointer. Characters get none: the original shows no name or goal over a character under the cursor (they appear in the
    /// information panel after a click; the character is only outlined, <see cref="HoverTarget"/>).
    /// </summary>
    void AddHoverLabel(List<UiControl> controls)
    {
        if (held is not null || jobDrag is not null || dragging || uiPainter is not { } painter) { HideTooltip(); return; }
        if (TooltipTarget(controls) is not { } hit) { HideTooltip(); return; }
        long now = Stopwatch.GetTimestamp();
        if (hit.Name != tipTarget)
        {
            (tipTarget, tipPoint, tipSince, tipShown) = (hit.Name, mouse, now, false);
        }
        else if (!tipShown && mouse != tipPoint)
        {
            // Moving before it shows starts the delay again; once shown it follows the pointer.
            (tipPoint, tipSince) = (mouse, now);
        }
        if (ResolveTooltip(hit) is not var (lines, kind) || lines.Count == 0) return;
        // An item's (ToolTipInventory) shows at once: the inventory's mouse-move hook shows it, not MyGUI's tooltip timer (ToolTipInventory::vfunc_6, Verified).
        if (!tipShown && (kind == TooltipKind.Inventory || Stopwatch.GetElapsedTime(tipSince, now).TotalSeconds >= UiTooltips.ShowDelaySeconds)) tipShown = true;
        if (!tipShown) return;
        // A static tooltip is shown in a widget of its own (the class names it: "static:AIToolTip"); an item's sits beside the window holding the item.
        UiRect? panel = kind == TooltipKind.Inventory ? ItemWindow(controls, hit) : null;
        if (kind == TooltipKind.Static)
        {
            string name = hit.Properties.GetValueOrDefault(UiTooltips.Class)![7..];
            if (controls.FirstOrDefault(c => c.Name == name) is not { } owner) return;
            panel = owner.Bounds;
        }
        PlaceTooltip(controls, painter, lines, kind, panel);
    }

    /// <summary>The window an item cell lies in (the topmost window over the cell's centre), the anchor of its tooltip; null: none, the tooltip goes at the pointer.</summary>
    static UiRect? ItemWindow(IReadOnlyList<UiControl> controls, UiControl cell)
    {
        float x = cell.Bounds.X + cell.Bounds.Width / 2, y = cell.Bounds.Y + cell.Bounds.Height / 2;
        for (int i = controls.Count - 1; i >= 0; i--)
            if (controls[i].Type == "Window" && controls[i].Bounds.Contains(x, y)) return controls[i].Bounds;
        return null;
    }

    static bool CarriesTooltip(UiControl c) => c.Properties.ContainsKey(UiTooltips.Type) || c.Properties.ContainsKey(ItemTipKey);

    /// <summary>
    /// The control whose tooltip is up: the topmost one under the pointer that takes the mouse, or a tooltip carrier above it that the hit test skips (a text row is no mouse target of
    /// its own). The widget under the pointer decides: one without a tooltip hides it, as the original's empty text does.
    /// </summary>
    UiControl? TooltipTarget(IReadOnlyList<UiControl> controls)
    {
        var top = MyGuiPresentation.HitTest(controls, mouse.X, mouse.Y);
        int from = 0;
        if (top is not null)
            for (int i = controls.Count - 1; i >= 0; i--)
                if (ReferenceEquals(controls[i], top)) { from = i + 1; break; }
        for (int i = controls.Count - 1; i >= from; i--)
        {
            var c = controls[i];
            if (CarriesTooltip(c) && c.Bounds.Contains(mouse.X, mouse.Y) && (c.Clip is null || c.Clip.Value.Contains(mouse.X, mouse.Y)) && c.Properties.GetValueOrDefault("NeedMouse") != "false") return c;
        }
        return top is not null && CarriesTooltip(top) ? top : null;
    }

    /// <summary>The rows of a control's tooltip and its class: from its item, else from its user strings by <c>tooltip_type</c> (0 text, 1 rows, 2 data record, 4 map place).</summary>
    (IReadOnlyList<TooltipLine>, TooltipKind)? ResolveTooltip(UiControl hit)
    {
        var p = hit.Properties;
        if (p.TryGetValue(ItemTipKey, out string? key) && itemTips.TryGetValue(key, out var item)) return (ItemLines(item), TooltipKind.Inventory);
        var kind = p.GetValueOrDefault(UiTooltips.Class) switch
        {
            "fixed" => TooltipKind.Fixed, "inventory" => TooltipKind.Inventory, { } c when c.StartsWith("static:", StringComparison.Ordinal) => TooltipKind.Static, _ => TooltipKind.Floating,
        };
        switch (p.GetValueOrDefault(UiTooltips.Type))
        {
            case "0" or "1": return (UiTooltips.Lines(p), kind);
            case "2":
                // A game data record: its name (the original adds a town's faction, relations and residents; Meitou has the map's own tooltip for those).
                return ItemRecordOf?.Invoke(p.GetValueOrDefault(UiTooltips.Data) ?? "") is { Name.Length: > 0 } record ? ([new(record.Name)], kind) : null;
            case "4":
                // A map place: its rows, one per line, the value after a tab.
                var rows = new List<TooltipLine>();
                foreach (string row in (p.GetValueOrDefault(UiTooltips.Data) ?? "").Split('\n'))
                {
                    string[] parts = row.Split('\t', 2);
                    var (text, tone) = UiTooltips.SplitTag(parts[0]);
                    var (value, valueTone) = UiTooltips.SplitTag(parts.Length > 1 ? parts[1] : "");
                    rows.Add(new(text, value, tone, valueTone));
                }
                return (rows, kind);
            default: return null;
        }
    }

    const string TooltipFont = "Kenshi_StandardFont_Medium";
    const string TipPlate = "meitou:tooltip:plate";
    /// <summary>The baked plate's margin (shadow room) and the radius of its corners, in its own pixels (<see cref="MenuArt.WindowSurface"/> at scale 1).</summary>
    const float TipPlateMargin = 16, TipPlateRadius = 10;

    /// <summary>Lays the tooltip out with the painter's measuring and adds its panel and text to <paramref name="controls"/>.</summary>
    /// <summary>A tooltip text in the active language; a bracketed heading keeps its brackets around the translated inside.</summary>
    static string TipTr(string text)
    {
        string whole = Loc.Tr(text);
        return whole != text || text.Length <= 2 || text[0] != '[' || text[^1] != ']' ? whole : "[" + Loc.Tr(text[1..^1]) + "]";
    }
    void PlaceTooltip(List<UiControl> controls, UiPainter painter, IReadOnlyList<TooltipLine> lines, TooltipKind kind, UiRect? given)
    {
        float minimum = TitleOpen ? 0 : StandardPixels;
        Vector2 Measure(string text, float wrap)
        {
            var size = painter.MeasureText(TooltipFont, text, Height, wrap, minimum);
            return new(size.Width, size.Height);
        }
        UiRect? anchor = kind == TooltipKind.Fixed ? HudTooltipAnchor() : given;
        // The tooltip texts the code sets are messages of the game's catalog: shown in the active language (a row's "-" mark of an indented key stays).
        if (Loc.Translating)
            lines = [.. lines.Select(l => l with { Key = l.Key.StartsWith('-') ? "-" + TipTr(l.Key[1..]) : TipTr(l.Key), Value = TipTr(l.Value) })];
        var layout = UiTooltips.Arrange(lines, kind, Width, Height, mouse, anchor, Measure);
        var panel = layout.Panel;
        float scale = Height / 1080f;
        bool meitou = kind != TooltipKind.Static && UiMode == UiMode.Meitou && TooltipPlateReady(painter);
        var none = Vector4.Zero;
        if (kind == TooltipKind.Static) { }   // drawn by the window's own panel
        else if (meitou)
        {
            float margin = TipPlateMargin * scale;
            controls.Add(new("tooltip-plate", "ImageBox", new(panel.X - margin, panel.Y - margin, panel.Width + 2 * margin, panel.Height + 2 * margin), "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = TipPlate, ["ImageBorder"] = (TipPlateMargin + TipPlateRadius).ToString(CultureInfo.InvariantCulture),
                    ["ImageBorderScale"] = scale.ToString(CultureInfo.InvariantCulture) }, none, Pickable: false));
        }
        else if (kind == TooltipKind.Floating)
            controls.Add(new("tooltip-panel", "Widget", panel, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), none, Pickable: false));
        else
        {
            // Kenshi_FloatingPanelLight: the floating panel skin with the parchment box in it (inset 5 for the inventory's; the HUD's own panel keeps 97.2 % of its height).
            controls.Add(new("tooltip-panel", "Widget", panel, "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), none, Pickable: false));
            var inner = kind == TooltipKind.Fixed ? panel with { Height = panel.Height * UiTooltips.FixedBoxFraction }
                : new UiRect(panel.X + 5 * scale, panel.Y + 5 * scale, Math.Max(0, panel.Width - 10 * scale), Math.Max(0, panel.Height - 10 * scale));
            controls.Add(new("tooltip-box", "Widget", inner, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), none, Pickable: false));
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var area = layout.Rows[i].Area;
            void Text(string part, UiRect at, string text, string align, TooltipTone tone)
            {
                if (text.Length == 0) return;
                controls.Add(new($"tooltip-{part}-{i}", "TextBox", at, text, "", "", true,
                    new Dictionary<string, string> { ["FontName"] = TooltipFont, ["TextAlign"] = align, ["WordWrap"] = "true", ["TextColour"] = TipColour(tone, meitou) },
                    none, Pickable: false));
            }
            Text("key", area, line.Key, "Left Top", line.KeyTone);
            float suffixWidth = line.Suffix.Length > 0 ? painter.MeasureText(TooltipFont, line.Suffix, Height, 0, minimum).Width : 0;
            Text("value", area with { Width = Math.Max(0, area.Width - suffixWidth) }, line.Value, "Right Top", line.ValueTone);
            Text("suffix", area, line.Suffix, "Right Top", line.SuffixTone);
        }
    }

    /// <summary>A <c>rrggbb</c> colour as MyGUI's "r g b" text.</summary>
    static string TipHex(string rgb) => string.Join(' ', Enumerable.Range(0, 3).Select(i =>
        (int.Parse(rgb.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f).ToString("0.###", CultureInfo.InvariantCulture)));

    /// <summary>The colour a tone takes: the original's own on its parchment (Faithful), the kit's matching colours on the plate (Meitou, docs/meitou-ui.md).</summary>
    static string TipColour(TooltipTone tone, bool plate) => tone switch
    {
        // The armour grades (ItemGrade0 to ItemGrade4 of kenshi_colours.xml) in both modes.
        TooltipTone.Grade0 => TipHex("666666"), TooltipTone.Grade1 => TipHex("d9ccbf"), TooltipTone.Grade2 => TipHex("4099b3"), TooltipTone.Grade3 => TipHex("66fc80"), TooltipTone.Grade4 => TipHex("fceb0d"),
        _ => plate ? PlateColour(tone) : ParchmentColour(tone),
    };
    static string PlateColour(TooltipTone tone) => tone switch
        {
            TooltipTone.Title => TipHex("d9ceb8"), TooltipTone.Bad or TooltipTone.BadBright => TipHex("bf4834"), TooltipTone.Good => TipHex("a8b774"),
            TooltipTone.Grey or TooltipTone.GreyBright => TipHex("777777"), TooltipTone.Cyan => TipHex("c4a062"), _ => TipHex("afa68b"),
        };
    static string ParchmentColour(TooltipTone tone) => tone switch
        {
            TooltipTone.Main => MainColour, TooltipTone.Ink => MyGuiColours.InkText, TooltipTone.Title => TitleColour, TooltipTone.Bad => BadColour, TooltipTone.BadBright => BadBrightColour,
            TooltipTone.Good => GoodColour, TooltipTone.Grey => GreyColour, TooltipTone.GreyBright => TipHex("777777"), TooltipTone.Cyan => TipHex("14ffdc"), _ => MyGuiColours.InkText,
        };

    /// <summary>Bakes the Meitou tooltip plate once (rust under the kit's tint, rounded, with its drop shadow; drawn nine-sliced).</summary>
    bool TooltipPlateReady(UiPainter painter)
    {
        if (painter.HasTexture(TipPlate)) return true;
        if (UiAtlas() is not { } uiAtlas) return false;
        painter.SetImage(TipPlate, MenuArt.WindowSurface(uiAtlas, 96, 96, 1), srgb: false);
        return true;
    }

    /// <summary>The HUD's <c>TooltipPanel</c> (the <c>ToolTipFixed</c> anchor, <c>FUN_14072d3b0</c>): the tooltip grows up from its bottom edge, starting at its left.</summary>
    UiRect HudTooltipAnchor() =>
        faithfulHud.Find("TooltipPanel") is { } panel ? panel.Position.RelativeTo(new(0, 0, Width, Height), true) : new(0.319792f * Width, 0.712963f * Height, 0.208333f * Width, 0.0666667f * Height);

    // ---- HUD tooltips (FUN_14072b540, FUN_14072d3b0: Verified) ----

    /// <summary>The texts the HUD's builders hand to the type 0 setter (<c>FUN_140920fe0</c>) per widget; the Sneak button's is two strings joined.</summary>
    static readonly (string Widget, string Text)[] HudTips =
    [
        ("OrdersBlockButton", "This will toggle the selected characters into defensive combat mode. When in defensive mode, a character will focus purely on staying alive and will gain 20 to his melee defense skill. However he will not attempt to make any attacks. This is handy for keeping your weaker characters alive when they are outclassed."),
        ("OrdersHoldButton", "HOLD POSITION means that the selected character won't run off to fight enemies, even to protect his allies.  He will hold his position and only attack any enemy that comes into close range.  He will still use a crossbow normally, but won't move around obstacles to get a shot.  Use this setting to tactically position fighters, or just to keep non-combatants away from the fighting."),
        ("OrdersPassiveButton", "In non-combat mode a character will never initiate combat, even if his allies are fighting or need help. He will only fight when directly attacked, and is less likely to be targeted by enemies."),
        ("OrdersChaseButton", "If this is disabled then the character won't do any of the jobs listed."),
        ("OrdersTauntButton", "The character will make himself offensive, noisy and conspicuous in an attempt to draw more of the enemy to attack him. Using this enables your experienced and well armoured characters to draw trouble away from your more vulnerable ones."),
        ("OrdersRangedButton", "If enabled then the character will prioritise ranged attacks, melee will be a last resort."),
        ("OrdersSneakButton", "Activates sneak mode so the character will try to go unnoticed.\nIf the character is unconscious, when he wakes up he will stay down on the ground and crawl to avoid detection."),
        ("OrdersMedicButton", "Pressing this button will order your selected characters to automatically heal everyone else in the squad, including themselves. They will need a medkit in their inventory."),
        ("OrdersRescueButton", "This will order your selected characters to pick up all your wounded allies. If you own any buildings nearby, they will take the wounded and put them in beds. Make sure to heal their wounds first."),
        ("OrdersProspectButton", "This will give you information on the richness of resources at your location. Stone, iron, water, fertility... It's important to check for these things before building an outpost."),
        ("ShortcutInventoryButton", "Open character inventory"),
        ("ShortcutStatsButton", "Open character status window"),
        ("ShortcutMapButton", "Open world map"),
        ("ShortcutTechButton", "Open research window"),
        ("ShortcutSquadButton", "Open squad management window"),
        ("ShortcutHelpButton", "Open help window"),
        ("BuildButton", "Opens the BUILD menu.  Here you can place buildings and start your own outpost anywhere in the world, but you should choose a safe area and check availability of resources using the PROSPECT button. You will also need building materials to finish construction."),
        ("FloorArrowUp", "Change the selected floor, use to move to a certain floor of a building, 0 is ground floor (also: Page Up key)"),
        ("FloorArrowDown", "Change the selected floor, use to move to a certain floor of a building, 0 is ground floor (also: Page Down key)"),
    ];

    /// <summary>
    /// A selected building's status word (the medical header) and the tooltip the building classes give it (<b>Observed</b>: the strings of the <c>vfunc_114</c> status functions, a title
    /// followed by its text; the machine classes' words, Meitou applies them to every building).
    /// </summary>
    static string? BuildingStatusTip(string status, bool farm) => status switch
    {
        "Normal" => "Building is in good condition.",
        "Under construction" => "This machine is currently under construction.",
        "Broken" => "This machine has broken down as a result of damage. Send one of your engineers to repair it.",
        "Output full" when farm => "The output is full. Empty out the items and put them in a storage box or something.",
        "Output full" => "The machine output is full. Empty out the items and put them in a storage box or something.",
        "Growing" => "Your crops are growing nicely. You don't have to do anything.",
        "No water" => "Out of water. Growth rate will be significantly reduced and your plants may die.",
        "Ready to harvest" => "Nobody is working at this farm at the present moment, you need to hurry up and harvest them before they die.",
        "No power" => "This machine needs electricity to function. You either need to build generators, or you are out of fuel / there is no wind.",
        _ => null,
    };

    // The colour tags of the bounty rows (kenshi_colours.xml Main, Title, Greyed; FUN_1405d2da0's static strings).
    const string MainTag = "#afa68b", TitleTag = "#492620", GreyTag = "#444444";

    /// <summary>
    /// The bounty panel's tooltip rows (<c>FUN_1405d2da0</c>, docs/game/ui-screens.md section 4): per faction its name and <c>c.{1,num}</c> (Main for the local faction, else Greyed),
    /// <c>-Wanted For: </c> with the first four crimes and a <c> </c> row with the rest; then the imprisonment row, or a rule, the turn-in line and the local faction's expiry.
    /// <paramref name="everyExpiry"/> (Meitou UI): each faction's expiry under its own rows instead.
    /// </summary>
    static List<(string Key, string Value)> BountyRows(IReadOnlyList<Meitou.Simulation.Crime.BountyLine> bounties, PrisonView? prison, bool everyExpiry)
    {
        var rows = new List<(string, string)>();
        foreach (var bounty in bounties)
        {
            string tone = bounty.Local ? MainTag : GreyTag, wanted = bounty.Local ? "" : GreyTag;
            rows.Add((tone + bounty.Faction, tone + Loc.Tr("c.{1,num}", bounty.Amount)));
            var crimes = bounty.Crimes.Split(", ", StringSplitOptions.RemoveEmptyEntries);
            if (crimes.Length > 0) rows.Add((wanted + Loc.Tr("-Wanted For:") + " ", wanted + string.Join(", ", crimes.Take(4))));
            if (crimes.Length > 4) rows.Add((" ", wanted + string.Join(", ", crimes.Skip(4))));
            if (everyExpiry) rows.Add((Expiry(bounty), ""));
        }
        if (prison is { } p)
        {
            int left = (int)Math.Ceiling(p.HoursLeft);
            rows.Add(p.Sentence >= 2000 ? (Loc.Tr("Imprisoned, sentence time:"), TitleTag + Loc.Tr("FOREVER"))
                : left > 0 ? (Loc.Tr("Imprisoned, sentence time remaining:"), TitleTag + Loc.Tr(left == 1 ? "{1,num} hr" : "{1,num} hrs", left))
                : (Loc.Tr("Imprisoned"), Loc.Tr("Waiting to be released")));
            return rows;
        }
        rows.Add(("-------------------------", ""));
        rows.Add((Loc.Tr("This character is wanted by the authorities. Turn them in for a reward."), ""));
        // The original appends the expiry to the turn-in line after a line break; a row of its own here, so its colour tag leads it.
        if (bounties.FirstOrDefault(v => v.Local) is { Amount: > 0 } local) { if (!everyExpiry) rows.Add((Expiry(local), "")); }
        else rows.Add((MainTag + Loc.Tr("This character is not wanted by the local faction"), ""));
        return rows;

        // FUN_1405d1110: int(lifetime - elapsed) hours, "Notorious" in Title above 9999 (the lifetime is then 9999999).
        static string Expiry(Meitou.Simulation.Crime.BountyLine bounty)
        {
            if (double.IsPositiveInfinity(bounty.HoursLeft)) return TitleTag + Loc.Tr("Notorious: The bounty will never expire.");
            int hours = (int)bounty.HoursLeft;
            return MainTag + Loc.Tr(hours == 1 ? "The bounty will expire in {1,num} hr" : "The bounty will expire in {1,num} hrs", hours);
        }
    }

    /// <summary>
    /// The bounty panel's own text (the other output of <c>FUN_1405d2da0</c>): wanted by the local faction, Title <c>Bounty:</c> and its amount in Main; else both Greyed
    /// with the largest bounty (<c>FUN_140853740</c>).
    /// </summary>
    static HudLine BountyLine(IReadOnlyList<Meitou.Simulation.Crime.BountyLine> bounties) =>
        bounties.FirstOrDefault(v => v.Local) is { Amount: > 0 } local
            ? new(Loc.Tr("Bounty:"), Loc.Tr("c.{1,num}", local.Amount), TitleColour, MainColour)
            : new(Loc.Tr("Bounty:"), Loc.Tr("c.{1,num}", bounties.Count == 0 ? 0 : bounties.Max(v => v.Amount)), GreyColour, GreyColour);

    /// <summary>
    /// The status lines of a slave (<c>FUN_1405d4260</c>, <b>Observed</b>; docs/game/ui-screens.md section 4): a title, "Slave" (state 1) or "Escaped slave" (state 2), and its text naming the owner
    /// ("someone" without one); a player character's text ends with the hint on the obedient-slave job (state 1) or on keeping the looks-like-a-slave level low (state 2). Engine choices:
    /// the hint is shown for player characters, the title takes the Title colour and the text the Main one.
    /// </summary>
    static List<(string Key, string Value)> SlaveRows(SlaveView slave, bool player)
    {
        string owner = slave.Owner.Length > 0 ? slave.Owner : Loc.Tr("someone");
        bool escaped = slave.State == 2;
        string hint = !player ? "" : escaped
            ? Loc.Tr("Keep your [Looks Like A Slave] level as low as possible, wear clothing or disguises and find a plastic surgeon to restore your hair. Don't get spotted.")
            : Loc.Tr("Your character will have a permanent job assigned called [Obedient slave]. While this is active he will automatically work for his master. You can remove this job, but beware of the consequences...");
        string text = escaped
            ? Loc.Tr("A slave of {1} that has been seen escaping. Needs to lay low for a day and avoid getting spotted. {2}", owner, hint)
            : Loc.Tr("A slave belonging to {1}. Not considered a person with rights, a slave works for his master until death and nothing more. {2}", owner, hint);
        return [(TitleTag + Loc.Tr(escaped ? "Escaped slave" : "Slave"), ""), (MainTag + text.TrimEnd(), "")];
    }

    /// <summary>Gives the HUD's widgets their tooltips (user strings through the bindings, which the arranged controls merge) and the selected building's status word its own.</summary>
    void BindTooltips(UiBindings b)
    {
        void Add(string name, string text)
        {
            var properties = b.Properties.TryGetValue(name, out var existing) ? new Dictionary<string, string>(existing) : [];
            foreach (var pair in UiTooltips.TextProperties(text, "fixed")) properties[pair.Key] = pair.Value;
            b.Properties[name] = properties;
        }
        foreach (var (widget, text) in HudTips) Add(widget, text);
        if (Lead() is { } wanted && (wanted.Bounties.Count > 0 || wanted.Slave is not null))
        {
            var rows = wanted.Slave is { } slave ? SlaveRows(slave, wanted.IsPlayer) : [];
            if (wanted.Bounties.Count > 0) rows.AddRange(BountyRows(wanted.Bounties, wanted.Prison, UiMode == UiMode.Meitou));
            foreach (string name in new[] { "BountyPanel", "BountyText" })
            {
                var properties = b.Properties.TryGetValue(name, out var existing) ? new Dictionary<string, string>(existing) : [];
                foreach (var pair in UiTooltips.RowProperties(rows, "fixed")) properties[pair.Key] = pair.Value;
                b.Properties[name] = properties;
            }
        }
        if (BuildingFocus() is { } focus)
        {
            var (_, _, _, status, _) = BuildingLines(focus);
            if (BuildingStatusTip(status, Machine(focus.SiteId)?.Farm is not null) is { } tip) Add("HealthText", tip);
        }
        else if (Lead()?.Body is { } body) Add("HealthText", HealthWordTip(body));
    }

    // ---- item tooltips (the Item classes' vfunc_79: FUN_1407ae130 and its callees) ----

    /// <summary>What an item cell's tooltip shows: the item as the inventory or the trade window lists it.</summary>
    internal sealed record ItemTip(string Record, string Material, string Name, int Quantity, float Weight, float Quality, string Company, int Level, float Charges, float MaxCharges,
        int StolenFrom, TradeItemView? Trade, string? MapNote)
    {
        /// <summary>Only the name (the machine slots' items, which the original's tooltip shows the same way).</summary>
        public bool Brief { get; init; }
        /// <summary>The customer's own item while trading (its Sell value is the transfer's price); otherwise a stock item's Value is.</summary>
        public bool Goods { get; init; }
        public static ItemTip NameOnly(string record, string material, string name, int quantity) => new(record, material, name, quantity, 0, 0, "", 0, 0, 0, -1, null, null) { Brief = true };
        public static ItemTip Of(InventoryItemView v, TradeItemView? trade = null, string? mapNote = null) =>
            new(v.Record, v.Material, v.Name, v.Quantity, v.Weight, v.Quality, v.Company, v.Level, v.Charges, v.MaxCharges, v.StolenFrom, trade, mapNote) { Goods = trade is not null };
        public static ItemTip Of(TradeItemView v) =>
            new(v.Record, v.Material, v.Name, v.Quantity, v.Weight, v.Quality, v.Company, v.Level, v.Charges, v.MaxCharges, v.StolenFrom, v, null);
    }

    static string Number(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// A prosthetic's rows (<c>FUN_1407a9080</c>, <b>Verified</b> in the decompile): the grade of its level in the grade colour, <c>-Max HP</c>, the <c>-----STATS-----</c> header, a row
    /// per stat whose multiplier at that level is not 1 (<see cref="Meitou.Data.Gameplay.Bodies.LimbReplacement.Multiplier"/>) as int((m - 1) x 100) percent with its sign, a bonus in
    /// the good colour and a penalty in the bad one, <c>-Unarmed damage bonus</c> when not 0, and a rule (the weight follows).
    /// </summary>
    static IEnumerable<TooltipLine> LimbRows(Meitou.Data.Gameplay.Bodies.LimbReplacement limb, int level)
    {
        int grade = ArmourTooltip.GradeOf(level);
        yield return new(Loc.Tr("{1} Grade", Loc.Tr(ArmourTooltip.GradeNames[grade])), "", GradeTone(grade));
        float t = Math.Clamp(level * 0.01f, 0, 1);
        yield return new("-Max HP", Number(MathF.Floor(limb.Hp(t))), TooltipTone.Title, TooltipTone.Ink);
        yield return new("-----STATS-----", "", TooltipTone.Grey);
        foreach (var stat in Enum.GetValues<Meitou.Data.Gameplay.Bodies.StatsEnumerated>())
        {
            float m = limb.Multiplier(stat, t);
            if (m == 1) continue;
            int percent = (int)((m - 1) * 100);
            yield return new("-" + StatLabel(stat), (percent >= 1 ? "+" : "") + percent.ToString(CultureInfo.InvariantCulture) + "%", TooltipTone.Title, m < 1 ? TooltipTone.Bad : TooltipTone.Good);
        }
        if (limb.UnarmedDamageBonus(t) is not 0 and var unarmed)
            yield return new("-Unarmed damage bonus", unarmed.ToString(CultureInfo.InvariantCulture), TooltipTone.Title, unarmed < 0 ? TooltipTone.Bad : TooltipTone.Good);
        yield return new("----------------", "", TooltipTone.Grey);
    }

    /// <summary>
    /// The rows of an item's tooltip, in the order the item classes write them (<b>Observed</b>, decompile of <c>FUN_1407ae130</c>, the weapon and armour functions): the name (with the stack
    /// count), kind markers, the stolen mark, the class's own rows, the weight, then the prices while trading, the charges and the quality. A row's label carries a leading hyphen as the
    /// original's do (docs/game/ui-screens.md "Tooltips"). What Meitou has no data for is left out (listed there).
    /// </summary>
    IReadOnlyList<TooltipLine> ItemLines(ItemTip t)
    {
        var record = ItemRecordOf?.Invoke(t.Record);
        var type = record?.Type;
        // A weapon's grade is its model ("Mk I"), shown under the name in the colour of its level's grade; a brief tooltip puts it before the name ("Mk I Fragment Axe").
        string? model = type == Meitou.Data.Fcs.FcsRecordType.WEAPON && t.Material.Length > 0 && RecordName?.Invoke(t.Material) is { Length: > 0 } m ? m : null;
        string name = t.Brief && model is not null ? model + " " + t.Name : t.Name;
        var lines = new List<TooltipLine> { new(t.Quantity > 1 ? $"{name} ({t.Quantity})" : name, "", TooltipTone.Title) };
        if (t.Trade is { Buyback: true }) lines.Add(new("Buyback", ""));
        if (t.Brief) return lines;
        // Every grade line (weapon model, armour, crossbow, prosthetic) goes right under the name, as in the original.
        if (model is not null) lines.Add(new(model, "", GradeTone(ArmourTooltip.GradeOf(t.Level))));
        else if (type == Meitou.Data.Fcs.FcsRecordType.CROSSBOW)
        {
            int grade = ArmourTooltip.GradeOf(t.Level);
            lines.Add(new(Loc.Tr("{1} Grade", Loc.Tr(ArmourTooltip.GradeNames[grade])), "", GradeTone(grade)));
        }
        var combat = CombatOf?.Invoke();
        var armour = type == Meitou.Data.Fcs.FcsRecordType.ARMOUR ? combat?.Armour(t.Record) : null;
        var armourHead = armour is not null ? ArmourTooltip.Head(armour, t.Level).Select(ArmourRow).ToList() : null;
        var limbRows = type == Meitou.Data.Fcs.FcsRecordType.LIMB_REPLACEMENT ? LimbRows(Meitou.Data.Gameplay.Bodies.LimbReplacement.From(record!), t.Level).ToList() : null;
        if (armourHead is not null) { lines.Add(armourHead[0]); armourHead.RemoveAt(0); }
        if (limbRows is not null) { lines.Add(limbRows[0]); limbRows.RemoveAt(0); }
        int function = type == Meitou.Data.Fcs.FcsRecordType.ITEM ? record!.GetInt("item function") : 0;
        float unitWeight = t.Quantity > 0 ? t.Weight / t.Quantity : t.Weight;
        TooltipLine Row(string label, string value, TooltipTone valueTone = TooltipTone.Ink) => new("-" + label, value, TooltipTone.Ink, valueTone);
        if (function == 3)
        {
            // One unit's charges left, truncated, then the record's ingredients by name (", "-joined, empty for raw food); no leading hyphen.
            lines.Add(new("[Food]", "", TooltipTone.Good));
            lines.Add(new("Nutrition", ((int)t.Charges).ToString(CultureInfo.InvariantCulture) + " nu", TooltipTone.Ink, TooltipTone.Ink));
            var ingredients = string.Join(", ", record!.GetReferences("ingredients").Select(r => RecordName?.Invoke(r.TargetStringId)).Where(n => n is { Length: > 0 }));
            lines.Add(new("Ingredients", ingredients, TooltipTone.Ink, TooltipTone.Ink));
        }
        else if (function == 15) lines.Add(new("Possibly Edible", "", TooltipTone.Good));
        else if (function == 13) lines.Add(new("[Book]", "", TooltipTone.Main));
        else if (function == 16) lines.Add(new("[Ammunition]", "", TooltipTone.Main));
        if (t.StolenFrom >= 0)
            lines.Add(FactionName?.Invoke(t.StolenFrom) is { Length: > 0 } owner ? new("Stolen from:", owner, TooltipTone.Bad, TooltipTone.Bad) : new("Stolen", "", TooltipTone.Bad));
        string weightText;
        if (type == Meitou.Data.Fcs.FcsRecordType.WEAPON && combat?.Weapon(t.Record) is { } weapon)
        {
            var maker = combat.Manufacturer(t.Company) ?? WeaponManufacturer.Neutral;
            var stats = WeaponStats.Compute(t.Level * 0.01f, weapon, maker, combat.WeaponMaterial(t.Material) ?? WeaponMaterial.None, combat.Constants);
            string? className = weapon.SkillCategory switch
            {
                (int)WeaponCategory.Katanas => "Katana class", (int)WeaponCategory.Sabres => "Sabre class", (int)WeaponCategory.Blunt => "Blunt weapon class",
                (int)WeaponCategory.Heavy => "Heavy weapons class", (int)WeaponCategory.Hackers => "Hacker class", (int)WeaponCategory.Bow => "Crossbow class",
                (int)WeaponCategory.Polearms => "Polearm class", _ => null,
            };
            if (className is not null) lines.Add(new(className, "", TooltipTone.Main));
            string Fixed2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            lines.Add(Row("Cutting damage", Fixed2(stats.CutMultiplier), TooltipTone.Main));
            lines.Add(Row("Blunt damage", Fixed2(stats.BluntMultiplier), TooltipTone.Main));
            lines.Add(Row("Blood loss", Fixed2(weapon.BleedMult), TooltipTone.Main));
            if (weapon.ArmourPenetration * 100 != 0) lines.Add(Row("Armour penetration", Number(weapon.ArmourPenetration * 100) + "%", TooltipTone.Main));
            if (stats.AttackMod != 0) lines.Add(Row("Attack bonus", stats.AttackMod.ToString(CultureInfo.InvariantCulture), stats.AttackMod < 0 ? TooltipTone.Bad : TooltipTone.Good));
            if (stats.DefenceMod != 0) lines.Add(Row("Defence bonus", stats.DefenceMod.ToString(CultureInfo.InvariantCulture), stats.DefenceMod < 0 ? TooltipTone.Bad : TooltipTone.Good));
            if (stats.IndoorsMod != 0) lines.Add(Row(stats.IndoorsMod < 0 ? "Indoors penalty" : "Indoors bonus", stats.IndoorsMod.ToString(CultureInfo.InvariantCulture), stats.IndoorsMod < 0 ? TooltipTone.Bad : TooltipTone.Good));
            lines.Add(new("----------------", "", TooltipTone.Grey));
            weightText = ((int)stats.Weight).ToString(CultureInfo.InvariantCulture) + " kg";
            lines.Add(Row("Weight", weightText));
        }
        else
        {
            // Armour: grade, class and material, resistances (ArmourTooltip; docs/game/ui-screens.md "Item tooltip").
            if (armourHead is not null) lines.AddRange(armourHead);
            if (limbRows is not null) lines.AddRange(limbRows);
            // "{n} x {unit} kg ({total} kg)" for a stack.
            // An armour piece prints whole kilograms, truncated (FUN_1407a1650, Observed).
            weightText = armour is not null ? ((int)unitWeight).ToString(CultureInfo.InvariantCulture) + " kg"
                : (t.Quantity > 1 ? $"{t.Quantity} x " : "") + Number(unitWeight) + " kg" + (t.Quantity > 1 ? $" ({Number(t.Weight)} kg)" : "");
            lines.Add(Row("Weight", weightText));
        }
        if (t.Trade is { } trade)
        {
            // Value and Sell value: one unit's prices (slot 81), with the trader factor as a mark-up in the bad colour (FUN_14079e7a0).
            int percent = trade.PriceFactor > 1 ? (int)((trade.PriceFactor - 1) * 100) : trade.PriceFactor < 1 ? (int)((1 - trade.PriceFactor) * 100) : 0;
            string suffix = trade.PriceFactor == 1 ? "" : $" ({(trade.PriceFactor > 1 ? "+" : "-")}{percent}%)";
            // A buyback price is the exact refund, not marked up.
            lines.Add(new("-Value", "c." + Money(trade.Value), TooltipTone.Ink, TooltipTone.Ink, trade.Buyback && !t.Goods ? "" : suffix, TooltipTone.Bad));
            lines.Add(new("-Sell value", "c." + Money(trade.SellValue), TooltipTone.Ink, TooltipTone.Ink, trade.Buyback && t.Goods ? "" : suffix, TooltipTone.Bad));
        }
        else if (GearPricesOf?.Invoke(t.Record, t.Level, t.Company) is { } gear)
        {
            // Outside a trade: the plain prices, no trader (the inventory tooltip prints them all the time).
            lines.Add(new("-Value", "c." + Money(gear.Value), TooltipTone.Ink, TooltipTone.Ink));
            lines.Add(new("-Sell value", "c." + Money(gear.Sell), TooltipTone.Ink, TooltipTone.Ink));
        }
        if (armour is not null)
            lines.AddRange(ArmourTooltip.Tail(armour, id => RecordName?.Invoke(id), t.Level).Select(ArmourRow));
        if (type == Meitou.Data.Fcs.FcsRecordType.ITEM && function is not (3 or 16))
        {
            if (t.MaxCharges > 1) lines.Add(Row("Charges", $"{Number(t.Charges)}/{Number(t.MaxCharges)}"));
            if (t.Quality > 0 && t.Quality != 100) lines.Add(Row("Quality", $"{Number(t.Quality)}/100"));
        }
        if (t.MapNote is { Length: > 0 } note)
            foreach (string row in note.Split('\n')) lines.Add(new(row, "", row.StartsWith("[Right", StringComparison.Ordinal) || row.StartsWith("[Location", StringComparison.Ordinal) ? TooltipTone.Cyan : TooltipTone.Ink));
        return lines;
    }
}
