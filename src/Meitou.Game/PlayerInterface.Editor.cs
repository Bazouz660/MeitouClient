using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The character editor (<c>CharacterEditWindow</c>, layout <c>Kenshi_CharacterEditor</c>; docs/characters.md "Character editor"): the start's characters after a new
/// game, or one character at a plastic surgeon. The window edits <see cref="CharacterDraft"/>s, shows them on the characters in the world (<see cref="EditorPreview"/>),
/// and on CONFIRM sends each one as an <see cref="EditCharacter"/> command; nothing in the world changes before that.
/// </summary>
sealed partial class PlayerInterface
{
    sealed class EditorSession(EditKind kind, IReadOnlyList<CharacterId> characters, CharacterId surgeon, bool paid = false)
    {
        /// <summary>A surgeon's dialogue took the money already (<see cref="EditCharacter.Paid"/>).</summary>
        public bool Paid { get; } = paid;
        public EditKind Kind { get; } = kind;
        public IReadOnlyList<CharacterId> Characters { get; } = characters;
        public CharacterId Surgeon { get; } = surgeon;
        public int Index;
        public EditorTab Tab = EditorTab.Body;
        public bool Clothes = true;
        public Dictionary<CharacterId, CharacterDraft> Drafts { get; } = [];
        public Dictionary<CharacterId, string> StartNames { get; } = [];
        public int Seed = Environment.TickCount;
        public CharacterId Current => Characters[Index];
        /// <summary>The camera's orbit, the character it was last set for, and the button dragging it (with where the pointer was last).</summary>
        public EditorOrbit Orbit { get; } = new();
        public CharacterId OrbitCharacter;
        public (Meitou.Engine.Input.MouseButton Button, Vector2 Last)? Drag;
    }

    EditorSession? editor;
    /// <summary>The game data and the install root the editor reads races and editor limits from (the host sets them).</summary>
    public (GameDatabase Db, string InstallRoot)? EditorData { get; set; }
    /// <summary>Builds a preview look from a record id and a loadout (the world's appearance source; the host sets it).</summary>
    public Func<string, Loadout, CharacterAppearance?>? PreviewSource { get; set; }
    /// <summary>What the editor shows of a character: the draft version and clothes it was made for, the look on screen, the one being prepared, and the look the shown one replaced since when (the glide).</summary>
    sealed record Preview(int Version, bool Clothes, CharacterAppearance? Shown, CharacterAppearance? Pending, CharacterAppearance? From = null, double Started = 0);
    readonly Dictionary<CharacterId, Preview> previews = [];
    /// <summary>How long the character glides from one look to the next: about the time between two looks while a slider is dragged, so the movement is continuous.</summary>
    const double BlendSeconds = 0.12;
    /// <summary>Whether the renderer has prepared a look (the host sets it); a preview is shown only once it is.</summary>
    public Func<CharacterAppearance, bool>? PreviewReady { get; set; }
    readonly Stopwatch previewClock = Stopwatch.StartNew();
    double lastPreviewBuild = double.NegativeInfinity;

    public bool EditorOpen => editor is not null && faithfulScreens.IsOpen("editor");
    /// <summary>The character the editor shows and its tab, for the host's camera; null while the editor is closed.</summary>
    public (CharacterId Character, EditorTab Tab)? EditorFocus => EditorOpen ? (editor!.Current, editor.Tab) : null;

    /// <summary>
    /// Opens the editor for <paramref name="characters"/> (the start's squad at creation; one character at a surgeon). Characters without a person's look (animals) are
    /// left out; nothing opens when none is left.
    /// </summary>
    public bool OpenEditor(EditKind kind, IReadOnlyList<CharacterId> characters, CharacterId surgeon = default, bool paid = false)
    {
        if (EditorData is null) return false;
        var people = characters.Where(id => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { Appearance: { Animal: null } look } && RecordOf(look) is not null).ToList();
        if (people.Count == 0) return false;
        editor = new EditorSession(kind, people, surgeon, paid);
        previews.Clear();
        faithfulScreens.Open("editor");
        ShowEditorCharacter();
        UpdateMenuPause();
        return true;
    }

    static Meitou.Data.Fcs.FcsRecord? RecordOf(CharacterAppearance look) => look.Loadout?.Appearance.Record ?? look.Body?.Record;

    CharacterDraft? Draft(CharacterId id)
    {
        if (editor is null || EditorData is not { } data) return null;
        if (editor.Drafts.TryGetValue(id, out var draft)) return draft;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { Appearance: { } look } snapshot || RecordOf(look) is not { } record) return null;
        draft = new CharacterDraft(data.Db, data.InstallRoot, record, snapshot.Name);
        editor.Drafts[id] = draft;
        editor.StartNames[id] = snapshot.Name;
        return draft;
    }

    void ShowEditorCharacter()
    {
        if (editor is null || Draft(editor.Current) is not { } draft) return;
        faithfulScreens.SetText("editor", "NameText", draft.Name);
        Select([editor.Current], false);
    }

    void CloseEditor()
    {
        faithfulScreens.Close("file");
        faithfulScreens.Close("editor");
        editor = null;
        previews.Clear();
        UpdateMenuPause();
    }

    /// <summary>
    /// The look to draw for a character while the editor has a draft of it (null: its own). A changed draft gets a new look (at most every 0.12 s while a slider is
    /// dragged); the last ready one stays on screen until the renderer has prepared the new one (<see cref="PreviewReady"/>), so the character never blinks out.
    /// </summary>
    public CharacterAppearance? EditorPreview(CharacterId id, CharacterAppearance? own)
    {
        if (!EditorOpen || !editor!.Drafts.TryGetValue(id, out var draft) || own?.Loadout is not { } loadout || PreviewSource is null) return null;
        bool clothes = editor.Clothes;
        if (draft.Version == 0 && clothes) { previews.Remove(id); return null; }
        var entry = previews.TryGetValue(id, out var found) ? found : new Preview(-1, true, own, null);
        double now = previewClock.Elapsed.TotalSeconds;
        if (entry.Pending is { } pending && (PreviewReady?.Invoke(pending) ?? true)) entry = entry with { Shown = pending, Pending = null, From = entry.Shown ?? own, Started = now };
        if ((entry.Version != draft.Version || entry.Clothes != clothes) && now - lastPreviewBuild >= 0.12)
        {
            lastPreviewBuild = now;
            if (EditorData is { } data && Meitou.Simulation.Saving.SavedKit.Wear(loadout, draft.Record, data.Db) is { } worn)
            {
                if (!clothes) { worn.Clothing.Clear(); worn.Backpack = null; }
                if (PreviewSource(own.Character?.StringId ?? "", worn) is { } look) entry = entry with { Pending = look };
            }
            entry = entry with { Version = draft.Version, Clothes = clothes };
        }
        previews[id] = entry;
        return entry.Shown ?? own;
    }

    /// <summary>The preview look being prepared for a character (the host lists it hidden, so the renderer builds it), or null.</summary>
    public CharacterAppearance? EditorPending(CharacterId id) => EditorOpen && previews.TryGetValue(id, out var entry) ? entry.Pending : null;

    /// <summary>The ImageBox texture name of the renderer's stage picture (<see cref="Meitou.Rendering.Characters.CharacterRenderer.StageTexture"/>).</summary>
    public const string StageTexture = "meitou:stage";
    Func<string, string?>? findAsset;
    string? groundFile;

    /// <summary>
    /// The editor's scene behind its panels (the original's compositor <c>Kenshi_Character_Editor</c>): <c>SandyGround.dds</c> over the whole window, stretched as the original's full-screen
    /// quad is, and the character's picture on it. First in the screen's controls, so the panels draw over both.
    /// </summary>
    List<UiControl> EditorBackdrop()
    {
        var backdrop = new List<UiControl>();
        if (uiPainter is not { } painter) return backdrop;
        groundFile ??= findAsset?.Invoke("SandyGround.dds") ?? "";
        var all = new UiRect(0, 0, Width, Height);
        // A sand colour first, so the window is covered before the ground texture is decoded.
        backdrop.Add(new("editor-base", "Widget", all, "", "", "", true, new Dictionary<string, string>(), new Vector4(0.42f, 0.40f, 0.34f, 1), Pickable: false));
        if (groundFile.Length > 0 && painter.IconReady(groundFile))
            backdrop.Add(new("editor-ground", "ImageBox", all, "", "", "", true, new Dictionary<string, string> { ["ImageTexture"] = Meitou.Rendering.Ui.UiPainter.IconTexture(groundFile) }, Vector4.Zero, Pickable: false));
        if (painter.HasTexture(StageTexture))
            backdrop.Add(new("editor-stage", "ImageBox", all, "", "", "", true, new Dictionary<string, string> { ["ImageTexture"] = StageTexture, ["ImageFlipV"] = "true" }, Vector4.Zero, Pickable: false));
        return backdrop;
    }

    /// <summary>The look the character's shown one has just replaced and how far the glide to the shown one is (0..1), or null once it has arrived (the renderer mixes the two).</summary>
    public (CharacterAppearance From, float Blend)? EditorBlend(CharacterId id)
    {
        if (!EditorOpen || !previews.TryGetValue(id, out var entry) || entry.From is not { } from) return null;
        double t = (previewClock.Elapsed.TotalSeconds - entry.Started) / BlendSeconds;
        return t >= 1 ? null : (from, (float)Math.Max(t, 0));
    }

    /// <summary>
    /// The editor's camera for the host: the character, the orbit and whether it is on the head. The orbit starts over when the tab changes between the body and the
    /// head (FACE and HAIR share one view) or the character changes, as the original's camera resets then.
    /// </summary>
    public (CharacterId Character, float Yaw, float Elevation, float Distance, bool Head)? EditorView()
    {
        if (!EditorOpen || editor is not { } s) return null;
        bool head = s.Tab != EditorTab.Body;
        if (s.Orbit.Head != head || s.OrbitCharacter != s.Current) { s.Orbit.Reset(head); s.OrbitCharacter = s.Current; s.Drag = null; }
        return (s.Current, s.Orbit.Yaw, s.Orbit.Elevation, s.Orbit.Distance, head);
    }

    /// <summary>Whether <paramref name="p"/> is on the editor's scene rather than on one of its panels or a window above it (the editor's screen is modal: what no widget of it covers counts as the scene).</summary>
    bool OverEditorScene(Vector2 p) => faithfulScreens.OpenScreens[^1] == "editor" && faithfulScreens.HitTest(p) is { Control.Name: "modal" };

    /// <summary>A mouse button pressed away from the editor's panels starts turning the camera; false when the press belongs to a panel.</summary>
    bool EditorPointerDown(Meitou.Engine.Input.MouseButton button, Vector2 p)
    {
        if (!EditorOpen || !UiVisible || editor is not { } s || !OverEditorScene(p)) return false;
        s.Drag = (button, p);
        return true;
    }

    bool EditorPointerMove(Vector2 p)
    {
        if (editor?.Drag is not { } drag) return false;
        editor.Orbit.Drag(p.X - drag.Last.X, p.Y - drag.Last.Y);
        editor.Drag = (drag.Button, p);
        return true;
    }

    void EditorPointerUp(Meitou.Engine.Input.MouseButton button)
    {
        if (editor?.Drag is { } drag && drag.Button == button) editor.Drag = null;
    }

    /// <summary>The wheel away from the panels zooms the camera.</summary>
    bool EditorWheel(Vector2 p, float steps)
    {
        if (!EditorOpen || editor is not { } s || !OverEditorScene(p)) return false;
        s.Orbit.Zoom(steps);
        return true;
    }

    void EditorAction(MyGuiEvent e)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        switch (e.Widget)
        {
            case "BodyTabButton": s.Tab = EditorTab.Body; return;
            case "FaceTabButton": s.Tab = EditorTab.Face; return;
            case "HairTabButton": s.Tab = EditorTab.Hair; return;
            case "NameText" when e.Action == "text": draft.Name = e.Text; return;
            case "RandomiseButton": draft.Randomise(s.Tab, s.Seed++); return;
            case "RandomiseAllButton": draft.Randomise(null, s.Seed++); return;
            case "ResetButton": draft.Reset(); return;
            case "ClothesButton": s.Clothes = !s.Clothes; return;
            case "CharLeftArrow" or "CharRightArrow":
                s.Index = (s.Index + (e.Widget == "CharLeftArrow" ? s.Characters.Count - 1 : 1)) % s.Characters.Count;
                ShowEditorCharacter();
                return;
            case "RaceLeftButton" or "RaceRightButton" or "SubRaceLeftButton" or "SubRaceRightButton" when creation:
            {
                var groups = EditorRaces(data.Db);
                if (groups.Count == 0) return;
                int g = Math.Max(0, IndexOf(groups, draft.Race));
                int r = Math.Max(0, groups[g].Races.ToList().FindIndex(x => x.StringId == draft.Race.StringId));
                int step = e.Widget.Contains("Left", StringComparison.Ordinal) ? -1 : 1;
                if (e.Widget.StartsWith("Race", StringComparison.Ordinal)) { g = (g + step + groups.Count) % groups.Count; r = 0; }
                else r = (r + step + groups[g].Races.Count) % groups[g].Races.Count;
                draft.SetRace(groups[g].Races[r], s.Seed++);
                return;
            }
            case "GenderLeftButton" or "GenderRightButton" when creation:
                draft.SetFemale(!draft.Female, s.Seed++);
                return;
            case "ConfirmButton":
                ConfirmEditor();
                return;
            case "ExportButton":
                ExportLook(draft, data.InstallRoot);
                return;
            case "ImportButton":
                importFiles = ExportedLooks(data.InstallRoot);
                importChoice = importFiles.FirstOrDefault();
                faithfulScreens.Open("file");
                return;
        }
        if (e.Action.StartsWith("editor-slider:", StringComparison.Ordinal) && Control(e.Action[14..]) is { } slider)
            draft.Set(slider, CharacterGenerator.Round(slider.Min + e.Value * (slider.Max - slider.Min)));
        else if (e.Action.StartsWith("editor-pick:", StringComparison.Ordinal))
        {
            var parts = e.Action.Split(':');
            if (parts.Length == 3 && Control(parts[1]) is { IsPicker: true } picker) draft.Step(picker.Kind, int.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        EditorControl? Control(string index) =>
            int.TryParse(index, CultureInfo.InvariantCulture, out int i) ? draft.Controls(s.Tab).ElementAtOrDefault(i) : null;
    }


    static int IndexOf(IReadOnlyList<EditorRaceGroup> groups, GameRecord race)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Races.Any(r => r.StringId == race.StringId)) return i;
        return -1;
    }

    /// <summary>The races this session offers: at creation the playable ones, limited to the start's <c>force race</c>; a surgeon offers none (race stays).</summary>
    IReadOnlyList<EditorRaceGroup> EditorRaces(GameDatabase db) =>
        editor?.Kind == EditKind.Creation ? CharacterDraft.PlayableRaces(db, [.. session.World.Player.Local.CreationRaces]) : [];

    /// <summary>
    /// CONFIRM. Like the original (<c>FUN_1405e9f40</c>), a squad with characters never shown asks first ("There are other characters whose appearance haven't
    /// been changed..."). Every changed character is sent to the world (a surgeon's patient always: the surgery is paid on confirming); at creation the creation then ends.
    /// </summary>
    void ConfirmEditor(bool asked = false)
    {
        if (editor is not { } s) return;
        if (!asked && s.Drafts.Count < s.Characters.Count)
        {
            Confirm("CONFIRM", "There are other characters whose appearance haven't been changed. Are you sure you want to continue?", () => ConfirmEditor(true));
            return;
        }
        long tick = session.World.Tick;
        foreach (var (id, draft) in s.Drafts)
            if (s.Kind == EditKind.Surgery || draft.Version > 0 || draft.Name != s.StartNames[id])
                Say(new EditCharacter(id, CharacterDraft.Copy(draft.Record), draft.Name, s.Kind) { Tick = tick, Surgeon = s.Surgeon, Paid = s.Paid });
        if (s.Kind == EditKind.Creation) Say(new FinishCreation { Tick = tick });
        CloseEditor();
    }


    /// <summary>For the screenshot showcase: the open editor's character gets a new random look and name, and CONFIRM is pressed (the creation path end to end).</summary>
    internal void ShowcaseEdit()
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft) return;
        draft.Randomise(null, 7);
        draft.Name = "Edited " + draft.Name;
        ConfirmEditor(asked: true);
    }
    /// <summary>The body files IMPORT and EXPORT use, under the install (<c>FUN_141539ad0</c>), and their extension.</summary>
    const string ExportFolder = "data/character/bodies/export/", BodyExtension = ".bod2";
    IReadOnlyList<string> importFiles = [];
    string? importChoice;

    /// <summary>EXPORT (<c>FUN_1405ea410</c>): the look goes to the export folder under the name typed in NameText, replacing a file of that name.</summary>
    void ExportLook(CharacterDraft draft, string installRoot)
    {
        string name = draft.Name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Notice($"Can't export: '{name}' is not a valid file name.", true); return; }
        string relative = ExportFolder + name + BodyExtension;
        try
        {
            Directory.CreateDirectory(Path.Combine(installRoot, ExportFolder));
            Meitou.Data.Fcs.FcsWriter.WriteFile(draft.Export(), Path.Combine(installRoot, relative));
            Notice(Loc.Tr("Character '{1}' exported to file {2}", name, "./" + relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice($"Can't export '{name}': {e.Message}", true); }
    }

    /// <summary>The body files in the export folder, by name.</summary>
    static IReadOnlyList<string> ExportedLooks(string installRoot)
    {
        try { return [.. Directory.EnumerateFiles(Path.Combine(installRoot, ExportFolder), "*" + BodyExtension).Order(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// IMPORT (<c>FUN_1405f4280</c>): the chosen file's look replaces the draft's, race and sex included. A file without an appearance record fails with the original's
    /// message. Meitou: a file of a race the editor doesn't offer, or (at a surgeon, where race and sex are fixed) of another race or sex, is refused.
    /// </summary>
    void ImportLook(string path)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        string file = Path.GetFileNameWithoutExtension(path);
        Meitou.Data.Fcs.FcsRecord appearance;
        try { appearance = AppearanceFile.Read(path).Record; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Meitou.Data.Fcs.FcsFormatException or InvalidDataException)
        { Notice(Loc.Tr("Error loading appearance file '{1}'. The file is in an old format or invalid.", file), true); return; }
        bool female = appearance.Bools.TryGetValue("sex female", out var f) && f;
        bool allowed = CharacterDraft.RaceOf(data.Db, appearance) is { } race && (s.Kind == EditKind.Creation
            ? IndexOf(EditorRaces(data.Db), race) >= 0
            : race.StringId == draft.Race.StringId && (female == draft.Female || race.GetBool("single gender")));
        if (!allowed || !draft.Import(appearance)) { Notice($"Can't import '{file}': this character can't take that race or sex.", true); return; }
        faithfulScreens.Close("file");
    }

    void FileDialogAction(MyGuiEvent e)
    {
        if (e.Widget == "ButtonCancel") faithfulScreens.Close("file");
        else if (e.Widget == "ListFiles" && e.Action == "select") importChoice = e.Text;
        else if (e.Widget == "ButtonOpenSave" && importChoice is { } path) ImportLook(path);
    }

    /// <summary>The import dialog (<c>Kenshi_OpenSaveFileDialog</c>, titled "Import character", <c>FUN_1405f4dd0</c>): the export folder's body files; no folder browsing.</summary>
    void BindFileDialog(UiBindings b)
    {
        b.Text["Root"] = "Import character";
        b.Text["ButtonOpenSave"] = "Import";
        foreach (string name in new[] { "ButtonCancel", "ButtonOpenSave" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        if (importChoice is null) b.Disabled.Add("ButtonOpenSave");
        foreach (string name in new[] { "ButtonUp", "EditFileName" }) b.Hidden.Add(name);
        b.Text["CurrentFolder"] = "./" + ExportFolder;
        b.Disabled.Add("CurrentFolder");
        var files = importFiles.ToArray();
        b.Lists["ListFiles"] = [.. files.Select(p => new UiListItem(p, Path.GetFileNameWithoutExtension(p)))];
        b.SelectedItems["ListFiles"] = Array.IndexOf(files, importChoice);
        b.Properties["ButtonOpenSave"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }

    /// <summary>Esc: a surgeon's or recruit's editor is left without changes; the new game's has to be confirmed.</summary>
    bool EditorEscape()
    {
        if (!EditorOpen) return false;
        if (editor!.Kind != EditKind.Creation) CloseEditor();
        return true;
    }

    void BindEditor(UiBindings b)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        foreach (string name in new[] { "BodyTabButton", "FaceTabButton", "HairTabButton", "RandomiseButton", "RandomiseAllButton", "ResetButton", "ClothesButton", "ConfirmButton" })
        { b.Disabled.Remove(name); b.Actions[name] = name; }
        // Race, subrace and gender arrows: only at creation, and only where there is a choice (FUN_1405ee0c0); hidden otherwise.
        var groups = EditorRaces(data.Db);
        int g = IndexOf(groups, draft.Race);
        void Arrows(bool live, params string[] names)
        {
            foreach (string name in names)
                if (live) { b.Disabled.Remove(name); b.Actions[name] = name; }
                else b.Hidden.Add(name);
        }
        Arrows(creation && groups.Count > 1, "RaceLeftButton", "RaceRightButton");
        Arrows(creation && g >= 0 && groups[g].Races.Count > 1, "SubRaceLeftButton", "SubRaceRightButton");
        Arrows(creation && !draft.Race.GetBool("single gender"), "GenderLeftButton", "GenderRightButton");
        if (s.Characters.Count > 1)
            foreach (string name in new[] { "CharLeftArrow", "CharRightArrow" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        else
        {
            foreach (string name in new[] { "CharLeftArrow", "CharRightArrow", "CharIndexText", "lbChangeCharacter" }) b.Hidden.Add(name);
            foreach (var frame in faithfulScreens.Layout("editor").Widgets.Where(w => w.Children.Any(c => c.Name == "CharIndexText"))) b.HiddenWidgets.Add(frame);
        }
        foreach (string name in new[] { "ImportButton", "ExportButton" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        string tabButton = s.Tab switch { EditorTab.Face => "FaceTabButton", EditorTab.Hair => "HairTabButton", _ => "BodyTabButton" };
        b.Properties[tabButton] = new Dictionary<string, string> { ["Checked"] = "true" };
        b.Properties["ClothesButton"] = new Dictionary<string, string> { ["Checked"] = s.Clothes ? "true" : "false", ["StateSelected"] = s.Clothes ? "true" : "false" };
        var everyRace = CharacterDraft.PlayableRaces(data.Db);
        int shown = IndexOf(everyRace, draft.Race);
        b.Text["RaceText"] = shown >= 0 ? everyRace[shown].Group.Name : draft.Race.Name;
        b.Text["SubRaceText"] = draft.Race.Name;
        b.Text["GenderText"] = draft.Race.GetBool("single gender") ? "" : draft.Female ? "Female" : "Male";
        b.Text["CharIndexText"] = $"{s.Index + 1}/{s.Characters.Count}";
        b.Text["DescriptionPanelText"] = draft.Race.GetString("description");
        b.Properties["DescriptionPanelText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.InkText };
        b.Properties["NameText"] = new Dictionary<string, string> { ["DefaultAction"] = "false" };
        b.Properties["ConfirmButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }

    void AddEditorContent(List<UiControl> controls)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft) return;
        if (controls.FirstOrDefault(c => c.Name == "TabsPanelContent") is { } panel) AddEditorRows(controls, draft, s.Tab, panel.Bounds);
        if (controls.FirstOrDefault(c => c.Name == "StatsPanelText") is { } stats) AddRaceStats(controls, draft.Race, stats.Bounds);
    }

    void AddEditorRows(List<UiControl> controls, CharacterDraft draft, EditorTab tab, UiRect area)
    {
        var rows = draft.Controls(tab);
        if (rows.Count == 0) return;
        float scale = Height / 1080f;
        float row = Math.Min(44 * scale, area.Height / rows.Count), pad = 8 * scale;
        float labelWidth = area.Width * 0.38f, trackX = area.X + labelWidth + pad, trackWidth = area.Width - labelWidth - 2 * pad;
        for (int i = 0; i < rows.Count; i++)
        {
            var c = rows[i];
            float y = area.Y + i * row;
            string index = i.ToString(CultureInfo.InvariantCulture);
            controls.Add(new($"editor-label-{i}", "TextBox", new(area.X + pad, y, labelWidth - pad, row), Loc.Tr(c.Label), "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = MainColour, ["TextAlign"] = "Left VCenter" }, Vector4.Zero, Pickable: false));
            if (c.IsPicker)
            {
                float arrow = Math.Min(row - 4 * scale, 30 * scale);
                controls.Add(new($"editor-left-{i}", "Button", new(trackX, y + (row - arrow) / 2, arrow, arrow), "", $"editor-pick:{index}:-1", "Kenshi_SpeedArrowLeftSkin", true,
                    new Dictionary<string, string>(), Vector4.Zero));
                controls.Add(new($"editor-right-{i}", "Button", new(trackX + trackWidth - arrow, y + (row - arrow) / 2, arrow, arrow), "", $"editor-pick:{index}:1", "Kenshi_SpeedArrowRightSkin", true,
                    new Dictionary<string, string>(), Vector4.Zero));
                controls.Add(new($"editor-choice-{i}", "TextBox", new(trackX + arrow + 4 * scale, y + 3 * scale, trackWidth - 2 * arrow - 8 * scale, row - 6 * scale),
                    draft.OptionName(c.Kind), "", "Kenshi_GenericTextBoxFlat", true,
                    new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = MainColour }, Vector4.Zero, Pickable: false));
                continue;
            }
            int value = draft.Value(c);
            float t = c.Max > c.Min ? Math.Clamp((value - c.Min) / (float)(c.Max - c.Min), 0, 1) : 0;
            float trackHeight = Math.Min(row * 0.6f, 24 * scale);
            var track = new UiRect(trackX, y + (row - trackHeight) / 2, trackWidth, trackHeight);
            controls.Add(new($"editor-track-{i}", "Widget", track, "", $"editor-slider:{index}", "Kenshi_SliderSkin", true,
                new Dictionary<string, string> { ["Slider"] = "true" }, Vector4.Zero, Pickable: true, Argument: index));
            float knobHeight = Math.Min(row * 0.95f, 36 * scale), knobWidth = knobHeight * 27 / 57f;
            controls.Add(new($"editor-knob-{i}", "Button", new(track.X + t * track.Width - knobWidth / 2, y + (row - knobHeight) / 2, knobWidth, knobHeight), "", "", "Kenshi_SliderButtonSkin", true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }
    }

    /// <summary>
    /// The RACE STATS panel (<see cref="RaceStatLines"/>), one line each, the value right-aligned. Built in code by the original too
    /// (its fonts and spacing are <b>Unknown</b>).
    /// </summary>
    void AddRaceStats(List<UiControl> controls, GameRecord race, UiRect area)
    {
        var lines = RaceStatLines(race);
        float scale = Height / 1080f, row = Math.Min(22 * scale, area.Height / Math.Max(1, lines.Count));
        for (int i = 0; i < lines.Count; i++)
        {
            var (label, value, good) = lines[i];
            var bounds = new UiRect(area.X + 10 * scale, area.Y + i * row, area.Width - 20 * scale, row);
            controls.Add(new($"editor-stat-{i}", "TextBox", bounds, label, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = MainColour, ["TextAlign"] = "Left VCenter" }, Vector4.Zero, Pickable: false));
            controls.Add(new($"editor-stat-value-{i}", "TextBox", bounds, value, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = good ? GoodColour : BadColour, ["TextAlign"] = "Right VCenter" }, Vector4.Zero, Pickable: false));
        }
    }

    readonly Dictionary<string, IReadOnlyList<(string, string, bool)>> raceStatCache = [];

    IReadOnlyList<(string Label, string Value, bool Good)> RaceStatLines(GameRecord race)
    {
        if (raceStatCache.TryGetValue(race.StringId, out var cached)) return cached;
        var lines = new List<(string, string, bool)>();
        if (EditorData is { } data)
        {
            // As the original's "Character_Editor_Race_Stats" table (FUN_1405ebd50 area, Observed): "No bleeding" below a bleed rate of 0.1, then each stat multiplier
            // that is not 1 (RACE stats good / bad, strength, dexterity: RaceData.StatMultipliers) as (m - 1) x 100 %. Its first line, the character's hit points, is left out.
            var r = Meitou.Data.Gameplay.Bodies.RaceData.From(race, data.Db);
            if (race.GetFloat("bleed rate", 1) < 0.1f) lines.Add((Loc.Tr("No bleeding"), "", true));
            for (int i = 1; i < r.StatMultipliers.Length; i++)
            {
                float m = r.StatMultipliers[i];
                if (Math.Abs(m - 1) < 0.001f || m <= 0) continue;
                lines.Add((StatLabel((Meitou.Data.Gameplay.Bodies.StatsEnumerated)i) + ":", ((m - 1) * 100).ToString("+0;-0", CultureInfo.InvariantCulture) + "%", m > 1));
            }
        }
        return raceStatCache[race.StringId] = lines;
    }
}
