using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using SimWorld = Meitou.Simulation.World;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>First aid and resting in play: the treatment rate and kit drain against the doc, a medic walking up and treating, and determinism.</summary>
public class FirstAidTests
{
    // ------------------------------------------------------------------ the formulas (docs/game/character-stats.md "First aid, medkits and robot repair")

    [Fact]
    public void The_treatment_rate_and_the_kit_drain_follow_the_documented_formulas()
    {
        var c = BaseGame;   // medic speed mult 3, robot medic speed mult 0.33, medkit drain 1 / 99 = 2 / 0.1 after the loader's scaling
        // lerp(1, 15, min(skill, quality) x 0.01) x medic speed mult: skill 50 with a quality 40 kit is limited by the kit (t = 0.4).
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 50, 40, false), 4);
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 90, 40, false), 4);     // a better medic gains nothing from a poor kit
        Assert.Equal((1 + 14 * 0.4f) * 3, MedicalState.TreatmentRate(c, 40, 100, false), 4);    // nor a better kit from a poor medic
        Assert.Equal(3f, MedicalState.TreatmentRate(c, 0, 100, false), 4);                      // t = 0: 1 x 3
        Assert.Equal(45f, MedicalState.TreatmentRate(c, 100, 100, false), 4);                   // t = 1: 15 x 3
        Assert.Equal(45f * 0.33f, MedicalState.TreatmentRate(c, 100, 100, true), 4);            // a robot kit is slower
        // lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt: an expert uses a kit 20 times slower than a beginner.
        Assert.Equal(1.05f * 2, MedicalState.KitDrain(c, 50, 2, false), 4);
        Assert.Equal(2f, MedicalState.KitDrain(c, 0, 1, false), 4);
        Assert.Equal(0.1f, MedicalState.KitDrain(c, 100, 1, false), 4);
        Assert.Equal(20f, MedicalState.KitDrain(c, 0, 1, false) / MedicalState.KitDrain(c, 100, 1, false), 3);
        Assert.Equal(2 * 0.33f, MedicalState.KitDrain(c, 0, 1, true), 4);
    }

    [Fact]
    public void Splint_need_and_splinting_follow_the_documented_rules()
    {
        // docs/game/character-stats.md "Splints": a broken right leg, h = -20 with 60 bandaged.
        var m = Healthy();
        var leg = m.Parts[RLeg];
        leg.Flesh = -20;
        leg.Bandage = 60;
        Assert.True(m.NeedsSplint(Human, 50));                // rig 0 < b, (h - s + rig) x 1.05 < 50, rig + 1 < trunc(50)
        Assert.False(m.NeedsSplint(Human, 1));                // rig + 1 < trunc(X) fails
        // Splint Kit quality 20, medic 50: t = 20 x 0.01f, just under 0.2, so the cap 1 - trunc(t x -100) is 20; rate lerp(1, 15, t) x medic speed mult 3 = 11.4 per unit of dt.
        var ctx = Context();
        Assert.Equal(11.4f, m.Splint(1, 50, 20, ctx), 3);
        Assert.Equal(11.4f, leg.Rig, 3);
        Assert.Equal(20 - 11.4f, m.Splint(1, 50, 20, ctx), 3); // clamped to the cap
        Assert.Equal(20f, leg.Rig, 3);
        Assert.Equal(0f, m.Splint(1, 50, 20, ctx));           // nothing else to splint
        Assert.False(leg.IsDown);                             // h - s + rig = 0: the leg holds again
        // rig never passes s + b: a part with only 5 bandaged takes 5.
        var arm = m.Parts[LArm];
        arm.Flesh = 0;
        arm.Bandage = 5;
        Assert.Equal(5f, m.Splint(1, 50, 20, ctx), 3);
        // A vital part is never splinted.
        var vital = Healthy();
        vital.Parts[HeadIx].Flesh = 10;
        vital.Parts[HeadIx].Bandage = 60;
        Assert.False(vital.NeedsSplint(Human, 50));
        Assert.Equal(0f, vital.Splint(1, 50, 20, ctx));
    }

    // ------------------------------------------------------------------ in the world

    const float KitQuality = 40, KitCharges = 400, SplintQuality = 20;

    static GameDatabase KitDatabase()
    {
        FcsRecord Kit(string id, string name, int function, float quality, float charges)
        {
            var r = new FcsRecord { StringId = id, Flags = 0x10, Name = name, RecordType = FcsRecordType.ITEM };
            r.Ints["item function"] = function;
            r.Floats["quality"] = quality;
            r.Floats["charges"] = charges;
            r.Floats["weight kg"] = 2;
            return r;
        }
        var file = new FcsFile();
        file.Records.AddRange([Kit("72-t", "Test kit", 1, KitQuality, KitCharges), Kit("73-t", "Test robot kit", 12, 50, 500), Kit("74-t", "Test splint kit", 2, SplintQuality, 100)]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    /// <summary>A small world of hand-made characters on open ground, with the movement, first aid and body systems (and combat when asked).</summary>
    sealed class Rig : IDisposable
    {
        public SimWorld World { get; }
        public ItemFactory Items { get; }
        readonly OpenGroundWalkability walk = new(SyntheticTown.Ground);
        public List<CharacterId> Ids { get; } = [];

        public Rig(int threads = 1, int minPartition = 16, FirstAidSettings? settings = null, ulong seed = 5)
        {
            Items = new ItemFactory(KitDatabase(), BaseGame);
            var movement = new MovementSystem(new PathService(walk, true));
            var systems = new List<ITickSystem> { new PlayerSystem(), movement, new FirstAidSystem(BaseGame, BodyOptions.Default, Items, settings), new BodySystem(BaseGame, BodyOptions.Default) };
            World = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition }, walk, systems);
        }

        /// <summary>Adds a character at a spot; <paramref name="kits"/> are record ids put in its inventory. All join squad <paramref name="squad"/> (created when new).</summary>
        public CharacterId Add(float x, float z, bool player = true, int squad = 0, float medic = 50, params string[] kits)
        {
            var inventory = new Inventory();
            foreach (var k in kits) inventory.Add(Items.Create(k)!);
            var stats = new CharacterStats();
            stats[StatsEnumerated.Medic] = medic;
            stats[StatsEnumerated.Strength] = 50;
            var cold = new CharacterCold
            {
                Name = "C" + Ids.Count, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50), Inventory = inventory, IsPlayer = player,
                SquadId = squad,
            };
            var hot = new CharacterHot { Position = new Vector3(x, 300, z), MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle };
            var id = World.Characters.Spawn(hot, cold, 0);
            Ids.Add(id);
            var sq = World.Squads.Find(squad);
            if (sq is null) World.Squads.Add(sq = new Squad { Id = squad, TemplateId = "t" });
            sq.Members.Add(id);
            if (player) World.Player.Local.Selection.Add(id);
            return id;
        }

        public CharacterCold Cold(CharacterId id) => World.Characters.Cold(id.Slot)!;
        public MedicalState Body(CharacterId id) => Cold(id).Medical!;
        public CharacterTask Task(CharacterId id) => (CharacterTask)World.Characters.Previous[id.Slot].Task;
        public Vector3 Where(CharacterId id) => World.Characters.Previous[id.Slot].Position;
        public void Say(SimCommand c) => World.Commands.Enqueue(c);
        public ItemInstance Kit(CharacterId id) => Cold(id).Inventory!.All().First(i => i.ItemFunction is 1 or 12);

        /// <summary>Wounds a part so first aid has something to do: its flesh is set to <paramref name="fraction"/> of the maximum.</summary>
        public void Hurt(CharacterId id, int part, float fraction) => Body(id).Parts[part].Flesh = Body(id).Parts[part].MaxHp * fraction;

        public void Dispose() => World.Dispose();
    }

    static float Bandage(MedicalState m) => m.Parts.Sum(p => p.Bandage);

    [Fact]
    public void A_medic_walks_to_the_patient_and_bandages_it_with_the_kit_at_the_documented_rate()
    {
        using var rig = new Rig(settings: new FirstAidSettings { AutoNpcs = false });
        var medic = rig.Add(1000, 2000, medic: 50, kits: "72-t");
        var patient = rig.Add(1130, 2000, squad: 1);
        rig.Hurt(patient, RLeg, 0.3f);                 // 70 to bandage
        rig.Hurt(patient, LArm, 0.6f);                 // 40
        rig.World.RunTicks(2);
        float startX = rig.Where(medic).X;
        rig.Say(new TreatOrder([medic], patient) { Tick = rig.World.Tick });
        // The medic walks (the task is Treat), the patient is untouched until it arrives.
        rig.World.RunTicks(10);
        Assert.Equal(CharacterTask.Treat, rig.Task(medic));
        Assert.Equal(0, Bandage(rig.Body(patient)));
        long arrived = -1;
        float before = 0;
        for (int t = 0; t < 1500 && arrived < 0; t++)
        {
            before = Bandage(rig.Body(patient));
            rig.World.RunTick();
            if (Bandage(rig.Body(patient)) > 0) arrived = rig.World.Tick;
        }
        Assert.True(arrived > 0, "the medic reached the patient");
        Assert.True(rig.Where(medic).X > startX + 50);
        // One tick of treatment: skill 50 (injury multiplier 1), kit quality 40: lerp(1, 15, 0.4) x 3 per second, a tick is 1/30 s.
        float expected = MedicalState.TreatmentRate(BaseGame, 50, KitQuality, false) / 30;
        Assert.Equal(2 * expected, Bandage(rig.Body(patient)) - before, 1e-3);   // both wounded parts take the rate
        var kit = rig.Kit(medic);
        float charges = kit.Charges;
        float medicBefore = rig.Cold(medic).Stats![StatsEnumerated.Medic];
        // Runs on until nothing is left to bandage.
        for (int t = 0; t < 3000 && rig.Task(medic) == CharacterTask.Treat; t++) rig.World.RunTick();
        Assert.Equal(CharacterTask.Idle, rig.Task(medic));
        Assert.False(rig.Body(patient).NeedsAid(Human, false));
        foreach (var p in rig.Body(patient).Parts) Assert.True(p.Flesh + p.Bandage >= p.MaxHp - 0.01f, $"{p.Template.Name} is bandaged up to its maximum");
        Assert.True(kit.Charges < charges && kit.Charges > 0, "the kit was used, not used up");
        Assert.True(rig.Cold(medic).Stats![StatsEnumerated.Medic] > medicBefore, "treating trains the medic skill");
    }

    [Fact]
    public void A_splint_order_rigs_the_broken_leg_with_the_kit_and_a_medic_without_one_refuses()
    {
        using var rig = new Rig(settings: new FirstAidSettings { AutoNpcs = false });
        var medic = rig.Add(1000, 2000, medic: 50, kits: "74-t");
        var bare = rig.Add(1000, 2200, medic: 50);           // beyond the kit reach of its squad mate's kit
        var patient = rig.Add(1060, 2000, squad: 1);
        var leg = rig.Body(patient).Parts[RLeg];
        leg.Flesh = -10;
        leg.Bandage = 60;
        rig.World.RunTicks(2);
        Assert.True(leg.IsDown);
        rig.Say(new SplintOrder([bare], patient) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);
        Assert.Equal(CharacterTask.Idle, rig.Task(bare));   // "I don't have a splint kit."
        var kit = rig.Cold(medic).Inventory!.All().First(i => i.ItemFunction == 2);
        float charges = kit.Charges;
        rig.Say(new SplintOrder([medic], patient) { Tick = rig.World.Tick });
        rig.World.RunTicks(2);
        Assert.Equal(CharacterTask.Treat, rig.Task(medic));
        for (int t = 0; t < 3000 && rig.Task(medic) == CharacterTask.Treat; t++) rig.World.RunTick();
        Assert.Equal(CharacterTask.Idle, rig.Task(medic));
        Assert.Equal(SplintQuality, leg.Rig, 2);              // up to the cap, 20 for quality 20 (see the formula test)
        Assert.False(leg.IsDown, $"h {leg.Flesh} s {leg.Stun} rig {leg.Rig}");
        Assert.True(kit.Charges < charges, "the kit drained");
    }

    // ------------------------------------------------------------------ determinism

    internal static List<ulong> Scripted(int threads) => Scripted(threads, out _);

    /// <summary>The scripted scenario; <paramref name="bandaged"/> is the bandage the players and the NPCs had put on at the end (to show the script does something).</summary>
    internal static List<ulong> Scripted(int threads, out (float Players, float Npcs) bandaged)
    {
        using var rig = new Rig(threads, minPartition: 1, seed: 12);
        var a = rig.Add(1000, 2000, player: true, medic: 60, kits: "72-t");
        var b = rig.Add(1060, 2000, player: true);
        var c = rig.Add(1100, 2060, player: true);
        var d = rig.Add(1500, 2000, player: false, squad: 1, medic: 30, kits: "72-t");
        var e = rig.Add(1530, 2000, player: false, squad: 1);
        var f = rig.Add(1560, 2030, player: false, squad: 1);
        rig.Hurt(b, RLeg, 0.3f);
        rig.Hurt(c, LArm, 0.5f);
        rig.Hurt(e, RArm, 0.1f);
        rig.Hurt(f, RLeg, 0.6f);
        void Say(SimCommand cmd) => rig.Say(cmd);
        Say(new TreatOrder([a], b) { Tick = 5 });
        Say(new RestOrder([c]) { Tick = 8 });
        Say(new MedicJobCommand([a]) { Tick = 200 });
        Say(new MoveOrder([c], new Vector3(1300, 0, 2200)) { Tick = 260 });
        Say(new TreatOrder([a], a) { Tick = 400 });
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 4, 60, 150, 400, 900, 1500 })
        {
            rig.World.RunTicks(target - ran);
            ran = target;
            hashes.Add(rig.World.StateHash());
        }
        bandaged = (Bandage(rig.Body(b)) + Bandage(rig.Body(c)) + Bandage(rig.Body(a)), Bandage(rig.Body(e)) + Bandage(rig.Body(f)));
        return hashes;
    }

    [Fact]
    public void A_scripted_game_of_first_aid_and_rest_hashes_the_same_at_1_4_and_16_threads()
    {
        var one = Scripted(1, out var done);
        Assert.True(done.Players > 100 && done.Npcs > 100, $"the script treats: {done}");
        Assert.Equal(one, Scripted(4));
        Assert.Equal(one, Scripted(16));
        Assert.Equal(one, Scripted(1));
    }
}

/// <summary>The kits of the installed game: a CHARACTER that lists one is given it by the usual loadout rules.</summary>
[Slow]
public class FirstAidInstallTests
{
    [Fact]
    public void A_character_that_lists_a_kit_carries_it()
    {
        Assert.SkipWhen(InstallData.BaseGame is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var items = new ItemFactory(db, GameConstants.FromDatabase(db));
        var citizen = db.OfType(FcsRecordType.CHARACTER).First(r => r.Name == "Citizen" && r.GetReferences("inventory").Any(i => i.TargetStringId == "515-gamedata.base"));
        var inventory = items.Build(citizen, null);
        Assert.Contains(inventory.All(), i => i.Record == "515-gamedata.base" && i.ItemFunction == 1);
    }
}
