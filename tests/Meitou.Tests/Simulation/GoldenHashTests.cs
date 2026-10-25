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
        ["fight"] = [0x9A80A650DB9E7C55UL, 0x72020BF5867E5566UL, 0xCAB29D4BA6C12985UL, 0x82E7605B3F2ABB68UL, 0x74EB0C1313B509E0UL],
        ["duels"] = [0x73D2508F9CB64F9CUL, 0xE2A96FC3B9958401UL, 0x6FDFF8DC184874B0UL, 0xE3C77A90297FF33EUL, 0xDA4D578C595094F5UL, 0x438C6C3C485296BDUL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x67922200746BAF42UL, 0x298601126BDA5981UL, 0x71296AC49E295C6EUL],
        ["first-aid"] = [0xD52CAC3747758BC1UL, 0x509D51E1B009446DUL, 0x539C08F4CC4F439BUL, 0x7C1AC03038E6D483UL, 0xB8D8413E3AD00751UL, 0xBC335E164DEE9C9EUL],
        ["aggro"] = [0xEC0D50A9EBDC7A65UL, 0x25D5A6B50D7069F4UL, 0xF84CADB3D5827B99UL, 0x581D2E751E7FB068UL, 0x92BC1410593AAB2DUL],
        ["ranged"] = [0xA7F6C6FC5272A9FFUL, 0x71AF0A51BF255B9AUL, 0x9B045B726C965015UL, 0xB645778EE28F3669UL, 0xC0DDCF6B71C088D4UL],
        ["carry"] = [0x144E6C2173B54EDAUL, 0x9A66C2D788E0DAFEUL, 0xA6E14E13CB528CD1UL, 0xACEBCD90DC44DFE7UL, 0x3ACBF75B3DEF6D09UL, 0x9147269E41A3B1CCUL, 0x4511A01BF97A52C5UL],
        ["crime"] = [0x006AE21BF12857E3UL, 0x4B73C9AB62BBBAACUL, 0x681C39D6C25B0CACUL, 0xDE51671B8E9CC9D4UL, 0x60FD2FC0217BE5EDUL, 0x727C279EBDF4FA05UL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0xC418387B8A899605UL, 0xB4B473CD123CE82DUL, 0x496988D59195716BUL, 0xD7209567B7A00CFAUL, 0x6CE50833B8BC9245UL, 0x332E04250CDD85D0UL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0xDC661C22FD5EA8A2UL, 0x510E5E6E591F03C0UL, 0x90C8C64AC93DDE66UL, 0x78D95AE8B8FDAAC5UL, 0xDDFE34663C31FCEEUL],
        ["ai"] = [0x01A74124E404A290UL, 0xC4E01355F7EE1B0DUL],
        ["arrest"] = [0x94BAD1AF3C9180F4UL, 0x13999A652C870457UL, 0x17288AFB5D95D835UL, 0x1D1F0677547B0CC1UL, 0xCA6D578FB4F2EC76UL],
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
