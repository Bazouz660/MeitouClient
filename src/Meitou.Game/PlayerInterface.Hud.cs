using System.Numerics;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The minimal HUD: the clock, the speed buttons, the selection panel, rings, paths and the drag box.</summary>
sealed partial class PlayerInterface
{

    /// <summary>Pause, then the clock's speeds.</summary>
    static readonly double[] ButtonSpeeds = [0, .. SimulationClock.Speeds];
    const float Margin = 10, Pad = 6;

    public float HudBottom => 48;

    // ---- notices ----

    string? notice;
    bool noticeIsError;
    long noticeUntil;

    /// <summary>Shows a short message at the top of the screen for a few seconds ("Saved", "Loaded", or an error text in red).</summary>
    public void Notice(string text, bool error = false)
    {
        notice = text.ReplaceLineEndings(" ");
        LogMessage(notice);
        noticeIsError = error;
        noticeUntil = System.Diagnostics.Stopwatch.GetTimestamp() + (long)((error ? 6 : 2.5) * System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Drops the transient state of the mouse (a drag box, a steering right click): the world it belonged to is gone.</summary>
    public void Reset()
    {
        floaters.Clear();
        dragFrom = null;
        dragging = false;
        steering = false;
        mapDrag = null;
        inventoryDrag = null;
        selectedFor = null;
        selected = [];
        selectedBuilding = null;
        Follow = null;
        CloseContextMenu();
    }

    void DrawNotice(DebugOverlay o, int width)
    {
        if (notice is null) return;
        if (System.Diagnostics.Stopwatch.GetTimestamp() > noticeUntil) { notice = null; return; }
        string text = notice.Length > 160 ? notice[..157] + "..." : notice;
        float w = text.Length * o.CharWidth + 4 * Pad, x0 = MathF.Max(Margin, (width - w) / 2), y0 = Margin;
        o.Rect(x0, y0, x0 + w, y0 + o.LineHeight + 2 * Pad, DebugOverlay.PanelColour);
        o.Text(text, x0 + 2 * Pad, y0 + Pad, noticeIsError ? new Vector4(1f, 0.4f, 0.35f, 1f) : new Vector4(0.35f, 1f, 0.45f, 1f));
    }

    bool OverHud(Vector2 p) => ContainsUi(p);

    /// <summary>A HUD speed button: pause, then the clock's speeds. In multiplayer a player the host's rules leave off the clock is told so here (the host would refuse it too).</summary>
    void PressButton(int index)
    {
        if (Net is { Role: not NetRole.None } net && !net.CanControlTime(net.LocalPlayer)) { Notice("Only the host can change the game speed.", true); return; }
        session.RequestClock(index == 0 ? ClockRequest.TogglePause : ClockRequest.Set(ButtonSpeeds[index]));
    }

    /// <summary>The selected characters of the current snapshot, found once per snapshot (the HUD box is asked for on every hit test).</summary>
    IReadOnlyList<CharacterSnapshot> Selected()
    {
        var snapshot = session.CurrentSnapshot;
        if (!ReferenceEquals(snapshot, selectedFor))
        {
            selected = [.. snapshot.Characters.Where(c => c.Selected)];
            inspected = snapshot.Characters.FirstOrDefault(c => c.Inspected);
            selectedFor = snapshot;
        }
        return selected;
    }

    WorldSnapshot? selectedFor;
    IReadOnlyList<CharacterSnapshot> selected = [];
    CharacterSnapshot? inspected;

    /// <summary>The character the HUD panels describe: the other faction's character last clicked, else the first selected one (docs/game/ui-screens.md section 4). The selection stays whoever is inspected.</summary>
    CharacterSnapshot? Lead() => Selected().FirstOrDefault() is { } first ? inspected ?? first : inspected;

    /// <summary>Draw the green ring under each selected character (Advanced → Selection rings).</summary>
    public bool SelectionRings { get; set; } = true;

    /// <summary>The HUD, the selection rings and the drag box, over the finished picture.</summary>
    public void Draw(DebugOverlay o, int width, int height)
    {
        PollUiSounds();
        PollNet();
        Width = width;
        Height = height;
        PollBiome();
        PollProspect();
        var white = DebugOverlay.TextColour;
        var green = new Vector4(0.35f, 1f, 0.45f, 1f);
        var red = new Vector4(1f, 0.4f, 0.35f, 1f);

        // Rings under the selected characters.
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.Selected || EditorOpen || !SelectionRings) continue;
            var centre = where(c);
            // A swimmer is drawn that far down (Speed.WadingHeight) so its body lies in the water: its ring stays on the surface.
            if (centre.Y <= Meitou.Data.World.WorldWater.Height - Meitou.Simulation.Bodies.Speed.DeepWaterDepth + 0.01f) centre.Y = Meitou.Data.World.WorldWater.Height;
            Vector2? previous = null, first = null;
            for (int i = 0; i <= 24; i++)
            {
                float a = i * MathF.Tau / 24;
                var point = centre + new Vector3(MathF.Sin(a) * 1.6f, 0.15f, MathF.Cos(a) * 1.6f);
                var s = Project(point);
                if (s is null) { previous = null; continue; }
                if (previous is { } prev) o.Line(prev.X, prev.Y, s.Value.X, s.Value.Y, 2, green);
                first ??= s;
                previous = s;
            }
        }

        DrawStealth(o);
        DrawFloaters(o);
        FadeMarker();

        // The box.
        if (dragging && dragFrom is { } from)
        {
            var edge = new Vector4(0.6f, 1f, 0.6f, 1);
            o.Rect(MathF.Min(from.X, mouse.X), MathF.Min(from.Y, mouse.Y), MathF.Max(from.X, mouse.X), MathF.Max(from.Y, mouse.Y), new Vector4(0.4f, 1f, 0.5f, 0.12f));
            o.Line(from.X, from.Y, mouse.X, from.Y, 1, edge);
            o.Line(mouse.X, from.Y, mouse.X, mouse.Y, 1, edge);
            o.Line(mouse.X, mouse.Y, from.X, mouse.Y, 1, edge);
            o.Line(from.X, mouse.Y, from.X, from.Y, 1, edge);
        }

        DrawGameUi(o, width, height);
        DrawNotice(o, width);
        o.Flush(width, height);
    }
}
