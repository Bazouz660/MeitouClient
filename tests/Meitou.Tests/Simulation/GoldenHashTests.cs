using Meitou.Tests.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins the state hashes of the scripted scenarios the other tests use, so a refactor that is meant to change nothing is checked to
/// change nothing (the thread-count tests only check that the threads agree with each other). A change of behaviour that is meant must
/// update these numbers in the same commit and say so.
/// </summary>
public class GoldenHashTests(ITestOutputHelper output)
{
    static readonly (string Name, Func<List<ulong>> Run)[] Scenarios =
    [
        ("wander", () => DeterminismTests.Hashes(7, 4)),
        ("player", () => PlayerTests.Scripted(4)),
        ("roaming", () => RoamingTests.Scripted(4)),
        ("bodies", () => BodyWiringTests.Hashes(9, 4, 2000)),
        ("fight", () => FightWiringTests.Hashes(4)),
        ("duels", () => DuelTests.Hashes(5, 4, out _)),
        ("wander-parts", () => Parts(WanderSystem.Create(11, 4, 200, minPartition: 4), 1, 30, 90, 300)),
        ("town-parts", () => Parts(TownParts(), 1, 40, 200, 700)),
        ("first-aid", () => FirstAidTests.Scripted(4)),
        ("aggro", () => AggroTests.Hashes(4)),
        ("ranged", () => RangedTests.Hashes(4)),
        ("carry", () => CarryTests.Scripted(4)),
        ("crime", () => CrimeTests.Scripted(4)),
        ("build", () => ConstructionTests.Scripted(4)),
        ("dialogue", () => DialogueTests.Scripted(4)),
    ];

    /// <summary>A roaming town with bodies and feeding, cut into many partitions (the other worlds here are mostly one partition at the default size).</summary>
    static Meitou.Simulation.World TownParts()
    {
        var db = SyntheticTown.Database(roaming: true, anatomy: true, items: true);
        return SyntheticTown.World(21, 4, db: db, bodies: true, bodyTimeScale: 2000, feed: new Meitou.Simulation.Items.FeedSettings(), minPartition: 4);
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
        ["bodies"] = [0xC63F5F7FC9E6EA0DUL, 0xE125AE7F4DE07CAEUL, 0x0D8C8E92EA236555UL, 0x4D87E3FFAD867FB2UL, 0x3E3D123FFBB1011AUL, 0x556C9888A0A7A129UL, 0x28B853DF422C2490UL],
        ["fight"] = [0xA5DC7E06C96C1CCBUL, 0xF94960B1AEBF6630UL, 0x8694E75E5C4E134BUL, 0x55FDB445BD78280CUL, 0x488DAE4EA3E166FAUL],
        ["duels"] = [0x5C4D20AD84690394UL, 0x83E98386DA9E5B69UL, 0x918EB7F8902CF3B4UL, 0x2F4A8768C59CF88AUL, 0x2C1D5890BFA17B53UL, 0x29A177383E63C9DBUL],
        ["wander-parts"] = [0x5834FD4C9D0CBAFBUL, 0xAF0D1DE7704AB8A2UL, 0xAED68BEA1EE5FB3DUL, 0x8148EAB94CB50308UL],
        ["town-parts"] = [0xA49779A4294505C7UL, 0xB37D1BD6157BFCFBUL, 0x0978306FD317BAA7UL, 0xA504C645FA093E49UL],
        ["first-aid"] = [0xA584EEEE5DC551CFUL, 0x9EC209B3466FED78UL, 0x274A4817DB52A349UL, 0x0E4215A0B0E0EF4AUL, 0x3DA0D84379011DD1UL, 0xB619964B14B666F1UL],
        ["aggro"] = [0xFB69CC1809418D0AUL, 0x70CF9E5F2A37E365UL, 0xB4F73AD07AC9D206UL, 0x3EA6379AC49D9FD4UL, 0x326258B31B90731FUL],
        ["ranged"] = [0xF18F5E0684EACC1EUL, 0xE57F49C1E72E9E10UL, 0x59AFBF345F147CF1UL, 0x36EE94B055640F26UL, 0x8A484C1D66962DD5UL],
        ["carry"] = [0xEEE52143FF4A616CUL, 0xA8C0D1E4C12CE74DUL, 0x51907E0AEC1031C4UL, 0xB86FB0BB05A8649EUL, 0xA2AE8A3D05633B26UL, 0x431F4662C71A33DCUL, 0x9FF06996AA3718B5UL],
        ["crime"] = [0x8CE03D2F6E7CB231UL, 0xD84CAC87714684FDUL, 0xD5BC6BAFFE093CE1UL, 0x51F3181AD1563895UL, 0xEADF84A676D4CEBAUL, 0x6DBA93A485327B89UL],
        ["build"] = [0x5FD895B94DA240C1UL, 0x53E8274898C7DC09UL, 0xD9F65927413D60C5UL, 0x68EF60B1127E77C1UL, 0x8D3419D9F8A421A2UL, 0x3C82488B44D2F1A2UL, 0x2665970E23ECF4C7UL, 0xB7B06F3728C4666AUL],
        ["dialogue"] = [0xD808F202FB909A02UL, 0xC375E8653110647AUL, 0x0A47829C60CB019CUL, 0x42F95AE1DF6D1368UL, 0x65ABF95A2B78CC38UL, 0xDC5BFEB735B99ADAUL],
    };

    [Fact]
    public void Scripted_scenarios_hash_as_pinned()
    {
        // The pins were made on Windows; the wandering scenarios use MathF.Sin/Cos/Atan2, which come from the platform's C library and can differ in the last bit elsewhere.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "hashes are pinned on Windows");
        var failures = new List<string>();
        foreach (var (name, run) in Scenarios)
        {
            var got = run();
            output.WriteLine($"[\"{name}\"] = [{string.Join(", ", got.Select(h => $"0x{h:X16}UL"))}],");
            if (!Golden.TryGetValue(name, out var want)) failures.Add($"{name}: not pinned");
            else if (!want.SequenceEqual(got)) failures.Add($"{name}: hashes changed");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
