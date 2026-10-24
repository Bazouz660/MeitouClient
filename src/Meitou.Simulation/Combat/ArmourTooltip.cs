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

    /// <summary>How a line is shown: a heading as is, a statistic with its label in the accent colour, a grade in the colour of its grade, a skill effect with its label in the accent colour and its value in the bad colour below neutral and the good colour above.</summary>
    public enum LineKind { Plain, Stat, Grade, Effect }

    /// <summary>One tooltip line: a label and a value (empty for a heading); <paramref name="Grade"/> is the grade of a <see cref="LineKind.Grade"/> line, and -1 or 1 (a penalty or a bonus) for an <see cref="LineKind.Effect"/> line.</summary>
    public readonly record struct Line(string Label, string Value, LineKind Kind = LineKind.Plain, int Grade = 0);

    static string Number(float v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);
    static string Percent(float fraction) => Number(fraction * 100) + "%";

    /// <summary>
    /// The lines above the weight: grade, class and material, resistances. <paramref name="quality"/> is the item's <c>level</c> (0 to 100), the grade's quality (95 for Masterwork): a saved
    /// item has <c>quality</c> 100 whatever its grade, and the original's numbers follow the level (<b>Observed</b>: the Samurai Legplates, level 95, resistances of 0.95).
    /// </summary>
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
    /// The lines below the price: the coverage by part and the skill effects that differ from none (<b>Observed</b> on the Samurai Armour at six grades and the Samurai Legplates). The
    /// Stealth effect is the piece's stealth factor (<see cref="ArmourPiece.StealthOf"/>); the Combat speed effect of a heavy piece is its <c>combat speed mult</c> x lerp(t, 0.9, 1)
    /// (fits all six grades of the Samurai Armour and the Legplates; other classes are <b>Unknown</b>, so they show none). The athletics effect (0.94x on the Legplates at 0.95 whose record says
    /// 1) and the dexterity, defence, perception and ranged bonus lines are not shown: their formulas and labels are <b>Unknown</b>.
    /// </summary>
    public static IReadOnlyList<Line> Tail(ArmourData a, Func<string, string?> partName, float quality)
    {
        float t = Math.Clamp(quality, 0, 100) / 100f + a.LevelBonus * 0.01f;
        var lines = new List<Line>();
        var parts = a.Coverage.Select(c => (Name: partName(c.PartStringId) ?? c.PartStringId, c.Percent)).ToList();
        if (parts.Count > 0)
        {
            lines.Add(new("[Coverage]", ""));
            foreach (var (name, percent) in parts.OrderBy(p => Array.IndexOf(PartOrder, p.Name) is var i and >= 0 ? i : PartOrder.Length))
                lines.Add(new("-" + name, percent + "%", LineKind.Stat));
        }
        var skills = new List<Line>();
        // A value that prints as 1.00x is neutral. The colour follows the side of neutral (the original's red for a penalty).
        void Multiplier(string label, float mult) { if (Number(mult, "0.00") != "1.00") skills.Add(new("-" + label, Number(mult, "0.00") + "x", LineKind.Effect, mult < 1 ? -1 : 1)); }
        void Bonus(string label, int bonus) { if (bonus != 0) skills.Add(new("-" + label, bonus.ToString(CultureInfo.InvariantCulture), LineKind.Effect, bonus < 0 ? -1 : 1)); }
        Multiplier("Crossbows effect", a.RangedSkillMult);
        Multiplier("Precision shooting effect", a.RangedSkillMult);
        Multiplier("Stealth effect", ArmourPiece.StealthOf(a, t));
        Multiplier("Dodge skill effect", a.DodgeMult);
        if (a.Class == ArmourClass.Heavy) Multiplier("Combat speed effect", a.CombatSpeedMult * WeaponStats.Lerp(t, 0.9f, 1f));
        Multiplier("Attack damage effect", a.DamageOutputMult);
        Multiplier("Assassination skill", a.AssassinationMult);
        Bonus("Melee attack bonus", a.CombatAttackBonus);
        Bonus("Martial arts bonus", a.UnarmedBonus);
        if (skills.Count > 0) { lines.Add(new("[Skills]", "")); lines.AddRange(skills); }
        return lines;
    }
}
