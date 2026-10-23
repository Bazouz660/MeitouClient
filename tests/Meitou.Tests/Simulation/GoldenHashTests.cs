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
        ["wander"] = [0x9203510895DB11E7UL, 0xFCFC923667CB7DDDUL, 0x63A7E495DC73FE28UL, 0x3AC1C2B7C9959BF8UL, 0x402C9815F7163D3DUL, 0xC4511CDCC521DC00UL],
        ["player"] = [0x67C1084E46968226UL, 0x830C7CEA8EC57A56UL, 0x31697931E19E0191UL, 0x17BDDF441D5EF7C1UL, 0x636D6BD64F0001B7UL, 0xBC49371B7A4A3397UL],
        ["roaming"] = [0x8F9047101B78297AUL, 0xA0EC8D38BB48EA0FUL, 0x7F4D0C361227B488UL, 0xEFCA61EFE8696AD9UL, 0xF84AA51BFC2658D3UL, 0x38EFA601124C151CUL],
        ["bodies"] = [0xC63F5F7FC9E6EA0DUL, 0xE125AE7F4DE07CAEUL, 0x9C37F1872374C409UL, 0x15FAD5ABDCE82618UL, 0xB1B01538333F2687UL, 0x623E0CDD270BAB47UL, 0x9760DA475A4D779BUL],
        ["fight"] = [0xA5DC7E06C96C1CCBUL, 0xF94960B1AEBF6630UL, 0x3003E561E4D7270FUL, 0xAA71B6A4DFDB8AB2UL, 0x8CC7381E239546B2UL],
        ["duels"] = [0x5C4D20AD84690394UL, 0x83E98386DA9E5B69UL, 0x1DD720164D90B33CUL, 0xE2255D09533DF1EAUL, 0x836C5E56D450DAB4UL, 0x852232ED1149D0C5UL],
        ["wander-parts"] = [0x5834FD4C9D0CBAFBUL, 0xAF0D1DE7704AB8A2UL, 0xAED68BEA1EE5FB3DUL, 0x8148EAB94CB50308UL],
        ["town-parts"] = [0xA49779A4294505C7UL, 0xB37D1BD6157BFCFBUL, 0x0978306FD317BAA7UL, 0xA504C645FA093E49UL],
        ["first-aid"] = [0xAA44DA416FE9BC89UL, 0xF856024EB5391FC3UL, 0x699657556E0B049BUL, 0xF911B512B056FA26UL, 0x987797A5C346E09AUL, 0x852765887DC596BCUL],
        ["aggro"] = [0xFB69CC1809418D0AUL, 0x70CF9E5F2A37E365UL, 0x8D81C739B207698AUL, 0xAE4582541EF05B42UL, 0x16B45BCDF17E36E8UL],
        ["ranged"] = [0xF18F5E0684EACC1EUL, 0x52502ED0E52C9B98UL, 0x7B7869AFBB45269EUL, 0xC16AD9991C639662UL, 0x51CA68C6B0FDAA12UL],
        ["carry"] = [0xD3EB27DFD3123552UL, 0x597CC9FC29B0B42EUL, 0xC24C71D749C5AEAAUL, 0xE13A952366319B52UL, 0x41FCF5841AC70882UL, 0x620DC31CD722D16FUL, 0xFDABB448492C6F3FUL],
        ["crime"] = [0x9A9670B42300C65DUL, 0x6B3803E7B76A810EUL, 0x25E617F1ED285154UL, 0x3BC963489968C427UL, 0x70257946E140EFE1UL, 0x3B6EE88C99EEBD0CUL],
        ["build"] = [0x5FD895B94DA240C1UL, 0x53E8274898C7DC09UL, 0xD9F65927413D60C5UL, 0x68EF60B1127E77C1UL, 0x8D3419D9F8A421A2UL, 0x3C82488B44D2F1A2UL, 0x2665970E23ECF4C7UL, 0xB7B06F3728C4666AUL],
        ["dialogue"] = [0xD808F202FB909A02UL, 0xC375E8653110647AUL, 0x0A47829C60CB019CUL, 0x42F95AE1DF6D1368UL, 0x65ABF95A2B78CC38UL, 0xDC5BFEB735B99ADAUL],
        ["ai"] = [0x530111E7DB579696UL, 0x820E7E080B19CEDBUL],
        ["arrest"] = [0x4535462D0CDFC65FUL, 0xA7F53D0459A70A81UL, 0x387CBC9D482BF374UL, 0x87FDD33BD088BFD5UL, 0x65467DCD49C1C4B2UL],
        ["kidnap"] = [0xA52166B8080903F1UL, 0x7E1E5F3A4F7406CBUL, 0xFCC7B4876E0F7AE9UL, 0xB33A2581D8BD1EDDUL, 0xF38BC33F850ADDCBUL],
        ["production"] = [0x8E4E889F61AD09D3UL, 0xF9CC599CA115E5C0UL, 0x09B6A788A58DAB83UL, 0x8BCB46DF5F8A38CDUL, 0x99AF5F24C474D3DDUL, 0xA1E4C7A85FD5D986UL],
        ["wild"] = [0x3A8FBCF8F2618264UL, 0x1ADEC1EDF6EE4BCDUL, 0xD03EB69B26C968CAUL, 0x9594229B470E1727UL],
        ["luckyren"] = [0xD3E7E62DA92D120EUL],
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
