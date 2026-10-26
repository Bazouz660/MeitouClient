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
        ["player"] = [0x16F9CCB24EDE7690UL, 0x7924539F69796648UL, 0x688CD08C1A95ADFEUL, 0xF64C29ACA5F03ECEUL, 0xAFD62CD719D76FFAUL, 0x8EA8462683D708A7UL],
        ["roaming"] = [0x94A2DB4EA2850679UL, 0x19EE18369857B174UL, 0x9A13B47F32AD8BCBUL, 0x7FFE038EF516BCE5UL, 0xC3045EF2A353C8AAUL, 0xE2073EB898E8CF55UL],
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0xE08EFB54DA3446B9UL, 0x0BE40BAF4FE2602CUL, 0xEF9EEB2B67F7BE1DUL, 0x4E44977FF180823BUL, 0x3298E63516A12114UL, 0x4140726FFA4EFE95UL],
        ["fight"] = [0x1356945C7B3D86F1UL, 0x217D4A738EAB1C29UL, 0x26FD9414EC65A95FUL, 0x2C3E2A90A1192981UL, 0x5BA4B3BBE94024E2UL],
        ["duels"] = [0x08B2A5EF3AC7171BUL, 0xE83CC765136424F3UL, 0x8795144C4DFA54C7UL, 0x5CA3C7AB33915C48UL, 0x4D3E3353085BF09EUL, 0xC76F17760940636BUL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x095278448BD17B20UL, 0x3B5BB5DCAF5B5B89UL, 0x8535C6494D1CA936UL],
        ["first-aid"] = [0x2FBE59352CE20EACUL, 0x036274814F48901CUL, 0xF7DC5BB1F012B3FBUL, 0xA92266E16DE33604UL, 0x5AF7926697A69869UL, 0x7F89D1544A17D95EUL],
        ["aggro"] = [0x80AA718FE8EEB273UL, 0xD601BDF0D07C9766UL, 0x94341D217D2502C7UL, 0x374502AD5F39AEE3UL, 0xC7B0120BCFB8CECAUL],
        ["ranged"] = [0x23FE2811924491DCUL, 0x41B06DF74F33491AUL, 0x8ADC927858DDDE3CUL, 0xFBF7C29AE4ACBAEDUL, 0x1FA9F488D134915CUL],
        ["carry"] = [0x85A112D1431D461BUL, 0x30C9B1E21D610D51UL, 0x0D2D0596D75989D2UL, 0x3F59B32C28EF8073UL, 0x8D8A7003EB49622EUL, 0xEC0085DC58842855UL, 0x9B61B52ECFDFB65BUL],
        ["crime"] = [0x2E219A3452DE1F1BUL, 0x7D38323FFBCC991FUL, 0xAA777FEC6ACCBBC3UL, 0x498917B73316CEDCUL, 0x89932ED5EBD85A1CUL, 0x7DEC576EFDF31FBAUL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0x4447F2E034B23198UL, 0x1D14987F6688F3F2UL, 0x65F2A68C6EA7ED4EUL, 0x05DE4BD06EEA910AUL, 0x8B82F70EBC5399C8UL, 0x99DE76A799C7EDEAUL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0x02B0FABFF0AB298BUL, 0x1A96548EA0E341C5UL, 0x621BF0B693CF4A72UL, 0x5181225FBA9C5D97UL, 0xD13EDC2381F6CC64UL],
        ["ai"] = [0xF55877C9311BB8FEUL, 0x215C25CDE3696219UL],
        ["arrest"] = [0x437C1D4E5389DC47UL, 0x720BF807B9FB3F1EUL, 0x37293637EF8223B0UL, 0x10FAE1C659A4BBF7UL, 0x09009D7B756ABBF9UL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0x73AAEC48E68591D3UL, 0x3ED1ADEF13DDC142UL, 0x5C594EAE1DF2C568UL, 0xBAE3466D25092BC4UL],
        ["production"] = [0xAD1A97A458381AABUL, 0x7706A7D83423FA84UL, 0x5174C67E5B99DADDUL, 0x1A689DC7AFA8D6F6UL, 0xEDF3CFB16D706F62UL, 0x0FE6D95D4FD2F9CBUL],
        ["wild"] = [0xA8ED9B2732D1A158UL, 0x02543B181F3877C1UL, 0x541F679A92DC31D9UL, 0x889F728F7BF8E44EUL],
        ["luckyren"] = [0x9968C5EC4BF60437UL],
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
