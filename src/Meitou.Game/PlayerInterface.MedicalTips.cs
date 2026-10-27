using System.Globalization;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The medical panel's tooltips (the medical section <c>FUN_14088a8f0</c>, docs/game/ui-screens.md "Tooltips"): one on each row's <c>LifeBarNTooltip</c> widget and one on the
/// health word, as far as Meitou models the states they describe.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The Blood row: the race's bleed rate multiplier, Bad above 1 and Good below.</summary>
    static string BloodTip(float rate) =>
        (rate > 1 ? "#59231a" : rate < 1 ? "#a8b774" : "") + Loc.Tr("Racial bleed rate multiplier: x{1,num=fix,p=2}", rate);

    /// <summary>
    /// A body part's row. With h its flesh (no splint: the original's tests read it alone), d the pending healing, s the stun and m the maximum: destroyed (h + d - s at or below -m) says so; below 0 a robotic limb
    /// "does not degenerate", a vital part above the KO point is "dangerous" (death at -m; unconscious: regain consciousness before the KO point) and at it "critical", any other part
    /// "getting worse"; from 0 a part more than 20 short of m needs bandaging (robotic: plainly), less than 5 % short (healing and stun within 5) is okay or healing, else bandaging is not
    /// urgent. Under 60 left a line on what the damage affects follows. The original's wear lines (skeletons), the kick line and the lost-limb line are not modelled.
    /// </summary>
    static string PartTip(HealthPartView part, float? koPoint, bool unconscious)
    {
        float h = part.Flesh, d = part.Bandage, s = part.Stun, m = part.Maximum;
        string text;
        if (h + d - s <= -m)
            text = Loc.Tr(part.Vital ? "Your {1} is totally destroyed. It's probably the reason you are dead." : "Your {1} is totally destroyed. Lucky it's not something you need to live.", part.Name);
        else if (h + d < 0)
        {
            if (!part.Vital) text = Loc.Tr(part.Robotic ? "This limb is severely damaged and in need of repairs. Luckily though skeleton limbs don't degenerate." : "This wound is getting worse. You need first aid.");
            else if (koPoint is { } ko && h + d >= ko)
                text = !unconscious ? Loc.Tr("This is a dangerous wound and is getting worse. Get some medical attention fast.  Death at {1}", (int)-m)
                    : "#59231a" + Loc.Tr("This is a dangerous wound and is getting worse. If you can regain consciousness before it goes below {1,num} then you could save yourself.\nIf it goes below that then you will have to hope you get rescued by somebody else.\n", (int)ko)
                      + Loc.Tr("You will die at {1}", (int)-m);
            else text = Loc.Tr("This is a critical wound. If it doesn't get medical treatment you will die.");
        }
        else if (m - h - d > 20)
            text = Loc.Tr(!part.Vital && part.Robotic ? "Your {1} needs bandaging." : "Your {1} needs bandaging, it's slowly getting worse.", part.Name);
        else if (m * 0.95f - s <= d + h)
            text = Loc.Tr(d + s <= 5 ? "Your {1} is okay." : "Your {1} is in the process of healing.", part.Name);
        else text = Loc.Tr("Your {1} needs bandaging, but it's not urgent.", part.Name);
        float left = h + d - s;
        if (left > 0 && left < 60)
        {
            string? affects = part.Vital ? "Your stats will be negatively affected." : part.Type == BodyPartType.Leg ? "Your movement speed will be negatively affected."
                : part.Type == BodyPartType.Arm ? "Your combat and manual labour skills will be negatively affected." : null;
            if (affects is not null) text += "\n" + Loc.Tr(affects);
        }
        return text;
    }

    /// <summary>
    /// A body part's arrows, the same section's row test (docs/game/ui-screens.md "Medical rows"; h, d, s, m and the KO point as in <see cref="PartTip"/>): "!!!" when destroyed;
    /// below 0 (h + d) "&lt;&lt;" on a part that is neither vital nor robotic, "&lt;&lt;!!" on a vital one at or under the KO point (none above it); from 0, "&lt;" while more than 20
    /// short of m (a robotic non-vital part excepted); else none. The bandage counts in the test, the splint does not.
    /// </summary>
    static string PartArrows(HealthPartView part, float? koPoint)
    {
        float h = part.Flesh, d = part.Bandage, s = part.Stun, m = part.Maximum;
        if (h + d - s <= -m) return "!!!   ";
        if (h + d < 0)
        {
            if (!part.Vital) return part.Robotic ? "" : "<<   ";
            return koPoint is { } ko && h + d >= ko ? "" : "<<!!   ";
        }
        return (m - h) - d > 20 && !(!part.Vital && part.Robotic) ? "<   " : "";
    }

    /// <summary>
    /// The Hunger row (0..3; Meitou keeps no stomach store, so the original's recovering texts never show): above 2 well fed, above 1 malnourished, else starving, or passed out
    /// alone or with squad mates to feed it. Engine choice: the stat penalty and the pass-out threshold the original's malnutrition and starving rows print are not
    /// modelled, so the malnourished row takes the Hungry health word's sentence and the starving row the Starving word's.
    /// </summary>
    static string HungerTip(float hunger, bool unconscious, int squadSize) =>
        hunger > 2 ? Loc.Tr("You are well fed and currently have no problem.")
        : hunger > 1 ? Loc.Tr("You are hungry and suffering from malnutrition. Your stats and strength will be affected, you need to get some food as soon as possible.")
        : !unconscious ? Loc.Tr("You are starving to death. Get some food fast.")
        : squadSize < 2 ? Loc.Tr("You have passed out from starvation. You're all alone and going to die. Nobody cares.")
        : Loc.Tr("You have passed out from starvation. One of your squad mates will have to feed you or you're going to die.");

    /// <summary>The KO point line's tooltip (<c>FUN_140895350</c>).</summary>
    static string KoPointTip(float koPoint) =>
        Loc.Tr("When an injury to a body part goes below your KO point of {1,num} then you are down for good. Your body will enter a coma and won't regain consciousness until all critical body parts have healed above 0.", (int)koPoint);
}
