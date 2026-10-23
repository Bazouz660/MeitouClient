using Meitou.Tests.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins the state hashes of the scripted scenarios the other tests use, so a refactor that is meant to change nothing is checked to
/// change nothing (<see cref="A_scenario_hashes_the_same_at_1_4_and_16_threads"/> only checks that the threads agree with each other). A change of behaviour that is meant must
/// update these numbers in the same commit and say so.
/// </summary>
public class GoldenHashTests(ITestOutputHelper output)
{
    static readonly (string Name, Func<int, List<ulong>> Run)[] Scenarios =
    [
        ("wander", t => DeterminismTests.Hashes(7, t)),
        ("player", PlayerTests.Scripted),
        ("roaming", RoamingTests.Scripted),
        ("bodies", t => BodyWiringTests.Hashes(9, t, 2000)),
        ("fight", FightWiringTests.Hashes),
        ("duels", t => DuelTests.Hashes(5, t, out _)),
        ("wander-parts", t => Parts(WanderSystem.Create(11, t, 200, minPartition: 4), 1, 30, 90, 300)),
        ("town-parts", t => Parts(TownParts(t), 1, 40, 200, 700)),
        ("first-aid", FirstAidTests.Scripted),
        ("aggro", t => AggroTests.Hashes(t)),
        ("ranged", t => RangedTests.Hashes(t)),
        ("carry", CarryTests.Scripted),
        ("crime", CrimeTests.Scripted),
        ("build", ConstructionTests.Scripted),
        ("dialogue", DialogueTests.Scripted),
        ("ai", AiTests.Scripted),
        ("arrest", ArrestTests.Scripted),
        ("kidnap", KidnapTests.Scripted),
        ("production", ProductionTests.Scripted),
        ("wild", WildTests.Scripted),
        ("luckyren", LuckyrenTests.Scripted),
    ];

    public static TheoryData<string> ScenarioNames => [.. Scenarios.Select(s => s.Name)];

    /// <summary>A roaming town with bodies and feeding, cut into many partitions (the other worlds here are mostly one partition at the default size).</summary>
    static Meitou.Simulation.World TownParts(int threads)
    {
        var db = SyntheticTown.Database(roaming: true, anatomy: true, items: true);
        return SyntheticTown.World(21, threads, db: db, bodies: true, bodyTimeScale: 2000, feed: new Meitou.Simulation.Items.FeedSettings(), minPartition: 4);
    }

    static List<ulong> Parts(Meitou.Simulation.World w, params int[] checkpoints)
    {
        using var world = w;
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in checkpoints)
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    static readonly Dictionary<string, ulong[]> Golden = new()
    {
        ["wander"] = [0xBAF0B5BF2616CE5FUL, 0xA4F9F8B0CE901A2FUL, 0xD6740CC1A1BCB3D4UL, 0x63AF3B0810419AE9UL, 0x8DDB54DDF59E681FUL, 0x5FBCC5E9BBBBB9EDUL],
        ["player"] = [0xC603FC971D4F7B5CUL, 0xD9AB7B5819FE987CUL, 0xCC66965249FFC90EUL, 0x1EC4B135E5A1B9F3UL, 0xCEEA6A3BE5379753UL, 0xE8EAF2C5BB0CC132UL],
        ["roaming"] = [0x32306F2B3E0281D2UL, 0x69BBFC68B9BBADDDUL, 0xBCAB93D3475B6261UL, 0x5D2A2E28E3BA2556UL, 0x3F4A60D07445B8BAUL, 0xC94945D5DEB7EC78UL],
        ["bodies"] = [0xF687971D55D1FEC5UL, 0x3F375C1BBA90AEA4UL, 0xF6956317C6A59FEEUL, 0xB8E2C0846F6AA5C1UL, 0xE00B316DE5D9E2C4UL, 0x9AB83936476CD286UL, 0xA48B1D99C8804B5EUL],
        ["fight"] = [0xEF9A412892AF271FUL, 0x96F1D97DB4A63846UL, 0x3F97475E26C429A5UL, 0xF0BFEE9ABC0C71BDUL, 0xA744DC8E8B5EB5B4UL],
        ["duels"] = [0x9096C1CA1986206EUL, 0x8E3159649D2BDCBBUL, 0x6F0E2312AC32A0E0UL, 0x491721251B1EB714UL, 0xD8F2E5A4E8B7C533UL, 0x444C956EFC068537UL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x08734201FCCFAC16UL, 0x60ADC24BEEE31E24UL, 0x772C5D2318E48B4AUL, 0x14538B7EC5F26711UL],
        ["first-aid"] = [0xD52CAC3747758BC1UL, 0xEB606D3BB47C18E1UL, 0x182D59443257E48BUL, 0xAC14CF43E1DD02C2UL, 0xE7ECE348F33A791AUL, 0xB0513D9992A48FA4UL],
        ["aggro"] = [0x15E75D362262D73AUL, 0x89DD88680748E784UL, 0x21E482128CE526E7UL, 0xC781C31D64FB0026UL, 0xF59D3333E5C5E96FUL],
        ["ranged"] = [0x36BB3D2D1AE00B1AUL, 0x97D4AA7D54BD95FEUL, 0x2CE5465910778900UL, 0x95E334623BD4FE46UL, 0xA43EDA58B1A5BE43UL],
        ["carry"] = [0x144E6C2173B54EDAUL, 0x9A66C2D788E0DAFEUL, 0x88D38E0446854D10UL, 0x51591A51EC6AC885UL, 0xA8600698FD1F7AAFUL, 0xD76B6D8A50812D9BUL, 0x4D17149631059552UL],
        ["crime"] = [0x66F96F6CCBD6A43BUL, 0xFB87A69CADFC02A1UL, 0xC9C035EAE75A72FEUL, 0xE24DA608059C0152UL, 0xC3E2C91F520B667EUL, 0x25F5217DFDB4BCF1UL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0x3C270B597FC1D352UL, 0xD3749604E391F1E3UL, 0xD9A7C269A0DAFFA0UL, 0x31C41ABAF5C5ACACUL, 0xFF8BB3546254AF21UL, 0xC8FCE052EF33F2E2UL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0xDC661C22FD5EA8A2UL, 0xF5A253B9EBD668BAUL, 0x800A9F68B4861E93UL, 0x5BBA2DBC78E89C52UL, 0xA8E4E5FEAFBCDC2AUL],
        ["ai"] = [0x3805BF85FE2B00E2UL, 0x558796933164A712UL],
        ["arrest"] = [0x3258460518DF91D6UL, 0x604E8949DE0ABDBAUL, 0xDC30404D2E144A2AUL, 0xDE47B6A21F3CA466UL, 0x45F7DB2D5E25DFBFUL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0xD3CBF715EF1BC0EBUL, 0xA7523993486E4A13UL, 0x5474B6D1632DAAC2UL, 0xCB32A40FE9483856UL],
        ["production"] = [0x68E186433E12F38EUL, 0x6AA2E112480D267EUL, 0x692FB7789E12C7D2UL, 0xA8EFCC7274D884B8UL, 0x72986564CDBF5D39UL, 0x99A9E0E88E30112FUL],
        ["wild"] = [0xCF9CE8E65EBF8E0DUL, 0xE4178815A9F2807AUL, 0x78E37733618B3490UL, 0x9539D38C0BC2D0B1UL],
        ["luckyren"] = [0x2B52A4D12F598C8AUL],
    };

    [Fact]
    public void Scripted_scenarios_hash_as_pinned()
    {
        // The pins were made on Windows; the wandering scenarios use MathF.Sin/Cos/Atan2, which come from the platform's C library and can differ in the last bit elsewhere.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "hashes are pinned on Windows");
        var failures = new List<string>();
        foreach (var (name, run) in Scenarios)
        {
            var got = run(4);
            output.WriteLine($"[\"{name}\"] = [{string.Join(", ", got.Select(h => $"0x{h:X16}UL"))}],");
            if (!Golden.TryGetValue(name, out var want)) failures.Add($"{name}: not pinned");
            else if (!want.SequenceEqual(got)) failures.Add($"{name}: hashes changed");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Theory, MemberData(nameof(ScenarioNames))]
    public void A_scenario_hashes_the_same_at_1_4_and_16_threads(string name)
    {
        var run = Scenarios.Single(s => s.Name == name).Run;
        var one = run(1);
        Assert.Equal(one, run(4));
        Assert.Equal(one, run(16));
    }
}
