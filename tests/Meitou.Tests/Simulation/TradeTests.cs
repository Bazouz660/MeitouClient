using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Prices in play (docs/game/economy.md sections 5 to 7) on an invented database: a trader faction and town with their cultures, and items of each price class.</summary>
public class TradeTests
{
    static FcsReference Ref(string id, int v0 = 0) => new(id, v0, 0, 0);

    /// <summary>
    /// Factions: Nameless (the player, 0), Traders (1). Towns: Trade Town (Traders, at the origin), Near Town (1000 away), Far Town (500000 away), a nest beside it. The faction's culture
    /// prices cloth at 200 %, the town's the katana at 150 % (and cloth not at all, so the faction's applies). <c>all trade goods</c> holds cloth.
    /// </summary>
    internal static PopulationData Data(params FcsRecord[] extra)
    {
        var db = Db([
            Rec("p-t", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-t", FcsRecordType.FACTION, r => { r.Name = "Traders"; r.References["trade culture"] = [Ref("cf-t")]; }),
            Rec("cf-t", FcsRecordType.ITEMS_CULTURE, r => r.References["trade prices"] = [Ref("cloth-t", 200)]),
            Rec("ct-t", FcsRecordType.ITEMS_CULTURE, r => r.References["trade prices"] = [Ref("katana-t", 150)]),
            Rec("t1-t", FcsRecordType.TOWN, r => { r.Ints["type"] = 2; r.References["faction"] = [Ref("f-t")]; r.References["trade culture"] = [Ref("ct-t")]; }),
            Rec("t2-t", FcsRecordType.TOWN, r => r.Ints["type"] = 2),
            Rec("t3-t", FcsRecordType.TOWN, r => r.Ints["type"] = 3),
            Rec("n-t", FcsRecordType.TOWN, r => r.Ints["type"] = 0),
            Rec("katana-t", FcsRecordType.WEAPON, r => r.Ints["value"] = 500),
            Rec("cloth-t", FcsRecordType.ITEM, r => { r.Ints["value"] = 100; r.Bools["trade item"] = true; r.Ints["stackable"] = 20; }),
            Rec("junk-t", FcsRecordType.ITEM, r => r.Ints["value"] = 100),
            Rec("ration-t", FcsRecordType.ITEM, r => { r.Ints["value"] = 10; r.Ints["item function"] = 3; r.Floats["charges"] = 100; r.Ints["stackable"] = 10; }),
            Rec("cats-t", FcsRecordType.ITEM, r => { r.Name = Market.MoneyItemName; r.Ints["value"] = 1; r.Ints["stackable"] = 100; }),
            Rec("atg-t", FcsRecordType.VENDOR_LIST, r => { r.Name = Market.AllTradeGoods; r.References["items"] = [Ref("cloth-t", 100)]; }),
            .. extra]);
        TownPlacement At(string town, float x) => new("i-" + town, town, new Vector3(x, 0, 0), null);
        return PopulationData.Create(db, [At("t1-t", 0), At("t2-t", 1000), At("t3-t", 500_000), At("n-t", 500_100)]);
    }


    // ------------------------------------------------------------------ a shop in a world

    /// <summary>A trader template (10 items from one list that always adds 500 cats) and a fence that buys stolen goods.</summary>
    internal static readonly FcsRecord[] Shops =
    [
        Rec("vl-t", FcsRecordType.VENDOR_LIST, r =>
        {
            r.References["items"] = [Ref("cloth-t", 100), Ref("junk-t", 0)];
            r.Floats["money item prob"] = 100;   // percent-looking, as in the data: always pays
            r.Ints["money item min"] = 500;
            r.Ints["money item max"] = 500;
        }),
        Rec("shop-t", FcsRecordType.SQUAD_TEMPLATE, r =>
        {
            r.Bools["is trader"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
            r.Ints["vendors fill total amount"] = 10;
            r.Floats["vendors refresh time"] = 24;
        }),
        Rec("fence-t", FcsRecordType.SQUAD_TEMPLATE, r =>
        {
            r.Bools["is trader"] = true;
            r.Bools["buys stolen"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
        }),
        Rec("bshop-t", FcsRecordType.SQUAD_TEMPLATE, r =>
        {
            r.Bools["is trader"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
            r.Ints["vendors fill total amount"] = 10;
            r.Ints["vendor money"] = 5000;
        }),
    ];

    [Fact]
    public void A_housed_trader_sells_from_its_homes_merged_stock_containers_with_its_vendor_money_purse()
    {
        using var b = new Bazaar();
        var shopkeeper = b.Spawn("shopkeeper", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "bshop-t");
        TownFixture Box(string id, int function, int w, int h) => new(id, "box", false, Vector3.Zero, 0) { Function = function, StorageWidth = w, StorageHeight = h };
        // Two stock containers (20 x 10 and 10 x 10); a cage and a chest of another function do not count.
        var home = new TownBuilding("shop", "shop", "Shop", Vector3.Zero, 0, true, false, [])
        {
            Fixtures = [Box("a", TownBuildings.ShopFunction, 20, 10), Box("b", TownBuildings.GeneralStorageFunction, 10, 10), Box("c", TownBuildings.CageFunction, 0, 0), Box("d", TownBuildings.RefineryFunction, 30, 30)],
        };
        b.SquadOf(shopkeeper).HomeBuilding = home;
        b.World.RunTicks(1);
        var shop = b.SquadOf(shopkeeper).Shop!;
        Assert.Equal(home.Key, shop.Building);
        Assert.Equal((20, 15), (shop.Stock.Width, shop.Stock.Height));   // the widest, and 300 cells at that width
        Assert.InRange(b.SquadOf(shopkeeper).Money, 5000 - 2000 + 500, 5000 + 2000 + 500);   // vendor money +/- 0.4 x it, plus the money roll
        Assert.InRange(shop.Stock.All().Sum(i => i.Quantity), 8, 12);

        // No stock container: a squad shop of 18 x 18.
        var bare = new TownBuilding("bare", "bare", "Bare", Vector3.Zero, 0, false, false, []) { Fixtures = [Box("c", TownBuildings.CageFunction, 0, 0)] };
        Assert.Null(Shop.ForBuilding(bare, shop.Terms));
        Assert.Equal((Shop.Width, Shop.Height), (new Shop { Terms = shop.Terms }.Stock.Width, new Shop { Terms = shop.Terms }.Stock.Height));
    }

    sealed class Bazaar : IDisposable
    {
        public PopulationData Data { get; }
        public SimWorld World { get; }
        public TradeSystem Trade { get; }

        public Bazaar(params FcsRecord[] extra)
        {
            Data = TradeTests.Data([.. Shops, .. extra]);
            Trade = new TradeSystem(Data);
            var ground = new OpenGroundWalkability((_, _) => 150);   // above the water line
            World = new SimWorld(new WorldSettings { Seed = 3 }, ground, [new MovementSystem(new PathService(ground, true)), Trade]);
            World.Player.Faction = Data.PlayerFaction;
            World.Player.Money = 1000;
        }

        public void Dispose() => World.Dispose();

        public CharacterId Spawn(string name, Vector2 at, int faction, bool player, string template = "p")
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = template, Faction = faction, Town = 0, HomeCentre = at, HomeRadius = 10 };
            World.Squads.Add(squad);
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = faction;
            cold.IsPlayer = player;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }

        public Squad SquadOf(CharacterId id) => World.Squads.Find(World.Characters.Cold(id.Slot)!.SquadId)!;
        public Inventory Bag(CharacterId id) => World.Characters.Cold(id.Slot)!.Inventory!;
        public void Say(SimCommand c) => World.Commands.Enqueue(c with { Tick = World.Tick });
        public TradeView View => World.Snapshot.Trade!;
    }

    [Fact]
    public void A_trader_squad_gets_a_shop_and_the_player_buys_and_sells_at_its_prices()
    {
        using var b = new Bazaar();
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var shopkeeper = b.Spawn("shopkeeper", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTicks(1);
        var shop = b.SquadOf(shopkeeper).Shop!;
        int units = shop.Stock.All().Sum(i => i.Quantity);
        Assert.InRange(units, 8, 12);                            // round(U(0.8, 1.2) x 10)
        Assert.Equal(3000 + 500, b.SquadOf(shopkeeper).Money);   // a small stock is worth less than 12000: the floor, plus the money roll
        Assert.Equal(1 + (long)(24 / BodySystem.HoursPerTick) - 1, shop.NextRestockTick);

        b.Say(new OpenTrade(hero, shopkeeper) { Tick = 0 });
        b.World.RunTicks(2);
        var view = b.View;
        Assert.Equal(units, view.Stock.Sum(i => i.Quantity));
        Assert.True(b.World.Snapshot.Characters.Single(c => c.Id == shopkeeper).IsTrader);

        // Buy one unit of the first stack: its unit price leaves the purse and enters the shop's.
        var first = view.Stock[0];
        b.Say(new BuyItem(first.Token, 1) { Tick = 0, Section = "main", X = 3, Y = 2 });   // dropped on a pocket cell
        b.World.RunTicks(1);
        Assert.Equal(1000 - first.UnitPrice, b.World.Player.Money);
        Assert.Equal(3500 + first.UnitPrice, b.SquadOf(shopkeeper).Money);
        Assert.Equal((1, 3, 2), b.Bag(hero).All().Select(i => (i.Quantity, i.X, i.Y)).Single());

        // Sell it back: the sell price (a trade item at full price, junk at half).
        var mine = b.View.Goods.Single();
        b.Say(new SellItem(mine.Token, 1) { Tick = 0, X = 11, Y = 17 });   // dropped on the stock's last row
        b.World.RunTicks(1);
        Assert.Equal(1000 - first.UnitPrice + mine.Price, b.World.Player.Money);
        Assert.Empty(b.Bag(hero).All());
        Assert.Contains(shop.Stock.Items, i => (i.X, i.Y) == (11, 17));

        // Too poor: nothing moves.
        b.World.Player.Money = 0;
        b.Say(new BuyItem(b.View.Stock[0].Token, 1) { Tick = 0 });
        b.World.RunTicks(1);
        Assert.Contains("You can't afford that.", b.World.TakeMessages());
        Assert.Empty(b.Bag(hero).All());

        // Walking away closes the trade; the stock is replaced when the refresh time comes.
        b.Say(new MoveOrder([hero], new Vector3(-300, 150, 0)) { Tick = 0 });
        b.World.RunTicks(300);
        Assert.Null(b.World.Snapshot.Trade);
        b.World.RunTicks((int)(shop.NextRestockTick - b.World.Tick) + 15);
        Assert.Equal(2, shop.Fills);
    }

    internal static FcsRecord BackpackRecord() => Rec("bag-t", FcsRecordType.CONTAINER, r =>
    {
        r.Ints["value"] = 100; r.Ints["inventory footprint width"] = 4; r.Ints["inventory footprint height"] = 4;
        r.Ints["storage size width"] = 6; r.Ints["storage size height"] = 8;
    });

    [Theory]
    [InlineData(null, true)]
    [InlineData("main", false)]
    [InlineData("backpack_attach", false)]
    public void Buying_a_backpack_transfers_its_container_and_selling_it_preserves_contents(string? section, bool equip)
    {
        using var b = new Bazaar(BackpackRecord());
        var hero = b.Spawn("hero", Vector2.Zero, b.Data.PlayerFaction, true);
        var trader = b.Spawn("trader", new(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTick();
        var shop = b.SquadOf(trader).Shop!;
        shop.Stock.Items.Clear();
        var bag = b.Data.Items.Create("bag-t")!;
        var contents = bag.Contents!;
        var ration = b.Data.Items.Create("ration-t")!;
        Assert.True(contents.Add(ration));
        Assert.True(shop.Stock.Add(bag));
        b.Say(new OpenTrade(hero, trader) { Tick = 0 }); b.World.RunTicks(2);
        var stock = b.View.Stock.Single(i => i.Record == "bag-t");
        long playerMoney = b.World.Player.Money, shopMoney = b.SquadOf(trader).Money;
        b.Say(new BuyItem(stock.Token, 1) { Tick = 0, Section = section, X = 2, Y = 3, Equip = equip }); b.World.RunTick();
        Assert.Same(bag, Assert.Single(b.Bag(hero).Items));
        Assert.Same(contents, bag.Contents); Assert.Same(ration, Assert.Single(contents.Items));
        Assert.Equal(section == "main" ? "main" : "backpack_attach", bag.Section);
        if (section == "main") Assert.Equal((2, 3), (bag.X, bag.Y));
        Assert.Empty(shop.Stock.All());
        Assert.Equal(playerMoney - stock.Price, b.World.Player.Money);
        Assert.Equal(shopMoney + stock.Price, b.SquadOf(trader).Money);
        var goods = b.View.Goods.Single(i => i.Record == "bag-t");
        b.Say(new SellItem(goods.Token, 1) { Tick = 0 }); b.World.RunTick();
        Assert.Empty(b.Bag(hero).All()); Assert.Same(bag, Assert.Single(shop.Stock.Items));
        Assert.Same(contents, bag.Contents); Assert.Same(ration, Assert.Single(contents.Items));
        Assert.Equal(playerMoney - stock.Price + goods.Price, b.World.Player.Money);
        Assert.Equal(shopMoney + stock.Price - goods.Price, b.SquadOf(trader).Money);
    }

    [Theory]
    [InlineData("money")]
    [InlineData("space")]
    [InlineData("occupied-slot")]
    [InlineData("nested-bag")]
    public void A_rejected_backpack_purchase_leaves_stock_contents_and_money_unchanged(string reason)
    {
        using var b = new Bazaar(BackpackRecord());
        var hero = b.Spawn("hero", Vector2.Zero, b.Data.PlayerFaction, true);
        var trader = b.Spawn("trader", new(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTick();
        var shop = b.SquadOf(trader).Shop!; shop.Stock.Items.Clear();
        var bag = b.Data.Items.Create("bag-t")!; Assert.True(shop.Stock.Add(bag));
        var contents = bag.Contents;
        if (reason == "money") b.World.Player.Money = 0;
        if (reason == "space") b.Bag(hero).Items.Add(new() { Record = "filler", Width = 8, Height = 10 });
        if (reason is "occupied-slot" or "nested-bag")
        {
            var worn = b.Data.Items.Create("bag-t")!; worn.Section = "backpack_attach"; b.Bag(hero).Items.Add(worn);
        }
        b.Say(new OpenTrade(hero, trader) { Tick = 0 }); b.World.RunTicks(2);
        var stock = b.View.Stock.Single();
        long playerMoney = b.World.Player.Money, shopMoney = b.SquadOf(trader).Money;
        b.Say(new BuyItem(stock.Token, 1) { Tick = 0, Section = reason == "occupied-slot" ? "backpack_attach" : reason == "nested-bag" ? "backpack_content" : "main" });
        b.World.RunTick();
        Assert.Same(bag, Assert.Single(shop.Stock.Items)); Assert.Same(contents, bag.Contents);
        Assert.Equal(stock.Token, b.View.Stock.Single().Token);
        Assert.DoesNotContain(b.Bag(hero).All(), i => ReferenceEquals(i, bag));
        Assert.Equal(playerMoney, b.World.Player.Money); Assert.Equal(shopMoney, b.SquadOf(trader).Money);
    }

    [Fact]
    public void Stolen_goods_are_only_safe_with_a_fence()
    {
        Assert.Equal(0.4f, TradeSystem.FenceChance(0, false), 5);
        Assert.Equal(1.25f, TradeSystem.FenceChance(100, false), 5);
        Assert.Equal(0.2f, TradeSystem.FenceChance(0, true), 5);   // halved when it was stolen from the shop's own faction

        using var b = new Bazaar();
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var fence = b.Spawn("fence", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "fence-t");
        var loot = b.Data.Items.Create("cloth-t", 2)!;
        loot.StolenFrom = b.Data.FactionIndex("f-t");
        b.Bag(hero).Add(loot);
        b.World.RunTicks(1);
        b.Say(new OpenTrade(hero, fence) { Tick = 0 });
        b.World.RunTicks(2);
        var goods = b.View.Goods.Single();
        Assert.True(goods.Stolen);
        b.Say(new SellItem(goods.Token, 2) { Tick = 0 });
        b.World.RunTicks(1);
        Assert.Equal(1000 + goods.Price, b.World.Player.Money);
        Assert.Empty(b.Bag(hero).All());
    }
}
