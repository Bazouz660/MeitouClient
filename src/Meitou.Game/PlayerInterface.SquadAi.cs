using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The overview's AI tab (docs/game/ai-tasks.md "Squad AI options"): a tick button per squad AI option in <c>AIOptions</c>, the hovered one's tooltip (a static tooltip, <see cref="PlayerInterface.AddHoverLabel"/>) in <c>AIToolTip</c>
/// (captions and tooltips are the original's, <c>FUN_140005e11</c>; the row order follows their order in the exe, <b>Observed</b>).
/// </summary>
sealed partial class PlayerInterface
{
    const int AiTab = 6;

    static readonly (SquadAi Option, string Caption, string Tip)[] AiOptionRows =
    [
        (SquadAi.Ditch, "Ditch items", "If they have nothing else to do, characters will automatically put any excess materials they are carrying into storage.  This can help to un-block the base AI when things grind to a halt from over-filled inventories."),
        (SquadAi.Sleep, "Sleep when injured", "Characters will automatically go to bed if they are injured and have nothing else to do"),
        (SquadAi.Sit, "Sit when idle", "Idle characters at home will sit down on chairs and benches, if you have any"),
        (SquadAi.Heal, "Heal Allies", "Characters with the medic job will also heal characters of allied factions"),
        (SquadAi.Help, "Help Allies", "Characters will help out characters from other allied factions if they are under attack"),
        (SquadAi.Rescue, "Rescue Allies", "Characters with the rescue job will also rescue allied characters from other factions"),
        (SquadAi.Share, "Share Food", "Characters in a squad will automatically share any of their food that is in a backpack (if in range)."),
        (SquadAi.Feed, "Feed Animals", "If food is running low, you can elect not to feed your animals."),
        (SquadAi.ShootFirst, "Shoot first, ask later", "Your turret gunners will shoot at any potentially dangerous but neutral targets, such as wild animals or bandits, without waiting for them to attack the base first."),
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
                "ai-option:" + i.ToString(CultureInfo.InvariantCulture), "Kenshi_TickButton1Skin", true,
                new Dictionary<string, string>(UiTooltips.TextProperties(AiOptionRows[i].Tip, "static:AIToolTip")) { ["Checked"] = ticked ? "true" : "false", ["StateSelected"] = ticked ? "true" : "false", ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" },
                Vector4.Zero, Pickable: true, Clip: panel.Bounds));
        }
    }

    /// <summary>
    /// Gives an order on a character, or with Shift held the job it becomes (the original's order dispatcher <c>FUN_1407fa650</c>; docs/game/ai-tasks.md "Perma-jobs"): First Aid,
    /// Splint Injuries, an attack and a loot become Medic (Robotics on a robot), Splint Rigging, Attacking enemies and Foraging animals.
    /// </summary>
    void OrderOrJob(Meitou.Data.Gameplay.Ai.TaskType ordered, CharacterId target, Action order)
    {
        if (SlowModifier && Meitou.Simulation.Production.PermaJobs.JobOf(ordered) is not null)
            Say(new Meitou.Simulation.Production.AddOrderJob(SelectedIds(), ordered, target) { Tick = session.World.Tick });
        else order();
    }

    /// <summary>The order a character verb gives, by its caption, for <see cref="OrderOrJob"/>.</summary>
    static readonly Dictionary<string, Meitou.Data.Gameplay.Ai.TaskType> JobVerbs = new()
    {
        ["Attack Target"] = Meitou.Data.Gameplay.Ai.TaskType.MELEE_ATTACK,
        ["First Aid"] = Meitou.Data.Gameplay.Ai.TaskType.FIRST_AID_ORDER,
        ["Splint Injuries"] = Meitou.Data.Gameplay.Ai.TaskType.SPLINT_ORDER,
        ["Loot"] = Meitou.Data.Gameplay.Ai.TaskType.LOOT_TARGET,
        ["Follow"] = Meitou.Data.Gameplay.Ai.TaskType.STAY_CLOSE_TO_TARGET,
        ["Bodyguard"] = Meitou.Data.Gameplay.Ai.TaskType.BODYGUARD,
    };

    /// <summary>A context-menu verb on <paramref name="target"/> that adds its job with Shift held (<see cref="OrderOrJob"/>); other verbs as they are.</summary>
    Action JobOrOrder(string caption, CharacterId target, Action give) =>
        JobVerbs.TryGetValue(caption, out var ordered) ? () => OrderOrJob(ordered, target, give) : give;

    void AiAction(MyGuiEvent e)
    {
        if (!e.Action.StartsWith("ai-option:", StringComparison.Ordinal) || !int.TryParse(e.Action.AsSpan(10), CultureInfo.InvariantCulture, out int i) || (uint)i >= (uint)AiOptionRows.Length) return;
        var option = AiOptionRows[i].Option;
        Say(new SquadAiCommand(option, (session.CurrentSnapshot.SquadAi & option) == 0) { Tick = session.World.Tick });
    }
}
