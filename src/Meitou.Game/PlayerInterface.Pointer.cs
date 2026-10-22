using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>Ctrl and Alt as the host last saw them: they turn the pointer over a downed character into the carry and loot pointers.</summary>
    public bool CtrlHeld, AltHeld;
    // Whether the mouse was over a control in the last arranged UI (ArrangeUi), so the pointer needs no arrange of its own.
    bool pointerOverUi;
    // The pointer and the character it is over, chosen again only when the tick, the mouse or a modifier changed (the picks walk every character).
    (long Tick, Vector2 Mouse, bool Ctrl, bool Alt, string Name, CharacterId? Target) pointer = (-1, default, false, false, "arrow", null);

    /// <summary>
    /// The mouse pointer (kenshi_pointers.xml) for what the right button would do under the cursor (docs/game/ui-input.md, pointers): the quick order's pointer
    /// (<see cref="QuickOrder"/>: talk, attk, med, lift, search), with Ctrl lift and with Alt search over a downed character; over the interface, while dragging or with no
    /// selection, the arrow.
    /// </summary>
    public string PointerName => Point().Name;

    /// <summary>
    /// The character under the cursor for the hover outline and its colour: the one a right click would act on, in the colour of that order (attack red, treat
    /// green, talk gold, carry or loot blue), else any character under the cursor in white; null over the interface or nobody.
    /// </summary>
    public (CharacterId Id, Vector4 Colour)? HoverTarget => Point() is { Target: { } id } p ? (id, PointerColour(p.Name)) : null;

    /// <summary>The outline colour for the order a pointer shows: attack red, treat green, talk gold, carry or loot blue, else white.</summary>
    static Vector4 PointerColour(string pointer) => pointer switch
    {
        "attk" => new Vector4(1f, 0.3f, 0.22f, 1),
        "med" => new Vector4(0.4f, 1f, 0.5f, 1),
        "talk" => new Vector4(1f, 0.82f, 0.4f, 1),
        "lift" or "search" => new Vector4(0.45f, 0.75f, 1f, 1),
        _ => WhiteOutline,
    };

    /// <summary>How far from the camera's focus the held Highlight key outlines characters (<see cref="HighlightTargets"/>).</summary>
    public const float HighlightRadius = 500;

    /// <summary>
    /// The characters the held Highlight key (Alt) outlines (docs/game/ui-input.md "Hover outline"): every character, the squad's, people and animals, within <paramref name="radius"/>
    /// of <paramref name="centre"/>, each in the colour hovering it would give (the quick order's rules applied to it alone), and the hovered one as <see cref="HoverTarget"/>.
    /// </summary>
    public List<(CharacterId Id, Vector4 Colour)> HighlightTargets(Vector3 centre, float radius)
    {
        var hover = HoverTarget;
        var list = new List<(CharacterId Id, Vector4 Colour)>();
        if (hover is { } h) list.Add(h);
        var selection = SelectedIds();
        foreach (var c in session.CurrentSnapshot.Characters)
            if (c.Id != hover?.Id && Vector3.DistanceSquared(c.Position, centre) <= radius * radius)
                list.Add((c.Id, PointerColour(selection.Count > 0 ? QuickPointer(c, selection[0]) : StandingHostile(c) ? "attk" : "arrow")));
        return list;
    }

    /// <summary><see cref="QuickOrder"/>'s pointer for <paramref name="c"/>, as if it alone were under the cursor.</summary>
    string QuickPointer(CharacterSnapshot c, CharacterId selected)
    {
        if (c.IsPlayer) return c.Body is { Dead: false, NeedsAid: true } ? "med" : !c.CarriedBy.IsNone ? "search" : c.Body is { Dead: true } or { Unconscious: true } ? "lift" : "arrow";
        if (c.Body is { Dead: true } or { Unconscious: true } || !c.CarriedBy.IsNone) return "search";
        if (Hostile(c)) return "attk";
        if (c.IsTrader) return "talk";
        return session.World.CanTalk(c.Id, selected) ? "talk" : "arrow";
    }

    /// <summary>Another faction's hostile character on its feet (not downed, dead or carried): the attack pointer and red outline whatever is selected.</summary>
    bool StandingHostile(CharacterSnapshot c) => !c.IsPlayer && c.Body is not ({ Dead: true } or { Unconscious: true }) && c.CarriedBy.IsNone && Hostile(c);

    static readonly Vector4 WhiteOutline = new(0.95f, 0.93f, 0.85f, 0.85f);

    /// <summary>Whether a faction is hostile to the player's (the world's relations); set by the host. Without it every other faction counts as hostile.</summary>
    public Func<int, bool>? HostileFaction { get; set; }

    /// <summary>Whether the character's faction is hostile to the player's: only a hostile one gets the attack pointer, the red outline and the quick-attack right click.</summary>
    bool Hostile(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c || Hostile(c);
    bool Hostile(CharacterSnapshot c) => HostileFaction?.Invoke(c.Faction) != false;

    (long Tick, Vector2 Mouse, bool Ctrl, bool Alt, string Name, CharacterId? Target) Point()
    {
        if (mapDrag is not null) return (-1, mouse, CtrlHeld, AltHeld, "hand", null);   // the map's pan
        if (UiVisible && pointerOverUi || UiCapturesPointer || ContextMenuOpen || dragging || settingsOpen || TitleOpen) return (-1, mouse, CtrlHeld, AltHeld, "arrow", null);
        if (BuildPointer() is { } building) return (-1, mouse, CtrlHeld, AltHeld, building, null);
        var snapshot = session.CurrentSnapshot;
        if (pointer.Tick == snapshot.Tick && pointer.Mouse == mouse && pointer.Ctrl == CtrlHeld && pointer.Alt == AltHeld) return pointer;
        (string Name, CharacterId? Target) chosen = ("arrow", null);
        if (SelectedIds().Count > 0)
        {
            if ((CtrlHeld || AltHeld) && PickDowned(mouse) is { } downed) chosen = (AltHeld ? "search" : "lift", downed);
            else
                // The nearest target, as the right click takes it (PickAt).
                switch (PickAt(mouse).Kind)
                {
                    case Picked.Character when QuickOrder(mouse) is { } order: chosen = (order.Pointer, order.Target); break;
                    case Picked.GroundItem: chosen = ("pick_item", null); break;
                    case Picked.Container when PickContainer(mouse) is { } container: chosen = (ContainerPointer(container), null); break;
                    case Picked.Door when PickDoor(mouse) is { } door: chosen = (DoorPointer(door), null); break;
                    case Picked.Rock: chosen = ("mine", null); break;   // a resource rock a right click would mine (the original's `mine` pointer)
                    case Picked.Site when SitePointer(mouse) is { } siteMode: chosen = (siteMode, null); break;
                }
        }
        chosen.Target ??= PickWhere(mouse, _ => true);
        // A hostile character shows the attack pointer and the red outline even with nobody selected to send at it.
        if (chosen.Name == "arrow" && chosen.Target is { } over && snapshot.Characters.FirstOrDefault(c => c.Id == over) is { } overChar && StandingHostile(overChar))
            chosen.Name = "attk";
        return pointer = (snapshot.Tick, mouse, CtrlHeld, AltHeld, chosen.Name, chosen.Target);
    }

    /// <summary>Draws the pointer <paramref name="name"/> at <paramref name="at"/> (framebuffer pixels) over everything drawn so far; nothing without the UI painter.</summary>
    public void DrawPointer(string name, Vector2 at, int width, int height) => uiPainter?.DrawPointer(name, at, width, height);
}
