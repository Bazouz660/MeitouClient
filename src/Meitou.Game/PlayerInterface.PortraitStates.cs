using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The squad portraits' states (docs/game/ui-screens.md "Portrait cells"): the original's per-cell flags and how its <c>Kenshi_PortraitCharacter</c> cell shows them, the
/// <c>Background</c> (Normal, or a flashing Hurt), the <c>BackOverlay</c> (Combat, Hunger), the <c>FrontOverlay</c> (Slave, Prison) and the faded portrait.
/// </summary>
sealed partial class PlayerInterface
{
    [Flags]
    enum PortraitState
    {
        None = 0,
        Selected = 1,
        /// <summary>Away from the camera and in danger: the red background, flashing.</summary>
        Hurt = 2,
        /// <summary>Down, carried or in bed: the portrait at a quarter alpha, and no flash.</summary>
        Faded = 4,
        Combat = 8,
        Slave = 0x10,
        Prison = 0x20,
        /// <summary>Being eaten: the <c>Eaten</c> front overlay, over Prison and Slave.</summary>
        Eaten = 0x40,
        Hunger = 0x80,
    }

    /// <summary>When each member's flash started (<see cref="Stopwatch"/> ticks); a member leaves when its flash stops.</summary>
    readonly Dictionary<CharacterId, long> portraitFlashes = [];

    /// <summary>
    /// The original's flags (<c>FUN_140414dc0</c>). Its danger test reads a float of the character's AI that is not decoded (eating also waits while it is above 0); Meitou takes
    /// "in a fight" for it. Being eaten (<c>+0x279 &amp; 7</c>, <see cref="BodyStatus.BeingEaten"/>) also flashes, and sets <c>Eaten</c>.
    /// </summary>
    PortraitState PortraitStateOf(CharacterSnapshot m, Vector3 eye, Vector3 focus)
    {
        var s = PortraitState.None;
        if (m.Selected) s |= PortraitState.Selected;
        bool dead = m.Body is { Dead: true } or { Unconscious: true }, carried = !m.CarriedBy.IsNone, eaten = m.Body is { BeingEaten: true };
        bool down = dead || carried;
        if (Vector3.DistanceSquared(m.Position, eye) >= 1e6f && Vector3.DistanceSquared(m.Position, focus) >= 1e5f && (!down && m.InCombat && m.Prison is null || eaten))
            s |= PortraitState.Hurt;
        if (eaten) s |= PortraitState.Eaten;
        if (dead || carried || m.InBed) s |= PortraitState.Faded;
        if (m.CombatMode) s |= PortraitState.Combat;
        if (m.Body is { Hunger: < 1.25f }) s |= PortraitState.Hunger;
        if (m.Shackled) s |= PortraitState.Slave;
        if (m.Prison is not null) s |= PortraitState.Prison;
        return s;
    }

    /// <summary>
    /// The background's alpha (<c>FUN_140415150</c>): a flash starts at 1 and moves by 2 per second, down to 0 and back, while the member is <see cref="PortraitState.Hurt"/> and not
    /// <see cref="PortraitState.Faded"/>; otherwise 1.
    /// </summary>
    float PortraitFlash(CharacterId id, PortraitState s)
    {
        if ((s & PortraitState.Hurt) == 0 || (s & PortraitState.Faded) != 0)
        {
            portraitFlashes.Remove(id);
            return 1;
        }
        long now = Stopwatch.GetTimestamp();
        if (!portraitFlashes.TryGetValue(id, out long start)) portraitFlashes[id] = start = now;
        double t = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
        return (float)Math.Abs(1 - 2 * (t - Math.Floor(t)));
    }

    /// <summary>Drops the flashes of characters no longer on the squad bar.</summary>
    void PrunePortraitFlashes(IReadOnlyList<CharacterSnapshot> members)
    {
        if (portraitFlashes.Count == 0) return;
        foreach (var id in portraitFlashes.Keys.ToList())
            if (!members.Any(m => m.Id == id)) portraitFlashes.Remove(id);
    }

    static string BackOverlayName(PortraitState s) => (s & PortraitState.Combat) != 0 ? "Combat" : (s & PortraitState.Hunger) != 0 ? "Hunger" : "";

    static string FrontOverlayName(PortraitState s) => (s & PortraitState.Eaten) != 0 ? "Eaten" : (s & PortraitState.Prison) != 0 ? "Prison" : (s & PortraitState.Slave) != 0 ? "Slave" : "";

    /// <summary>An image of the cell's <c>Kenshi_PortraitImage</c> set at <paramref name="alpha"/>.</summary>
    static UiControl PortraitImage(string name, UiRect bounds, string group, string image, float alpha = 1)
    {
        var properties = new Dictionary<string, string> { ["ImageResource"] = "Kenshi_PortraitImage", ["ImageGroup"] = group, ["ImageName"] = image };
        if (alpha < 1) properties["Alpha"] = alpha.ToString(CultureInfo.InvariantCulture);
        return new(name, "ImageBox", bounds, "", "", "", true, properties, Vector4.Zero, Pickable: false);
    }
}
