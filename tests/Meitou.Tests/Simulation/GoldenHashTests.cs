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
        ["wander"] = [0x9C9775CE6597DD6BUL, 0xFC7CDBEE899281FCUL, 0xBB6C16EF02A628E9UL, 0xBDFB49EB74CDADFBUL, 0xF598C41B6B09487BUL, 0x67FB41FE12600A56UL],
        ["player"] = [0x9D2594097781F39DUL, 0xA526FDA283EA1DF1UL, 0x87A3809AEB36D17BUL, 0x118BAC6ED3ABCBA3UL, 0x7F821C745FA48383UL, 0x2C2C7393280D614FUL],
        ["roaming"] = [0x38D72B4558FA87C4UL, 0x01F7410FA4FA45BBUL, 0xD875656223F3FB26UL, 0x46AF676F962042B5UL, 0x52EE9A33CD9996F6UL, 0xB8CE9E1E07866E87UL],
        ["bodies"] = [0x8996DF8EE939ADA2UL, 0x56C0302ACE799D22UL, 0xFEE74C380A6CC39DUL, 0x00FFD31192E7727DUL, 0xEB5A5F56347A0B3AUL, 0x17EF8A068727170CUL, 0x512479B9842EE400UL],
        ["fight"] = [0xAFD100B5D5A103EFUL, 0x6E1E6CB99A854811UL, 0xB22C13565140E525UL, 0x8CDACD918E27C204UL, 0x96FE5A49E785F339UL],
        ["duels"] = [0xCB48BE7C00CEA9B5UL, 0x7999323A412E2EE9UL, 0xC1219CA1B65C5329UL, 0x09140343A9074FFEUL, 0x4238CCA30F2024F6UL, 0x78A5C001D908D07BUL],
        ["wander-parts"] = [0x3543B26C51534781UL, 0x8B2D54DAA6E46871UL, 0x0A60CEAEB398B8A0UL, 0x147F1D52DE1D4B4AUL],
        ["town-parts"] = [0x0B77F8D9DE908484UL, 0xC9D553A03304D51DUL, 0xCEF5FAC707704781UL, 0xBFB990A86D0CF1EEUL],
        ["first-aid"] = [0xBF17302ED6B23714UL, 0x9CF95520B6C2E14AUL, 0x3D9E0AA38A43E24FUL, 0xD7AC9D6817486BE5UL, 0x0AF171E0EB5A29E9UL, 0xFE4DF26B5E8C9D66UL],
        ["aggro"] = [0xC195D045D841D14EUL, 0x1C83DE9535DB1DCEUL, 0x06DE00BD6365D7A5UL, 0x1C0D8B7DDEAE4175UL, 0x4281EDC1CDCECE8BUL],
        ["ranged"] = [0xE568D90CF6852D36UL, 0xA93E005EFCCBA40FUL, 0xA226B0AFF22585EAUL, 0xA0480187369AE329UL, 0xE1A90C255C9C1989UL],
        ["carry"] = [0xFB6457CC83EE9BDAUL, 0xB9B54DAB899194CFUL, 0xB5C9FB32D5EAF09CUL, 0x5190672AC93417FEUL, 0x48ECA15308CF9F63UL, 0x3F09B6E2D7296795UL, 0x81C5577CB5946D41UL],
        ["crime"] = [0x21C5A7B717D594DBUL, 0xC1126C11F921A5AEUL, 0xB3C629A77CEC63C9UL, 0xB7B2FC98AE63E970UL, 0x851E88E5491C107BUL, 0x508983180EAC773AUL],
        ["build"] = [0x65D4C75216A7692CUL, 0xA8C813F2CD3ECC26UL, 0x4C77BC946AC3E9D8UL, 0x5E86BB43B164AAC7UL, 0xB1D7B4CE0B74E759UL, 0xAA9839F9FD80A9C1UL, 0x0002677BB86FF1E8UL, 0x2B97AC9EF598FEBBUL],
        ["dialogue"] = [0xDD56E9565C8AD0FEUL, 0x3E0A77AA4F593314UL, 0x3FD582EAC093CC20UL, 0x6963BBAEB6C0E9FCUL, 0x784B35604C393664UL, 0xA3DFB392251E4108UL],
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
