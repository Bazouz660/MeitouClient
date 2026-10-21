using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

/// <summary>The player opens a trade between one of their characters and a trader: the customer walks up to the trader and the trade window opens in reach.</summary>
public sealed record OpenTrade(CharacterId Customer, CharacterId Trader) : SimCommand;
/// <summary>Closes the open trade.</summary>
public sealed record CloseTrade : SimCommand;
/// <summary>
/// Buys <see cref="Quantity"/> units of the stock item <see cref="Item"/> (its index in the shop's stock) into the customer's inventory: at the cell <see cref="X"/>, <see cref="Y"/> of
/// <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>, the cell the item was dropped on) when that is free, else wherever it fits.
/// </summary>
public sealed record BuyItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    public string? Section { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>The window's right click (no cell chosen): one unit goes into the equipment slot that takes it when that is free, before the grids (<see cref="InventoryCommands.TryEquip"/>).</summary>
    public bool Equip { get; init; }
}

/// <summary>Sells <see cref="Quantity"/> units of the customer's item <see cref="Item"/> (its index in the customer's inventory) to the shop, at the stock cell dropped on when free.</summary>
public sealed record SellItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    public int? X { get; init; }
    public int Y { get; init; }
}

/// <summary>One item of a trade window, with what it costs (stock) or fetches (the customer's goods) as a whole stack.</summary>
public sealed record TradeItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity, float Quality, long Price, long UnitPrice, bool Stolen)
{
    /// <summary>The item's material record ("" none), which picks its icon.</summary>
    public string Material { get; init; } = "";
    /// <summary>How full the item is (0 to 1); null: no charge bar.</summary>
    public float? Charge { get; init; }
    /// <summary>The item's manufacturer record (<c>company sid</c>; "" none), model level and charges, and the trader factor of its price (<c>FUN_14079e6d0</c>, 1: none).</summary>
    public string Company { get; init; } = "";
    public int Level { get; init; }
    public float Charges { get; init; }
    public float MaxCharges { get; init; }
    public float PriceFactor { get; init; } = 1;
    /// <summary>The faction a stolen item was stolen from, -1 when it is not stolen.</summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>The tooltip's two prices of the whole stack (<c>FUN_14079e7a0</c>): what the shop asks for it (Value) and what it pays (Sell value).</summary>
    public long Value { get; init; }
    public long SellValue { get; init; }
    /// <summary>The stack's weight in kg.</summary>
    public float Weight { get; init; }
    /// <summary>A stolen good of the customer's that was stolen from the shop's own faction: the chance a sale goes through unnoticed (the fencing prompt's number); null otherwise.</summary>
    public float? FenceChance { get; init; }
    /// <summary>
    /// A stolen good of the customer's that a shop which is no fence would buy only with a risk: the chance (0 to 1, below 1) a sale goes through unnoticed, whoever it was stolen from
    /// (<c>FUN_140715290</c> asks "Fence it anyway?" with the opposite in per cent); null when the sale is safe or the item is not stolen.
    /// </summary>
    public float? FenceOdds { get; init; }
    public InventoryItemToken Token => new(Index, Record, Section, X, Y, Quantity, Quality);
}

/// <summary>Arranges the open trade's stock (the trader window's ARRANGE button): biggest items first into the first free cells, as <see cref="InventoryCommands.Arrange"/>.</summary>
public sealed record ArrangeStock : SimCommand;

/// <summary>The open trade as the UI shows it (published with the snapshot).</summary>
public sealed record TradeView(CharacterId Customer, CharacterId Trader, string TraderName, long ShopMoney, long PlayerMoney, int StockWidth, int StockHeight,
    IReadOnlyList<TradeItemView> Stock, IReadOnlyList<TradeItemView> Goods, bool BuysStolen);

/// <summary>A system that has an open trade to publish (the trade system).</summary>
public interface ITradeSource
{
    TradeView? PublishTrade();
    /// <summary>Player <paramref name="player"/>'s open trade (docs/simulation.md "Multiplayer (host-authoritative co-op)"); the parameterless one is player 0's.</summary>
    TradeView? PublishTrade(int player) => player == 0 ? PublishTrade() : null;
}

public sealed class TradeSettings
{
    /// <summary>How close the customer must stand to the trader (the original's "Out of trading range." distance is <b>Unknown</b>; engine choice).</summary>
    public float Range { get; init; } = 30;
    /// <summary>Ticks between looks at the squads for new shops and due restocks (engine choice: half a game second).</summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>Game seconds the customer has to reach the trader before the opening is dropped (engine choice).</summary>
    public float ApproachSeconds { get; init; } = 60;
}

/// <summary>
/// The economy in play (docs/simulation.md "Trade", docs/game/economy.md sections 8 and 9):
/// <list type="bullet">
/// <item><b>Shops</b>: a squad made from a template with <c>is trader</c> and vendor lists gets a <see cref="Shop"/> when it appears (the game's first fill at squad creation), filled by
/// <see cref="ShopFiller"/>, with the purse in <see cref="Squad.Money"/>; it restocks every <c>vendors refresh time</c> game hours (9.3). Squads of the player's faction never get one.
/// A housed one whose template has no <c>prisoners</c> or <c>slaves</c> gets a building shop when its home has stock containers (<see cref="Shop.ForBuilding"/>).</item>
/// <item><b>Trading</b> (the trade window <c>FUN_140715560</c>, <b>Observed</b>): a purchase is priced by <see cref="Market.StackPrice"/> without the sell flag, a sale with it; when the payer
/// cannot afford the stack the number of units it can afford moves instead, none: "You can't afford that." (3) or "The shopkeeper can't afford that." (4). What does not fit: "No room for
/// that item." (2). The cats move between the player's purse and the shop's.</item>
/// <item><b>Fencing</b> (8.4, <b>Observed</b>): a stolen item sold to a shop that is not a knowing fence (<c>buys stolen</c>) is caught unless a roll is below
/// lerp(thievery / 100, 0.4, 1.25), halved when the item was stolen from the shop's own faction; caught: "Got caught selling stolen item." (9), the sale is refused and the seller gets a
/// pending Fencing crime against the shop's faction, booked at once with the shopkeeper as the witness. The prompt that shows the chance is the UI's. The smuggling rules (8.5) are not
/// decoded and not built.</item>
/// </list>
/// The shops' stock and restock times are in the state hash (through the squads); the open trades are not (they only decide what is published). Each player has
/// a deal of their own (docs/simulation.md "Dialogue and trade per player"); a trader another player's window holds (<see cref="World.HolderOf"/>) is refused.
/// </summary>
public sealed class TradeSystem : ITickSystem, ITradeSource, INpcHolder
{
    readonly PopulationData data;
    readonly CharacterAssembly assembly;
    readonly Dictionary<string, SquadTemplate?> templates = new(StringComparer.Ordinal), homeTemplates = new(StringComparer.Ordinal);
    Market? market;
    // Each player's trade, by player number.
    readonly SortedDictionary<int, Deal> deals = [];

    /// <summary>One player's trade: the customer walking up to the trader (until <see cref="ApproachUntil"/>), then the open window.</summary>
    sealed class Deal
    {
        public required int Player;
        public CharacterId Customer = CharacterId.None, Trader = CharacterId.None;
        public bool Open, TraderWandered;
        public long ApproachUntil;
        public TradeView? View;
    }

    Deal DealOf(int player)
    {
        if (!deals.TryGetValue(player, out var deal)) deals[player] = deal = new Deal { Player = player };
        return deal;
    }

    public TradeSystem(PopulationData data, TradeSettings? settings = null)
    {
        this.data = data;
        assembly = new(data);
        Settings = settings ?? new TradeSettings();
    }

    public TradeSettings Settings { get; }
    /// <summary>The crime system a caught fence is reported to (null: no crimes).</summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>The prices of this world (made at the first tick, from the world's seed).</summary>
    public Market Market(World world) => market ??= new Market(data, world.Seed);

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case OpenTrade o:
                {
                    // Another player's window holds the trader (a trade, a conversation or its approach): the first holder keeps it.
                    if (world.HolderOf(o.Trader) is >= 0 and var holder && holder != o.Player)
                    {
                        if (world.Characters.TryResolveNext(o.Trader, out int busy)) world.Post(o.Player, $"{world.Characters.Cold(busy)!.Name} is busy with someone else.");
                        break;
                    }
                    var deal = DealOf(o.Player);
                    Close(world, deal);
                    deal.Customer = o.Customer;
                    deal.Trader = o.Trader;
                    deal.ApproachUntil = world.Tick + (long)(Settings.ApproachSeconds / world.TickSeconds);
                    if (!Resolve(world, deal, out int c, out int t, out _))
                    {
                        // A right click on an enemy is still an attack (the UI sends the trade for any trader; only the world knows who is hostile).
                        if (world.Characters.TryResolveNext(deal.Trader, out int who) && world.Characters.Cold(who) is { } tc && AggroSystem.IsEnemy(data.Relations, world.Player.Faction, tc.Faction))
                            world.Defer(new Combat.AttackOrder([deal.Customer], deal.Trader) { Tick = world.Tick + 1, Player = o.Player });
                        Close(world, deal);
                        break;
                    }
                    Hold(world, deal, t);
                    if (!InReach(world, c, t)) world.Defer(new MoveOrder([deal.Customer], world.Characters.Next[t].Position) { Tick = world.Tick + 1, Player = o.Player });
                    break;
                }
                case CloseTrade close when deals.TryGetValue(close.Player, out var closed):
                    Close(world, closed);
                    break;
                case BuyItem buy when deals.TryGetValue(buy.Player, out var deal) && deal.Open:
                    Buy(world, deal, buy);
                    break;
                case SellItem sell when deals.TryGetValue(sell.Player, out var deal) && deal.Open:
                    Sell(world, deal, sell);
                    break;
                case ArrangeStock arrange when deals.TryGetValue(arrange.Player, out var deal) && deal.Open:
                    if (Resolve(world, deal, out _, out _, out var arranged)) InventoryCommands.Arrange(arranged!.Stock);
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        if (world.Tick % Settings.CheckEveryTicks == 0) Stock(world);
        foreach (var deal in deals.Values)
        {
            if (deal.Customer.IsNone) continue;
            if (!Resolve(world, deal, out int c, out int t, out _)) Close(world, deal);
            else if (InReach(world, c, t))
            {
                deal.Open = true;
                Hold(world, deal, t);
            }
            else if (deal.Open || world.Tick >= deal.ApproachUntil) Close(world, deal);   // walked away, or never arrived
            else Hold(world, deal, t);
        }
        RefreshView(world);
    }

    internal void RefreshView(World world)
    {
        foreach (var deal in deals.Values)
            deal.View = deal.Open && Resolve(world, deal, out int ci, out int ti, out var shop) ? Publish(world, deal, ci, ti, shop!) : null;
    }

    public TradeView? PublishTrade() => PublishTrade(0);
    public TradeView? PublishTrade(int player) => deals.TryGetValue(player, out var deal) ? deal.View : null;

    /// <summary>The player whose deal (open or on its way) is with <paramref name="npc"/>; -1 none.</summary>
    public int HolderOf(CharacterId npc)
    {
        if (npc.IsNone) return -1;   // a closed deal's Trader is None
        foreach (var deal in deals.Values)
            if (deal.Trader == npc) return deal.Player;
        return -1;
    }

    /// <summary>Keeps the trader standing while the customer comes and trades (residents wander; the original's shopkeepers stand at their node). Its wander resumes on close.</summary>
    void Hold(World world, Deal deal, int t)
    {
        ref var n = ref world.Characters.Next[t];
        world.Characters.Cold(t)!.Engaged = true;   // the AI leaves it alone until the trade closes
        if (n.Task == (byte)CharacterTask.Wander) deal.TraderWandered = true;
        if (n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) MovementSystem.Stop(world.Characters, t);
    }

    void Close(World world, Deal deal)
    {
        if (world.Characters.TryResolveNext(deal.Trader, out int held)) world.Characters.Cold(held)!.Engaged = false;
        if (deal.TraderWandered && world.Characters.TryResolveNext(deal.Trader, out int t) && world.Characters.Next[t].Task == (byte)CharacterTask.Idle)
            world.Characters.Next[t].Task = (byte)CharacterTask.Wander;
        deal.Customer = deal.Trader = CharacterId.None;
        deal.Open = deal.TraderWandered = false;
        deal.View = null;
    }

    // ------------------------------------------------------------------ shops

    SquadTemplate? TemplateOf(string id)
    {
        if (!templates.TryGetValue(id, out var t))
            templates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { HasShop: true } shop ? shop : null;
        return t;
    }

    void Stock(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction >= 0 && (squad.Faction == world.Player.Faction || squad.Faction == data.PlayerFaction)) continue;
            if (squad.Shop is { } shop)
            {
                if (world.Tick < shop.NextRestockTick || TemplateOf(squad.TemplateId) is not { } again) continue;
                Refill(world, squad, shop, again);
            }
            else if (TemplateOf(squad.TemplateId) is { } template)
            {
                var terms = new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal);
                // A housed trader whose template makes no prisoner or slave squads keeps a building shop when its home has stock containers (the squad factory, Observed).
                squad.Shop = (squad.HomeBuilding is { } home && template.Prisoners.Count == 0 && template.Slaves.Count == 0 ? Shop.ForBuilding(home, terms) : null)
                    ?? new Shop { Terms = terms };
                Refill(world, squad, squad.Shop, template);
            }
            else if (squad.HomeBuilding is { } home && HomeTemplateOf(squad.TemplateId) is { } contents) FillHome(world, squad, contents, home);
        }
    }

    SquadTemplate? HomeTemplateOf(string id)
    {
        if (!homeTemplates.TryGetValue(id, out var t))
            homeTemplates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { IsTrader: false, Vendors.Count: > 0 } fill ? fill : null;
        return t;
    }

    /// <summary>
    /// A housed squad with <c>vendors</c> that is no trader (a resident's <c>house contents</c>): the building filler fills its home's storage containers as a trader's (the squad factory
    /// <c>FUN_140583a10</c> tests <c>vendors</c>, not <c>is trader</c>, <b>Observed</b>), unlocking them; here each unit goes into the first container with room and what fits nowhere is
    /// lost. Engine choice: again every <c>vendors refresh time</c> hours, but only while every container is empty (a loaded save keeps what they hold); the purse is not touched.
    /// </summary>
    void FillHome(World world, Squad squad, SquadTemplate template, Meitou.Data.World.TownBuilding home)
    {
        if (world.Tick < squad.NextHomeFillTick) return;
        squad.NextHomeFillTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
        var boxes = new List<Locks.Fixture>();
        foreach (var f in home.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: not null } box) boxes.Add(box);
        if (boxes.Count == 0 || boxes.Any(b => b.Contents!.Items.Count > 0)) return;
        if (Shop.ForBuilding(home, new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal)) is not { } merged) return;
        merged.Fills = (int)world.Tick;
        ShopFiller.Fill(merged, template, Market(world), data, world.Seed, (ulong)squad.Id);
        foreach (var item in merged.Stock.Items)
            foreach (var box in boxes)
                if (box.Contents!.Add(item)) break;
        foreach (var box in boxes)
            if (box.Lock is { } l) l.Locked = false;
    }

    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        squad.Money = ShopFiller.Fill(shop, template, Market(world), data, world.Seed, (ulong)squad.Id);
        // The building filler unlocks the stock containers it fills (FUN_14095b210).
        if (shop.Building is not null && squad.HomeBuilding is { } home)
            foreach (var f in home.Fixtures)
                if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Lock is { } l) l.Locked = false;
        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>The shop a character trades for: its squad's (null: not a trader).</summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

    // ------------------------------------------------------------------ trading

    bool Resolve(World world, Deal deal, out int c, out int t, out Shop? shop)
    {
        var table = world.Characters;
        shop = null;
        t = -1;
        if (!table.TryResolveNext(deal.Customer, out c) || !table.TryResolveNext(deal.Trader, out t)) return false;
        var cc = table.Cold(c)!;
        var tc = table.Cold(t)!;
        if (!cc.IsPlayer || cc.Medical is { Incapacitated: true } || tc.Medical is { Incapacitated: true } || tc.InCombat || cc.Inventory is null) return false;
        if (AggroSystem.IsEnemy(data.Relations, cc.Faction, tc.Faction)) return false;
        shop = ShopOf(world, tc);
        return shop is not null;
    }

    bool InReach(World world, int c, int t)
    {
        var a = world.Characters.Next[c].Position;
        var b = world.Characters.Next[t].Position;
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz <= Settings.Range * Settings.Range;
    }

    static ItemInstance? Find(Inventory inventory, InventoryItemToken token)
    {
        var item = inventory.All().ElementAtOrDefault(token.Index);
        return item is not null && item.Record == token.Record && item.Section == token.Section && item.X == token.X && item.Y == token.Y && item.Quantity == token.Quantity
            && item.Quality == token.Quality && item.Contents is null ? item : null;
    }

    /// <summary>A purchase for the deal's player (its notices go to them; the purse is the faction's).</summary>
    void Buy(World world, Deal deal, BuyItem buy)
    {
        if (!Resolve(world, deal, out int c, out int t, out var shop)) return;
        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        if (Find(shop!.Stock, buy.Item) is not { } item) return;
        var m = Market(world);
        int units = Affordable(m, item, Math.Clamp(buy.Quantity, 1, item.Quantity), false, shop.Terms, squad.Faction, world.Player.Money);
        if (units == 0)
        {
            world.Post(deal.Player, "You can't afford that.");
            return;
        }
        var bought = item.WithQuantity(units);
        long price = m.StackPrice(bought, false, shop.Terms, squad.Faction);
        int placed;
        if (buy.Section is { } section) placed = PlaceAt(cold.Inventory!, bought, units, section, buy.X, buy.Y);
        else if (buy.Equip && InventoryCommands.TryEquip(cold.Inventory!, bought.WithQuantity(1)))
        {
            // One unit is worn; the rest of a stack goes where it fits.
            assembly.RefreshEquipment(cold);
            placed = 1;
            if (units > 1)
            {
                var rest = bought.WithQuantity(units - 1);
                placed += Place(cold.Inventory!, rest, units - 1);
            }
        }
        else placed = Place(cold.Inventory!, bought, units);
        if (placed == 0)
        {
            world.Post(deal.Player, "No room for that item.");
            return;
        }
        if (placed < units) price = m.StackPrice(item.WithQuantity(placed), false, shop.Terms, squad.Faction);
        Take(shop.Stock, item, placed);
        world.Player.Money -= price;
        squad.Money += price;
        world.Post(deal.Player, $"{cold.Name} bought {Units(placed, item.Name)} for c.{price}");
    }

    /// <summary>A sale for the deal's player (its notices go to them; the purse is the faction's).</summary>
    void Sell(World world, Deal deal, SellItem sell)
    {
        if (!Resolve(world, deal, out int c, out int t, out var shop)) return;
        var cold = world.Characters.Cold(c)!;
        var tc = world.Characters.Cold(t)!;
        var squad = world.Squads.Find(tc.SquadId)!;
        if (Find(cold.Inventory!, sell.Item) is not { } item || !InventoryCommands.IsMovableSection(item.Section)) return;
        if (item.LockedOn) { world.Post(deal.Player, "This item is locked in place and cannot be removed."); return; }
        var m = Market(world);
        int units = Affordable(m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, squad.Money);
        if (units == 0)
        {
            world.Post(deal.Player, "The shopkeeper can't afford that.");
            return;
        }
        if (item.IsStolenFor(cold.Faction) && !shop.Terms.BuysStolen && Caught(world, c, item, squad))
        {
            world.Post(deal.Player, "Got caught selling stolen item.");
            if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, deal.Trader, Crime.CrimeSystem.MinTimer))
                crimes.Book(world, c, squad.Faction, deal.Trader);
            return;
        }
        var sold = item.WithQuantity(units);
        long price = m.StackPrice(sold, true, shop.Terms, cold.Faction);
        int placed = sell.X is { } sx ? PlaceAt(shop.Stock, sold, units, "main", sx, sell.Y) : Place(shop.Stock, sold, units);
        if (placed == 0)
        {
            world.Post(deal.Player, "No room for that item.");
            return;
        }
        if (placed < units) price = m.StackPrice(item.WithQuantity(placed), true, shop.Terms, cold.Faction);
        Take(cold.Inventory!, item, placed);
        if (InventoryCommands.IsEquipmentSection(item.Section)) assembly.RefreshEquipment(cold);
        squad.Money -= price;
        world.Player.Money += price;
        world.Post(deal.Player, $"{cold.Name} sold {Units(placed, item.Name)} for c.{price}");
    }

    /// <summary>
    /// The chance a shop that is no fence lets a stolen item through (8.4, <c>FUN_140793490</c>): lerp(thievery / 100, 0.4, 1.25) of the seller, halved when the item was stolen from the
    /// shop's faction; 1 or more never gets caught. The roll is the seller's and the tick's.
    /// </summary>
    public static float FenceChance(float thievery, bool stolenFromShopFaction)
    {
        float p = 0.4f + (1.25f - 0.4f) * (thievery / 100f);
        return stolenFromShopFaction ? p * 0.5f : p;
    }

    bool Caught(World world, int seller, ItemInstance item, Squad shopSquad)
    {
        var cold = world.Characters.Cold(seller)!;
        float p = FenceChance(cold.Stats?[StatsEnumerated.Thieving] ?? 0, item.StolenFrom == shopSquad.Faction);
        if (p >= 1) return false;
        return Rng.Float(Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(seller)), RngPurpose.Trade, (ulong)world.Tick)) >= p;
    }

    /// <summary>How many of <paramref name="wanted"/> units the payer's <paramref name="money"/> covers (the trade window splits the stack to what is affordable).</summary>
    static int Affordable(Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
    {
        for (int n = wanted; n > 0; n--)
            if (m.StackPrice(n == item.Quantity ? item : item.WithQuantity(n), sell, terms, holderFaction) <= money) return n;
        return 0;
    }

    /// <summary>Puts <paramref name="units"/> of an item into an inventory; returns how many went in.</summary>
    /// <summary>
    /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in <paramref name="root"/> (the backpack's
    /// contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within the limit; otherwise wherever they fit, as <see cref="Place"/>.
    /// </summary>
    static int PlaceAt(Inventory root, ItemInstance copy, int units, string section, int x, int y)
    {
        var grid = section == "main" ? root : section == "backpack_content" ? root.Backpack?.Contents : null;
        if (grid is not null && x >= 0 && y >= 0 && x + copy.Width <= grid.Width && y + copy.Height <= grid.Height && units <= grid.LimitOf(copy)
            && grid.IsFree(section, x, y, copy.Width, copy.Height))
        {
            copy.Section = section;
            copy.X = x;
            copy.Y = y;
            grid.Items.Add(copy);
            return units;
        }
        return Place(root, copy, units);
    }

    static int Place(Inventory into, ItemInstance copy, int units)
    {
        copy.Section = "main";
        return into.Add(copy) ? units : units - copy.Quantity;
    }

    /// <summary>Takes <paramref name="units"/> off an item's stack, removing it when none are left.</summary>
    static void Take(Inventory from, ItemInstance item, int units)
    {
        if (units >= item.Quantity) from.Remove(item);
        else item.Quantity -= units;
    }

    static string Units(int n, string name) => n == 1 ? name : $"{n} x {name}";

    TradeView Publish(World world, Deal deal, int c, int t, Shop shop)
    {
        var m = Market(world);
        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        float thievery = cold.Stats?[StatsEnumerated.Thieving] ?? 0;
        TradeItemView View(ItemInstance item, int index, bool sell, int holder)
        {
            long value = m.StackPrice(item, false, shop.Terms, holder), sellValue = m.StackPrice(item, true, shop.Terms, holder);
            bool stolen = item.IsStolenFor(holder);
            return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                item.Quality, sell ? sellValue : value, m.UnitPrice(item.Quantity == 1 ? item : item.WithQuantity(1), sell, shop.Terms, holder), stolen)
            {
                Material = item.MaterialId, Charge = item.ChargeFraction, Value = value, SellValue = sellValue, Weight = item.Weight, PriceFactor = m.TraderFactor(item, shop.Terms, holder),
                Company = item.CompanyId, Level = item.Level, Charges = item.Charges, MaxCharges = item.MaxCharges, StolenFrom = stolen ? item.StolenFrom : -1,
                FenceChance = sell && stolen && item.StolenFrom == squad.Faction ? Math.Clamp(FenceChance(thievery, true), 0, 1) : null,
                FenceOdds = sell && stolen && !shop.Terms.BuysStolen && FenceChance(thievery, item.StolenFrom == squad.Faction) is < 1 and var odds ? Math.Max(odds, 0) : null,
            };
        }
        var stock = shop.Stock.All().Select((item, index) => View(item, index, false, squad.Faction)).ToArray();
        var goods = cold.Inventory!.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
            .Select(p => View(p.item, p.index, true, cold.Faction)).ToArray();
        return new TradeView(deal.Customer, deal.Trader, world.Characters.Cold(t)!.Name, squad.Money, world.Player.Money, shop.Stock.Width, shop.Stock.Height, stock, goods, shop.Terms.BuysStolen);
    }
}
