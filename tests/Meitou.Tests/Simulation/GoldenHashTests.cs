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
        ["player"] = [0xF238AFC5F550FFCFUL, 0xE478389C2FFC5445UL, 0xA238AB70AF88B4DAUL, 0xB78FF89A09C1969FUL, 0x7B19325C4CE137C3UL, 0x760851C8B196AF5FUL],
        ["roaming"] = [0x6254B9D810CB4FADUL, 0x990C6069D32C264BUL, 0x5EB9D8B40FADFF29UL, 0x8D0AA75645221015UL, 0x18E9EACCF4E210E7UL, 0x73CCF21E28B660E0UL],
        ["bodies"] = [0xF687971D55D1FEC5UL, 0x6E14103FA9706E95UL, 0x633FC6377484A636UL, 0xE27E2A07C29EA4E1UL, 0x88FF38165A8297A2UL, 0x9441B5F64F2B3D9EUL, 0xCA926BB1830D23D9UL],
        ["fight"] = [0xEF9A412892AF271FUL, 0x96F1D97DB4A63846UL, 0x98C93C713793E158UL, 0x26DD39FA53A22DD3UL, 0xF21C81DEB27ED77BUL],
        ["duels"] = [0x9096C1CA1986206EUL, 0x191B222811EAF3D3UL, 0x97E2F706E2BC5242UL, 0x3DA7D9A60433B608UL, 0x6D26D593FB155AE3UL, 0x5755BE687B2BFE77UL],
        ["wander-parts"] = [0xB290D6E546B22E8BUL, 0x117970AEAD082661UL, 0x836FA52178779480UL, 0x6BD962576344B10AUL],
        ["town-parts"] = [0x08734201FCCFAC16UL, 0x4B8F6E4C9FFD7CC8UL, 0x0714BFD8703283E9UL, 0x996FCCC543125CBFUL],
        ["first-aid"] = [0xD52CAC3747758BC1UL, 0x509D51E1B009446DUL, 0x539C08F4CC4F439BUL, 0x7C1AC03038E6D483UL, 0xB8D8413E3AD00751UL, 0xBC335E164DEE9C9EUL],
        ["aggro"] = [0xE78F2527CB079A6EUL, 0xE024365C940BDF0EUL, 0xC9CA3C33B76AEF56UL, 0x5CC3EEF953C4D674UL, 0xF5416E4159AE861AUL],
        ["ranged"] = [0x36BB3D2D1AE00B1AUL, 0x97D4AA7D54BD95FEUL, 0x2CE5465910778900UL, 0x9DE8615358E7DEEAUL, 0x2E98B694F5C5E10FUL],
        ["carry"] = [0x144E6C2173B54EDAUL, 0x9A66C2D788E0DAFEUL, 0xA6E14E13CB528CD1UL, 0xACEBCD90DC44DFE7UL, 0x3ACBF75B3DEF6D09UL, 0x9147269E41A3B1CCUL, 0x4511A01BF97A52C5UL],
        ["crime"] = [0xFE7222A822658BEFUL, 0x4894F658A7EAC82AUL, 0x7448306311891D9AUL, 0xADAA21EAA001146FUL, 0x957FCD2825CC3197UL, 0xE644EE50BEA53132UL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0xC418387B8A899605UL, 0xB4B473CD123CE82DUL, 0x496988D59195716BUL, 0xD7209567B7A00CFAUL, 0x6CE50833B8BC9245UL, 0x332E04250CDD85D0UL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0xDC661C22FD5EA8A2UL, 0x510E5E6E591F03C0UL, 0x90C8C64AC93DDE66UL, 0x78D95AE8B8FDAAC5UL, 0xDDFE34663C31FCEEUL],
        ["ai"] = [0x01A74124E404A290UL, 0xC4E01355F7EE1B0DUL],
        ["arrest"] = [0xC96C9CACC9361506UL, 0x2B9506A5534A0F7FUL, 0x64CACB3D77C9A2E9UL, 0xB09429352CA4A73AUL, 0xE59BC8DE21E43AE2UL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0xD3CBF715EF1BC0EBUL, 0xA7523993486E4A13UL, 0x5474B6D1632DAAC2UL, 0xCB32A40FE9483856UL],
        ["production"] = [0xAD1A97A458381AABUL, 0x7706A7D83423FA84UL, 0x5174C67E5B99DADDUL, 0x1A689DC7AFA8D6F6UL, 0xEDF3CFB16D706F62UL, 0x05792788FD61C369UL],
        ["wild"] = [0xF66EF78C3DC28B7EUL, 0x08AA813FA2D1D432UL, 0xB21E15845E9526CAUL, 0x3DB1A36DB9B0A985UL],
        ["luckyren"] = [0x465FBD82CBC4D792UL],
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
