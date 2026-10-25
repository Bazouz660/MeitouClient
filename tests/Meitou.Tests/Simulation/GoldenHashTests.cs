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
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0x4EB858E564D8E79DUL, 0x023BFFBBD619978FUL, 0x0E6640B2BE0FF449UL, 0x6D0A622959DFAB1CUL, 0x7D4345DF7DA6D137UL, 0xC0AFD355CA35860EUL],
        ["fight"] = [0x7256777FEC12AAB9UL, 0xC1107F462A8C0D22UL, 0x1FED4A1A497A9C89UL, 0xE8C446A675480FE2UL, 0x5B1C16F32856E773UL],
        ["duels"] = [0x73D2508F9CB64F9CUL, 0xE2A96FC3B9958401UL, 0xD07EAC18026212A5UL, 0x6112D2BE91A66773UL, 0xD0857D9C7FC3DAAEUL, 0x159725F44DECCB98UL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x67922200746BAF42UL, 0x298601126BDA5981UL, 0x71296AC49E295C6EUL],
        ["first-aid"] = [0xD52CAC3747758BC1UL, 0x509D51E1B009446DUL, 0x539C08F4CC4F439BUL, 0x7C1AC03038E6D483UL, 0xB8D8413E3AD00751UL, 0xBC335E164DEE9C9EUL],
        ["aggro"] = [0xEC0D50A9EBDC7A65UL, 0xC51A03549DC9CC1AUL, 0xA20EBA41AEB4CECEUL, 0x214D4FBD5E0A4E3BUL, 0x524472B083BAA85AUL],
        ["ranged"] = [0xA7F6C6FC5272A9FFUL, 0x0D5A54BE52416729UL, 0x6997177D67D19BF3UL, 0x0B5D2A56852BDE9FUL, 0xFF3E802DFE8A58E9UL],
        ["carry"] = [0x144E6C2173B54EDAUL, 0x9A66C2D788E0DAFEUL, 0xA6E14E13CB528CD1UL, 0xACEBCD90DC44DFE7UL, 0x3ACBF75B3DEF6D09UL, 0x9147269E41A3B1CCUL, 0x4511A01BF97A52C5UL],
        ["crime"] = [0x006AE21BF12857E3UL, 0x4B73C9AB62BBBAACUL, 0x56D64B2EA50F9790UL, 0xB458143E64834A1BUL, 0xAF8EEA91069FEE3BUL, 0xDC331BBBFB92D196UL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0xC418387B8A899605UL, 0xB4B473CD123CE82DUL, 0x496988D59195716BUL, 0xD7209567B7A00CFAUL, 0x6CE50833B8BC9245UL, 0x332E04250CDD85D0UL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0xDC661C22FD5EA8A2UL, 0x510E5E6E591F03C0UL, 0x90C8C64AC93DDE66UL, 0x78D95AE8B8FDAAC5UL, 0xDDFE34663C31FCEEUL],
        ["ai"] = [0x01A74124E404A290UL, 0xC4E01355F7EE1B0DUL],
        ["arrest"] = [0x94BAD1AF3C9180F4UL, 0x5A933C86D3128B94UL, 0xCA9A9CE8E2783060UL, 0x4D3BFDF642A1A1D1UL, 0x532D3462D5815EBAUL],
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
