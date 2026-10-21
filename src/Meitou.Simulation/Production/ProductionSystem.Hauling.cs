using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

// Hauling (docs/simulation.md "Production"; the original's Task_EmptyMachine / Task_FillMachine in docs/game/buildings-production.md "Hauling"): with Settings.AutoHaul an operator at its
// machine fills empty inputs with what it carries, takes a full output, carries the output to the nearest storage that takes it and fetches an empty input's item from the nearest storage
// holding it. The trip is CharacterCold.HaulSite / HaulFetch; Schedule walks there, and the operator walks back to its machine once the items have moved.
public sealed partial class ProductionSystem
{
    /// <summary>The storage a hauling operator walks to; a storage that is gone ends the trip.</summary>
    (BuildSite Site, Machine Machine)? Hauling(World world, CharacterCold cold)
    {
        if (cold.HaulSite is not { } id) return null;
        if (Site(world, id) is { Complete: true } site && world.Machines.Find(id) is { Storage: not null } m) return (site, m);
        cold.HaulSite = null;
        cold.HaulFetch = null;
        return null;
    }

    /// <summary>What an operator at its machine does before working; true when it set off for a storage.</summary>
    bool StartHaul(World world, CharacterCold cold, BuildSite site, Machine m)
    {
        if (cold.Inventory is not { } inv) return false;
        bool changed = false;
        foreach (var input in m.Inputs)
        {
            int room = input.Recipe.Capacity - input.Count;
            if (input.Rate <= 0 || room <= 0) continue;
            foreach (var item in Carried(inv, input.Item).ToList())
            {
                int n = Math.Min(room, item.Quantity);
                input.Count += n;
                room -= n;
                item.Quantity -= n;
                if (item.Quantity == 0) inv.Remove(item);
                changed = true;
                if (room == 0) break;
            }
        }
        // A full output is taken only when some storage takes it; without one the machine stays full.
        if (m.Output is { Full: true } output && items.Create(output.Recipe.Item, output.Count) is { } made &&
            NearestStorage(world, site, s => s.Data.Accepts(made.Record, made.Type) && Fits(s.Storage!, made)) is not null)
        {
            int moved = Placed(inv, made);
            if (moved > 0) { output.Count -= moved; changed = true; }
        }
        if (changed) world.Machines.Touch();
        if (m.Output is { } o && Carried(inv, o.Recipe.Item).FirstOrDefault() is { } carried &&
            NearestStorage(world, site, s => s.Data.Accepts(carried.Record, carried.Type) && Fits(s.Storage!, carried)) is { } into)
            return Go(cold, into, null);
        if (m.Craft is { Queue.Count: 0 }) return false;
        foreach (var input in m.Inputs)
            if (input.Rate > 0 && input.Count == 0 && NearestStorage(world, site, s => s.Storage!.Items.Any(i => i.Record == input.Item && i.Quantity > 0 && i.Section == "main")) is { } from)
                return Go(cold, from, input.Item);
        return false;
    }

    static bool Go(CharacterCold cold, string storage, string? fetch)
    {
        cold.HaulSite = storage;
        cold.HaulFetch = fetch;
        return true;
    }

    /// <summary>At the storage: puts the carried output in, or takes what the machine's empty inputs hold room for; then the trip ends.</summary>
    void HaulAt(World world, CharacterCold cold, Machine machine, Machine store)
    {
        var inv = cold.Inventory;
        var storage = store.Storage!;
        if (inv is not null && cold.HaulFetch is { } want)
        {
            int room = 0;
            foreach (var input in machine.Inputs) if (input.Item == want) room += Math.Max(0, input.Recipe.Capacity - input.Count);
            foreach (var item in storage.Items.Where(i => i.Record == want && i.Section == "main").ToList())
            {
                if (room <= 0) break;
                int n = Math.Min(room, item.Quantity);
                var moving = n == item.Quantity ? item : item.WithQuantity(n);
                if (ReferenceEquals(moving, item)) storage.Items.Remove(item);
                else item.Quantity -= n;
                int placed = Placed(inv, moving);
                // What the character has no room for goes back.
                if (placed < n) storage.Add(moving);
                room -= placed;
                if (placed == 0) break;
            }
        }
        else if (inv is not null && machine.Output is { } output)
        {
            foreach (var item in Carried(inv, output.Recipe.Item).ToList())
            {
                if (!store.Data.Accepts(item.Record, item.Type)) break;
                inv.Remove(item);
                if (storage.Add(item)) continue;
                // What the storage has no room for stays with the character.
                if (!inv.Add(item)) world.GroundItems.Drop(item, store.Position, 0);
                break;
            }
        }
        cold.HaulSite = null;
        cold.HaulFetch = null;
        world.Machines.Touch();
    }

    /// <summary>Puts an item into an inventory; the units placed (<see cref="Inventory.Add"/> leaves the quantity as it was when all fit).</summary>
    internal static int Placed(Inventory into, ItemInstance item)
    {
        int before = item.Quantity;
        return into.Add(item) ? before : before - item.Quantity;
    }

    /// <summary>The loose stacks of an ITEM in a character's main grid and backpack.</summary>
    static IEnumerable<ItemInstance> Carried(Inventory inv, string record) =>
        inv.All().Where(i => i.Record == record && i.Section is "main" or "backpack_content" && !i.LockedOn && i.Contents is null);

    /// <summary>Whether some of an item fits: a stack of it with room, or a free cell.</summary>
    static bool Fits(Inventory storage, ItemInstance item)
    {
        int limit = storage.LimitOf(item);
        foreach (var other in storage.Items)
            if (other.Record == item.Record && other.Quality == item.Quality && other.StolenFrom == item.StolenFrom && other.Section == "main" && other.Quantity < limit) return true;
        return storage.FindCell("main", item.Width, item.Height, out _, out _);
    }

    /// <summary>The player's nearest complete storage within <see cref="ProductionSettings.HaulRange"/> of a machine that passes <paramref name="ok"/> (ties: the lower site id).</summary>
    string? NearestStorage(World world, BuildSite from, Func<Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (!s.Data.IsStorage || s.Storage is null || s.Site == from.Id || Site(world, s.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            float gap = Gap(site.Position, from.Position);
            if (gap > Settings.HaulRange || gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0) || !ok(s)) continue;
            best = s.Site;
            bestGap = gap;
        }
        return best;
    }
}
