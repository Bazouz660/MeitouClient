namespace Meitou.Simulation.Items;

/// <summary>Snapshot identity prevents a drag started before eating/looting from moving a different item.</summary>
public sealed record InventoryItemToken(int Index, string Record, string Section, int X, int Y, int Quantity, float Quality);
public sealed record MoveInventoryItem(CharacterId Character, InventoryItemToken Item, string Section, int X, int Y) : SimCommand;
public sealed record ArrangeInventory(CharacterId Character) : SimCommand;
/// <summary>Drops the character's item on the ground at its feet (an item dragged out of the inventory window onto the world); a locked-on item or a fitted limb stays.</summary>
public sealed record DropInventoryItem(CharacterId Character, InventoryItemToken Item) : SimCommand;

public static class InventoryCommands
{
    public static bool IsEquipmentSection(string section) => section is "back" or "hip" or "shirt" or "head" or "legs" or "boots" or "armour" or "belt" or "backpack_attach";
    public static bool IsMovableSection(string section) => section is "main" or "backpack_content" || IsEquipmentSection(section);
    /// <summary>A fitted prosthetic's section (<see cref="Meitou.Data.Gameplay.Bodies.LimbReplacement.SlotOf"/>): the limbs window's slots; not traded, not saved with the inventory.</summary>
    public static bool IsLimbSection(string section) => LimbSlotOf(section) >= 0;
    /// <summary>The limb slot (0 left arm, 1 right arm, 2 left leg, 3 right leg) of a prosthetic section; -1 for other sections.</summary>
    public static int LimbSlotOf(string section) => section switch { "left_arm_replace" => 0, "right_arm_replace" => 1, "left_leg_replace" => 2, "right_leg_replace" => 3, _ => -1 };

    // Engine slot capacities, in inventory cells. Clothing and backpack slots hold one item.
    public static (int Width, int Height) SectionSize(string section) => section switch
    {
        "back" => (10, 2), "hip" => (7, 1), "backpack_attach" => (5, 4),
        "shirt" or "boots" => (4, 2), "head" => (4, 3), "legs" => (4, 5),
        "armour" => (4, 6), "belt" => (2, 2), _ => (0, 0),
    };

    /// <summary>
    /// Puts one unit into the equipment slot that takes it when that is free (the trade window's right click, <c>FUN_140713d20</c>: sections that accept the item's slot type are tried before
    /// the general grids): weapons the first free cell of <c>back</c>, then <c>hip</c>; armour its <see cref="ItemInstance.EquipmentSection"/> when nothing is worn there. The order of two
    /// matching sections is <b>Unknown</b>; trade transfers bags whole with <see cref="Transfer"/> instead of copying them. False: no slot, the caller places it in a grid.
    /// </summary>
    public static bool TryEquip(Inventory root, ItemInstance item)
    {
        if (item.Quantity != 1 || item.Width <= 0 || item.Height <= 0) return false;
        string[] sections = item.Type is Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW ? ["back", "hip"]
            : item.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.ITEM && IsEquipmentSection(item.EquipmentSection) && item.EquipmentSection is not ("back" or "hip" or "backpack_attach") ? [item.EquipmentSection] : [];
        foreach (string section in sections)
        {
            int x = 0, y = 0;
            if (section is "back" or "hip")
            {
                var size = SectionSize(section);
                var cell = Enumerable.Range(0, Math.Max(0, size.Height - item.Height + 1))
                    .SelectMany(cy => Enumerable.Range(0, Math.Max(0, size.Width - item.Width + 1)).Select(cx => (X: cx, Y: cy)))
                    .Cast<(int X, int Y)?>().FirstOrDefault(c => root.IsFree(section, c!.Value.X, c.Value.Y, item.Width, item.Height));
                if (cell is not { } free) continue;
                (x, y) = free;
            }
            else if (root.Items.Any(i => i.Section == section)) continue;
            item.Section = section;
            item.X = x;
            item.Y = y;
            root.Items.Add(item);
            return true;
        }
        return false;
    }

    public static bool Move(Inventory root, InventoryItemToken token, string section, int x, int y) => Transfer(root, token, root, section, x, y, limbs: true);

    /// <summary>
    /// Moves at most <paramref name="count"/> units (0: all) of <paramref name="item"/>, held directly by <paramref name="owner"/>, into <paramref name="into"/> where they first fit
    /// (<see cref="Inventory.Add"/>: stacks of it, then free cells); what does not fit stays where it was. Returns the units moved. A right click between two open windows moves one unit this
    /// way, with Shift the stack (docs/game/ui-input.md, the input tables).
    /// </summary>
    public static int MoveUnits(Inventory owner, ItemInstance item, int count, Inventory into)
    {
        if (count <= 0 || count >= item.Quantity)
        {
            int before = item.Quantity, at = owner.Items.IndexOf(item);
            if (at < 0) return 0;
            owner.Items.RemoveAt(at);
            bool death = item.Death;
            item.Death = false;   // leaving its owner, it is no longer a death item
            if (into.Add(item)) return before;
            item.Death = death;
            if (item.Quantity > 0) owner.Items.Insert(at, item);
            return before - item.Quantity;
        }
        var unit = item.WithQuantity(count);
        unit.Death = false;
        item.Quantity -= count;
        if (into.Add(unit)) return count;
        item.Quantity += unit.Quantity;
        return count - unit.Quantity;
    }

    /// <summary>
    /// Moves the item <paramref name="token"/> names in <paramref name="from"/> to <paramref name="section"/> at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="into"/>: the same
    /// inventory (<see cref="Move"/>) or another character's (the loot window), by the same rules. Prosthetic sections only with <paramref name="limbs"/>. False, nothing moved: the token is
    /// stale or the place does not take the item.
    /// </summary>
    public static bool Transfer(Inventory from, InventoryItemToken token, Inventory into, string section, int x, int y, bool limbs = false)
    {
        var item = from.All().ElementAtOrDefault(token.Index);
        bool Movable(string s) => IsMovableSection(s) || limbs && IsLimbSection(s);
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y || item.Quantity != token.Quantity || item.Quality != token.Quality ||
            !Movable(item.Section) || !Movable(section)) return false;
        if (item.LockedOn && (section != item.Section || into != from)) return false;   // worn locked shackles stay on
        var owner = from.Items.Contains(item) ? from : from.Backpack?.Contents;
        var target = section == "backpack_content" ? into.Backpack?.Contents : into;
        // A bag cannot contain itself or another container; moving it preserves all its contents.
        if (section == "backpack_content" && item.Contents is not null) return false;
        var size = section is "back" or "hip" ? SectionSize(section) : IsEquipmentSection(section) || IsLimbSection(section) ? (item.Width, item.Height) : (target?.Width ?? 0, target?.Height ?? 0);
        if (owner is null || target is null || !owner.Items.Contains(item) || x < 0 || y < 0 || item.Width <= 0 || item.Height <= 0 ||
            x > size.Item1 - item.Width || y > size.Item2 - item.Height || item.Quantity > target.LimitOf(item)) return false;
        if (IsEquipmentSection(section))
        {
            bool allowed = section switch
            {
                "back" or "hip" => item.Type is Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW,
                "backpack_attach" => item.Type == Meitou.Data.Fcs.FcsRecordType.CONTAINER && item.Contents is not null,
                _ => item.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.ITEM && item.EquipmentSection == section,
            };
            if (!allowed || item.Quantity != 1) return false;
            if (section is not ("back" or "hip") && (x != 0 || y != 0 || target.Items.Any(i => i != item && i.Section == section))) return false;
        }
        // A prosthetic slot holds one LIMB_REPLACEMENT; whether it fits the limb is the caller's check (PlayerState).
        if (IsLimbSection(section) && (item.Type != Meitou.Data.Fcs.FcsRecordType.LIMB_REPLACEMENT || item.Quantity != 1 || x != 0 || y != 0 || target.Items.Any(i => i != item && i.Section == section))) return false;
        foreach (var other in target.Items)
            if (other != item && other.Section == section && x < other.X + other.Width && other.X < x + item.Width && y < other.Y + other.Height && other.Y < y + item.Height) return false;
        if (owner != target) { owner.Items.Remove(item); target.Items.Add(item); }
        if (into != from) item.Death = false;
        item.Section = section; item.X = x; item.Y = y; return true;
    }
    public static bool Arrange(Inventory root)
    {
        var saved = root.All().Select(i => (Item: i, i.X, i.Y)).ToArray();
        foreach (var (grid, section) in new[] { (root, "main"), (root.Backpack?.Contents, "backpack_content") })
        {
            if (grid is null) continue;
            var items = grid.Items.Where(i => i.Section == section).OrderByDescending(i => i.Width * (long)i.Height).ToArray();
            foreach (var item in items) { item.X = -item.Width; item.Y = -item.Height; }
            foreach (var item in items)
            {
                if (item.Width <= 0 || item.Height <= 0 || !grid.FindCell(section, item.Width, item.Height, out int x, out int y))
                {
                    foreach (var old in saved) { old.Item.X = old.X; old.Item.Y = old.Y; } return false;
                }
                item.X = x; item.Y = y;
            }
        }
        return true;
    }
}
