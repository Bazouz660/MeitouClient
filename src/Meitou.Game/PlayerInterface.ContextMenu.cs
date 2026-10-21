using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The context menu (the original's ContextMenuGUI, <c>Kenshi_ContextMenu</c>; docs/game/ui-input.md section 7): holding the command (right) button on a character for
/// <see cref="ContextHoldSeconds"/> opens a menu of the orders the selection can give it, titled with its name; a quicker click gives the default order at once, as before.
/// A left click on a verb gives it; any other click, or Esc, closes the menu. The verbs are the original's names for the orders Meitou has (attack, trade, first aid, pick up,
/// put down, loot); Talk opens a conversation (<see cref="Meitou.Simulation.Dialogue.DialogueSystem"/>); the rest of its verb list (follow, bodyguard...) waits for those systems.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How long the command button is held on a target before the menu opens (<b>Observed</b>: 0.2 s, FUN_140800250).</summary>
    public const double ContextHoldSeconds = 0.2;

    sealed record ContextMenu(string Title, Vector2 At, IReadOnlyList<(string Caption, Action Give)> Verbs);
    ContextMenu? contextMenu;
    // A right press on a target that has verbs: when, where, and the order a quick click gives (null: none).
    (CharacterId Target, Vector2 At, long Since, Action? Quick)? contextPending;

    public bool ContextMenuOpen => contextMenu is not null;

    /// <summary>The verbs the selection has for <paramref name="target"/>, in the original's names.</summary>
    List<(string Caption, Action Give)> Verbs(CharacterSnapshot target)
    {
        var verbs = new List<(string, Action)>();
        var selection = SelectedIds();
        if (selection.Count == 0) return verbs;
        var id = target.Id;
        bool down = target.Body is { } b && (b.Dead || b.Unconscious);
        var carriers = Selected().Where(c => c.Carrying == id).Select(c => c.Id).ToArray();
        if (carriers.Length > 0) verbs.Add(("Put Down", () => Say(new Meitou.Simulation.Carrying.PutDownOrder(carriers) { Tick = session.World.Tick })));
        // Meitou stand-in for the original's cage use (docs/simulation.md "Arrest and jail"): carry the body to the nearest free cage.
        if (carriers.Length > 0) verbs.Add(("Put in Cage", () => Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Tick = session.World.Tick })));
        if (!down && !target.IsPlayer)
        {
            if (session.World.CanTalk(id, selection[0])) verbs.Add(("Talk", () => Say(new Meitou.Simulation.Dialogue.TalkOrder(selection[0], id) { Tick = session.World.Tick })));
            if (target.IsTrader) verbs.Add(("Trade", () => Say(new Meitou.Simulation.Trading.OpenTrade(selection[0], id) { Tick = session.World.Tick })));
            verbs.Add(("Attack Target", () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, id) { Tick = session.World.Tick })));
            // The original's "Stealth KO" (order 228), offered while a selected character sneaks; the chance is in the caption (the original shows it at the pointer).
            if (target.StealthKoChance is { } chance)
                verbs.Add(($"{Loc.Tr("Stealth KO")} ({Math.Clamp(chance, 0, 100):0}%)", () => Say(new Meitou.Simulation.Carrying.StealthKoOrder(selection, id) { Tick = session.World.Tick })));
        }
        if (target.Body is { Dead: false, NeedsAid: true })
            verbs.Add(("First Aid", () => Say(new Meitou.Simulation.FirstAid.TreatOrder(selection, id) { Tick = session.World.Tick })));
        if (target.Body is { Dead: false, NeedsSplint: true })
            verbs.Add(("Splint Injuries", () => Say(new Meitou.Simulation.FirstAid.SplintOrder(selection, id) { Tick = session.World.Tick })));
        // A caged character: pick its cage's lock (the prisoner its own, a mate from outside), and once it is open the prisoner may get out (an escape unless served).
        if (target.Prison is { } prison && !down)
        {
            if (prison.Locked) verbs.Add(("Pick Lock", () => Say(new Meitou.Simulation.Locks.PickLockOrder(selection, id) { Tick = session.World.Tick })));
            else if (selection.Contains(id)) verbs.Add(("Get Out", () => Say(new Meitou.Simulation.Locks.GetOutOrder([id]) { Tick = session.World.Tick })));
        }
        // Locked shackles (the original's verbs 201, 185, 186): pick the lock, saw them off with a tool, or break them by strength; the wearer's own or a mate's (another faction's: a crime).
        if (target.ShacklesLocked && target.Body is not { Dead: true })
        {
            verbs.Add(("Unlock Shackles", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Pick) { Tick = session.World.Tick })));
            verbs.Add(("Use Tools", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Cut) { Tick = session.World.Tick })));
            verbs.Add(("Use Strength", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Break) { Tick = session.World.Tick })));
            // The wearer works its own shackles when it is selected, else the selection.
            IReadOnlyList<CharacterId> Workers() => selection.Contains(id) ? [id] : selection;
        }
        // Meitou stand-in for dragging shackles onto a down or caged character's armour slot: a selected character carrying some puts them on, locked.
        if (target.CanBeShackled && carriers.Length == 0 && Selected().Any(c => c.CarriesShackles))
            verbs.Add(("Put on Shackles", () => Say(new Meitou.Simulation.Locks.PutShacklesOrder(Selected().Where(c => c.CarriesShackles).Select(c => c.Id).ToArray(), id) { Tick = session.World.Tick })));
        // Out of an unlocked cage a mate lifts the prisoner, awake or down (out of another faction's cage a crime for both, locks.md "Cages and beds").
        if (target.Prison is { Locked: false } && !selection.Contains(id) && target.CarriedBy.IsNone && carriers.Length == 0)
            verbs.Add(("Pick Up", () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = session.World.Tick })));
        if (down && target.CarriedBy.IsNone && carriers.Length == 0 && target.Prison is null)
        {
            verbs.Add(("Pick Up", () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = session.World.Tick })));
            verbs.Add(("Loot", () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], id) { Tick = session.World.Tick })));
        }
        return verbs;
    }

    /// <summary>A right press on a character with verbs starts the hold timer (true); the quick order, if any, waits for the release.</summary>
    bool ContextPress(Vector2 p, Action? quick)
    {
        if (PickWhere(p, _ => true) is not { } id || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } target || Verbs(target).Count == 0) return false;
        contextPending = (id, p, System.Diagnostics.Stopwatch.GetTimestamp(), quick);
        return true;
    }

    /// <summary>Once a frame: a press held long enough opens the menu.</summary>
    void UpdateContextMenu()
    {
        if (contextPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        contextPending = null;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == pending.Target) is not { } target) return;
        var verbs = Verbs(target);
        if (verbs.Count > 0) contextMenu = new(target.Name, pending.At, verbs);
    }

    /// <summary>The right button let go: a press released before the menu opened gives the quick order.</summary>
    void ContextRelease()
    {
        if (contextPending is not { } pending) return;
        contextPending = null;
        pending.Quick?.Invoke();
    }

    void ContextAction(string argument)
    {
        var menu = contextMenu;
        contextMenu = null;
        if (menu is not null && int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < menu.Verbs.Count) menu.Verbs[i].Give();
    }

    /// <summary>Closes the menu (true when one was open).</summary>
    bool CloseContextMenu()
    {
        bool open = contextMenu is not null;
        contextMenu = null;
        contextPending = null;
        return open;
    }

    /// <summary>The menu as the original lays it out: a floating panel an eighth of the screen wide, the name on top, a button per verb, beside the cursor and kept on screen.</summary>
    void AddContextMenu(List<UiControl> controls)
    {
        if (contextMenu is not { } menu) return;
        // Kenshi_ContextMenu: Root 0.125 x 0.0685 of the screen for the title and one option, the title 47.3 % of that height, an option the rest.
        float w = MathF.Max(200, Width * 0.125f), full = MathF.Max(64, Height * 0.0685185f);
        float titleH = full * 0.472973f, optionH = full - titleH, h = titleH + optionH * menu.Verbs.Count + 6;
        float x = Math.Clamp(menu.At.X + 12, 0, Math.Max(0, Width - w)), y = Math.Clamp(menu.At.Y, 0, Math.Max(0, Height - h));
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        controls.Add(new("context-menu", "Widget", new(x, y, w, h), "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), new(0.1f, 0.09f, 0.08f, 0.94f), ink, Pickable: true));
        controls.Add(new("context-title", "TextBox", new(x + 6, y + 2, w - 12, titleH), menu.Title, "", "", true,
            new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" }, default, ink));
        for (int i = 0; i < menu.Verbs.Count; i++)
            controls.Add(new($"context-verb-{i}", "Button", new(x + w * 0.0208333f, y + titleH + i * optionH + optionH * 0.05f, w * 0.958f, optionH * 0.9f), Loc.Tr(menu.Verbs[i].Caption),
                "meitou:context", "Kenshi_Button1Skin", true, new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" },
                default, ink, Pickable: true, Argument: i.ToString(CultureInfo.InvariantCulture)));
    }
}
