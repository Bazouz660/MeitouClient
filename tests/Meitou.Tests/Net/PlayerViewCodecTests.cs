using System.Numerics;
using Meitou.Net;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Tests.Net;

/// <summary>The player and detail view codecs (kinds 1-4, 15-25) round-trip populated data (docs/net.md, "Player and detail views").</summary>
public class PlayerViewCodecTests
{
    static byte[] Encode<T>(ItemCodec<T> codec, T value)
    {
        using var w = new NetWriter();
        codec.Write(w, new StringTableWriter(confirmOnSend: true), value);
        return w.Written.ToArray();
    }

    /// <summary>Writes, reads, checks the whole body was consumed and that writing the result again gives the same bytes (so no field was lost).</summary>
    static T RoundTrip<T>(ItemCodec<T> codec, T value)
    {
        byte[] bytes = Encode(codec, value);
        var r = new NetReader(bytes);
        var back = codec.Read(ref r, new StringTableReader());
        r.ExpectEnd();
        Assert.Equal(bytes, Encode(codec, back));
        return back;
    }

    /// <summary>Sends <paramref name="now"/> through a keyed sync and decoder over a reliable string table pair.</summary>
    static IReadOnlyList<T> Keyed<TKey, T>(KeyedViewSync<TKey, T> sync, ItemCodec<T> codec, IReadOnlyList<T> now) where TKey : notnull
    {
        using var w = new NetWriter();
        Assert.True(sync.Encode(now, structural: true, TimeSpan.Zero, w, new StringTableWriter(confirmOnSend: true)));
        var decoder = new KeyedViewDecoder<T>(codec);
        var r = new NetReader(w.Written);
        Assert.True(decoder.Apply(ref r, new StringTableReader()));
        r.ExpectEnd();
        return decoder.Items;
    }

    static CharacterId Id(int slot) => new(slot, slot + 3);

    [Fact]
    public void Header_round_trips()
    {
        var header = new HeaderView(SquadAi.Ditch | SquadAi.Sit, 7, 2, 12, -123_456_789_012L, true, ["Greenlander", "Shek"]);
        var back = RoundTrip(ViewCodecs.HeaderCodec, header);
        Assert.Equal(header with { CreationRaces = [] }, back with { CreationRaces = [] });
        Assert.Equal(["Greenlander", "Shek"], back.CreationRaces);
        var applied = back.ApplyTo(WorldSnapshot.Empty);
        Assert.Equal(-123_456_789_012L, applied.Money);
        Assert.Equal(2, applied.Viewer);
    }

    [Fact]
    public void Towns_round_trip()
    {
        TownKnowledge[] towns = [TownKnowledge.None, TownKnowledge.Discovered, TownKnowledge.Discovered | TownKnowledge.Explored, TownKnowledge.Recent | TownKnowledge.Discovered];
        Assert.Equal(towns, RoundTrip(ViewCodecs.TownsCodec, towns));
    }

    [Fact]
    public void Camps_round_trip_through_a_keyed_view()
    {
        CampView[] camps =
        [
            new(4, "TOWN_CAMP", "Bandit Camp", "Dust Bandits", false, new Vector3(1.5f, -2, 300.25f), TownKnowledge.Discovered | TownKnowledge.Explored),
            new(9, "TOWN_NEST", "Beak Thing Nest", "Beak Things", true, null, TownKnowledge.Recent),
        ];
        Assert.Equal(camps, Keyed(ViewCodecs.CampSync(), ViewCodecs.CampCodec, camps));
    }

    [Fact]
    public void Squads_round_trip_through_a_keyed_view()
    {
        SquadView[] squads = [new(3, "Squad A", [Id(1), Id(2), Id(5)]), new(8, "Wanderers", [])];
        var back = Keyed(ViewCodecs.SquadSync(), ViewCodecs.SquadCodec, squads);
        Assert.Equal(2, back.Count);
        Assert.Equal(squads[0].Members, back[0].Members);
        Assert.Equal("Wanderers", back[1].Name);
        Assert.Empty(back[1].Members);
    }

    [Fact]
    public void Trade_round_trips_with_every_item_field_and_the_closed_form()
    {
        var stock = new TradeItemView(2, "1234-gamedata.base", "Iron Plate", "Backpack", 3, 4, 2, 2, 5, 0.75f, 900, 180, true)
        {
            Material = "MAT_IRON", Charge = 0.5f, Value = 1200, SellValue = 400, Weight = 12.5f, FenceChance = 0.2f, FenceOdds = 0.8f,
        };
        var plain = new TradeItemView(0, "5-gamedata.base", "Bandage", "", 0, 0, 1, 1, 1, 1, 10, 10, false);
        var trade = new TradeView(Id(1), Id(40), "Shopkeeper", 25_000, 1_500, 10, 8, [stock, plain], [plain with { Index = 9 }], true);
        var back = RoundTrip(ViewCodecs.TradeCodec, trade)!;
        Assert.Equal(trade with { Stock = [], Goods = [] }, back with { Stock = [], Goods = [] });
        Assert.Equal(stock, back.Stock[0]);
        Assert.Equal(plain, back.Stock[1]);
        Assert.Equal(9, back.Goods[0].Index);
        Assert.Null(back.Stock[1].Charge);
        Assert.Null(RoundTrip(ViewCodecs.TradeCodec, null));
    }

    [Fact]
    public void Conversation_sends_only_the_new_log_lines_and_survives_a_close()
    {
        var first = new ConversationView(Id(3), Id(1), Id(3), "Hobo", "Beep", "Got any spare cats?", ["Sure", "No", "Leave"], ["Hobo: Hello.", "Beep: Hi."], 77);
        var second = first with { Text = "Thanks!", Choices = ["Leave"], Log = [.. first.Log, "Beep: Sure.", "Hobo: Thanks!"], PlayerMoney = 70 };

        var writer = new ConversationViewWriter();
        var reader = new ConversationViewReader();
        var wireStrings = new StringTableWriter(confirmOnSend: true);
        var readStrings = new StringTableReader();
        using var w = new NetWriter();

        ConversationView? Send(ConversationView? view, out int bytes)
        {
            w.Reset();
            Assert.True(writer.Encode(view, w, wireStrings));
            bytes = w.Length;
            var r = new NetReader(w.Written);
            var read = reader.Read(ref r, readStrings);
            r.ExpectEnd();
            return read;
        }

        var back = Send(first, out int firstBytes)!;
        Assert.Equal(first.Log, back.Log);
        Assert.Equal(first.Choices, back.Choices);
        Assert.Equal(first.Text, back.Text);
        Assert.Equal(77, back.PlayerMoney);

        w.Reset();
        Assert.False(writer.Encode(first with { }, w, wireStrings));
        Assert.Equal(0, w.Length);

        back = Send(second, out int secondBytes)!;
        Assert.Equal(second.Log, back.Log);
        Assert.Equal(["Leave"], back.Choices);
        Assert.True(secondBytes < firstBytes + 20);

        Assert.Null(Send(null, out _));
        back = Send(first, out _)!;
        Assert.Equal(first.Log, back.Log);
    }

    [Fact]
    public void DialogueRequest_round_trips_with_the_empty_form()
    {
        var request = new DialogueRequest(41, DialogueRequestKind.RecruitEditor, Id(2), Id(30));
        Assert.Equal(request, RoundTrip(ViewCodecs.DialogueRequestCodec, request));
        Assert.Null(RoundTrip(ViewCodecs.DialogueRequestCodec, null));
    }

    [Fact]
    public void Speech_round_trips_through_a_keyed_view()
    {
        SpeechBubble[] bubbles = [new(Id(1), "Hey you!", false), new(Id(6), "BANDITS!", true)];
        Assert.Equal(bubbles, Keyed(ViewCodecs.SpeechSync(), ViewCodecs.SpeechCodec, bubbles));
    }

    [Fact]
    public void Relations_round_trip()
    {
        var relations = new RelationsView(12, [100f, -35.5f, 0, 14.25f]);
        var back = RoundTrip(ViewCodecs.RelationsCodec, relations);
        Assert.Equal(12, back.Revision);
        Assert.Equal(relations.Values, back.Values);
        var snapshot = back.ApplyTo(WorldSnapshot.Empty);
        Assert.Equal(12, snapshot.RelationsRevision);
    }

    [Fact]
    public void Talkable_round_trips_and_equal_sets_are_equal_bytes()
    {
        IReadOnlySet<CharacterId> ids = new HashSet<CharacterId> { Id(9), Id(2), Id(31) };
        IReadOnlySet<CharacterId> sameIds = new HashSet<CharacterId> { Id(31), Id(9), Id(2) };
        Assert.True(ids.SetEquals(RoundTrip(ViewCodecs.TalkableCodec, ids)));
        Assert.Equal(Encode(ViewCodecs.TalkableCodec, ids), Encode(ViewCodecs.TalkableCodec, sameIds));
    }

    [Fact]
    public void Prospected_round_trips_with_the_empty_form()
    {
        var report = new ProspectReport(3, new Vector3(100, 20, -400), "Beep", 42, "Stobe's Gamble", 118, 560,
            [new ProspectLine("Green", 0.5f)], [new ProspectLine("Iron", 0.75f), new ProspectLine("Copper", 0.125f)]);
        var back = RoundTrip(ViewCodecs.ProspectedCodec, report)!;
        Assert.Equal(report with { Environment = [], Resources = [] }, back with { Environment = [], Resources = [] });
        Assert.Equal(report.Environment, back.Environment);
        Assert.Equal(report.Resources, back.Resources);
        Assert.Null(RoundTrip(ViewCodecs.ProspectedCodec, null));
    }

    [Fact]
    public void Stats_round_trip_through_a_keyed_view_with_an_infinite_bounty()
    {
        var full = new StatsDetail(Id(1), new SkillSummary(10, 20, 30, 40, 50, 60), [1, 2, 3.5f, 99], [1, 1.5f, 0.5f],
            new HudView(90, 100, 0.25f, 0.9f, [new HudStatLine("Melee attack", 42, 50), new HudStatLine("Dodge", 12, 12)], new ExtendedStats(1, 2, 3, -100, 5, 6, 7)),
            [new BountyLine("Holy Nation", 3000, "Murder, theft", double.PositiveInfinity), new BountyLine("United Cities", 500, "Trespass", 36.5)]);
        var bare = new StatsDetail(Id(2), null, [], [], new HudView(1, 2, 3, 4, [], null), []);
        var back = Keyed(ViewCodecs.StatsSync(), ViewCodecs.StatsCodec, [full, bare]);

        Assert.Equal(full.Skills, back[0].Skills);
        Assert.Equal(full.AllSkills, back[0].AllSkills);
        Assert.Equal(full.SkillXpMultipliers, back[0].SkillXpMultipliers);
        Assert.Equal(full.Hud!.Extended, back[0].Hud!.Extended);
        Assert.Equal(full.Hud.Stats, back[0].Hud!.Stats);
        Assert.Equal(full.Bounties, back[0].Bounties);
        Assert.True(double.IsPositiveInfinity(back[0].Bounties[0].HoursLeft));
        Assert.Null(back[1].Skills);
        Assert.Null(back[1].Hud!.Extended);
        Assert.Equal(2f, back[1].Hud!.BloodCapacity);
    }

    [Fact]
    public void Health_round_trips_through_a_keyed_view()
    {
        var health = new HealthDetail(Id(1), [new HealthPartView("head", "Head", 80, 5, 2.5f, 0, 100, false), new HealthPartView("armL", "Left Arm", 10, 0, 0, 1, 90, true)]);
        var back = Keyed(ViewCodecs.HealthSync(), ViewCodecs.HealthCodec, [health]);
        Assert.Equal(health.Parts, back[0].Parts);
    }

    [Fact]
    public void Inventory_round_trips_through_a_keyed_view()
    {
        var grid = new InventoryView(6, 4, 8, 6, [
            new InventoryItemView(0, "10-gamedata.base", "Katana", "Weapon", 0, 0, 1, 4, 1, 3.5f, 0.9f) { Material = "MAT_STEEL", Charge = 0.25f },
            new InventoryItemView(1, "11-gamedata.base", "Bandage", "Backpack", 2, 1, 1, 1, 6, 0.1f, 1),
        ]) { CarryCapacity = 120.5f, TotalWeight = 22.75f };
        var detail = new InventoryDetail(Id(4), grid, ["Katana", "6 Bandage"]);
        var empty = new InventoryDetail(Id(5), null, []);
        var back = Keyed(ViewCodecs.InventorySync(), ViewCodecs.InventoryCodec, [detail, empty]);
        Assert.Equal(detail.Inventory, back[0].Inventory);
        Assert.Equal(grid.Items, back[0].ItemGrid!.Items);
        Assert.Equal((grid.Width, grid.Height, grid.BagWidth, grid.BagHeight, grid.CarryCapacity, grid.TotalWeight),
            (back[0].ItemGrid!.Width, back[0].ItemGrid!.Height, back[0].ItemGrid!.BagWidth, back[0].ItemGrid!.BagHeight, back[0].ItemGrid!.CarryCapacity, back[0].ItemGrid!.TotalWeight));
        Assert.Null(back[1].ItemGrid);
    }

    [Fact]
    public void Path_round_trips_at_a_sixty_fourth_through_a_keyed_view()
    {
        var path = new PathDetail(Id(1), [new Vector3(100.5f, 3.25f, -2000), new Vector3(-147_000.015625f, 0, 1)]);
        var back = Keyed(ViewCodecs.PathSync(), ViewCodecs.PathCodec, [path]);
        Assert.Equal(path.Path[0], back[0].Path[0]);
        Assert.Equal(path.Path[1], back[0].Path[1]);
    }
}
