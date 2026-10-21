using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of the player's buildings in a save (docs/simulation.md "Production"): the production and farm keys of each building's GAMESTATE_BUILDING (docs/game/buildings-production.md
/// "Production", "Farming" and "Power": <c>production amount</c>, <c>resource mult</c>, <c>power on</c>, <c>gets battery</c>, <c>batterycharge</c>, <c>consumption amount-&lt;k&gt;</c>, <c>plant growth</c>, <c>plant death</c>, <c>plant start</c>, <c>plants harvested</c>,
/// <c>save time</c>), and its items in an INVENTORY_STATE named first by the building's instance states (docs/formats/save.md "Zone files"). The items' sections are <c>in&lt;k&gt;</c>, <c>out</c>
/// and, for the storage grid, <c>main</c> (the original's storage section name is <b>Unknown</b>; no sample save holds a player building with items, so the original reading these is unverified).
/// A loaded farm's plants all take the farm's growth (the harvested ones -10): the stagger between plants is not saved.
/// </summary>
public static class MachineSave
{
    const string StateSuffix = "-S23";
    const string InventorySuffix = "-inventory";

    /// <summary>Writes every machine into its building's state (after <see cref="BuildingSave.Write"/>).</summary>
    public static void Write(SaveGame game, World world)
    {
        if (world.Machines.Count == 0) return;
        foreach (var m in world.Machines.All)
        {
            if (world.Buildings.Find(m.Site) is not { } site || !game.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList is not { } list) continue;
            var file = zone.File;
            var state = file.Records.FirstOrDefault(r => r.StringId == site.Id + StateSuffix && r.RecordType == FcsRecordType.GAMESTATE_BUILDING);
            var instance = list.Instances.FirstOrDefault(i => i.Id == site.Id);
            if (state is null || instance is null) continue;
            WriteKeys(state, m, world.GameHours);
            var saveItems = Items(m);
            string inventoryId = site.Id + InventorySuffix;
            var inventory = file.Records.FirstOrDefault(r => r.StringId == inventoryId && r.RecordType == FcsRecordType.INVENTORY_STATE);
            instance.States.Remove(inventoryId);
            if (saveItems.Count == 0)
            {
                if (inventory is not null) SaveInventory.Replace(file, inventory, [], n => $"{n}-zone{site.Zone.X}.{site.Zone.Y}-INGAME");
                if (inventory is not null) file.Records.Remove(inventory);
                continue;
            }
            if (inventory is null)
            {
                inventory = new FcsRecord { Type = (int)FcsRecordType.INVENTORY_STATE, Id = SaveBuilder.NextNumber(file), Name = "0", StringId = inventoryId };
                file.Records.Add(inventory);
            }
            SaveInventory.Replace(file, inventory, saveItems, n => string.Create(CultureInfo.InvariantCulture, $"{n}-zone{site.Zone.X}.{site.Zone.Y}-INGAME"));
            instance.States.Insert(0, inventoryId);
        }
    }

    static void WriteKeys(FcsRecord state, Machine m, double gameHours)
    {
        state.Floats["resource mult"] = m.Yield;
        // The power keys (Observed, vfunc_17/18 of the building classes FUN_1402a14b0 / FUN_1402a1750).
        state.Bools["power on"] = m.PowerOn;
        state.Bools["gets battery"] = m.GetsBattery;
        if (m.Data.IsBattery) state.Floats["batterycharge"] = m.Charge;
        if (m.Output is { } o) state.Floats["production amount"] = o.Progress;
        for (int k = 0; k < m.Inputs.Length; k++) state.Floats["consumption amount-" + k.ToString(CultureInfo.InvariantCulture)] = m.Inputs[k].Level;
        if (m.Craft is { } c) WriteCraft(state, c);
        if (m.Farm is not { } f) return;
        state.Floats["plant growth"] = f.Growth;
        state.Floats["plant death"] = f.Death;
        state.Floats["plant start"] = f.Start;
        state.Floats["save time"] = (float)gameHours;
        state.Ints["plants harvested"] = f.Harvested;
    }

    /// <summary>The crafting keys (<b>Observed</b>, <c>CraftingBuilding::vfunc_17</c>): <c>num crafts</c> (a float), <c>repeat</c>, and per queued craft k <c>current&lt;k&gt;</c> (the item),
    /// <c>currentExtraData&lt;k&gt;</c> (a weapon's model, else empty), <c>current prog&lt;k&gt;</c> and <c>crit&lt;k&gt;</c>. The original's partly drawn items (<c>craft partial item ...</c>) are not written.</summary>
    static void WriteCraft(FcsRecord state, CraftState c)
    {
        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith("current", StringComparison.Ordinal)).ToList()) state.Strings.Remove(key);
        foreach (var key in state.Floats.Keys.Where(k => k.StartsWith("current prog", StringComparison.Ordinal)).ToList()) state.Floats.Remove(key);
        foreach (var key in state.Ints.Keys.Where(k => k.StartsWith("crit", StringComparison.Ordinal)).ToList()) state.Ints.Remove(key);
        state.Floats["num crafts"] = c.Queue.Count;
        state.Bools["repeat"] = c.Repeat;
        for (int k = 0; k < c.Queue.Count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            state.Strings["current" + n] = c.Queue[k].Option.Item;
            state.Strings["currentExtraData" + n] = c.Queue[k].Option.Model ?? "";
            state.Floats["current prog" + n] = c.Queue[k].Progress;
            state.Ints["crit" + n] = c.Queue[k].Critical;
        }
    }

    static List<SaveItem> Items(Machine m)
    {
        var list = new List<SaveItem>();
        for (int k = 0; k < m.Inputs.Length; k++)
            if (m.Inputs[k].Count > 0) list.Add(new SaveItem { BaseId = m.Inputs[k].Item, Section = "in" + k.ToString(CultureInfo.InvariantCulture), Quantity = m.Inputs[k].Count });
        if (m.Output is { Count: > 0 } o) list.Add(new SaveItem { BaseId = o.Recipe.Item, Section = "out", Quantity = o.Count });
        if (m.Craft is { } c) list.AddRange(c.Done.Select(SavedKit.FromItem));
        if (m.Storage is { } s) list.AddRange(SavedKit.FromInventory(s));
        return list;
    }

    /// <summary>Reads the machines of the complete player buildings the world holds (after <see cref="BuildingSave.Read"/>).</summary>
    public static void Read(SaveGame save, World world, ProductionSystem production, ItemFactory factory)
    {
        var states = HeldSave.BuildingStates(save);
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Owner != world.Player.Faction || production.Data(site.Record) is null) continue;
            if (production.Ensure(world, site) is not { } m) continue;
            if (states.TryGetValue(BuildingKey.Of(site.Zone, site.Id), out var state))
            {
                ReadKeys(state, m);
                if (m.Craft is not null) ReadCraft(state, m, production);
            }
            if (!save.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList?.Instances.FirstOrDefault(i => i.Id == site.Id) is not { } instance) continue;
            var inventory = instance.States.Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.INVENTORY_STATE)).FirstOrDefault(r => r is not null);
            if (inventory is null) continue;
            var storage = new List<SaveItem>();
            foreach (var item in SaveInventory.Read(zone.File, inventory))
            {
                if (item.Section == "out" && m.Craft is { } craft) { if (SavedKit.ItemOf(item, factory) is { } done) craft.Done.Add(done); }
                else if (item.Section == "out" && m.Output is { } o && o.Recipe.Item == item.BaseId) o.Count = Math.Min(o.Recipe.Capacity, o.Count + item.Quantity);
                else if (item.Section.StartsWith("in", StringComparison.Ordinal) && int.TryParse(item.Section.AsSpan(2), CultureInfo.InvariantCulture, out int k) && k < m.Inputs.Length
                    && (m.Inputs[k].Item == item.BaseId || m.Craft is not null && m.Inputs[k].Count == 0))
                {
                    // A crafting bench's sections hold its last craft's ingredients, also with nothing queued.
                    m.Inputs[k].Item = item.BaseId;
                    m.Inputs[k].Count = Math.Min(m.Inputs[k].Recipe.Capacity, m.Inputs[k].Count + item.Quantity);
                }
                else storage.Add(item);
            }
            if (m.Storage is { } grid && storage.Count > 0)
                foreach (var item in SavedKit.ToInventory(storage, factory).Items) grid.Items.Add(item);
        }
        world.Machines.Touch();
    }

    static void ReadCraft(FcsRecord state, Machine m, ProductionSystem production)
    {
        m.Craft!.Repeat = state.Bools.GetValueOrDefault("repeat");
        int count = (int)state.Floats.GetValueOrDefault("num crafts");
        for (int k = 0; k < count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            if (!state.Strings.TryGetValue("current" + n, out var item)) continue;
            string? model = state.Strings.GetValueOrDefault("currentExtraData" + n) is { Length: > 0 } x ? x : null;
            production.RestoreCraft(m, item, model, state.Floats.GetValueOrDefault("current prog" + n), (byte)Math.Clamp(state.Ints.GetValueOrDefault("crit" + n, 2), 0, 2));
        }
    }

    static void ReadKeys(FcsRecord state, Machine m)
    {
        if (state.Floats.TryGetValue("resource mult", out float y)) m.Yield = y;
        m.PowerOn = state.Bools.GetValueOrDefault("power on", true);
        m.GetsBattery = state.Bools.GetValueOrDefault("gets battery", true);
        if (m.Data.IsBattery) m.Charge = Math.Max(0, state.Floats.GetValueOrDefault("batterycharge"));
        if (m.Output is { } o) o.Progress = state.Floats.GetValueOrDefault("production amount");
        for (int k = 0; k < m.Inputs.Length; k++) m.Inputs[k].Level = state.Floats.GetValueOrDefault("consumption amount-" + k.ToString(CultureInfo.InvariantCulture));
        if (m.Farm is not { } f || !state.Floats.ContainsKey("plant growth")) return;
        f.Growth = state.Floats.GetValueOrDefault("plant growth");
        f.Death = state.Floats.GetValueOrDefault("plant death");
        f.Start = state.Floats.GetValueOrDefault("plant start");
        f.Harvested = Math.Clamp(state.Ints.GetValueOrDefault("plants harvested"), 0, f.Plants.Length);
        for (int i = 0; i < f.Plants.Length; i++) f.Plants[i] = i < f.Harvested ? -10 : f.Growth;
    }
}
