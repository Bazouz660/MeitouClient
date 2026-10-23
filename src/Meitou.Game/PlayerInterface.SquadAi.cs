using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The overview's AI tab (docs/game/ai-tasks.md "Perma-jobs"): a tick button per squad AI option Meitou runs in <c>AIOptions</c>, the hovered one's tooltip (a static tooltip, <see cref="PlayerInterface.AddHoverLabel"/>) in <c>AIToolTip</c>
/// (captions and tooltips are the original's, <c>FUN_140005e11</c>). The original's other options (heal, help and rescue allies, shoot first) are not
/// listed until they do something.
/// </summary>
sealed partial class PlayerInterface
{
    const int AiTab = 6;

    static readonly (SquadAi Option, string Caption, string Tip)[] AiOptionRows =
    [
        (SquadAi.Ditch, "Ditch items", "If they have nothing else to do, characters will automatically put any excess materials they are carrying into storage.  This can help to un-block the base AI when things grind to a halt from over-filled inventories."),
        (SquadAi.Sleep, "Sleep when injured", "Characters will automatically go to bed if they are injured and have nothing else to do"),
        (SquadAi.Sit, "Sit when idle", "Idle characters at home will sit down on chairs and benches, if you have any"),
        (SquadAi.Feed, "Feed Animals", "If food is running low, you can elect not to feed your animals."),
        (SquadAi.Share, "Share Food", "Characters in a squad will automatically share any of their food that is in a backpack (if in range)."),
    ];

    void AddAiContent(List<UiControl> controls)
    {
        if (faithfulScreens.GetTab("overview", "TabsMain") != AiTab || controls.FirstOrDefault(c => c.Name == "AIOptions") is not { } panel) return;
        var on = session.CurrentSnapshot.SquadAi;
        float line = MathF.Max(22, 30 * InventoryScale);
        for (int i = 0; i < AiOptionRows.Length; i++)
        {
            bool ticked = (on & AiOptionRows[i].Option) != 0;
            controls.Add(new($"ai-option-{i}", "Button", new(panel.Bounds.X + 6, panel.Bounds.Y + i * line, panel.Bounds.Width - 12, line - 4), Loc.Tr(AiOptionRows[i].Caption),
                "ai-option:" + i.ToString(CultureInfo.InvariantCulture), "Kenshi_TickButton1", true,
                new Dictionary<string, string>(UiTooltips.TextProperties(AiOptionRows[i].Tip, "static:AIToolTip")) { ["Checked"] = ticked ? "true" : "false", ["StateSelected"] = ticked ? "true" : "false" },
                Vector4.Zero, Pickable: true, Clip: panel.Bounds));
        }
    }

    void AiAction(MyGuiEvent e)
    {
        if (!e.Action.StartsWith("ai-option:", StringComparison.Ordinal) || !int.TryParse(e.Action.AsSpan(10), CultureInfo.InvariantCulture, out int i) || (uint)i >= (uint)AiOptionRows.Length) return;
        var option = AiOptionRows[i].Option;
        Say(new SquadAiCommand(option, (session.CurrentSnapshot.SquadAi & option) == 0) { Tick = session.World.Tick });
    }
}
