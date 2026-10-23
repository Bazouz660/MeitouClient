using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The Overview's faction tab (<c>FactionsScreen</c>, docs/game/ui-screens.md "Faction tab"): the player faction's name, a <c>Kenshi_FactionRelationsLine</c> per faction with
/// the relation's number and its bar, and the information panel's two headings.
/// </summary>
sealed partial class PlayerInterface
{
    const int FactionTab = 1;

    /// <summary>The factions the faction tab lists with the player's relation towards each (-100..100); null before a world with game data. The host sets it.</summary>
    public Func<IReadOnlyList<(string Name, float Relation)>>? FactionRelations { get; set; }

    MyGuiLayout? relationsLineLayout;
    float relationsScroll;

    bool FactionShown => faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == FactionTab;

    const string FactionNameBox = "FactionNameText";

    /// <summary>What was typed in the name box and is not yet accepted (null: nothing).</summary>
    string? factionDraft;

    /// <summary>
    /// The player faction's name in the tab's name box (the original's text is the player interface's faction name). The box takes typing; accepting it or losing the key focus renames
    /// (<see cref="UpdateFactionName"/>, <c>FUN_1404923d0</c>), and a box not being typed in shows the faction's current name.
    /// </summary>
    void BindFactionTab(UiBindings b)
    {
        string name = FactionName?.Invoke(session.World.Player.Faction) ?? "Nameless";
        if (faithfulScreens.Focus is not { Screen: "overview", Widget: FactionNameBox }) faithfulScreens.SetText("overview", FactionNameBox, name);
        b.Text[FactionNameBox] = name;
    }

    /// <summary>A text change in the name box is kept as the draft; the box losing the focus (or the tab closing) sends the rename of the last one.</summary>
    void FactionAction(MyGuiEvent e)
    {
        if (e.Widget == FactionNameBox && e.Action == "text") factionDraft = e.Text;
    }

    /// <summary>Once a frame: a draft whose box no longer has the key focus is sent as <see cref="RenameFactionCommand"/>.</summary>
    void UpdateFactionName()
    {
        if (factionDraft is not { } draft || faithfulScreens.Focus is { Screen: "overview", Widget: FactionNameBox }) return;
        factionDraft = null;
        Say(new Meitou.Simulation.RenameFactionCommand(draft) { Tick = session.World.Tick });
    }

    /// <summary>
    /// The relations list (<c>FUN_140499960</c> / <c>FUN_140499630</c>): the lines sorted by faction name, each the panel's width less the scroll bar and stacked with 3 px (at
    /// 1080 lines) between them; the number in BadBright below -30, Good above 60, else Main; a negative relation fills the left bar leftwards from the middle, a positive one
    /// the right bar rightwards, |relation| % of their width.
    /// </summary>
    void AddFactionContent(List<UiControl> controls)
    {
        if (!FactionShown) return;
        var scale = Height / 1080f;
        if (controls.FirstOrDefault(c => c.Name == "FactionInfoPanel") is not null)
            AddLines(controls, "FactionInfoPanel", [new(Loc.Tr("---Active Contracts---"), "", GreyColour), new("", ""), new(Loc.Tr("---Upcoming Events---"), "", GreyColour)],
                20 * scale, StandardPixels);
        if (relationsLineLayout is null || controls.FirstOrDefault(c => c.Name == "RelationsPanel") is not { } panel) return;
        var factions = (FactionRelations?.Invoke() ?? []).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var area = panel.Bounds;
        var clip = panel.Clip is { } outer ? area.Intersect(outer) : area;
        var root = relationsLineLayout.Roots.FirstOrDefault();
        float lineHeight = root is null ? 24 * scale : root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition).Height;
        float lineWidth = area.Width - SquadScrollBar * scale, step = lineHeight + 3 * scale;
        relationsScroll = Math.Clamp(relationsScroll, 0, Math.Max(0, factions.Count * step - area.Height));
        for (int i = 0; i < factions.Count; i++)
        {
            var (name, relation) = factions[i];
            var line = new UiRect(area.X, area.Y + i * step - relationsScroll, lineWidth, lineHeight);
            if (line.Intersect(clip).Height <= 0) continue;
            var b = new UiBindings { TemplateScale = scale };
            if (root is not null) b.Positions[root.Name] = new(0, 0, lineWidth, lineHeight);
            b.Text["FactionName"] = name;
            b.Text["FactionValue"] = ((int)relation).ToString(CultureInfo.InvariantCulture);
            b.Properties["FactionValue"] = new Dictionary<string, string> { ["TextColour"] = relation < -30 ? BadBrightColour : relation > 60 ? GoodColour : MainColour };
            foreach (var (bar, negative) in new[] { ("LeftBar", true), ("RightBar", false) })
            {
                if (relationsLineLayout.Find(bar) is not { } widget) continue;
                var full = widget.Position.RelativeTo(new(0, 0, lineWidth, lineHeight), widget.RealPosition);
                float share = negative ? Math.Max(0, -relation) / 100 : Math.Max(0, relation) / 100;
                float width = full.Width * Math.Min(share, 1);
                b.Positions[bar] = negative ? full with { X = full.X + full.Width - width, Width = width } : full with { Width = width };
            }
            foreach (var c in MyGuiPresentation.Arrange(relationsLineLayout, (int)MathF.Round(lineWidth), (int)MathF.Round(lineHeight), b, uiResources.Layers))
            {
                var own = c.Clip is { } inner ? (inner with { X = inner.X + line.X, Y = inner.Y + line.Y }).Intersect(clip) : clip;
                controls.Add(c with { Name = c.Name.Length > 0 ? $"faction-{i}-{c.Name}" : c.Name, Bounds = c.Bounds with { X = c.Bounds.X + line.X, Y = c.Bounds.Y + line.Y }, Clip = own,
                    Properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["FactionLine"] = "1" } });
            }
        }
    }

    /// <summary>The wheel over the relations list scrolls it.</summary>
    bool FactionWheel(Vector2 p, float steps)
    {
        if (!FactionShown || faithfulScreens.HitTest(p) is not { Screen: "overview" } hit || !(hit.Control.Name == "RelationsPanel" || hit.Control.Properties.ContainsKey("FactionLine"))) return false;
        relationsScroll -= steps * 44 * (Height / 1080f);   // clamped when the list is laid out
        return true;
    }
}
