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
        ["player"] = [0x16F9CCB24EDE7690UL, 0x8107CCF5D73C572EUL, 0x339443CD6BE4A61DUL, 0x11964BA8705B5068UL, 0xAC777071C589064EUL, 0xA6B501D97B118EA4UL],
        ["roaming"] = [0x94A2DB4EA2850679UL, 0x19EE18369857B174UL, 0x9A13B47F32AD8BCBUL, 0x7FFE038EF516BCE5UL, 0xC3045EF2A353C8AAUL, 0xE2073EB898E8CF55UL],
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0x12AA7F07EA841195UL, 0x9923B3BC7075B3E6UL, 0x33791D988C65C16BUL, 0xAB23509F5205808CUL, 0x018F1A531A0E3F13UL, 0x1237559E7B34B8DEUL],
        ["fight"] = [0x8CAA6E39B6139CA4UL, 0x04EECE3B97B0FDFEUL, 0x8302C6D6942E9AF4UL, 0x100B6452E087D4AAUL, 0xB5E2B90404DE8016UL],
        ["duels"] = [0xD93F6C82E5E46B4DUL, 0xD2C6534C197DECC6UL, 0x8353F453BADD30CFUL, 0xDABE20EDC9E5E1FDUL, 0x817BEACDA67611CDUL, 0xECE77FA2B0AB134CUL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x5BCAA4EB049EC38EUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0x2FBE59352CE20EACUL, 0x5EF3ED8AE596C893UL, 0x6A08A3A801FA8D9DUL, 0x39A72938FED356E6UL, 0x8C91A341AC18EA0BUL, 0xCD6D92A58EB79D80UL],
        ["aggro"] = [0x8AAC656E8480385CUL, 0xCB90F2CBE248CC21UL, 0x0822E657DE26B331UL, 0x78213838FD4DDDE7UL, 0x3CF6F09AB3E61638UL],
        ["ranged"] = [0x4D0981E3A12D17F6UL, 0xB7E19DE1C842A003UL, 0x51887DF52E245229UL, 0x817FC9AA44C6CC68UL, 0x4BF50850584EE10CUL],
        ["carry"] = [0x755FEB141A8C01C7UL, 0x6E12306765EB7A5EUL, 0xB6A43E301817E49BUL, 0xAEC0165BA41CE6E5UL, 0x37F26ACF538F3177UL, 0x4B8F393B52C6DE48UL, 0x5F13B403420B711CUL],
        ["crime"] = [0x5790992287C3130CUL, 0xA70C7195AB42F06FUL, 0x6091FF44C37A591EUL, 0x66B7D2483C802C5FUL, 0x9FC61E472C67A7A1UL, 0x970D14799AF30E3FUL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0x4447F2E034B23198UL, 0x1D14987F6688F3F2UL, 0x65F2A68C6EA7ED4EUL, 0x05DE4BD06EEA910AUL, 0x8B82F70EBC5399C8UL, 0x99DE76A799C7EDEAUL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0x02B0FABFF0AB298BUL, 0x1A96548EA0E341C5UL, 0x621BF0B693CF4A72UL, 0x5181225FBA9C5D97UL, 0xD13EDC2381F6CC64UL],
        ["ai"] = [0xF55877C9311BB8FEUL, 0x960F04F3C2C3A8E3UL],
        ["arrest"] = [0xCA6D7A7951C81D46UL, 0x932F6400262663C2UL, 0xA39086A9943F7DA5UL, 0x3295F80442499E27UL, 0xD240849F3E7B6F22UL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0xE056DA4E7BB0F837UL, 0x1B2FF026C7C4F051UL, 0x270184B34C1FE0F9UL, 0x493B89974BE243C8UL],
        ["production"] = [0xF8ACD9EEC9A0472FUL, 0x62CFB57EC6117B28UL, 0x42B13E4E867A9648UL, 0x47565339B3528B76UL, 0x4EB5E81033F3750EUL, 0xC8BFD8A9A83BDF53UL],
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
