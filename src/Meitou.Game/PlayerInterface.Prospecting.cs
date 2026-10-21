using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Production;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The Prospect button and the Prospecting Results window (docs/game/ui-screens.md "Prospecting"): the button gives the selected character nearest the camera the prospecting task, the window
/// shows what <see cref="World.Prospected"/> found. The map overlay of the original's window is not modelled.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The host's wind speed at the camera in the data's units (the window shows it times 0.22369362 as mph); null: calm.</summary>
    public Func<float>? WindSpeed { get; set; }

    object? prospectWorld;
    long prospectSerial;
    ProspectReport? prospectReport;
    int prospectRow;

    /// <summary>
    /// The HUD's Prospect action (<c>FUN_140721800</c>): with the window open it closes it; else the selected character nearest the camera pivot starts prospecting, unless it already does,
    /// and the click sounds. The sound is not part of an order's Right_Click: the exe posts it here.
    /// </summary>
    void ProspectAction()
    {
        if (faithfulScreens.IsOpen("prospect")) { faithfulScreens.Close("prospect"); return; }
        var pivot = session.Camera.Current.Target;
        CharacterSnapshot? nearest = null;
        float best = float.MaxValue;
        foreach (var c in Selected())
        {
            float d = Vector3.DistanceSquared(c.Position, pivot);
            if (d < best) (nearest, best) = (c, d);
        }
        if (nearest is null || nearest.Task == CharacterTask.Prospect) return;
        Say(new ProspectOrder(nearest.Id) { Tick = session.World.Tick });
        Sound("Prospect_Click");
    }

    /// <summary>Once a frame: a new finished prospecting opens the results window (the exe shows it the moment the task hands it the spot) and sounds <c>Prospect_Results</c>. A new world only takes the baseline.</summary>
    void PollProspect()
    {
        var world = session.World;
        if (!ReferenceEquals(prospectWorld, world))
        {
            prospectWorld = world;
            prospectSerial = world.Prospected?.Serial ?? 0;
            prospectReport = null;
            faithfulScreens.Close("prospect");
        }
        if (world.Prospected is not { } report || report.Serial == prospectSerial) return;
        prospectSerial = report.Serial;
        prospectReport = report;
        prospectRow = 0;
        faithfulScreens.Open("prospect");
        Sound("Prospect_Results");
    }

    void BindProspectScreen(UiBindings b)
    {
        b.Text["Root"] = "PROSPECTING RESULTS";
        b.Hidden.Add("CameraMarker");
    }

    /// <summary>
    /// The two data panels and the resource rows (<c>FUN_14049d850</c>): panel 1 the zone, altitude, the prospector's skill and the area size; panel 2 the wind and the environment; each resource a
    /// radio row with its value in the original's colours (above 0.75 good, below 0.5 bad).
    /// </summary>
    void AddProspectContent(List<UiControl> controls)
    {
        if (prospectReport is not { } r) return;
        float row = 20 * Height / 1080f, pixels = StandardTextPixels * Height / 1080f;
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        AddLines(controls, "OverviewDatapanel1",
        [
            new("Zone:", r.Zone, TitleColour, TitleColour),
            new("Altitude:", Loc.Tr("{1,num} m", r.Altitude)),
            new("Prospector Skill:", N(r.Skill)),
            new("Area Size:", Loc.Tr("{1,num} m", r.AreaMetres)),
        ], row, pixels);
        // The wind of the camera's region (140785f50) in mph; the environment lists its coverages, "Dead" when there are none, the first on the label's line.
        double wind = Math.Round((WindSpeed?.Invoke() ?? 0) * 0.22369362f, 1);
        var panel = new List<HudLine> { new("Wind Speed:", Loc.Tr("{1,num} mph", wind)) };
        var environment = r.Environment.Count == 0 ? [Loc.Tr("Dead")] : r.Environment.Select(l => $"{Loc.Tr(l.Name)} {N(l.Percent)}%").ToArray();
        for (int i = 0; i < environment.Length; i++) panel.Add(new(i == 0 ? "Environment:" : "", environment[i], null, MainColour));
        AddLines(controls, "OverviewDatapanel2", panel, row, pixels);

        if (controls.FirstOrDefault(c => c.Name == "ResourcesList") is not { } list) return;
        float height = Height * 0.0277778f;
        for (int i = 0; i < r.Resources.Count; i++)
        {
            var line = r.Resources[i];
            var area = new UiRect(list.Bounds.X, list.Bounds.Y + i * height, list.Bounds.Width, height);
            bool checkedRow = i == prospectRow;
            controls.Add(new($"prospect-row-{i}", "Button", new(area.X, area.Y, area.Width * 0.662835f, area.Height), line.Name, "prospect-row:" + N(i), "Kenshi_TickButton2", true,
                new Dictionary<string, string> { ["Checked"] = checkedRow ? "true" : "false", ["StateSelected"] = checkedRow ? "true" : "false" }, Vector4.Zero, Pickable: true, Clip: list.Bounds));
            string colour = line.Value > 0.75f ? GoodColour : line.Value < 0.5f ? BadColour : MyGuiColours.InkText;
            controls.Add(new($"prospect-value-{i}", "TextBox", new(area.X + area.Width * 0.724138f, area.Y, area.Width * 0.275862f, area.Height), N(line.Percent), "", "Kenshi_GenericTextBoxFlatSmall", true,
                new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = colour }, Vector4.Zero, Pickable: false, Clip: list.Bounds));
        }
    }

    void ProspectScreenAction(MyGuiEvent e)
    {
        if (e.Action.StartsWith("prospect-row:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(13), CultureInfo.InvariantCulture, out int i)) prospectRow = i;
    }
}
