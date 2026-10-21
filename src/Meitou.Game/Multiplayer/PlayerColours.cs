using System.Globalization;
using System.Numerics;
using Meitou.Net;
using Meitou.Net.Session;

namespace Meitou.Game;

/// <summary>
/// The players' colours (docs/meitou-ui.md "Multiplayer", engine choice): light enough for the dark HUD plates, shadowed over terrain. The host is 0, the
/// single-player marker colour. Names are always shown beside a colour, so colour is never the only cue.
/// </summary>
static class PlayerColours
{
    public const int Count = NetProtocol.MaxPlayers;
    // Sky (MarkerPlayer of kenshi_colours.xml), amber, orchid, mint, rose, lime, peach, periwinkle: hues about 45° apart, the first four the most distinct, no red
    // (the map's enemy) or dark green (ally).
    static readonly int[] Rgb = [0x8DCDFF, 0xF2C14E, 0xD69CF2, 0x7DDCB0, 0xF58FC0, 0xB9E36B, 0xFFAB7A, 0xA4AAFF];
    /// <summary>A player not in the roster (gone, or never known).</summary>
    public static readonly Vector4 Absent = new(0.62f, 0.62f, 0.62f, 1);
    /// <summary>The map's selected-marker colour, the host's selected colour.</summary>
    static readonly Vector4 HostSelected = new(0.652f, 0.903f, 1, 1);

    /// <summary>The colour <paramref name="colour"/> (a <c>RosterEntry.Colour</c>); <see cref="Absent"/> out of range.</summary>
    public static Vector4 Of(int colour) =>
        (uint)colour < Count ? new(((Rgb[colour] >> 16) & 255) / 255f, ((Rgb[colour] >> 8) & 255) / 255f, (Rgb[colour] & 255) / 255f, 1) : Absent;

    /// <summary><see cref="Of"/> as MyGUI writes a colour ("r g b", invariant culture) for <c>TextColour</c> and <c>Colour</c> properties.</summary>
    public static string MyGui(int colour)
    {
        var c = Of(colour);
        return string.Create(CultureInfo.InvariantCulture, $"{c.X:0.###} {c.Y:0.###} {c.Z:0.###}");
    }

    /// <summary>A selected marker: the host's is the map's own selected colour, the others their colour lightened by 35 %.</summary>
    public static Vector4 Selected(int colour) => colour == 0 ? HostSelected : Vector4.Lerp(Of(colour), Vector4.One, 0.35f) with { W = 1 };

    /// <summary>
    /// The colour of a joining player: <paramref name="preferred"/> (its number modulo <see cref="Count"/>, or the colour it had) when in range and free, else the
    /// lowest free one; -1 when every colour is <paramref name="taken"/>. The host, first in, takes 0.
    /// </summary>
    public static int Assign(IReadOnlyCollection<int> taken, int preferred = -1)
    {
        // preferred -1 (none) maps to colour 0 by the rule; an out-of-range number is reduced modulo Count.
        return PlayerColourRule.Assign(taken, preferred < 0 ? 0 : preferred);
    }
}
