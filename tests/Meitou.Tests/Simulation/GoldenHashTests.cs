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
        ("goal-fights", AggroTests.GoalFightHashes),
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
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0x12AA7F07EA841195UL, 0x9923B3BC7075B3E6UL, 0x49B95DA0970C7637UL, 0x18CFFFEA6355FED2UL, 0x75D93D3E9CB60D3DUL, 0x5B08370490AE5DB5UL],
        ["fight"] = [0x68D954DEA414BC9DUL, 0x277BFC817A6E6D07UL, 0x7E7ED910BC016FCBUL, 0x3733A1C055897D85UL, 0x072A752B606AE38DUL],
        ["duels"] = [0x8F2B6C19C8258200UL, 0xD7A7249A4A0359D3UL, 0xED10741CF76741B4UL, 0xA1D646EBBF95E284UL, 0xE6EFB6D2CD5E31EBUL, 0x91C3A6BA0B1B3D9EUL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x5BCAA4EB049EC38EUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0x2FBE59352CE20EACUL, 0x5EF3ED8AE596C893UL, 0x6A08A3A801FA8D9DUL, 0x39A72938FED356E6UL, 0x8C91A341AC18EA0BUL, 0xCD6D92A58EB79D80UL],
        ["aggro"] = [0xA894E2545C237DCFUL, 0x43A038A42F20CEF6UL, 0xBDD699F4A5C3D209UL, 0x9B546ABD54280DE2UL, 0x16E406D43AF8378DUL],
        ["goal-fights"] = [0xE31AF9A13A510D93UL, 0x40C7F1C14ADA5B3AUL, 0x828DFED24D70FC58UL, 0x6517DCB5DD7D744BUL],
        ["ranged"] = [0x0EE823F3700D3BBDUL, 0x8FB3E4D6C80B658CUL, 0x7DB90447EFBCB50BUL, 0x09D5440DEB1CDA80UL, 0x66495252C9E0C9FFUL],
        ["carry"] = [0x755FEB141A8C01C7UL, 0x6E12306765EB7A5EUL, 0xB6A43E301817E49BUL, 0xAEC0165BA41CE6E5UL, 0x37F26ACF538F3177UL, 0x4B8F393B52C6DE48UL, 0x5F13B403420B711CUL],
        ["crime"] = [0x5790992287C3130CUL, 0x8BAA9BF7B3A78265UL, 0x2AAC497BA43478B5UL, 0xB85079B841081724UL, 0x8CBE689C3E19B4BDUL, 0x8D1A2D418FFF215AUL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0x4447F2E034B23198UL, 0x1D14987F6688F3F2UL, 0x65F2A68C6EA7ED4EUL, 0x05DE4BD06EEA910AUL, 0x8B82F70EBC5399C8UL, 0x99DE76A799C7EDEAUL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0x02B0FABFF0AB298BUL, 0x1A96548EA0E341C5UL, 0x621BF0B693CF4A72UL, 0x5181225FBA9C5D97UL, 0xD13EDC2381F6CC64UL],
        ["ai"] = [0x2474DB836F30F738UL, 0x0C3727D7D1C7DD0CUL],
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
