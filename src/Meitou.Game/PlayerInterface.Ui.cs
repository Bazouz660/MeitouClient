using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Data.Characters;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    public UiMode UiMode { get; set; }
    int lastMemberClick = -1;
    bool infoExpanded;
    long lastMemberClickAt;
    MyGuiLayout faithfulHud = null!;
    MyGuiResources uiResources = new();
    readonly UiHost uiHost = new();
    IDisposable? uiRegistration;
    UiPainter? uiPainter;
    internal UiPainter? SettingsPainter => uiPainter;
    internal Func<string, string?>? SettingsResolve => uiResolve;
    bool settingsOpen;
    internal void SetSettingsOpen(bool value) { settingsOpen = value; if (value) CancelInventoryDrag(); UpdateMenuPause(); }
    MyGuiScreenHost faithfulScreens = null!;
    public event Action<MyGuiEvent>? ScreenAction;

    /// <summary>The viewed floor of the interior view (the host's): read for the HUD's floor text, stepped by its arrows.</summary>
    public (Func<int> Get, Action<int> Step)? Floor { get; set; }
    public bool UiVisible { get; set; } = true;
    /// <summary>The share of the screen under fully opaque controls in the last drawn UI frame (<see cref="UiPainter.OpaqueCoverage"/>); 0 without a painter.</summary>
    public float OpaqueUiCoverage => uiPainter?.OpaqueCoverage ?? 0;
    internal IReadOnlyList<UiControl> UiFrame => ArrangeUi();

    public void LoadUi(UiMode mode, Func<string, string?> resolve)
    {
        UiMode = mode;
        uiResolve = resolve;
        statHelp = StatHelpCatalog.Read(resolve("locale/en_GB/LC_MESSAGES/main.pot")
            ?? (Meitou.Content.GameInstall.Locate() is { } install ? Path.Combine(install.Root, "locale/en_GB/LC_MESSAGES/main.pot") : null));
        uiResources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
        faithfulHud = MyGuiTemplates.Expand(MyGuiReader.ReadLayout(resolve("Kenshi_MainPanel.layout") ?? throw new FileNotFoundException("Kenshi_MainPanel.layout not found.")), uiResources);
        faithfulScreens = new(uiResources);
        foreach (var (id, file, modal) in FaithfulScreenFiles)
            faithfulScreens.Register(id, MyGuiReader.ReadLayout(resolve(file + ".layout") ?? throw new FileNotFoundException(file)), modal,
                centreRoot: id is "new-game" or "save" or "load");
        faithfulScreens.Activated += FaithfulAction;
        uiHost.Diagnostic += d => Console.Error.WriteLine($"ui        {d.Extension} {d.Operation}: {d.Error.Message}");
        uiRegistration = uiHost.Register(new PlayerUiExtension(ExecuteUiAction));
    }

    /// <summary>The original layouts the faithful mode opens as screens: id, layout file, modal.</summary>
    internal static readonly (string Id, string File, bool Modal)[] FaithfulScreenFiles =
    [
        ("inventory", "Kenshi_InventoryCharacterWindow", false), ("stats", "Kenshi_StatsWindow", false),
        ("overview", "Kenshi_OverviewWindow", false), ("menu", "Kenshi_MainMenuPopupPanel", true),
        ("title", "Kenshi_MainMenu", true), ("new-game", "Kenshi_NewGamePanel", true),
        ("advanced", "Kenshi_AdvancedOptionsWindow", true), ("save", "Kenshi_SaveGamePanel", true),
        ("load", "Kenshi_LoadGamePanel", true), ("message", "Kenshi_MessageBox", true),
        ("bag", "Kenshi_InventoryGenericWindow", false), ("limbs", "Kenshi_LimbReplacementWindow", false), ("trader", "Kenshi_InventoryTraderWindow", false),
        ("building", "Kenshi_GenericBuildingWindow", false), ("bench", "Kenshi_InventoryResearchWindow", false),
        ("build", "Kenshi_BuildWindow", false), ("editor", "Kenshi_CharacterEditor", true), ("dialogue", "Kenshi_ConversationPanel", true),
    ];

    /// <summary>The expanded layout and the current bindings of the HUD (<c>"hud"</c>) or of a faithful screen, for the layout audit test.</summary>
    internal (MyGuiLayout Layout, UiBindings Bindings) FaithfulBindings(string id) =>
        id == "hud" ? (faithfulHud, BindUi()) : (faithfulScreens.Layout(id), BindFaithfulScreen(id));

    public void CreateUiPainter(DebugOverlay overlay, Func<string, string?> resolve) { uiPainter = new(overlay, uiResources, resolve); findAsset = resolve; }
    public void DisposeUi() { uiPainter?.Dispose(); uiPainter = null; uiRegistration?.Dispose(); uiRegistration = null; }
    /// <summary>Future code-mod loader entry point; disposal removes the extension's owned registrations.</summary>
    public IDisposable RegisterUiExtension(IUiExtension extension) => uiHost.Register(extension);
    public bool ContainsUi(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        return faithfulScreens.Contains(p) || MyGuiPresentation.HitTest(controls, p.X, p.Y) is not null;
    }

    public void SetUiMode(UiMode mode)
    {
        UiMode = mode;
        dragFrom = null;
        dragging = false;
        steering = false;
        mapDrag = null;
        CancelInventoryDrag();
        UpdateMenuPause();
    }

    UiBindings BindUi()
    {
        var b = new UiBindings();
        var lead = Lead();
        b.Text["clock"] = $"{session.Clock.DayText}  {session.Clock.TimeText}";
        b.Text["DayText"] = session.Clock.DayText;
        b.Text["TimeText"] = session.Clock.TimeText;
        // The exe's money format string is c.{1,num} (docs/game/ui-screens.md section 1): no space after the point.
        b.Text["MoneyAmountText"] = "c." + session.World.Player.Money.ToString("N0", CultureInfo.InvariantCulture);
        // The name bar draws its own lines over the widget (PlayerInterface.HudText.cs).
        b.Text["NameText"] = lead?.Name ?? "No character selected";
        b.Properties["NameText"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        b.Text["hunger"] = lead?.Body is { } hungry ? $"Hunger {hungry.Hunger:0.00} / 3" : "";
        b.Text["condition"] = lead?.Body is { } status ? status.Dead ? "Dead" : status.Unconscious ? "Unconscious" : status.NeedsAid ? "Needs first aid" : "Conscious" : "No character selected";
        b.Text["blood"] = lead?.Body is { } blood ? $"Blood   {blood.Blood * 100:0}%" : "Blood --";
        b.Text["body"] = lead?.Body is { } worst ? $"Worst body part   {worst.WorstPart * 100:0}%" : "Body --";
        b.Text["bloodFraction"] = (lead?.Body?.Blood ?? 0).ToString(CultureInfo.InvariantCulture);
        b.Text["bodyFraction"] = (lead?.Body?.WorstPart ?? 0).ToString(CultureInfo.InvariantCulture);
        b.Text["hungerFraction"] = ((lead?.Body?.Hunger ?? 0) / 3).ToString(CultureInfo.InvariantCulture);
        var roster = session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToArray();
        b.Text["squad.count"] = roster.Length.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < Math.Min(8, roster.Length); i++) b.Text[$"squad.{i}.name"] = roster[i].Name;
        b.Text["members"] = $"{PlayerCharacters().Count} members | {Selected().Count} selected";
        b.Text["ExtendedStatsText"] = "";
        // The exe sets the money label without the layout's colon (docs/game/ui-screens.md section 4).
        b.Text["MoneyLabelText"] = "Money";
        b.Text["BountyText"] = lead is null || lead.Bounties.Count == 0 ? "" : $"Bounty c.{lead.Bounties.Sum(v => v.Amount)}";
        for (int i = 0; i < ButtonSpeeds.Length; i++) b.Actions[$"TimeSpeedButton{i + 1}"] = $"meitou:speed.{i}";
        b.Actions["ShortcutInventoryButton"] = "meitou:inventory";
        b.Actions["ShortcutStatsButton"] = "meitou:stats";
        b.Text["ShortcutTechButton"] = "TEC";
        foreach (string name in new[] { "ShortcutMapButton", "ShortcutTechButton", "ShortcutSquadButton" }) b.Actions[name] = "faithful:" + name;
        b.Actions["OrdersMedicButton"] = "meitou:medic";
        b.Actions["OrdersRescueButton"] = "meitou:rescue";
        var own = Selected().FirstOrDefault() ?? lead;   // the orders, jobs and squad tab stay the player's while another faction's character is inspected
        BindOrders(b, own);
        foreach (string name in new[] { "LoadingPanel", "TownPanel", "TooltipPanel", "MessagePanel", "BiomeTextPanel" }) b.Hidden.Add(name);
        // The viewed floor (the exe's "Floor {1,num}", docs/game/ui-screens.md section 4; docs/formats/zones.md "Interior view"); the arrows step it as PageUp / PageDown do.
        b.Text["FloorText"] = $"Floor {Floor?.Get() ?? 0}";
        b.Actions["FloorArrowUp"] = "meitou:floor-up";
        b.Actions["FloorArrowDown"] = "meitou:floor-down";
        b.Actions["BuildButton"] = "meitou:build";
        if (buildMode) foreach (string name in BuildHiddenHud) b.Hidden.Add(name);
        if (lead is null || lead.Bounties.Count == 0) b.Hidden.Add("BountyPanel");
        // The information panel starts collapsed (as the original's, docs/game/ui-screens.md section 4): the extending part (name, state lines, extended stats) slid down
        // until its bottom is the panel's, behind the stats panel, so only the name and the state lines show; the ^ button between them toggles it.
        if (!infoExpanded && faithfulHud.Find("InformationPanel") is { } info && faithfulHud.Find("ExtendingPanel") is { } extending)
        {
            var infoRect = info.Position.RelativeTo(new(0, 0, Width, Height), true);
            var rect = extending.Position.RelativeTo(infoRect, true);
            b.Positions["ExtendingPanel"] = rect with { Y = infoRect.Y + infoRect.Height - rect.Height };
        }
        b.Actions["ExtendedStatsButton"] = "meitou:extendedstats";
        if (session.Simulation.Speed != 0) b.Hidden.Add("PausedPanel");
        var buildingNow = BuildingFocus();
        bool buildingFocus = buildingNow is not null;
        BindMedicalBars(b, buildingFocus ? null : lead);
        // A selected building shows only the status word in the medical header (its condition bars are not modelled: Meitou has no building health).
        if (buildingNow is not null)
        {
            var (_, _, _, buildingStatus, bad) = BuildingLines(buildingNow);
            b.Text["HealthText"] = buildingStatus;
            b.Properties["HealthText"] = new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = bad ? BadColour : MyGuiColours.InkText };
        }
        BindJobs(b, own);
        // The squad bar has one tab per squad of the player's; Meitou has one squad, so the first page shows, captioned with its name.
        foreach (var widget in faithfulHud.Find("SquadPanel")?.Children.Where(w => w.Type == "TabItem").Skip(1) ?? []) b.HiddenWidgets.Add(widget);
        b.Text["SquadPanel@tab0"] = SquadTabCaption(own);
        if (lead is null && !buildingFocus) b.Hidden.Add("InformationPanel");
        if (lead is null && !buildingFocus) b.Hidden.Add("MedicalPanel");
        // Buttons without a backend are disabled, except those the original never greys out (the build and floor buttons look live there in the open).
        foreach (var w in faithfulHud.Widgets.Where(w => w.Type == "Button" && !b.Actions.ContainsKey(w.Name) && w.Name is not ("BuildButton" or "FloorArrowUp" or "FloorArrowDown"))) b.Disabled.Add(w.Name);
        if (Selected().Count == 0)
            foreach (string name in new[] { "OrdersMedicButton", "OrdersRescueButton", "medic", "rescue", "stop" }) b.Disabled.Add(name);
        return b;
    }

    /// <summary>
    /// The squad tab's caption. The original captions a tab with its squad's name, which for the player's first squad reads like <c>Nameless_0</c>: the faction's
    /// name and the squad's number (<b>Observed</b>; where the original makes it is <b>Unknown</b>). Meitou's player squad keeps its start's template name, so the
    /// tab shows the faction's name and 0.
    /// </summary>
    string SquadTabCaption(Meitou.Simulation.CharacterSnapshot? lead)
    {
        string? faction = FactionName?.Invoke(lead?.Faction ?? session.World.Player.Faction);
        return string.IsNullOrEmpty(faction) ? "Squad 1" : faction + "_0";
    }

    IReadOnlyList<UiControl> ArrangeUi()
    {
        SyncTradeScreens();
        SyncMachineScreens();
        SyncDialogueScreen();
        ValidateInventoryDrag();
        var b = BindUi();
        bool hud = !TitleOpen && !EditorOpen && !conversationShown;   // the conversation panel covers the HUD bar (engine reading of its layout)
        var controls = hud ? MyGuiPresentation.Arrange(faithfulHud, Width, Height, b, uiResources.Layers).ToList() : [];
        UiRect FaithfulSlot(string name) => controls.FirstOrDefault(c => c.Name == name)?.Bounds ?? default;
        var slots = new Dictionary<string, UiRect>
        {
            [UiSlots.Overlay] = new(0, 0, Width, Height),
            [UiSlots.Toolbar] = FaithfulSlot("TimeMoneyPanel"),
            [UiSlots.Selection] = FaithfulSlot("InformationPanel"),
            [UiSlots.Squad] = FaithfulSlot("SquadPanel"),
        };
        if (hud) controls.AddRange(uiHost.Arrange(UiMode, Width, Height, b, slots));
        if (controls.FirstOrDefault(c => c.Name == "SquadPanel") is { } squadPanel) AddSquadPortraits(controls, squadPanel.Bounds);
        AddHudText(controls);
        AddJobRemoveButtons(controls);
        foreach (var screen in faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent))
        {
            if (screen.Id == "editor") controls.AddRange(EditorBackdrop());
            controls.AddRange(screen.Controls.Where(c => UiMode == UiMode.Faithful ||
                !c.Properties.TryGetValue("ImageResource", out var resource) || resource != "Kenshi_TitleScreen"));
            if (screen.Id == "title" && UiMode == UiMode.Meitou && !showCredits && TitleLogoReady())
            {
                float logoWidth = Math.Min(Width * 0.3f, Height * 0.6f);
                controls.Add(new("MeitouTitle", "ImageBox", new(Width * 0.975f - logoWidth, Height * 0.04f, logoWidth, logoWidth * 0.469f), "", "", "", true,
                    new Dictionary<string, string> { ["ImageTexture"] = TitleLogoTexture }, Vector4.Zero, Pickable: false));
            }
            else if (screen.Id == "title" && UiMode == UiMode.Meitou && !showCredits)
                controls.Add(new("MeitouTitle", "TextBox", new(Width * 0.6f, Height * 0.04f, Width * 0.37f, Height * 0.14f), "KENSHI", "", "", true,
                    new Dictionary<string, string> { ["FontName"] = "Meitou_TitleFont", ["TextAlign"] = "Right VCenter" },
                    Vector4.Zero, new Vector4(0.89f, 0.84f, 0.71f, 1), TextScale: Height / 720f));
        }
        AddInventoryDrag(controls);
        var nameTags = new List<UiControl>();   // the tags are on the original's Back layer: under every panel
        AddNameTags(nameTags);
        controls.InsertRange(0, nameTags);
        AddSpeech(controls);
        AddContextMenu(controls);
        pointerOverUi = faithfulScreens.Contains(mouse) || MyGuiPresentation.HitTest(controls, mouse.X, mouse.Y) is not null;
        AddHoverLabel(controls);
        return controls;
    }

    bool UiMouseDown(Vector2 p)
    {
        if (!UiVisible) return false;
        var controls = ArrangeUi();
        if (InventoryPointerDown(p)) return true;
        if (MapPointerDown(p)) return true;
        if (faithfulScreens.PointerDown(p)) { UpdateMenuPause(); return true; }
        if (MyGuiPresentation.HitTest(controls, p.X, p.Y) is not { } c) return false;
        if (c.Action.StartsWith("faithful:", StringComparison.Ordinal))
        {
            faithfulScreens.Open("overview");
            faithfulScreens.SetTab("overview", "TabsMain", c.Name == "ShortcutTechButton" ? 2 : c.Name == "ShortcutSquadButton" ? 4 : 0);
            return true;
        }
        uiHost.Dispatch(c);
        return true;
    }

    void ExecuteUiAction(string action, string argument)
    {
        SoundForAction(action, argument);
        if (action.StartsWith("speed:", StringComparison.Ordinal)) PressButton(int.Parse(action[6..], CultureInfo.InvariantCulture));
        else if (action is "inventory" or "stats")
        {
            if (faithfulScreens.IsOpen(action))
            {
                faithfulScreens.Close(action);
                if (action == "inventory") { faithfulScreens.Close("bag"); faithfulScreens.Close("limbs"); }
            }
            else faithfulScreens.Open(action);
        }
        else if (action == "extendedstats") infoExpanded = !infoExpanded;
        else if (action is "floor-up" or "floor-down") Floor?.Step(action == "floor-up" ? 1 : -1);
        else if (action == "build") ToggleBuildMode();
        else if (action == "context") ContextAction(argument);
        else if (action == "squad") Select(PlayerCharacters(), false);
        else if (action == "member")
        {
            // A portrait click selects, a double click centres and follows (docs/game/ui-input.md section 7).
            int index = int.Parse(argument, CultureInfo.InvariantCulture);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool doubleClick = index == lastMemberClick && System.Diagnostics.Stopwatch.GetElapsedTime(lastMemberClickAt, now).TotalSeconds <= DoubleTapSeconds;
            (lastMemberClick, lastMemberClickAt) = (doubleClick ? -1 : index, now);
            SelectMember(index, false, doubleClick);
        }
        else if (action == "stop") Say(new StopCommand(SelectedIds()) { Tick = session.World.Tick });
        else if (action == "medic") Say(new Meitou.Simulation.FirstAid.MedicJobCommand(SelectedIds()) { Tick = session.World.Tick });
        else if (action == "rescue") Say(new Meitou.Simulation.Carrying.RescueJobCommand(SelectedIds()) { Tick = session.World.Tick });
        else if (action == "job-remove") RemoveJob(argument);
        else if (action.StartsWith("order.", StringComparison.Ordinal) && Enum.TryParse(action[6..], out CharacterOrders order))
            Say(new OrdersCommand(SelectedIds(), order) { Tick = session.World.Tick });
        else if (action.StartsWith("runspeed.", StringComparison.Ordinal))
        {
            int step = int.Parse(action[9..], CultureInfo.InvariantCulture);
            Say(new RunSpeedCommand(SelectedIds(), step) { Tick = session.World.Tick });
            if (Selected().FirstOrDefault() is { } lead) Notice("Move speed: " + RunSpeedName((byte)((((lead.RunSpeed + step) % SpeedMode.Count) + SpeedMode.Count) % SpeedMode.Count)));
        }
    }

    void DrawGameUi(DebugOverlay overlay, int width, int height) =>
        uiPainter?.Draw(ArrangeUi(), width, height, mouse, $"meitou:speed.{Array.IndexOf(ButtonSpeeds, session.Simulation.Speed)}",
            minimumTextPixels: TitleOpen ? 0 : StandardTextPixels * height / 1080f);

    /// <summary>For screenshot reviews: open the same panel through its registered button action.</summary>
    public void ShowUiPanel(string? panel, bool? expandInformation = null)
    {
        if (expandInformation is { } expanded) infoExpanded = expanded;
        if (panel?.StartsWith("editor", StringComparison.Ordinal) == true)
        {
            var tab = panel == "editor-face" ? EditorTab.Face : panel == "editor-hair" ? EditorTab.Hair : EditorTab.Body;
            if (!EditorOpen) { faithfulScreens.CloseAll(); if (buildMode) SetBuildMode(false); if (!OpenEditor(EditKind.Creation, PlayerCharacters())) return; }
            editor!.Tab = tab;
            return;
        }
        faithfulScreens.CloseAll();
        UpdateMenuPause();
        if (panel is null) return;
        if (panel is "overview" or "menu" or "new-game" or "save" or "load" or "title")
        {
            if (panel == "title") ShowTitle();
            else if (panel is "save" or "load") OpenSlots(panel);
            else { faithfulScreens.Open(panel); if (panel == "overview") faithfulScreens.SetTab(panel, "TabsMain", 0); }
            UpdateMenuPause(); return;
        }
        if (panel == "build") { if (buildMode) faithfulScreens.Open("build"); else SetBuildMode(true); return; }
        if (panel is not ("inventory" or "stats")) throw new ArgumentException("Unknown UI panel.", nameof(panel));
        var control = ArrangeUi().First(c => c.Action == $"meitou:{panel}");
        uiHost.Dispatch(control);
    }
}
