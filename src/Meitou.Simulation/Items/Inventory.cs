using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// A character's inventory: its items in sections, with their grid cells. The main section is a grid of <see cref="MainWidth"/> x <see cref="MainHeight"/> cells (the
/// size observed in the vanilla inventory screenshot, docs/game/ui-screens.md); a backpack's contents are an inventory of the CONTAINER's storage size. Only serial phases change it.
/// </summary>
public sealed class Inventory(int width, int height)
{
    public const int MainWidth = 8, MainHeight = 10;

    public Inventory() : this(MainWidth, MainHeight) { }

    public int Width { get; } = width;
    public int Height { get; } = height;
    public List<ItemInstance> Items { get; } = [];

    /// <summary>The worn backpack, if any.</summary>
    public ItemInstance? Backpack => Items.Find(i => i.Section == "backpack_attach");

    /// <summary>The weight of everything in this inventory (not including a carried person or fitted prosthetics, which vanilla keeps outside the inventory).</summary>
    public float ContentWeight()
    {
        float w = 0;
        foreach (var i in Items) if (!InventoryCommands.IsLimbSection(i.Section)) w += i.Weight;
        return w;
    }

    /// <summary>First cell where a w x h footprint fits among the items of <paramref name="section"/>, or false.</summary>
    public bool FindCell(string section, int w, int h, out int x, out int y)
    {
        for (y = 0; y + h <= Height; y++)
            for (x = 0; x + w <= Width; x++)
                if (IsFree(section, x, y, w, h)) return true;
        x = y = 0;
        return false;
    }

    /// <summary>Whether a w x h footprint at (x, y) of <paramref name="section"/> overlaps no item (bounds are the caller's to check).</summary>
    public bool IsFree(string section, int x, int y, int w, int h)
    {
        foreach (var i in Items)
        {
            if (i.Section != section) continue;
            if (x < i.X + i.Width && i.X < x + w && y < i.Y + i.Height && i.Y < y + h) return false;
        }
        return true;
    }

    /// <summary>A backpack's contents: the CONTAINER's <c>stackable bonus minimum</c> and <c>stackable bonus mult</c> (1 and 1 for a character's own grid), see <see cref="LimitOf"/>.</summary>
    public int StackBonusMinimum { get; init; } = 1;
    public float StackBonusMult { get; init; } = 1;

    /// <summary>
    /// How many units of <paramref name="item"/> one cell of this inventory holds: its <see cref="ItemInstance.StackLimit"/>, with this inventory's bonus applied
    /// (<see cref="ItemInfo.StackLimitIn"/>; a bag with mult 5 holds five of an item that does not stack elsewhere, <b>Observed</b>); 1 for the types that never stack.
    /// </summary>
    public int LimitOf(ItemInstance item)
    {
        if (item.Type is FcsRecordType.WEAPON or FcsRecordType.ARMOUR or FcsRecordType.CONTAINER or FcsRecordType.MAP_ITEM or FcsRecordType.CROSSBOW or FcsRecordType.LIMB_REPLACEMENT)
            return 1;
        if (StackBonusMinimum == 1 && StackBonusMult == 1) return Math.Max(item.StackLimit, 1);
        return ItemInfo.StackLimitIn(item.StackLimit, StackBonusMinimum, StackBonusMult);
    }

    /// <summary>
    /// Puts an item in, honouring the stack limit of each inventory (<see cref="LimitOf"/>): first it tops up stacks of the same record and quality in the main grid, then in the
    /// backpack; what is left takes the first free cells of <c>main</c>, then of the backpack, at most a full stack per cell (the item itself goes into the last cell, split-off
    /// copies into the others). True when every unit found a place; otherwise what did not fit stays in <paramref name="item"/>'s <see cref="ItemInstance.Quantity"/> (the caller
    /// keeps or drops it) and the units placed stay placed.
    /// </summary>
    public bool Add(ItemInstance item)
    {
        var bag = Backpack?.Contents;
        if (TopUp(this, item) || (bag is not null && TopUp(bag, item))) return true;
        if (Place(this, "main", item)) return true;
        return bag is not null && Place(bag, "backpack_content", item);
    }

    /// <summary>Moves units of <paramref name="item"/> into stacks of <paramref name="into"/> with room left; true when none are left.</summary>
    static bool TopUp(Inventory into, ItemInstance item)
    {
        int limit = into.LimitOf(item);
        if (limit <= 1) return false;
        foreach (var other in into.Items)
        {
            if (ReferenceEquals(other, item) || other.Record != item.Record || other.Quality != item.Quality || other.StolenFrom != item.StolenFrom || other.Death != item.Death || other.Contents is not null || other.Section is not ("main" or "backpack_content")) continue;
            int move = Math.Min(limit - other.Quantity, item.Quantity);
            if (move <= 0) continue;
            other.Quantity += move;
            item.Quantity -= move;
            if (item.Quantity == 0) return true;
        }
        return false;
    }

    /// <summary>Puts the units of <paramref name="item"/> into free cells of <paramref name="into"/>, a full stack per cell; true when none are left.</summary>
    static bool Place(Inventory into, string section, ItemInstance item)
    {
        int limit = into.LimitOf(item);
        while (into.FindCell(section, item.Width, item.Height, out int x, out int y))
        {
            var unit = item.Quantity <= limit ? item : item.WithQuantity(limit);
            unit.Section = section;
            unit.X = x;
            unit.Y = y;
            into.Items.Add(unit);
            if (ReferenceEquals(unit, item)) return true;
            item.Quantity -= limit;
        }
        return false;
    }

    /// <summary>Every item of this inventory and of the backpack, in order.</summary>
    public IEnumerable<ItemInstance> All()
    {
        foreach (var i in Items)
        {
            yield return i;
            if (i.Contents is { } bag) foreach (var inner in bag.All()) yield return inner;
        }
    }

    /// <summary>Removes one unit of an item (the whole item when it is the last), wherever it is.</summary>
    public bool RemoveOne(ItemInstance item)
    {
        if (Items.Contains(item))
        {
            if (item.Quantity > 1) item.Quantity--;
            else Items.Remove(item);
            return true;
        }
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.RemoveOne(item)) return true;
        return false;
    }

    /// <summary>Removes an item whole (every unit of its stack), wherever it is (this inventory or the backpack).</summary>
    public bool Remove(ItemInstance item)
    {
        if (Items.Remove(item)) return true;
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.Remove(item)) return true;
        return false;
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Items.Count);
        foreach (var i in Items)
        {
            h.Add(Rng.StableHash(i.Record));
            h.Add(Rng.StableHash(i.Section));
            h.Add(i.X);
            h.Add(i.Y);
            h.Add(i.Quantity);
            h.Add(i.Level);
            h.Add(i.Quality);
            h.Add(i.Charges);
            i.Contents?.Hash(ref h);
        }
    }
}
