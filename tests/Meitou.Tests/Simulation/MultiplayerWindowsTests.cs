using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Each player's windows (docs/simulation.md "Dialogue and trade per player"): conversations, trades, editor grants, recruits and containers are the issuing player's, and an NPC
/// one player's window holds is refused to the others.
/// </summary>
public class MultiplayerWindowsTests
{
    static FcsReference Ref(string id, int v0 = 0) => new(id, v0, 0, 0);

    const float Height = 150;
    static readonly Vector3 BoxAt = new(200, Height, 0);

    /// <summary>
    /// Villagers who say "Hello." (event 1), a drifter who joins with the editor (DA 3), a surgeon whose line opens the character editor (DA 11), a trader template selling cloth,
    /// and a town with a store whose one container has an inventory.
    /// </summary>
    static PopulationData Data() => PopulationData.Create(Db([
        Rec("p-w", FcsRecordType.FACTION, r => r.Name = "Nameless"),
        Rec("f-w", FcsRecordType.FACTION, r => r.Name = "Villagers"),
        Rec("npc-w", FcsRecordType.CHARACTER, r => { r.Name = "Villager"; r.References["dialogue"] = [Ref("hello-d")]; }),
        Rec("hello-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("hello-l")]),
        Rec("hello-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Hello."),
        Rec("rec-w", FcsRecordType.CHARACTER, r => { r.Name = "Drifter"; r.References["dialogue"] = [Ref("join-d")]; }),
        Rec("join-a", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.JoinSquadWithEdit),
        Rec("join-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("join-l")]),
        Rec("join-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "I'll come."; r.References["effects"] = [Ref("join-a")]; }),
        Rec("doc-w", FcsRecordType.CHARACTER, r => { r.Name = "Surgeon"; r.References["dialogue"] = [Ref("edit-d")]; }),
        Rec("edit-a", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.CharacterEditor),
        Rec("edit-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("edit-l")]),
        Rec("edit-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "Lie down."; r.References["effects"] = [Ref("edit-a")]; }),
        Rec("cloth-w", FcsRecordType.ITEM, r => { r.Ints["value"] = 100; r.Bools["trade item"] = true; r.Ints["stackable"] = 20; }),
        Rec("cats-w", FcsRecordType.ITEM, r => { r.Name = Market.MoneyItemName; r.Ints["value"] = 1; r.Ints["stackable"] = 100; }),
        Rec("atg-w", FcsRecordType.VENDOR_LIST, r => { r.Name = Market.AllTradeGoods; r.References["items"] = [Ref("cloth-w", 100)]; }),
        Rec("vl-w", FcsRecordType.VENDOR_LIST, r =>
        {
            r.References["items"] = [Ref("cloth-w", 100)];
            r.Floats["money item prob"] = 100;
            r.Ints["money item min"] = 500;
            r.Ints["money item max"] = 500;
        }),
        Rec("shop-w", FcsRecordType.SQUAD_TEMPLATE, r =>
        {
            r.Bools["is trader"] = true;
            r.References["vendors"] = [Ref("vl-w", 100)];
            r.Ints["vendors fill total amount"] = 10;
            r.Floats["vendors refresh time"] = 24;
        }),
        Rec("store-w", FcsRecordType.BUILDING, r => r.Name = "Store"),
        Rec("chest-w", FcsRecordType.BUILDING, r => r.Name = "Chest"),
        Rec("town-w", FcsRecordType.TOWN, r => { r.Ints["type"] = 2; r.References["faction"] = [Ref("f-w")]; }),
    ]), [new TownPlacement("i-town", "town-w", Vector3.Zero, null)], buildings: new Dictionary<string, List<TownBuilding>>
    {
        ["i-town"] = [new TownBuilding("store", "store-w", "Store", BoxAt, 0, false, false, []) { Fixtures = [new TownFixture("box", "chest-w", false, BoxAt, 0) { StorageWidth = 4, StorageHeight = 4 }] }],
    });

    /// <summary>A world with the player, movement, dialogue and trade systems, a purse of 1000, player 1 (Guest) joined and the town's fixtures registered.</summary>
    sealed class Town : IDisposable
    {
        public PopulationData Data { get; } = MultiplayerWindowsTests.Data();
        public SimWorld World { get; }
        public DialogueSystem Dialogue { get; }
        public TradeSystem Trade { get; }

        public Town()
        {
            Dialogue = new DialogueSystem(Data);
            Trade = new TradeSystem(Data);
            var ground = new OpenGroundWalkability((_, _) => Height);
            World = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, ground, [new PlayerSystem(Data), new MovementSystem(new PathService(ground, true)), Dialogue, Trade]);
            World.Player.Faction = Data.PlayerFaction;
            World.Player.Money = 1000;
            World.Player.Join(1, Guid.NewGuid(), "Guest");
            World.Fixtures.RegisterSite(World, Data, 0);
        }

        public void Dispose() => World.Dispose();

        public PlayerSession Local => World.Player.Local;
        public PlayerSession Guest => World.Player.Find(1)!;

        Squad NewSquad(int faction, int owner, string template)
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = template, Faction = faction, Town = 0, HomeRadius = 10, Owner = owner };
            World.Squads.Add(squad);
            return squad;
        }

        /// <summary>A squad of player <paramref name="owner"/>'s.</summary>
        public Squad PlayerSquad(int owner) => NewSquad(Data.PlayerFaction, owner, "p");

        public CharacterId Hero(string name, Vector2 at, Squad squad) => Spawn(name, at, squad, true, "");

        public CharacterId Npc(string name, Vector2 at, string record, string template = "t") => Spawn(name, at, NewSquad(Data.FactionIndex("f-w"), 0, template), false, record);

        CharacterId Spawn(string name, Vector2 at, Squad squad, bool player, string record)
        {
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = squad.Faction;
            cold.IsPlayer = player;
            cold.RecordId = record;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, Height, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            return id;
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public void Say(SimCommand c) => World.Commands.Enqueue(c with { Tick = World.Tick });
    }

    [Fact]
    public void Two_players_talk_and_trade_at_once_with_different_npcs()
    {
        using var t = new Town();
        var hiro = t.Hero("Hiro", new(0, 0), t.PlayerSquad(0));
        var kai = t.Hero("Kai", new(400, 0), t.PlayerSquad(1));
        var anna = t.Npc("Anna", new(20, 0), "npc-w");
        var bea = t.Npc("Bea", new(420, 0), "npc-w");
        var sellerA = t.Npc("Seller A", new(0, 20), "", "shop-w");
        var sellerB = t.Npc("Seller B", new(400, 20), "", "shop-w");
        t.World.RunTicks(1);   // the shops fill

        t.Say(new TalkOrder(hiro, anna) { Tick = 0 });
        t.Say(new TalkOrder(kai, bea) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Equal((anna, hiro, "Hello."), (t.Dialogue.PublishConversation(0)!.Npc, t.Dialogue.PublishConversation(0)!.Player, t.Dialogue.PublishConversation(0)!.Text));
        Assert.Equal((bea, kai, "Hello."), (t.Dialogue.PublishConversation(1)!.Npc, t.Dialogue.PublishConversation(1)!.Player, t.Dialogue.PublishConversation(1)!.Text));

        // Player 1 leaves; player 0's conversation goes on.
        t.Say(new DialogueChoice(0) { Tick = 0, Player = 1 });
        t.World.RunTicks(1);
        Assert.Null(t.Dialogue.PublishConversation(1));
        Assert.Equal(anna, t.Dialogue.PublishConversation(0)!.Npc);
        Assert.True(t.Cold(anna).Engaged);
        Assert.False(t.Cold(bea).Engaged);

        t.Say(new EndConversation { Tick = 0 });
        t.Say(new OpenTrade(hiro, sellerA) { Tick = 0 });
        t.Say(new OpenTrade(kai, sellerB) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Null(t.Dialogue.PublishConversation(0));
        var mine = t.Trade.PublishTrade(0)!;
        var theirs = t.Trade.PublishTrade(1)!;
        Assert.Equal((hiro, sellerA), (mine.Customer, mine.Trader));
        Assert.Equal((kai, sellerB), (theirs.Customer, theirs.Trader));

        // Player 1 buys: the cloth goes to their customer, the cats from the one purse, the notice to them; player 0's trade stays open.
        t.World.TakeMessages();
        var cloth = theirs.Stock[0];
        t.Say(new BuyItem(cloth.Token, 1) { Tick = 0, Player = 1 });
        t.World.RunTicks(1);
        Assert.Single(t.Cold(kai).Inventory!.All());
        Assert.Empty(t.Cold(hiro).Inventory!.All());
        Assert.Equal(1000 - cloth.UnitPrice, t.World.Player.Money);
        Assert.Contains(t.World.TakeMessages(), m => m.Player == 1 && m.Text.StartsWith("Kai bought", StringComparison.Ordinal));
        Assert.Equal(sellerA, t.Trade.PublishTrade(0)!.Trader);
    }

    [Fact]
    public void A_second_player_is_refused_an_npc_the_first_holds_and_the_first_is_unaffected()
    {
        using var t = new Town();
        var hiro = t.Hero("Hiro", new(0, 0), t.PlayerSquad(0));
        var kai = t.Hero("Kai", new(20, 20), t.PlayerSquad(1));   // in reach of both NPCs too
        var anna = t.Npc("Anna", new(20, 0), "npc-w");
        var seller = t.Npc("Seller", new(0, 20), "", "shop-w");
        t.World.RunTicks(1);

        t.Say(new TalkOrder(hiro, anna) { Tick = 0 });
        t.World.RunTicks(2);
        t.World.TakeMessages();
        t.Say(new TalkOrder(kai, anna) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Contains(new PlayerMessage(1, "Anna is busy with someone else."), t.World.TakeMessages());
        Assert.Null(t.Dialogue.PublishConversation(1));
        Assert.Equal((anna, hiro), (t.Dialogue.PublishConversation(0)!.Npc, t.Dialogue.PublishConversation(0)!.Player));
        Assert.True(t.Cold(anna).Engaged);

        // The same for a trader player 0 trades with.
        t.Say(new EndConversation { Tick = 0 });
        t.Say(new OpenTrade(hiro, seller) { Tick = 0 });
        t.World.RunTicks(2);
        t.World.TakeMessages();
        t.Say(new OpenTrade(kai, seller) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Contains(new PlayerMessage(1, "Seller is busy with someone else."), t.World.TakeMessages());
        Assert.Null(t.Trade.PublishTrade(1));
        Assert.Equal((hiro, seller), (t.Trade.PublishTrade(0)!.Customer, t.Trade.PublishTrade(0)!.Trader));
        Assert.True(t.Cold(seller).Engaged);
    }

    [Fact]
    public void A_recruit_joins_the_talking_players_active_squad()
    {
        using var t = new Town();
        var ours = t.PlayerSquad(0);
        var theirs = t.PlayerSquad(1);
        var camp = t.PlayerSquad(1);
        var hiro = t.Hero("Hiro", new(-300, 0), ours);
        var kai = t.Hero("Kai", new(0, 0), theirs);
        var lin = t.Hero("Lin", new(300, 0), camp);
        var drifter = t.Npc("Drifter", new(20, 0), "rec-w");
        // Player 1's active squad is the camp (Lin's), not the talker's; player 0's is theirs.
        t.Say(new SelectCommand([hiro]) { Tick = 0 });
        t.Say(new SelectCommand([lin]) { Tick = 0, Player = 1 });
        t.World.RunTicks(1);
        Assert.Equal((ours.Id, camp.Id), (t.Local.Squad, t.Guest.Squad));

        t.Say(new TalkOrder(kai, drifter) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Contains(drifter, camp.Members);
        Assert.DoesNotContain(drifter, ours.Members);
        Assert.DoesNotContain(drifter, theirs.Members);
        Assert.Equal(1, t.World.OwnerOf(drifter));
        // DA 3's editor and the recruit edit it allows are player 1's.
        Assert.Equal((DialogueRequestKind.RecruitEditor, drifter), (t.Dialogue.PublishRequest(1)!.Kind, t.Dialogue.PublishRequest(1)!.Player));
        Assert.Null(t.Dialogue.PublishRequest(0));
        Assert.Equal(new EditGrant(EditKind.Recruit, drifter, CharacterId.None), t.Guest.Grant);
        Assert.Null(t.Local.Grant);
    }

    [Fact]
    public void The_character_editor_of_a_conversation_is_granted_to_its_player_only()
    {
        using var t = new Town();
        t.Hero("Hiro", new(-300, 0), t.PlayerSquad(0));
        var kai = t.Hero("Kai", new(0, 0), t.PlayerSquad(1));
        var surgeon = t.Npc("Surgeon", new(20, 0), "doc-w");
        t.World.RunTicks(1);

        t.Say(new TalkOrder(kai, surgeon) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        var request = t.Dialogue.PublishRequest(1)!;
        Assert.Equal((DialogueRequestKind.CharacterEditor, kai, surgeon), (request.Kind, request.Player, request.Npc));
        Assert.Null(t.Dialogue.PublishRequest(0));
        Assert.Equal(new EditGrant(EditKind.Surgery, kai, surgeon), t.Guest.Grant);
        Assert.Null(t.Local.Grant);
    }

    [Fact]
    public void Containers_are_per_session()
    {
        using var t = new Town();
        var box = t.World.Fixtures.All.Single(f => f.Contents is not null);
        var hiro = t.Hero("Hiro", new(BoxAt.X - 5, BoxAt.Z), t.PlayerSquad(0));
        var kai = t.Hero("Kai", new(BoxAt.X + 5, BoxAt.Z), t.PlayerSquad(1));

        t.Say(new OpenContainerOrder([hiro], box.Id) { Tick = 0 });
        t.Say(new OpenContainerOrder([kai], box.Id) { Tick = 0, Player = 1 });
        t.World.RunTicks(2);
        Assert.Equal((box.Id, hiro), (t.Local.OpenContainer, t.Local.ContainerOpener));
        Assert.Equal((box.Id, kai), (t.Guest.OpenContainer, t.Guest.ContainerOpener));

        t.Say(new CloseContainer { Tick = 0, Player = 1 });
        t.World.RunTicks(1);
        Assert.Null(t.Guest.OpenContainer);
        Assert.Equal(box.Id, t.Local.OpenContainer);
    }
}
