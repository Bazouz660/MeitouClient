using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>Makes item instances and the inventories of new characters from the game data.</summary>
public sealed class ItemFactory(GameDatabase db, GameConstants constants)
{
    readonly ConcurrentDictionary<string, ItemInfo?> infos = new();

    public GameDatabase Db { get; } = db;
    public GameConstants Constants { get; } = constants;

    public ItemInfo? Info(string recordId) => infos.GetOrAdd(recordId, id => Db.Find(id) is { } r ? ItemInfo.From(r) : null);

    readonly ConcurrentDictionary<string, Meitou.Data.Gameplay.Bodies.LimbReplacement?> limbs = new(StringComparer.Ordinal);
    /// <summary>The prosthetic data of a LIMB_REPLACEMENT record (null for another record).</summary>
    public Meitou.Data.Gameplay.Bodies.LimbReplacement? Limb(string recordId) =>
        limbs.GetOrAdd(recordId, id => Db.Find(id) is { Type: FcsRecordType.LIMB_REPLACEMENT } r ? Meitou.Data.Gameplay.Bodies.LimbReplacement.From(r) : null);

    /// <summary>The cheapest food in the data (lowest <c>value</c> per nutrition point, ties by id): the ration an NPC without food is given.</summary>
    public ItemInstance? CheapestFood() => Create(CheapestFoodId() ?? "");

    readonly Lazy<string?> cheapestFoodId = new(() => db.OfType(FcsRecordType.ITEM).Select(ItemInfo.From).Where(i => i.IsFood && i.Charges > 0)
        .OrderBy(i => i.Value / i.Charges).ThenBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault()?.Id);   // the factory is shared with the build thread, hence Lazy

    string? CheapestFoodId() => cheapestFoodId.Value;

    /// <summary>A new instance of an item record (null for an unknown record). Food gets its charges scaled by <c>food quality mult</c>; weapons weigh <c>weapon inventory weight mult</c> of their weight.</summary>
    public ItemInstance? Create(string recordId, int quantity = 1, float quality = 0)
    {
        if (Info(recordId) is not { } info) return null;
        bool weapon = info.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW;
        return new ItemInstance
        {
            Record = info.Id,
            Name = info.Name,
            Type = info.Type,
            EquipmentSection = info.Type is FcsRecordType.ARMOUR or FcsRecordType.ITEM && Db.Find(info.Id) is { } worn ? SectionOf(worn) : "",
            IsLocked = info.Type == FcsRecordType.ARMOUR && Db.Find(info.Id) is { } locked && locked.GetBool("is locked"),
            Lock = info.Type == FcsRecordType.ARMOUR && Db.Find(info.Id) is { } shackles && shackles.GetBool("is locked")
                ? new Locks.LockState { Level = Math.Clamp(shackles.Ints.GetValueOrDefault("lock level"), 0, 100), Hardness = shackles.Floats.GetValueOrDefault("hardness"), Locked = true }
                : null,
            Quantity = Math.Max(quantity, 1),
            Quality = quality,
            ItemFunction = info.Function,
            Charges = info.IsFood ? info.Charges * Constants.FoodQualityMult : info.Charges,
            MaxCharges = info.IsFood ? info.Charges * Constants.FoodQualityMult : info.Charges,
            Width = info.Width,
            Height = info.Height,
            UnitWeight = weapon ? info.Weight * Constants.WeaponInventoryWeightMult : info.Weight,
            StackLimit = info.StackLimit,
            IsFood = info.IsFood,
            Contents = info.Type == FcsRecordType.CONTAINER
                ? new Inventory(Math.Max(info.StorageWidth, 1), Math.Max(info.StorageHeight, 1)) { StackBonusMinimum = info.StackBonusMinimum, StackBonusMult = info.StackBonusMult }
                : null,
            ContentsFactor = info.EncumbranceEffect,
        };
    }

    /// <summary>
    /// The main grid of a character of <paramref name="record"/>: an ANIMAL_CHARACTER's <c>inventory w</c> x <c>inventory h</c> (Skimmer and Garru 4 x 8, Beak Thing 8 x 8; <b>Verified</b>
    /// by probe), a person's <see cref="Inventory.MainWidth"/> x <see cref="Inventory.MainHeight"/>.
    /// </summary>
    public static (int Width, int Height) MainSize(GameRecord? record) =>
        record is { Type: Meitou.Data.Fcs.FcsRecordType.ANIMAL_CHARACTER } animal && animal.GetInt("inventory w") > 0 && animal.GetInt("inventory h") > 0
            ? (animal.GetInt("inventory w"), animal.GetInt("inventory h"))
            : (Inventory.MainWidth, Inventory.MainHeight);

    /// <summary>
    /// The inventory of a new character: what the generator rolled to wear and carry (clothing in its slot's section, a backpack, the crossbow and weapons at hip/back), then the CHARACTER
    /// record's <c>inventory</c> list (an ITEM reference whose first value is the quantity; entries with 0 never spawn, <b>Observed</b>), and last its <c>death items</c> the same way,
    /// marked <see cref="ItemInstance.Death"/> (docs/game/animals.md "Death items").
    /// </summary>
    public Inventory Build(GameRecord? character, Meitou.Data.Characters.Loadout? loadout)
    {
        var (width, height) = MainSize(character);
        var inventory = new Inventory(width, height);
        if (loadout is not null)
        {
            foreach (var c in loadout.Clothing)
                if (Create(c.Record.StringId, 1, c.Quality) is { } cloth)
                {
                    cloth.Section = SectionOf(c.Record);
                    cloth.MaterialId = c.Material?.StringId ?? "";
                    inventory.Items.Add(cloth);
                }
            if (loadout.Backpack is { } b && Create(b.Record.StringId, 1, b.Quality) is { } pack)
            {
                pack.Section = "backpack_attach";
                inventory.Items.Add(pack);
            }
            if (loadout.Crossbow is { } x) AddWeapon(inventory, x);
            foreach (var w in loadout.Weapons) AddWeapon(inventory, w);
        }
        if (character is not null)
            foreach (var r in character.GetReferences("inventory"))
            {
                if (r.Values.Value0 <= 0) continue;
                if (Create(r.TargetStringId, r.Values.Value0) is { } item) inventory.Add(item);
            }
        if (loadout?.Crossbow is { } bow) AddAmmo(inventory, bow.Weapon);
        if (character is not null)
            foreach (var r in character.GetReferences("death items"))
            {
                // One stack of the whole quantity in the first free cell, whatever the item's stack limit (as the saves hold them).
                if (r.Values.Value0 < 1 || Create(r.TargetStringId, r.Values.Value0) is not { } item || !inventory.FindCell("main", item.Width, item.Height, out int x, out int y)) continue;
                (item.Death, item.X, item.Y) = (true, x, y);
                inventory.Items.Add(item);
            }
        return inventory;
    }

    /// <summary>
    /// An <b>engine choice</b>: a character whose loadout rolled a crossbow gets one item of the crossbow's <c>ammo</c> (one stack of bolts: 10 to 40 shots by the record's
    /// <c>charges</c>) unless its record's list already gave it some. Only 3 of the 40 base-game CHARACTER records with crossbows list bolts; how the original equips
    /// NPC crossbowmen with ammunition is <b>Unknown</b> (docs/simulation.md "Combat", ranged).
    /// </summary>
    void AddAmmo(Inventory inventory, GameRecord crossbow)
    {
        var refs = crossbow.GetReferences("ammo");
        if (refs.Count == 0) return;
        string ammo = refs[0].TargetStringId;
        foreach (var i in inventory.All()) if (i.Record == ammo) return;
        if (Create(ammo) is { } bolts) inventory.Add(bolts);
    }

    /// <summary>
    /// A loadout weapon in its section and row; when another weapon already holds those cells (a two-row crossbow fills the back), in the hip when that is free, else in the
    /// pockets: two weapons never share cells.
    /// </summary>
    void AddWeapon(Inventory inventory, Meitou.Data.Characters.LoadoutWeapon w)
    {
        if (Create(w.Weapon.StringId, 1) is not { } item) return;
        item.Level = w.Level;
        item.MaterialId = w.Model?.StringId ?? "";
        item.CompanyId = w.Manufacturer?.StringId ?? "";
        if (Place(inventory, item, w.Section, w.Row) || Place(inventory, item, "hip", 0)) inventory.Items.Add(item);
        else
        {
            item.Section = "main";
            inventory.Add(item);
        }
    }

    /// <summary>Puts <paramref name="item"/> at the start of <paramref name="row"/> of an equipment <paramref name="section"/> when it fits there and the cells are free.</summary>
    static bool Place(Inventory inventory, ItemInstance item, string section, int row)
    {
        var size = InventoryCommands.SectionSize(section);
        if (item.Width > size.Width || row + item.Height > size.Height || !inventory.IsFree(section, 0, row, item.Width, item.Height)) return false;
        (item.Section, item.X, item.Y) = (section, 0, row);
        return true;
    }

    /// <summary>
    /// The section a worn ARMOUR goes to by its <c>slot</c> (the save's section names); an ITEM only to <c>belt</c>, the slot of the two lanterns (the only ITEMs with slot 14 in the
    /// merged data, docs/formats/lights.md "Carried lanterns").
    /// </summary>
    static string SectionOf(GameRecord worn) => worn.Type == FcsRecordType.ITEM && worn.GetInt("slot") != (int)Meitou.Data.Characters.AttachSlot.Belt ? "" : (Meitou.Data.Characters.AttachSlot)worn.GetInt("slot") switch
    {
        Meitou.Data.Characters.AttachSlot.Shirt => "shirt",
        Meitou.Data.Characters.AttachSlot.Hat => "head",
        Meitou.Data.Characters.AttachSlot.Boots => "boots",
        Meitou.Data.Characters.AttachSlot.Body => "armour",
        Meitou.Data.Characters.AttachSlot.Legs => "legs",
        Meitou.Data.Characters.AttachSlot.Belt => "belt",
        _ => "main",
    };
}
