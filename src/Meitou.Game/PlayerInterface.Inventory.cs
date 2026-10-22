using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>A character's item being dragged: the selected lead's, or (<c>Loot</c>) the open loot's.</summary>
    (CharacterId Character, InventoryItemView Item, Vector2 Grab, bool Loot)? inventoryDrag;
    /// <summary>A stock item of the open trade being dragged out of the trader window (dropped on the character's grid it is bought).</summary>
    (TradeItemView Item, Vector2 Grab)? tradeDrag;
    /// <summary>The character the inventory window was pinned on (its caption's pin button), none while it is not pinned.</summary>
    CharacterId inventoryPin = CharacterId.None;
    /// <summary>The pin last told to the simulation (<see cref="ShowInventoryCommand"/>), which publishes the pinned character's grid while it is not selected.</summary>
    CharacterId inventoryPinSent = CharacterId.None;

    /// <summary>
    /// The character the inventory window (and its bag and limbs) shows: the lead, or while the window is pinned the one it was pinned on, whoever is selected
    /// (docs/game/ui-screens.md section 6). A pinned character who is gone falls back to the lead.
    /// </summary>
    CharacterSnapshot? InventoryOwner()
    {
        if (!faithfulScreens.IsPinned("inventory")) inventoryPin = CharacterId.None;
        else if (inventoryPin.IsNone) inventoryPin = Selected().FirstOrDefault()?.Id ?? CharacterId.None;
        if (inventoryPin != inventoryPinSent) Say(new ShowInventoryCommand(inventoryPinSent = inventoryPin) { Tick = session.World.Tick });
        if (!inventoryPin.IsNone && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == inventoryPin) is { } pinned) return pinned;
        return Selected().FirstOrDefault();
    }
    CharacterSnapshot? InventoryOwner(CharacterId id) => InventoryOwner() is { } owner && owner.Id == id ? owner : null;

    bool InventoryPointerDown(Vector2 p)
    {
        if (!UiVisible || faithfulScreens.HitTest(p) is not { } picked || !picked.Control.Enabled) return false;
        var bounds = picked.Control.Bounds;
        Vector2 Grab() => (p - new Vector2(bounds.X, bounds.Y)) / new Vector2(bounds.Width, bounds.Height);
        if (ScrollThumbDown(picked.Screen, picked.Control, p)) return true;
        if (picked.Control.Properties.TryGetValue("TradeItem", out var stockIndex))
        {
            if (session.CurrentSnapshot.Trade?.Stock.FirstOrDefault(i => i.Index == int.Parse(stockIndex, CultureInfo.InvariantCulture)) is not { } stock) return false;
            tradeDrag = (stock, Grab());
            return true;
        }
        if (MachinePointerDown(picked.Control, Grab())) return true;
        bool loot = IsLootScreen(picked.Screen);
        if (!picked.Control.Properties.TryGetValue("InventoryItem", out var index) || (loot ? LootBody() : InventoryOwner()) is not { ItemGrid: { } grid } owner) return false;
        var item = grid.Items.FirstOrDefault(i => i.Index == int.Parse(index, CultureInfo.InvariantCulture));
        if (item is null || !(InventoryCommands.IsMovableSection(item.Section) || InventoryCommands.IsLimbSection(item.Section))) return false;
        inventoryDrag = (owner.Id, item, Grab(), loot);
        Sound("Item_Lift", item: item.Record);
        return true;
    }

    void InventoryPointerUp(Vector2 p)
    {
        scrollDrag = null;
        var buying = tradeDrag;
        var moving = inventoryDrag;
        var taking = machineDrag;
        tradeDrag = null;
        inventoryDrag = null;
        machineDrag = null;
        if ((buying is null && moving is null && taking is null) || !UiVisible) return;
        ArrangeUi();
        if (faithfulScreens.HitTest(p) is not { } hit)
        {
            // A character's item let go over the world (no window, no HUD): dropped on the ground at its feet.
            if (moving is { Loot: false } dropped && !OverHud(p))
            {
                var i = dropped.Item;
                Say(new DropInventoryItem(dropped.Character, new InventoryItemToken(i.Index, i.Record, i.Section, i.X, i.Y, i.Quantity, i.Quality)) { Tick = session.World.Tick });
            }
            return;
        }
        if (taking is { } t) { MachineDrop(t, hit.Screen); return; }
        var trade = session.CurrentSnapshot.Trade;

        if (buying is { } b)
        {
            // Stock dropped on the customer's pocket or bag grid: a purchase into the cell under the drop; a bag dropped on the backpack slot: into that slot.
            if (trade is null || hit.Screen is not ("inventory" or "bag") || InventoryOwner(trade.Customer)?.ItemGrid is not { } grid) return;
            if (b.Item.Container && EquipmentSlotAt(p, hit.Screen) == "backpack_attach")
            {
                Say(new BuyItem(b.Item.Token, 1) { Tick = session.World.Tick, Section = "backpack_attach" });
                return;
            }
            string section = hit.Screen == "bag" ? "backpack_content" : "main";
            int columns = section == "main" ? grid.Width : grid.BagWidth, rows = section == "main" ? grid.Height : grid.BagHeight;
            if (!DropCell(p, hit.Screen, section, columns, rows, b.Grab, b.Item.Width, b.Item.Height, Footprints(grid.Items, section, -1), out int x, out int y)) return;
            Say(new BuyItem(b.Item.Token, b.Item.Quantity) { Tick = session.World.Tick, Section = section, X = x, Y = y });
            return;
        }

        var drag = moving!.Value;
        var item = drag.Item;
        var token = new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality);
        if (drag.Loot) { TakeLootDrop(p, hit.Screen, item, token, drag.Grab); return; }
        if (IsLootScreen(hit.Screen)) { PutLootDrop(p, hit.Screen, drag.Character, item, token, drag.Grab); return; }
        if (hit.Screen == "trader")
        {
            // The customer's item dropped on the trader's stock: a sale, into the stock cell under the drop when it is free.
            if (trade is null || trade.Customer != drag.Character) return;
            int? sx = null;
            int sy = 0;
            if (TraderGrid(hit.Screen) is { } stockGrid && DropCell(p, hit.Screen, stockGrid, trade.StockWidth, trade.StockHeight, drag.Grab, item.Width, item.Height,
                    trade.Stock.Select(s => (s.X, s.Y, s.Width, s.Height)), out int cx, out int cy))
                (sx, sy) = (cx, cy);
            Sell(trade, token, item.Quantity, sx, sy);
            return;
        }
        if (PutDrop(p, hit.Screen, drag.Character, item, token, drag.Grab)) return;
        if (hit.Screen == "limbs")
        {
            // A prosthetic dropped on a slot of the limbs window: fitted there (the sim checks the side and that the limb is missing).
            var controls = faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent).First(f => f.Id == hit.Screen).Controls;
            if (controls.LastOrDefault(c => InventoryCommands.IsLimbSection(c.Name) && c.Bounds.Contains(p.X, p.Y) && (c.Clip?.Contains(p.X, p.Y) ?? true)) is { } slot)
                Say(new MoveInventoryItem(drag.Character, token, slot.Name, 0, 0) { Tick = session.World.Tick });
            return;
        }
        if (hit.Screen is not ("inventory" or "bag") || InventoryOwner(drag.Character)?.ItemGrid is not { } own) return;
        if (DropPlace(p, hit.Screen, own, item, drag.Grab, item.Index, out string target, out int tx, out int ty))
            Say(new MoveInventoryItem(drag.Character, token, target, tx, ty) { Tick = session.World.Tick });
    }

    /// <summary>
    /// Where an item let go at <paramref name="p"/> over a character window (<c>inventory</c> / <c>loot</c>: an equipment slot under the pointer, else the main grid; <c>bag</c> /
    /// <c>loot-bag</c>: the bag's grid) of <paramref name="grid"/> lands; <paramref name="dragged"/> is its index there (-1: from another inventory). False: no cell.
    /// </summary>
    bool DropPlace(Vector2 p, string screen, InventoryView grid, InventoryItemView item, Vector2 grab, int dragged, out string section, out int x, out int y) =>
        DropPlace(p, screen, grid, item, grab, dragged, out section, out x, out y, out _);

    /// <inheritdoc cref="DropPlace(Vector2, string, InventoryView, InventoryItemView, Vector2, int, out string, out int, out int)"/>
    /// <param name="area">The screen area it would take: its cells, or the whole slot.</param>
    bool DropPlace(Vector2 p, string screen, InventoryView grid, InventoryItemView item, Vector2 grab, int dragged, out string section, out int x, out int y, out UiRect area)
    {
        var slot = EquipmentSlotControlAt(p, screen);
        section = slot?.Name ?? (screen is "bag" or "loot-bag" ? "backpack_content" : "main");
        x = y = 0;
        area = slot?.Bounds ?? default;
        // Clothing and bags occupy one slot; weapon sections retain their grid rows.
        if (InventoryCommands.IsEquipmentSection(section) && section is not ("back" or "hip")) return true;
        var (columns, rows) = InventoryCommands.IsEquipmentSection(section) ? InventoryCommands.SectionSize(section) : section == "main" ? (grid.Width, grid.Height) : (grid.BagWidth, grid.BagHeight);
        return DropCell(p, screen, section, columns, rows, grab, item.Width, item.Height, Footprints(grid.Items, section, dragged), out x, out y, out area);
    }

    /// <summary>The equipment slot of a character window (<c>inventory</c> / <c>loot</c>) under <paramref name="p"/>, or null.</summary>
    string? EquipmentSlotAt(Vector2 p, string screen) => EquipmentSlotControlAt(p, screen)?.Name;

    UiControl? EquipmentSlotControlAt(Vector2 p, string screen) => screen is "inventory" or "loot"
        ? faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent).First(f => f.Id == screen).Controls
            .LastOrDefault(c => InventoryCommands.IsEquipmentSection(c.Name) && c.Bounds.Contains(p.X, p.Y) && (c.Clip?.Contains(p.X, p.Y) ?? true))
        : null;

    /// <summary>The cells taken in <paramref name="section"/> by items other than the dragged one (<paramref name="dragged"/>, its index; -1 none).</summary>
    static IEnumerable<(int X, int Y, int Width, int Height)> Footprints(IEnumerable<InventoryItemView> items, string section, int dragged) =>
        items.Where(i => i.Section == section && i.Index != dragged).Select(i => (i.X, i.Y, i.Width, i.Height));

    /// <summary>
    /// The cell of a grid widget (<paramref name="slotName"/> of screen <paramref name="screen"/>) an item lands in when dropped at <paramref name="p"/>, held at <paramref name="grab"/>.
    /// Forgiving: the pointer may be up to a cell outside the grid, the cell is clamped into it, and an occupied cell snaps to the nearest free one within
    /// <see cref="DropSnapCells"/> (<paramref name="occupied"/>: the other items of that grid). Without a free cell nearby the clamped cell is returned and the sim refuses it.
    /// </summary>
    bool DropCell(Vector2 p, string screen, string slotName, int columns, int rows, Vector2 grab, int width, int height, IEnumerable<(int X, int Y, int Width, int Height)> occupied, out int x, out int y) =>
        DropCell(p, screen, slotName, columns, rows, grab, width, height, occupied, out x, out y, out _);

    /// <inheritdoc cref="DropCell(Vector2, string, string, int, int, Vector2, int, int, IEnumerable{ValueTuple{int, int, int, int}}, out int, out int)"/>
    /// <param name="area">The screen area of the cells it would take.</param>
    bool DropCell(Vector2 p, string screen, string slotName, int columns, int rows, Vector2 grab, int width, int height, IEnumerable<(int X, int Y, int Width, int Height)> occupied, out int x, out int y, out UiRect area)
    {
        x = y = 0;
        area = default;
        var controls = faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent).First(f => f.Id == screen).Controls;
        var slot = controls.FirstOrDefault(c => c.Name == slotName);
        if (slot is null || columns <= 0 || rows <= 0 || width > columns || height > rows) return false;
        var b = slot.Bounds;
        float cellW = b.Width / columns, cellH = b.Height / rows;
        if (p.X < b.X - cellW || p.X > b.X + b.Width + cellW || p.Y < b.Y - cellH || p.Y > b.Y + b.Height + cellH ||
            slot.Clip is { } clip && !clip.Contains(Math.Clamp(p.X, b.X, b.X + b.Width), Math.Clamp(p.Y, b.Y, b.Y + b.Height))) return false;
        // Where the item's top-left corner is, in cells.
        float fx = (p.X - b.X) / cellW - grab.X * width, fy = (p.Y - b.Y) / cellH - grab.Y * height;
        x = Math.Clamp((int)MathF.Round(fx), 0, columns - width);
        y = Math.Clamp((int)MathF.Round(fy), 0, rows - height);
        var others = occupied.ToArray();
        bool Free(int cx, int cy) => !others.Any(o => cx < o.X + o.Width && o.X < cx + width && cy < o.Y + o.Height && o.Y < cy + height);
        if (!Free(x, y))
        {
            float best = DropSnapCells * DropSnapCells + 0.01f;
            int bx = x, by = y;
            for (int cy = 0; cy <= rows - height; cy++)
                for (int cx = 0; cx <= columns - width; cx++)
                {
                    float d = (cx - fx) * (cx - fx) + (cy - fy) * (cy - fy);
                    if (d < best && Free(cx, cy)) (best, bx, by) = (d, cx, cy);
                }
            (x, y) = (bx, by);
        }
        area = new(b.X + x * cellW, b.Y + y * cellH, width * cellW, height * cellH);
        return true;
    }

    /// <summary>How far, in cells, a drop on an occupied cell looks for a free one.</summary>
    const float DropSnapCells = 2;

    /// <summary>
    /// The screen area the item being dragged would land in if let go at <paramref name="p"/> (its grid cells or the slot), as <see cref="InventoryPointerUp"/> places it; null
    /// where it has no cell (over the world, a machine's input, a trade without a free cell).
    /// </summary>
    UiRect? DropArea(Vector2 p)
    {
        if (faithfulScreens.HitTest(p) is not { } hit) return null;
        string screen = hit.Screen;
        var trade = session.CurrentSnapshot.Trade;
        UiRect area;
        if (tradeDrag is { } b)
        {
            if (trade is null || screen is not ("inventory" or "bag") || InventoryOwner(trade.Customer)?.ItemGrid is not { } grid) return null;
            if (b.Item.Container && EquipmentSlotControlAt(p, screen) is { Name: "backpack_attach" } slot) return slot.Bounds;
            string section = screen == "bag" ? "backpack_content" : "main";
            int columns = section == "main" ? grid.Width : grid.BagWidth, rows = section == "main" ? grid.Height : grid.BagHeight;
            return DropCell(p, screen, section, columns, rows, b.Grab, b.Item.Width, b.Item.Height, Footprints(grid.Items, section, -1), out _, out _, out area) ? area : null;
        }
        if (inventoryDrag is not { } drag) return null;
        var item = drag.Item;
        if (drag.Loot)
            return screen is "inventory" or "bag" && InventoryOwner() is { ItemGrid: { } own } lead && lead.Id == session.CurrentSnapshot.LootOpener &&
                DropPlace(p, screen, own, item, drag.Grab, -1, out _, out _, out _, out area) ? area : null;
        if (IsLootScreen(screen))
            return LootBody()?.ItemGrid is { } into && DropPlace(p, screen, into, item, drag.Grab, -1, out _, out _, out _, out area) ? area : null;
        if (screen == "trader")
            return trade is not null && trade.Customer == drag.Character && TraderGrid(screen) is { } stockGrid && DropCell(p, screen, stockGrid, trade.StockWidth, trade.StockHeight,
                drag.Grab, item.Width, item.Height, trade.Stock.Select(s => (s.X, s.Y, s.Width, s.Height)), out _, out _, out area) ? area : null;
        if (screen is "bench" or "storage")
            return Machine(shownMachine)?.Storage is { } storage && DropCell(p, screen, "backpack_content", storage.Width, storage.Height, drag.Grab, item.Width, item.Height,
                storage.Items.Select(i => (i.X, i.Y, i.Width, i.Height)), out _, out _, out area) ? area : null;
        if (screen == "limbs")
            return faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent).First(f => f.Id == screen).Controls
                .LastOrDefault(c => InventoryCommands.IsLimbSection(c.Name) && c.Bounds.Contains(p.X, p.Y) && (c.Clip?.Contains(p.X, p.Y) ?? true))?.Bounds;
        return screen is "inventory" or "bag" && InventoryOwner(drag.Character)?.ItemGrid is { } mine &&
            DropPlace(p, screen, mine, item, drag.Grab, item.Index, out _, out _, out _, out area) ? area : null;
    }

    void AddInventoryDrag(List<UiControl> controls)
    {
        // White over the cells the dragged item would be let go into (vanilla's drop highlight).
        if ((inventoryDrag is not null || tradeDrag is not null) && UiVisible && DropArea(mouse) is { Width: > 0, Height: > 0 } target)
            controls.Add(new("drop-target", "Widget", target, "", "", "", true, new Dictionary<string, string>(), new(1, 1, 1, 0.35f), Pickable: false));
        if (inventoryDrag is { } drag)
            Preview(drag.Item.Record, drag.Item.Material, drag.Item.Name, drag.Item.Quantity, drag.Item.Width, drag.Item.Height, drag.Grab);
        else if (machineDrag is { } machine)
            Preview(machine.Record, "", machine.Name, machine.Quantity, machine.Width, machine.Height, machine.Grab);
        else if (tradeDrag is { } stock)
            Preview(stock.Item.Record, stock.Item.Material, stock.Item.Name, stock.Item.Quantity, stock.Item.Width, stock.Item.Height, stock.Grab);
        AddFencePrompt(controls);

        void Preview(string record, string material, string name, int quantity, int columns, int rows, Vector2 grab)
        {
            float width = columns * InventoryCell, height = rows * InventoryCell;
            AddItemCell(controls, "drag-item", new(mouse.X - grab.X * width, mouse.Y - grab.Y * height, width, height),
                record, material, name, quantity, null, "DragItem", 0, null, pickable: false);
        }
    }

    bool CancelInventoryDrag()
    {
        bool captured = inventoryDrag is not null || tradeDrag is not null || scrollDrag is not null || machineDrag is not null;
        scrollDrag = null;
        machineDrag = null;
        inventoryDrag = null;
        tradeDrag = null;
        return captured;
    }

    /// <summary>The characters whose bag was left open with OPEN BAG: their inventory shows it again when it opens or they are selected.</summary>
    readonly HashSet<Meitou.Simulation.CharacterId> bagOpen = [];

    /// <summary>OPEN BAG: opens or closes the lead character's bag and remembers it for that character.</summary>
    void ToggleBag()
    {
        if (InventoryOwner() is not { } lead) return;
        if (!bagOpen.Remove(lead.Id)) bagOpen.Add(lead.Id);
        SyncBagScreen();
    }

    /// <summary>Once a frame: the bag window is open while the inventory is, the lead character wears a bag and left it open (<see cref="bagOpen"/>).</summary>
    void SyncBagScreen()
    {
        var lead = InventoryOwner();
        bool want = faithfulScreens.IsOpen("inventory") && lead is { ItemGrid: { BagWidth: > 0, BagHeight: > 0 } } && bagOpen.Contains(lead.Id);
        if (want == faithfulScreens.IsOpen("bag")) return;
        if (!want) { faithfulScreens.Close("bag"); return; }
        faithfulScreens.Open("bag");
        if (shownTrader.IsNone) PlaceBagWindow(); else PlaceTradeWindows();
    }

    void ValidateInventoryDrag()
    {
        if (faithfulScreens.HasModal) { CancelInventoryDrag(); return; }
        SyncBagScreen();
        if (inventoryDrag is { } drag)
        {
            var owner = drag.Loot ? LootBody() : InventoryOwner();
            string window = drag.Item.Section == "backpack_content" ? drag.Loot ? "loot-bag" : "bag" : drag.Loot ? "loot" : "inventory";
            if (!faithfulScreens.IsOpen(window) || owner?.Id != drag.Character || owner.ItemGrid?.Items.FirstOrDefault(i => i.Index == drag.Item.Index) != drag.Item) inventoryDrag = null;
        }
        if (tradeDrag is { } stock && (!faithfulScreens.IsOpen("trader") ||
            session.CurrentSnapshot.Trade?.Stock.FirstOrDefault(i => i.Index == stock.Item.Index) != stock.Item)) tradeDrag = null;
    }
}
