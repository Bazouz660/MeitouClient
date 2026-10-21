using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>The ARMOUR record of an item, null for anything else (the host sets it).</summary>
    public Func<string, ArmourData?>? ArmourOf { get; set; }
    /// <summary>The name of a record by string id (a body part of an armour's coverage); the host sets it.</summary>
    public Func<string, string?>? RecordName { get; set; }

    /// <summary>The tone of a grade's name (<c>ItemGrade0</c> to <c>ItemGrade4</c> of kenshi_colours.xml; Specialist and Masterwork share the last).</summary>
    static TooltipTone GradeTone(int grade) => grade switch { 0 => TooltipTone.Grade0, 1 => TooltipTone.Grade1, 2 => TooltipTone.Grade2, 3 => TooltipTone.Grade3, _ => TooltipTone.Grade4 };

    /// <summary>An armour line (<see cref="ArmourTooltip"/>, docs/game/ui-screens.md "Item tooltip") as a tooltip row: a statistic's label in the title colour, a grade in its grade's colour.</summary>
    static TooltipLine ArmourRow(ArmourTooltip.Line line) => line.Kind switch
    {
        ArmourTooltip.LineKind.Grade => new(line.Label, "", GradeTone(line.Grade)),
        ArmourTooltip.LineKind.Stat => new(line.Label, line.Value, TooltipTone.Title, TooltipTone.Ink),
        _ => new(line.Label, line.Value, TooltipTone.Main, TooltipTone.Main),
    };
}
