using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// Loose items under the pointer (docs/simulation.md "Ground items"): a right click on one sends the nearest selected character to pick it up (pointer <c>pick_item</c>).
/// While the Highlight key is held each one near the focus shows its name label (docs/game/ui-input.md "Ground item labels"), which picks like the item.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer an item's projected middle must be, in pixels (an engine choice: the items have no pick mesh).</summary>
    const float GroundItemPickPixels = 24;

    /// <summary>The camera's focus while the Highlight key is held (the host sets it; null otherwise): the item labels show round it.</summary>
    public Vector3? HighlightFocus;

    /// <summary>The labels drawn last frame, front to back, for <see cref="PickGroundItem"/>.</summary>
    readonly List<(int Id, UiRect Rect)> itemLabels = [];

    /// <summary>
    /// The ground item whose label is under the pixel, else whose middle (half a unit above where it lies) projects nearest the pixel within
    /// <see cref="GroundItemPickPixels"/>, if any; one inside or behind a building seen from outside does not count.
    /// </summary>
    int? PickGroundItem(Vector2 pixel) => PickGroundItemAt(pixel)?.Id;

    /// <summary><see cref="PickGroundItem"/> with the ray distance to the item's middle (0 for a label: it is drawn over everything).</summary>
    (int Id, float T)? PickGroundItemAt(Vector2 pixel)
    {
        if (HighlightFocus is not null)
            foreach (var (id, rect) in itemLabels)
                if (rect.Contains(pixel.X, pixel.Y)) return (id, 0);
        if (Ray(pixel) is not { } ray) return null;
        (int, float)? best = null;
        float bestDistance = GroundItemPickPixels;
        foreach (var g in session.CurrentSnapshot.GroundItems)
        {
            var middle = g.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), t = Vector3.Distance(ray.Origin, middle);
            if (d < bestDistance && !Hidden(ray, t)) (best, bestDistance) = ((g.Id, t), d);
        }
        return best;
    }

    /// <summary>A right click on a ground item: the pick-up order (true), with a selection.</summary>
    bool GroundItemClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickGroundItem(p) is not { } item) return false;
        Say(new Meitou.Simulation.Items.PickUpItemOrder([], item) { Tick = session.World.Tick });
        return true;
    }

    /// <summary>
    /// The name label over each ground item within <see cref="HighlightRadius"/> of the focus while the Highlight key is held: the original's <c>Kenshi_ItemDropLabel</c>
    /// (<b>Verified</b>), a <c>Kenshi_GenericTextBoxFlatSmallSkin</c> 0.0259 of the screen high and fitted to the name, centred in Kenshi_StandardFont_Medium; white
    /// (<c>DropItemLabelBackDefault</c> / <c>TextDefault</c>), green (<c>...Selected</c>, #66ff66) for the one the pointer picks. Labels that would overlap the nearer
    /// one's are lifted above it, and hidden ones are skipped (engine choices).
    /// </summary>
    void AddItemLabels(List<UiControl> controls)
    {
        itemLabels.Clear();
        if (HighlightFocus is not { } focus || uiPainter is not { } painter || EditorOpen) return;
        float scale = Height / 1080f, height = Height * 0.0259259f, em = 20 * 50f / 72f * scale;
        var near = new List<(Meitou.Simulation.Items.GroundItemSnapshot Item, float Distance, Vector2 At)>();
        foreach (var g in session.CurrentSnapshot.GroundItems)
        {
            if (g.Name.Length == 0 || Vector3.DistanceSquared(focus, g.Position) > HighlightRadius * HighlightRadius) continue;
            var middle = g.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at || Ray(at) is not { } ray) continue;
            float distance = Vector3.Distance(ray.Origin, middle);
            if (!Hidden(ray, distance)) near.Add((g, distance, at));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var placed = new List<(int Id, UiRect Rect)>();
        foreach (var (g, _, at) in near)
        {
            float width = painter.MeasureStandard(g.Name, em) + em;
            var rect = new UiRect(at.X - width / 2, at.Y - 6 * scale - height, width, height);
            for (bool moved = true; moved;)
            {
                moved = false;
                foreach (var (_, other) in placed)
                    if (rect.Intersect(other) is { Width: > 0, Height: > 0 }) { rect = rect with { Y = other.Y - height - 1 }; moved = true; }
            }
            placed.Add((g.Id, rect));
        }
        itemLabels.AddRange(placed);
        int? hovered = PickGroundItem(mouse);
        // Drawn far to near so the nearer labels lie on top.
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            var (id, rect) = placed[i];
            string colour = id == hovered ? "0.4 1 0.4" : "1 1 1";
            controls.Add(new($"itemlabel-{i}", "Widget", rect, "", "", "Kenshi_GenericTextBoxFlatSmallSkin", true,
                new Dictionary<string, string> { ["Colour"] = colour }, Vector4.Zero, Pickable: false));
            controls.Add(new($"itemlabel-text-{i}", "TextBox", rect, near[i].Item.Name, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center", ["TextColour"] = colour }, Vector4.Zero, Pickable: false, TextScale: 1));
        }
    }
}
