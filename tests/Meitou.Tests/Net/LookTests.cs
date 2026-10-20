using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Net;
using Meitou.Net.Replication;
using Meitou.Simulation;
using Meitou.Simulation.Saving;

namespace Meitou.Tests.Net;

/// <summary>Looks on the wire (docs/net.md, "Looks"): the <c>LookDefinition</c> codec, and recipes that rebuild the host's looks from the game data.</summary>
public class LookTests
{
    static FcsRecord Appearance()
    {
        var record = new FcsRecord { Type = (int)FcsRecordType.CHARACTER_APPEARANCE, Name = "look", StringId = "77-look" };
        record.Bools["sex female"] = true;
        record.Floats["Height"] = 112.5f;
        record.Floats["Hair Colour"] = 0.25f;
        record.Ints["morph index"] = 3;
        record.Vector3s["Skin Tone"] = new Vector3(0.1f, 0.2f, 0.3f);
        record.Strings["head"] = "12-head.mod";
        record.Strings["hair style"] = "";
        record.References["race"] = [new("5-race.mod", 0, 0, 0)];
        return record;
    }

    static IReadOnlyList<(uint Id, AppearanceRecipe Recipe)> Samples() =>
    [
        (1, new AppearanceRecipe.Rolled("10-hiver.base", "204-gamedata.base", 0x7FFFFFFF)),
        (2, new AppearanceRecipe.Rolled("11-bonedog.base", null, -3)
        {
            Shape = new CharacterShapeInput
            {
                Sliders = new Dictionary<string, float> { ["Shoulders"] = 111, ["Height"] = 95.5f }, Female = true, Muscle = 0.6f, Starvation = 0.25f,
                Missing = new HashSet<Limb> { Limb.LeftArm, Limb.RightLeg }, HideStump = HideStump.BothArms, LegRatio = -0.125f, NarrowRightThigh = true,
            },
        }),
        (3, new AppearanceRecipe.Kit("12-drifter.base", new LoadoutWire(
            Seed: 123456, Character: "12-drifter.base", Race: "5-race.mod", Female: true, Appearance: Appearance(), Generated: true, MorphIndex: 3,
            Clothing: [new("20-shirt.base", 55, "21-cotton.base"), new("22-boots.base", 0, null)], Backpack: new("23-pack.base", 40, null),
            Carried: [new("24-lantern.base", 0, null)], Crossbow: new("25-bow.base", null, null, 0, "back", 1),
            Weapons: [new("26-katana.base", "27-maker.base", "28-model.base", 7, "hip", 0), new("29-sabre.base", null, "30-model.base", -1, "back", 2)],
            Limbs: [new LoadoutLimbWire(1, null), null, new LoadoutLimbWire(2, "31-leg.base"), new LoadoutLimbWire(3, null)]))),
        (4_000_000_000, new AppearanceRecipe.Kit("32-rat.base", new LoadoutWire(
            Seed: -1, Character: null, Race: "33-race.base", Female: false, Appearance: new FcsRecord(), Generated: false, MorphIndex: -1,
            Clothing: [], Backpack: null, Carried: [], Crossbow: null, Weapons: [], Limbs: [null, null, null, null]))),
    ];

    /// <summary>The canonical bytes of a recipe (every string a literal), so equal recipes give equal bytes whatever the table state.</summary>
    static byte[] Canonical(AppearanceRecipe recipe)
    {
        using var w = new NetWriter();
        LookCodec.Write(w, StringTableWriter.CreateLiteralOnly(), recipe);
        return w.Written.ToArray();
    }

    static void AssertSame(AppearanceRecipe expected, AppearanceRecipe actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(Canonical(expected), Canonical(actual));
        switch (expected, actual)
        {
            case (AppearanceRecipe.Rolled e, AppearanceRecipe.Rolled a):
                Assert.Equal((e.RecordId, e.FactionId, e.Seed), (a.RecordId, a.FactionId, a.Seed));
                break;
            case (AppearanceRecipe.Kit e, AppearanceRecipe.Kit a):
                var (x, y) = (e.Loadout, a.Loadout);
                Assert.Equal(e.RecordId, a.RecordId);
                Assert.Equal((x.Seed, x.Character, x.Race, x.Female, x.Generated, x.MorphIndex), (y.Seed, y.Character, y.Race, y.Female, y.Generated, y.MorphIndex));
                Assert.Equal(x.Appearance.Floats.ToArray(), y.Appearance.Floats.ToArray());
                Assert.Equal(x.Appearance.Strings.ToArray(), y.Appearance.Strings.ToArray());
                Assert.Equal(x.Clothing, y.Clothing);
                Assert.Equal(x.Backpack, y.Backpack);
                Assert.Equal(x.Carried, y.Carried);
                Assert.Equal(x.Crossbow, y.Crossbow);
                Assert.Equal(x.Weapons, y.Weapons);
                Assert.Equal(x.Limbs, y.Limbs);
                break;
        }
        Assert.Equal(expected.Shape is null, actual.Shape is null);
        if (expected.Shape is { } s && actual.Shape is { } t)
        {
            Assert.Equal(s.Sliders.OrderBy(p => p.Key, StringComparer.Ordinal), t.Sliders.OrderBy(p => p.Key, StringComparer.Ordinal));
            Assert.Equal((s.Female, s.Muscle, s.Starvation, s.HideStump, s.LegRatio, s.NarrowLeftThigh, s.NarrowRightThigh),
                (t.Female, t.Muscle, t.Starvation, t.HideStump, t.LegRatio, t.NarrowLeftThigh, t.NarrowRightThigh));
            Assert.Equal(s.Missing.Order(), t.Missing.Order());
        }
    }

    [Fact]
    public void Look_definitions_round_trip_and_repeated_ids_become_string_references()
    {
        var samples = Samples();
        var table = new StringTableWriter(confirmOnSend: true);
        var strings = new StringTableReader();
        int[] lengths = new int[2];
        for (int pass = 0; pass < 2; pass++)
        {
            using var w = new NetWriter();
            foreach (var (id, recipe) in samples) LookCodec.WriteDefinition(w, table, id, recipe);
            lengths[pass] = w.Length;
            var r = new NetReader(w.Written);
            foreach (var (id, recipe) in samples)
            {
                var read = LookCodec.ReadDefinition(ref r, strings, out uint readId);
                Assert.Equal(id, readId);
                AssertSame(recipe, read);
            }
            r.ExpectEnd();
        }
        Assert.True(lengths[1] < lengths[0], $"The second pass ({lengths[1]} B) should reference the ids the first ({lengths[0]} B) defined.");
    }

    // ---- Game data ----

    static (string, string, string, AttachMode, AttachSlot?, string?, string?, WeaponShown, int)[] Parts(CharacterAppearance look) =>
        [.. look.Parts.Select(p => (p.Record.StringId, p.Mesh, p.Field, p.Mode, p.Slot, p.Point, p.Material?.StringId, p.Shown, p.HideSide))];

    static void AssertSameLook(CharacterAppearance host, CharacterAppearance? client)
    {
        Assert.NotNull(client);
        Assert.Equal((host.Race.StringId, host.Character?.StringId, host.Animal?.StringId, host.Female),
            (client.Race.StringId, client.Character?.StringId, client.Animal?.StringId, client.Female));
        Assert.Equal((host.BodyMesh, host.BodyTexture, host.BodyNormal, host.BodyMask, host.PartMap),
            (client.BodyMesh, client.BodyTexture, client.BodyNormal, client.BodyMask, client.PartMap));
        Assert.Equal((host.Head?.StringId, host.Hair?.StringId, host.Beard?.StringId), (client.Head?.StringId, client.Hair?.StringId, client.Beard?.StringId));
        Assert.Equal((host.SkinToneParameter, host.HairColour, host.HiddenLimbs), (client.SkinToneParameter, client.HairColour, client.HiddenLimbs));
        Assert.Equal(host.PoseWeights.OrderBy(p => p.Key, StringComparer.Ordinal), client.PoseWeights.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(Parts(host), Parts(client));
        Assert.Equal(host.BodyLayers.Select(l => (l.Record.StringId, l.Diffuse, l.Normal, l.ColourMap)), client.BodyLayers.Select(l => (l.Record.StringId, l.Diffuse, l.Normal, l.ColourMap)));
        Assert.Equal(host.Lights.Select(l => l.Bone), client.Lights.Select(l => l.Bone));
    }

    /// <summary>The recipe through the wire into a fresh client cache over its own source, built on the thread pool.</summary>
    static CharacterAppearance? Rebuild(AppearanceRecipe recipe, LookCache cache, uint id, StringTableWriter table, StringTableReader strings)
    {
        using var w = new NetWriter();
        LookCodec.WriteDefinition(w, table, id, recipe);
        var r = new NetReader(w.Written);
        var read = LookCodec.ReadDefinition(ref r, strings, out uint readId);
        r.ExpectEnd();
        cache.Define(readId, read);
        Assert.True(cache.WhenBuilt(readId).Wait(TimeSpan.FromMinutes(1)));
        Assert.True(cache.IsReady(readId));
        return cache.Resolve(readId);
    }

    [Fact]
    [Slow]
    public void Rolled_and_kit_recipes_rebuild_the_hosts_parts_and_meshes_from_the_game_data()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        string root = InstallData.Install!.Root;
        var host = new RecordingAppearances(new GeneratedAppearances(db, root));
        var registry = new LookRegistry(host);
        var cache = new LookCache(new GeneratedAppearances(db, root), db);
        var (table, strings) = (new StringTableWriter(confirmOnSend: true), new StringTableReader());
        var prosthetic = db.OfType(FcsRecordType.LIMB_REPLACEMENT).Where(r => r.GetInt("slot") == 53).OrderBy(r => r.StringId, StringComparer.Ordinal).FirstOrDefault();

        var people = db.OfType(FcsRecordType.CHARACTER).OrderBy(r => r.StringId, StringComparer.Ordinal).ToList();
        var picked = Enumerable.Range(0, 16).Select(i => people[i * people.Count / 16]).ToList();
        var faction = db.OfType(FcsRecordType.FACTION).OrderBy(r => r.StringId, StringComparer.Ordinal).First().StringId;
        int rolled = 0, kits = 0;
        for (int i = 0; i < picked.Count; i++)
        {
            var record = picked[i];
            if (host.Create(record.StringId, i % 2 == 0 ? faction : null, 1000 + i) is not { } look) continue;
            var recipe = Assert.IsType<AppearanceRecipe.Rolled>(registry.RecipeOf(registry.IdOf(look)));
            AssertSameLook(look, Rebuild(recipe, cache, registry.IdOf(look), table, strings));
            rolled++;
            if (look.Loadout is not { } loadout) continue;

            // The fallback for a look the recorder never saw: a Kit from the look's own loadout makes the same look.
            AssertSameLook(look, Rebuild(Assert.IsType<AppearanceRecipe.Kit>(AppearanceRecipe.FromLook(look)), cache, 100_000 + (uint)i, table, strings));

            // A kit with a stump, a crushed arm and a prosthetic leg (boots over it lose a half).
            var kit = SavedKit.Copy(loadout);
            kit.Limbs[0] = new LoadoutLimb(1);
            kit.Limbs[1] = new LoadoutLimb(3);
            if (prosthetic is not null) kit.Limbs[3] = new LoadoutLimb(2, prosthetic);
            if (host.Create(record.StringId, kit) is not { } kitLook) continue;
            var kitRecipe = Assert.IsType<AppearanceRecipe.Kit>(registry.RecipeOf(registry.IdOf(kitLook)));
            AssertSameLook(kitLook, Rebuild(kitRecipe, cache, registry.IdOf(kitLook), table, strings));
            kits++;
        }
        Assert.True(rolled >= 8 && kits >= 8, $"{rolled} rolled and {kits} kit looks were built.");

        // Animals: one recipe per species, rebuilt the same.
        foreach (var animal in db.OfType(FcsRecordType.ANIMAL_CHARACTER).OrderBy(r => r.StringId, StringComparer.Ordinal).Take(4))
        {
            if (host.Create(animal.StringId, null, 7) is not { } look) continue;
            Assert.Equal(new AppearanceRecipe.Rolled(animal.StringId, null, 0), registry.RecipeOf(registry.IdOf(look)));
            AssertSameLook(look, Rebuild(registry.RecipeOf(registry.IdOf(look))!, cache, registry.IdOf(look), table, strings));
        }
    }
}
