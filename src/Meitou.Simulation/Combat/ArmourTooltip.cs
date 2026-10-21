using System.Globalization;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The armour lines of an item tooltip (docs/game/ui-screens.md "Item tooltip"): the grade, the class and material, the resistances, the coverage and the skill effects. The numbers are the
/// ones the hit code uses (<see cref="ArmourPiece.Create"/>), so what the tooltip says is what the armour does.
/// </summary>
public static class ArmourTooltip
{
    /// <summary>The armour grade names by grade 0 to 5 (the six crafting tiers of <c>CraftBench.ArmourQualities</c>, 5 to 95 per cent).</summary>
    public static readonly string[] GradeNames = ["Prototype", "Shoddy", "Standard", "High", "Specialist", "Masterwork"];

    static readonly int[] GradeQualities = [5, 20, 40, 60, 80, 95];

    /// <summary>The coverage list's order as the original prints it (<b>Observed</b>: Chest, Stomach, Right Arm, Left Arm); parts not named here follow in record order.</summary>
    static readonly string[] PartOrder = ["Head", "Chest", "Stomach", "Right Arm", "Left Arm", "Right Leg", "Left Leg"];

    /// <summary>The grade of a quality (0 to 100): the highest grade whose quality it reaches; below the first grade's quality it is grade 0. The cut-offs between grades are <b>Unknown</b> (items made or found have exactly the six qualities).</summary>
    public static int GradeOf(float quality)
    {
        int grade = 0;
        for (int i = 1; i < GradeQualities.Length; i++) if (quality >= GradeQualities[i]) grade = i;
        return grade;
    }

    /// <summary>How a line is shown: a heading as is, a statistic with its label in the accent colour, a grade in the colour of its grade.</summary>
    public enum LineKind { Plain, Stat, Grade }

    /// <summary>One tooltip line: a label and a value (empty for a heading); <paramref name="Grade"/> is the grade of a <see cref="LineKind.Grade"/> line.</summary>
    public readonly record struct Line(string Label, string Value, LineKind Kind = LineKind.Plain, int Grade = 0);

    static string Number(float v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);
    static string Percent(float fraction) => Number(fraction * 100) + "%";

    /// <summary>The lines above the weight: grade, class and material, resistances.</summary>
    public static IReadOnlyList<Line> Head(ArmourData a, float quality)
    {
        var piece = ArmourPiece.Create(a, Math.Clamp(quality, 0, 100) / 100f);
        int grade = GradeOf(quality);
        string material = a.Material == ArmourType.MetalPlate ? "Metal Plate" : a.Material.ToString();
        return
        [
            new(Loc.Tr("{1} Grade", Loc.Tr(GradeNames[grade])), "", LineKind.Grade, grade),
            new($"[{a.Class} Armour Class]", $"[{material}]"),
            new("-Blunt resistance", Percent(piece.BluntDefence), LineKind.Stat),
            new("-Cut resistance", Percent(piece.CutDefence), LineKind.Stat),
            new("-Cut resistance efficiency", Percent(1 - piece.CutIntoStun), LineKind.Stat),
            new("-Harpoon resistance", Number(piece.PierceDefence, "0.#") + "pts", LineKind.Stat),
        ];
    }

    /// <summary>
    /// The lines below the price: the coverage by part and the skill effects that differ from none (<b>Observed</b> on the Samurai Armour at six grades). The effects that grow with the
    /// grade (stealth, combat speed) and the dexterity, athletics, defence, perception and ranged bonus lines are not shown: their formulas and labels are <b>Unknown</b>.
    /// </summary>
    public static IReadOnlyList<Line> Tail(ArmourData a, Func<string, string?> partName)
    {
        var lines = new List<Line>();
        var parts = a.Coverage.Select(c => (Name: partName(c.PartStringId) ?? c.PartStringId, c.Percent)).ToList();
        if (parts.Count > 0)
        {
            lines.Add(new("[Coverage]", ""));
            foreach (var (name, percent) in parts.OrderBy(p => Array.IndexOf(PartOrder, p.Name) is var i and >= 0 ? i : PartOrder.Length))
                lines.Add(new("-" + name, percent + "%", LineKind.Stat));
        }
        var skills = new List<Line>();
        void Multiplier(string label, float mult) { if (mult != 1) skills.Add(new("-" + label, Number(mult, "0.00") + "x")); }
        void Bonus(string label, int bonus) { if (bonus != 0) skills.Add(new("-" + label, bonus.ToString(CultureInfo.InvariantCulture))); }
        Multiplier("Crossbows effect", a.RangedSkillMult);
        Multiplier("Precision shooting effect", a.RangedSkillMult);
        Multiplier("Dodge skill effect", a.DodgeMult);
        Multiplier("Attack damage effect", a.DamageOutputMult);
        Multiplier("Assassination skill", a.AssassinationMult);
        Bonus("Melee attack bonus", a.CombatAttackBonus);
        Bonus("Martial arts bonus", a.UnarmedBonus);
        if (skills.Count > 0) { lines.Add(new("[Skills]", "")); lines.AddRange(skills); }
        return lines;
    }
}
