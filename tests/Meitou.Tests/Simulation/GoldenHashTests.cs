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
        ["player"] = [0x1A4F9B34B7B70986UL, 0xB61C17CD0BA8D3B4UL, 0x3D11FF841428016EUL, 0xAA1C1A1C97A658B7UL, 0x30D6D5EEA4B8980FUL, 0x106295DA17FFFF7AUL],
        ["roaming"] = [0x78AF1B550B51BCD3UL, 0x7901D39E8BF5C2A3UL, 0x3E24ADAA743A7981UL, 0x81295E4179DB181FUL, 0xA07B911A83D3233EUL, 0x3655FE13EE6C7F38UL],
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0x4EB858E564D8E79DUL, 0x023BFFBBD619978FUL, 0x5F53902F5D0C8218UL, 0x3A3B0396A7370C73UL, 0x2FD5278972580D4DUL, 0xCF527556D5C3036DUL],
        ["fight"] = [0xAA99DA4B67BB392EUL, 0x32A8232650DEFF17UL, 0x0E90F87DDBADCDF7UL, 0x69B1BBDAED12D77EUL, 0xA3C82294570A4128UL],
        ["duels"] = [0xC70B98466E31BEC4UL, 0xCC696EE148360449UL, 0x47073A9A9A0AB5B0UL, 0x5AC167D8031D1B11UL, 0xB7680993410D59CEUL, 0xCC9F7BB70FA9A5C9UL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x67922200746BAF42UL, 0x298601126BDA5981UL, 0x71296AC49E295C6EUL],
        ["first-aid"] = [0xD52CAC3747758BC1UL, 0x509D51E1B009446DUL, 0x539C08F4CC4F439BUL, 0x7C1AC03038E6D483UL, 0xB8D8413E3AD00751UL, 0xBC335E164DEE9C9EUL],
        ["aggro"] = [0x1C962087C1E67BB6UL, 0x61C0FC4E6C2161C1UL, 0xBDAEB27C4B11AE24UL, 0x8266B825EE8A3818UL, 0x46E0911647D31014UL],
        ["ranged"] = [0x0A83EB3E3F88EF32UL, 0x478F79E4DBD913B0UL, 0x0863E1A47728A0AEUL, 0xCC8C5077A46B0E3CUL, 0x0EE78A8E31F4EA60UL],
        ["carry"] = [0x144E6C2173B54EDAUL, 0x9A66C2D788E0DAFEUL, 0xA6E14E13CB528CD1UL, 0xACEBCD90DC44DFE7UL, 0x3ACBF75B3DEF6D09UL, 0x9147269E41A3B1CCUL, 0x4511A01BF97A52C5UL],
        ["crime"] = [0xA67923762EDE3F4BUL, 0x829859AE31A51260UL, 0x0D897E43CDE10947UL, 0x8E788330EBD80C42UL, 0x7EA7F4C288CBE563UL, 0x058B4F754C6D5903UL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0xC418387B8A899605UL, 0xB4B473CD123CE82DUL, 0x496988D59195716BUL, 0xD7209567B7A00CFAUL, 0x6CE50833B8BC9245UL, 0x332E04250CDD85D0UL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0xDC661C22FD5EA8A2UL, 0x510E5E6E591F03C0UL, 0x90C8C64AC93DDE66UL, 0x78D95AE8B8FDAAC5UL, 0xDDFE34663C31FCEEUL],
        ["ai"] = [0x01A74124E404A290UL, 0xC4E01355F7EE1B0DUL],
        ["arrest"] = [0x9CCC0E42971C060DUL, 0xC0AC3C5950B2A74CUL, 0x590E2392DDD384C6UL, 0xB617F73FF3AB2249UL, 0xB364F75E8C394320UL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0xD3CBF715EF1BC0EBUL, 0xA7523993486E4A13UL, 0x5474B6D1632DAAC2UL, 0xCB32A40FE9483856UL],
        ["production"] = [0xAD1A97A458381AABUL, 0x7706A7D83423FA84UL, 0x5174C67E5B99DADDUL, 0x1A689DC7AFA8D6F6UL, 0xEDF3CFB16D706F62UL, 0x05792788FD61C369UL],
        ["wild"] = [0xB4DAB70315EF5E4EUL, 0x3E34912179CDC9FAUL, 0xC5CDBB80FC3AAD7FUL, 0x926FFFFCBD8B28A3UL],
        ["luckyren"] = [0x0108143DB9B1E85DUL],
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
