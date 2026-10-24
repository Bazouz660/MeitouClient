using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>Confirms the quoted price of a town house; the simulation rechecks eligibility and funds.</summary>
public sealed record BuyBuilding(string Building, long Price) : SimCommand;

public sealed partial class FixtureRegistry
{
    sealed class Property(PopulationData data, int site, TownBuilding building, int owner)
    {
        public PopulationData Data { get; } = data;
        public int Site { get; } = site;
        public TownBuilding Building { get; } = building;
        public int Owner { get; set; } = owner;
        public bool Purchased { get; set; }
    }
    readonly Dictionary<string, Property> properties = new(StringComparer.Ordinal);
    IReadOnlySet<string>? ownedProperties;
    int ownedFaction = -1;

    void RegisterProperty(PopulationData data, int site, TownBuilding building, int owner)
    {
        var record = data.Db.Find(building.BuildingId);
        if (record is null || record.GetBool("is wall gate") || record.GetBool("links together")) return;
        int? function = TownBuildings.FunctionOf(data.Db, record, functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return;
        properties[building.Key] = new(data, site, building, owner);
        ownedProperties = null;
    }

    public int? PropertyOwner(string building) => properties.TryGetValue(building, out var p) ? p.Owner : null;

    public bool OwnsBuilding(string building, int faction) => faction >= 0 &&
        (properties.TryGetValue(building, out var p) ? p.Owner == faction : OfBuilding(building).Any(f => f.Owner == faction));

    public IReadOnlySet<string> OwnedProperties(int faction)
    {
        if (ownedProperties is null || ownedFaction != faction)
        {
            ownedFaction = faction;
            ownedProperties = properties.Where(p => faction >= 0 && p.Value.Owner == faction).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        }
        return ownedProperties;
    }

    public long? SalePrice(string building, int player)
    {
        if (player < 0 || !properties.TryGetValue(building, out var p) || p.Owner < 0 || p.Owner == player || p.Purchased) return null;
        var data = p.Data;
        if (data.Sites[p.Site].Town.Type is not (TownType.Outpost or TownType.Town or TownType.Village)
            || data.SiteFactions[p.Site] == player || data.Relations.IsAlly(p.Owner, player)) return null;
        var saved = Saved?.GetValueOrDefault(building);
        var state = p.Building.LevelState;
        bool sale = saved?.Bools.TryGetValue("sell home", out bool savedSale) == true ? savedSale
            : state?.Bools.TryGetValue("sell home", out bool levelSale) == true ? levelSale
            : data.Db.Find(p.Building.ResidentsTemplate)?.GetBool("sell home") ?? (saved is null && state is null);
        bool inside = saved is not null ? saved.Bools.GetValueOrDefault("is inside building") : state?.GetBool("is inside building") ?? false;
        if (!sale || inside || data.Db.Find(p.Building.BuildingId) is not { } record) return null;
        bool destroyed = saved?.Bools.GetValueOrDefault("destroyed") ?? p.Building.Destroyed;
        float multiplier = data.Db.Find(data.Factions[p.Owner].Id)?.GetFloat("building cost mult", 1) ?? 1;
        float price = (destroyed ? 0.375f : 1) * BuildingBuildData.From(record).Target * multiplier * 400;
        return float.IsFinite(price) && price >= 0 && price <= int.MaxValue ? (long)price : null;
    }

    public void Buy(World world, BuyBuilding order)
    {
        if (SalePrice(order.Building, world.Player.Faction) is not { } price || price != order.Price) return;
        if (world.Player.Money < price) { world.Post(Meitou.Localisation.Loc.Tr("You can't afford that.")); return; }
        var p = properties[order.Building];
        world.Player.Money -= price;
        p.Owner = world.Player.Faction;
        p.Purchased = true;
        ownedProperties = null;
        foreach (var f in OfBuilding(order.Building)) f.Owner = p.Owner;
        containerSpots = null; doorPicks = null; bedSpots = null; cageList = null;
        world.ItemSlots.TransferBuilding(world, p.Building, p.Owner);
        SetPublic(order.Building, false);
    }

    void WriteProperty(string building, FcsRecord state)
    {
        if (!properties.TryGetValue(building, out var p) || !p.Purchased) return;
        state.Strings["owner faction ID"] = p.Data.Factions[p.Owner].Id;
        state.Bools["sell home"] = false;
        state.Bools["is public night"] = false;
    }

    void HashProperties(ref StateHasher h)
    {
        foreach (var (key, p) in properties.Where(p => p.Value.Purchased || p.Value.Owner == p.Value.Data.PlayerFaction).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            h.Add(Rng.StableHash(key)); h.Add(p.Owner);
        }
    }
}
