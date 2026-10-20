using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Net;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Net;

/// <summary>Views 5-14 (world and production): every kind round-trips populated and non-default, and the keyed delta handles insert, update, remove and reorder (docs/net.md, "World and production views").</summary>
public class WorldViewCodecTests
{
    delegate void Apply(ref NetReader r, StringTableReader s);

    /// <summary>One reliable view channel: a writer-side string table that keeps its definitions and the reader's matching table.</summary>
    sealed class Link
    {
        readonly StringTableWriter output = new(true);
        readonly StringTableReader input = new();

        public byte[]? Send(Func<NetWriter, StringTableWriter, bool> encode)
        {
            using var w = new NetWriter();
            return encode(w, output) ? w.Written.ToArray() : null;
        }

        public void Receive(byte[]? body, Apply apply)
        {
            Assert.NotNull(body);
            var r = new NetReader(body);
            apply(ref r, input);
            r.ExpectEnd();
        }
    }

    static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    static BuildingSnapshot Building(string id, float progress = 0.5f, string? parent = null) =>
        new(id, "wall-1", new Vector3(10.5f, -3f, 1e5f), new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), 2, parent, 7, progress, 12.5f,
            [new MaterialView("1-planks", 4, 1.5f), new MaterialView("2-iron plates", 2, 0)], true, true)
        { Dismantling = true, WallParent = "9-zone1.2-INGAME" };

    static void SameBuilding(BuildingSnapshot expected, BuildingSnapshot actual)
    {
        Assert.Equal(expected with { Materials = [] }, actual with { Materials = [] });
        Assert.Equal(expected.Materials, actual.Materials);
    }

    static void SameMachine(MachineSnapshot expected, MachineSnapshot actual)
    {
        Assert.Equal(expected with { Inputs = [], Storage = null, Farm = null, Craft = null }, actual with { Inputs = [], Storage = null, Farm = null, Craft = null });
        Assert.Equal(expected.Inputs, actual.Inputs);
        Assert.Equal(expected.Storage is null, actual.Storage is null);
        if (expected.Storage is { } es)
        {
            Assert.Equal(es with { Items = [] }, actual.Storage! with { Items = [] });
            Assert.Equal(es.Items, actual.Storage!.Items);
        }
        Assert.Equal(expected.Farm is null, actual.Farm is null);
        if (expected.Farm is { } ef)
        {
            Assert.Equal(ef with { PlantGrowth = [] }, actual.Farm!.Value with { PlantGrowth = [] });
            Assert.Equal(ef.PlantGrowth, actual.Farm.Value.PlantGrowth);
        }
        Assert.Equal(expected.Craft is null, actual.Craft is null);
        if (expected.Craft is { } ec)
        {
            Assert.Equal(ec.Queue, actual.Craft!.Queue);
            Assert.Equal(ec.Repeat, actual.Craft.Repeat);
            Assert.Equal(ec.Done, actual.Craft.Done);
        }
    }

    static MachineSnapshot FullMachine(string site, int count = 3) =>
        new(site, "27-well", [new MachineSlotView("1-copper", "Copper", count, 10, 0.25f), new MachineSlotView("2-water", "Water", 0, 5, 1f)],
            new MachineSlotView("3-plates", "Plates", 2, 8, 0.75f),
            new InventoryView(4, 3, 2, 1, [new InventoryItemView(0, "10-bread", "Bread", "main", 1, 2, 1, 1, 5, 0.5f, 0.9f) { Material = "m1", Charge = 0.4f }, new InventoryItemView(1, "11-axe", "Axe", "main", 0, 0, 2, 3, 1, 3.5f, 1f)])
            { CarryCapacity = 80.5f, TotalWeight = 4.25f },
            new FarmView(0.5f, 0.1f, 3, 4, FarmStatus.Growing, 1.5f, 0.75f, 0.25f) { PlantGrowth = [0.1f, -10f, 0.9f, 1f] },
            2, 1, 3)
        {
            IsContainer = true, TechLevel = 4, Operable = true, IsStorage = true,
            Craft = new CraftView([("k1", "Iron plate", 0.5f, 2.5f), ("k2", "Plank", 0f, 1f)], true,
                [new InventoryItemView(0, "12-plate", "Plate", "out", 0, 0, 1, 1, 2, 1f, 1f) { Material = "m2" }]),
            Power = new PowerView(PowerKind.Battery, 1f, 2f, 3f, 4f, 5f, true),
            Sound = new MachineSound(0.6f, true),
        };

    static ResearchSnapshot FullResearch(float rate = 1.5f, string description = "Lets you build\nmore walls.") =>
        new(3, 2, [("r1", "Iron", 0.25f), ("r2", "Steel", 0f)], rate,
            [new ResearchOption("r1", "Iron", "Metals", 2, 4.5f, true, false, 0.25f) { Description = description, Cost = [("Copper", 5), ("Plates", 2)] },
             new ResearchOption("r3", "Farming", "Food", 1, 1f, false, true, 0f)])
        { Finished = 6 };

    static void SameResearch(ResearchSnapshot expected, ResearchSnapshot actual)
    {
        Assert.Equal(expected with { Queue = [], Available = [] }, actual with { Queue = [], Available = [] });
        Assert.Equal(expected.Queue, actual.Queue);
        Assert.Equal(expected.Available.Count, actual.Available.Count);
        for (int i = 0; i < expected.Available.Count; i++)
        {
            Assert.Equal(expected.Available[i] with { Cost = [] }, actual.Available[i] with { Cost = [] });
            Assert.Equal(expected.Available[i].Cost, actual.Available[i].Cost);
        }
    }

    [Fact]
    public void Buildings_round_trip_with_every_field()
    {
        var link = new Link();
        var sync = ViewCodecs.BuildingsSync();
        var decoder = ViewCodecs.BuildingsDecoder();
        var sent = new List<BuildingSnapshot> { Building("1-zone0.0-INGAME"), Building("2-zone0.0-INGAME", 1f, "1-zone0.0-INGAME") with { Complete = false, Paused = false, Dismantling = false, WallParent = null } };

        link.Receive(link.Send((w, s) => sync.Encode(sent, true, T0, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));

        Assert.Equal(2, decoder.Items.Count);
        for (int i = 0; i < sent.Count; i++) SameBuilding(sent[i], decoder.Items[i]);
    }

    [Fact]
    public void Ground_items_containers_and_door_picks_round_trip()
    {
        var link = new Link();

        var items = new List<GroundItemSnapshot> { new(7, "10-bread", "Bread", new Vector3(1, 2, 3), -1.5f), new(-2, "11-axe", "", new Vector3(-4, 0, 5e4f), 3.1f) };
        var groundSync = ViewCodecs.GroundItemsSync();
        var groundDecoder = ViewCodecs.GroundItemsDecoder();
        link.Receive(link.Send((w, s) => groundSync.Encode(items, false, T0, w, s)), (ref NetReader r, StringTableReader s) => groundDecoder.Apply(ref r, s));
        Assert.Equal(items, groundDecoder.Items);

        var containers = new List<ContainerSpot> { new("c1", "chest", new Vector3(5, 6, 7)), new("c2", "safe", new Vector3(-5, 0, 0.5f)) };
        var containerSync = ViewCodecs.ContainersSync();
        var containerDecoder = ViewCodecs.ContainersDecoder();
        link.Receive(link.Send((w, s) => containerSync.Encode(containers, false, T0, w, s)), (ref NetReader r, StringTableReader s) => containerDecoder.Apply(ref r, s));
        Assert.Equal(containers, containerDecoder.Items);

        var picks = new List<DoorPick> { new("d1", "1-zone0.0-INGAME", 0, 3, new Vector3(1, 1, 1)), new("d2", "1-zone0.0-INGAME", 2, -1, new Vector3(9, 8, 7)) };
        var pickSync = ViewCodecs.DoorPicksSync();
        var pickDecoder = ViewCodecs.DoorPicksDecoder();
        link.Receive(link.Send((w, s) => pickSync.Encode(picks, false, T0, w, s)), (ref NetReader r, StringTableReader s) => pickDecoder.Apply(ref r, s));
        Assert.Equal(picks, pickDecoder.Items);
    }

    [Fact]
    public void Doors_unlocked_and_inspected_round_trip()
    {
        var link = new Link();

        var doors = new Dictionary<(string Building, int Door), DoorView>
        {
            [("b2", 1)] = new(0.5f, DoorMotion.Opening, true, false, true, new Vector3(1, 2, 3), "door-1"),
            [("b1", 0)] = new(1f, DoorMotion.Open, false, true),
            [("b1", 3)] = new(0f, DoorMotion.Closed, false, false, false, new Vector3(-1, 0, 1), "door-2"),
        };
        var doorSync = ViewCodecs.DoorsSync();
        var doorDecoder = ViewCodecs.DoorsDecoder();
        link.Receive(link.Send((w, s) => doorSync.Encode(doors, false, T0, w, s)), (ref NetReader r, StringTableReader s) => doorDecoder.Apply(ref r, s));
        Assert.Equal(doors.OrderBy(d => d.Key), doorDecoder.Value.OrderBy(d => d.Key));

        IReadOnlySet<string> unlocked = new HashSet<string> { "wall-1", "gate", "bed" };
        var unlockedSync = ViewCodecs.UnlockedSync();
        var unlockedDecoder = ViewCodecs.UnlockedDecoder();
        link.Receive(link.Send((w, s) => unlockedSync.Encode(unlocked, false, T0, w, s)), (ref NetReader r, StringTableReader s) => unlockedDecoder.Apply(ref r, s));
        Assert.True(unlocked.SetEquals(unlockedDecoder.Value));

        var detail = new BuildingDetail("1-zone0.0-INGAME", [new DoorDetail(true, false, 40, false), new DoorDetail(false, true, 100, true)]);
        var inspectSync = ViewCodecs.InspectedSync();
        var inspectDecoder = ViewCodecs.InspectedDecoder();
        link.Receive(link.Send((w, s) => inspectSync.Encode(detail, T0, w, s)), (ref NetReader r, StringTableReader s) => inspectDecoder.Apply(ref r, s));
        Assert.Equal(detail.Instance, inspectDecoder.Value!.Instance);
        Assert.Equal(detail.Doors, inspectDecoder.Value.Doors);

        link.Receive(link.Send((w, s) => inspectSync.Encode(null, T0, w, s)), (ref NetReader r, StringTableReader s) => inspectDecoder.Apply(ref r, s));
        Assert.Null(inspectDecoder.Value);
        Assert.Null(link.Send((w, s) => inspectSync.Encode(null, T0, w, s)));
    }

    [Fact]
    public void Machines_round_trip_full_and_minimal()
    {
        var link = new Link();
        var sync = ViewCodecs.MachinesSync();
        var decoder = ViewCodecs.MachinesDecoder();
        var minimal = new MachineSnapshot("m2", "bench", [], null, null, null, 0, 0, 0);
        var sent = new List<MachineSnapshot> { FullMachine("m1"), minimal };

        link.Receive(link.Send((w, s) => sync.Encode(sent, true, T0, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));

        Assert.Equal(2, decoder.Items.Count);
        SameMachine(sent[0], decoder.Items[0]);
        SameMachine(sent[1], decoder.Items[1]);
    }

    [Fact]
    public void Research_round_trips_and_an_empty_one_decodes_to_Empty()
    {
        var link = new Link();
        var sync = ViewCodecs.ResearchSync();
        var decoder = ViewCodecs.ResearchDecoder();

        var full = FullResearch();
        link.Receive(link.Send((w, s) => sync.Encode(full, true, T0, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        SameResearch(full, decoder.Value);

        // A header-only change (rate) is sent once the interval passed, with the options unchanged.
        var faster = full with { Rate = 3f };
        Assert.Null(link.Send((w, s) => sync.Encode(faster, false, T0 + TimeSpan.FromMilliseconds(50), w, s)));
        link.Receive(link.Send((w, s) => sync.Encode(faster, false, T0 + TimeSpan.FromMilliseconds(300), w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        SameResearch(faster, decoder.Value);
        Assert.Null(link.Send((w, s) => sync.Encode(faster, false, T0 + TimeSpan.FromSeconds(5), w, s)));

        // An option change, a removal and a reorder travel as a keyed delta.
        var reordered = faster with { Available = [faster.Available[1], faster.Available[0] with { Progress = 0.5f }] };
        link.Receive(link.Send((w, s) => sync.Encode(reordered, true, T0 + TimeSpan.FromSeconds(6), w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        SameResearch(reordered, decoder.Value);
        var trimmed = reordered with { Available = [reordered.Available[1]] };
        link.Receive(link.Send((w, s) => sync.Encode(trimmed, true, T0 + TimeSpan.FromSeconds(7), w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        SameResearch(trimmed, decoder.Value);

        link.Receive(link.Send((w, s) => sync.Encode(ResearchSnapshot.Empty, true, T0 + TimeSpan.FromSeconds(8), w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        Assert.Same(ResearchSnapshot.Empty, decoder.Value);
    }

    [Fact]
    public void Crafts_round_trip_per_site()
    {
        var link = new Link();
        var sync = ViewCodecs.CraftsSync();
        var decoder = ViewCodecs.CraftsDecoder();
        IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> crafts = new Dictionary<string, IReadOnlyList<CraftOptionView>>
        {
            ["bench-b"] = [new CraftOptionView("k1", "Iron plate", CraftKind.Item, 3, 2.5f, [("Iron", 2f), ("Wood", 0.5f)], true), new CraftOptionView("k2", "Sword", CraftKind.Weapon, 1, 8f, [], false)],
            ["bench-a"] = [new CraftOptionView("k3", "Armour", CraftKind.Robotics, 5, 0.25f, [("Plates", 4f)], true)],
        };

        link.Receive(link.Send((w, s) => sync.Encode(crafts, false, T0, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));

        Assert.Equal(crafts.Keys.Order(), decoder.Value.Keys.Order());
        foreach (var (site, options) in crafts)
        {
            var got = decoder.Value[site];
            Assert.Equal(options.Count, got.Count);
            for (int i = 0; i < options.Count; i++)
            {
                Assert.Equal(options[i] with { Inputs = [] }, got[i] with { Inputs = [] });
                Assert.Equal(options[i].Inputs, got[i].Inputs);
            }
        }
    }

    [Fact]
    public void Keyed_delta_handles_insert_update_remove_and_reorder()
    {
        var link = new Link();
        var sync = ViewCodecs.BuildingsSync();
        var decoder = ViewCodecs.BuildingsDecoder();
        void Deliver(IReadOnlyList<BuildingSnapshot> now, TimeSpan time, bool structural = false)
        {
            link.Receive(link.Send((w, s) => sync.Encode(now, structural, time, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
            Assert.Equal(now.Count, decoder.Items.Count);
            for (int i = 0; i < now.Count; i++) SameBuilding(now[i], decoder.Items[i]);
        }
        byte[]? Quiet(IReadOnlyList<BuildingSnapshot> now, TimeSpan time) => link.Send((w, s) => sync.Encode(now, false, time, w, s));

        var a = Building("a"); var b = Building("b"); var c = Building("c");
        Deliver([a, b], T0);
        var held = decoder.Items;

        // Nothing changed: nothing is sent, even for an equal copy of the list.
        Assert.Null(Quiet([a, b], T0 + TimeSpan.FromMilliseconds(50)));
        Assert.Same(held, decoder.Items);

        // Update: an item-only change waits for the minimum interval, then goes alone.
        var b2 = Building("b", 0.9f);
        Assert.Null(Quiet([a, b2], T0 + TimeSpan.FromMilliseconds(100)));
        var update = link.Send((w, s) => sync.Encode([a, b2], false, T0 + TimeSpan.FromMilliseconds(300), w, s));
        link.Receive(update, (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        SameBuilding(b2, decoder.Items[1]);
        Assert.Equal(0.9f, decoder.Items[1].Progress);

        // Insert in the middle (membership goes at once), then reorder, remove and reinsert.
        Deliver([a, c, b2], T0 + TimeSpan.FromMilliseconds(1260));
        Deliver([b2, c, a], T0 + TimeSpan.FromMilliseconds(1261));
        Deliver([c, a], T0 + TimeSpan.FromMilliseconds(1262));
        Deliver([c, a, b2], T0 + TimeSpan.FromMilliseconds(1263));
        Deliver([], T0 + TimeSpan.FromMilliseconds(1264));
        Assert.Empty(decoder.Items);
    }

    [Fact]
    public void Doors_delta_updates_one_door_and_drops_removed_ones()
    {
        var link = new Link();
        var sync = ViewCodecs.DoorsSync();
        var decoder = ViewCodecs.DoorsDecoder();
        IReadOnlyDictionary<(string Building, int Door), DoorView> first = new Dictionary<(string Building, int Door), DoorView>
        {
            [("b1", 0)] = new(0f, DoorMotion.Closed, false, false),
            [("b1", 1)] = new(1f, DoorMotion.Open, false, false),
        };
        link.Receive(link.Send((w, s) => sync.Encode(first, false, T0, w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        Assert.Equal(2, decoder.Value.Count);

        IReadOnlyDictionary<(string Building, int Door), DoorView> second = new Dictionary<(string Building, int Door), DoorView>
        {
            [("b1", 1)] = new(0.5f, DoorMotion.Closing, true, false),
            [("b0", 2)] = new(0f, DoorMotion.Closed, false, true),
        };
        link.Receive(link.Send((w, s) => sync.Encode(second, false, T0 + TimeSpan.FromMilliseconds(10), w, s)), (ref NetReader r, StringTableReader s) => decoder.Apply(ref r, s));
        Assert.Equal(second.OrderBy(d => d.Key), decoder.Value.OrderBy(d => d.Key));
        Assert.False(decoder.Value.ContainsKey(("b1", 0)));
    }
}
