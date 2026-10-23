using System.Globalization;
using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The "Show damage floaters" option (the original's <c>floaters</c>: Off 0, Simple info 1, Full info 2).</summary>
enum FloaterMode { Off, Simple, Full }

/// <summary>The words and colours of the floating numbers (docs/game/ui-screens.md "Damage floaters", <c>FUN_1406508d0</c>).</summary>
static class FloaterText
{
    /// <summary>The game's default floater colour, and the two it tints in (0.9 alpha); heals are Meitou's own.</summary>
    public static readonly Vector4 Grey = new(0.9f, 0.9f, 0.9f, 0.9f), Green = new(0.11f, 0.6f, 0.11f, 0.9f), Red = new(1f, 0.31f, 0.31f, 0.9f), HealGreen = new(0.35f, 1f, 0.45f, 1f);

    /// <summary>
    /// Simple: the whole-number total, then the armour's share in brackets when it is above 0 ("12 (-5)"). Full: the part, then cut (with pierce when above 1), and blunt with stun when the
    /// blunt is 1 or more, each with its own bracket ("Left Arm: 8 (-3) + 4"). A heal is "+N". Null when there is nothing to show.
    /// </summary>
    public static string? Format(in Floater f, FloaterMode mode)
    {
        if (f.Kind == FloaterKind.Text) return f.Part.Length > 0 ? f.Part : null;   // the eating text (Meitou draws it with the damage floaters, so it shows only while they are on)
        if (mode == FloaterMode.Off) return null;
        if (f.Kind == FloaterKind.Heal) return f.Healed >= 1 ? "+" + Whole(f.Healed) : null;
        var d = f.Damage;
        var a = f.Absorbed;
        if (mode == FloaterMode.Simple)
        {
            string total = Whole(d.Cut + d.Blunt + d.Pierce + d.ExtraStun);
            int stopped = (int)(a.Cut + a.Blunt + a.Pierce);
            return stopped > 0 ? $"{total} (-{stopped})" : total;
        }
        var text = new System.Text.StringBuilder(f.Part).Append(": ");
        bool withPierce = d.Pierce > 1;
        text.Append(Whole(withPierce ? d.Pierce + d.Cut : d.Cut));
        float firstStopped = withPierce ? a.Pierce + a.Cut : a.Cut;
        if (firstStopped > 0) text.Append(" (-").Append(Whole(firstStopped)).Append(')');
        if (d.Blunt >= 1)
        {
            text.Append(" + ").Append(Whole(d.Blunt + d.ExtraStun));
            if (a.Blunt > 0) text.Append(" (-").Append(Whole(a.Blunt)).Append(')');
        }
        return text.ToString();
    }

    /// <summary>Red when a player's character is hit, green when one lands the blow (Meitou's reading of the original's two relation tints), else grey.</summary>
    public static Vector4 Colour(in Floater f) => f.Kind == FloaterKind.Text ? White : f.Kind == FloaterKind.Heal ? HealGreen : f.TargetIsPlayer ? Red : f.SourceIsPlayer ? Green : Grey;

    /// <summary>The text floaters' colour: white at 0.9 alpha (<c>FUN_1405d0780</c>).</summary>
    public static readonly Vector4 White = new(1, 1, 1, 0.9f);

    static string Whole(float v) => ((int)v).ToString(CultureInfo.InvariantCulture);
}
