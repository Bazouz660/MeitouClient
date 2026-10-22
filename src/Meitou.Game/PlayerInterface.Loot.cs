using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// The loot window (docs/simulation.md "Carrying, rescue and looting", engine choice: the original's is <b>Unknown</b>): a downed character's whole inventory in a second character window (<c>loot</c>, <c>Kenshi_InventoryCharacterWindow</c>) beside the
/// looter's, its worn gear in the equipment slots, and its bag in <c>loot-bag</c> (the generic window, OPEN BAG). Items are dragged between the two characters' windows
/// (<see cref="TakeLootItem"/>, <see cref="PutLootItem"/>); closing it closes the loot (<see cref="CloseContainer"/>).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The body whose loot window is shown, and one whose window the player closed (not reopened while the sim still has it open).</summary>
    CharacterId shownLoot = CharacterId.None, closedLoot = CharacterId.None;
    /// <summary>The loot's bag window is wanted (OPEN BAG in the loot window toggles it).</summary>
    bool lootBagOpen = true;

    static bool IsLootScreen(string screen) => screen is "loot" or "loot-bag";

    /// <summary>The open loot's body (its snapshot carries the item grid), or null.</summary>
    CharacterSnapshot? LootBody()
    {
        var id = session.CurrentSnapshot.OpenLoot;
        return id.IsNone ? null : session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>Once a frame: the loot windows follow the sim's open loot; the player closing the window closes the loot.</summary>
    void SyncLootScreen()
    {
        if (faithfulScreens is null) return;
        var open = session.CurrentSnapshot.OpenLoot;
        if (closedLoot != open) closedLoot = CharacterId.None;
        if (!shownLoot.IsNone && shownLoot == open && !faithfulScreens.IsOpen("loot"))
        {
            Say(new CloseContainer { Tick = session.World.Tick });
            (closedLoot, shownLoot) = (open, CharacterId.None);
            return;
        }
        if (shownLoot != open && open != closedLoot)
        {
            if (!shownLoot.IsNone) faithfulScreens.Close("loot");
            shownLoot = open;
            if (!open.IsNone)
            {
                if (Selected().Count > 0 && !faithfulScreens.IsOpen("inventory")) faithfulScreens.Open("inventory");
                faithfulScreens.Open("loot");
                PlaceInventoryBeside("loot", left: false);
                inventoryScroll.Remove("loot-bag");
            }
        }
        bool want = faithfulScreens.IsOpen("loot") && lootBagOpen && LootBody() is { ItemGrid: { BagWidth: > 0, BagHeight: > 0 } };
        if (want == faithfulScreens.IsOpen("loot-bag")) return;
        if (!want) { faithfulScreens.Close("loot-bag"); return; }
        faithfulScreens.Open("loot-bag");
        PlaceInventoryBeside("loot-bag", left: false, anchor: "loot");
    }

    void BindLootScreen(string id, UiBindings b)
    {
        var body = LootBody();
        b.Text["Root"] = (id == "loot-bag" ? body?.ItemGrid?.Items.FirstOrDefault(i => i.Section == "backpack_attach")?.Name ?? "BACKPACK" : body?.Name ?? "INVENTORY").ToUpperInvariant();
        if (id == "loot-bag") { b.Hidden.Add("CapacityText"); b.Hidden.Add("TypeText"); return; }
        b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        if (body?.ItemGrid is { BagWidth: > 0, BagHeight: > 0 }) { b.Disabled.Remove("OpenBagButton"); b.Actions["OpenBagButton"] = "OpenBagButton"; }
    }

    /// <summary>The loot's item let go over the looter's inventory or bag: taken into the slot or cell under it (else wherever it fits, by the sim).</summary>
    void TakeLootDrop(Vector2 p, string screen, InventoryItemView item, InventoryItemToken token, Vector2 grab)
    {
        if (screen is not ("inventory" or "bag") || InventoryOwner() is not { ItemGrid: { } own } lead) return;
        string? section = null;
        int x = 0, y = 0;
        if (lead.Id == session.CurrentSnapshot.LootOpener && DropPlace(p, screen, own, item, grab, -1, out string place, out int px, out int py)) (section, x, y) = (place, px, py);
        Say(new TakeLootItem(token, section, x, y) { Tick = session.World.Tick });
    }

    /// <summary>The looter's item let go over the loot window or its bag: put into the slot or cell under it.</summary>
    void PutLootDrop(Vector2 p, string screen, CharacterId character, InventoryItemView item, InventoryItemToken token, Vector2 grab)
    {
        if (LootBody()?.ItemGrid is not { } into || !DropPlace(p, screen, into, item, grab, -1, out string section, out int x, out int y)) return;
        Say(new PutLootItem(character, token, x, y, section) { Tick = session.World.Tick });
    }
}
