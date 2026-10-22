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
        ["player"] = [0x1867314DF4C2ED05UL, 0x45B7DBF4A2EDD593UL, 0xF11D8AF954CAA4C9UL, 0x522E598477814D6AUL, 0xCCA7EC135DA70D0AUL, 0x3C9C94D3B0A86EFAUL],
        ["roaming"] = [0x8EF4D5F7049289AAUL, 0x940490B41EF61C59UL, 0x0B2B693E66BAE99AUL, 0x0C36CE0883EB6710UL, 0x090F63009871C687UL, 0xAD611D4B5480FC00UL],
        ["bodies"] = [0xE208478497813B28UL, 0x29EC7FCB5B302DF3UL, 0x77C5247181F3F2C7UL, 0x91458F7639B6582DUL, 0xEA9F0893CEFA9736UL, 0xA581CC74F3105FAEUL, 0x1D1E3DC20237490DUL],
        ["fight"] = [0x6BB4A971A6E4BDD1UL, 0xB7F32A7B136DE809UL, 0x4747D7317D47594EUL, 0xF1363B43A63352B0UL, 0xC6791624BC1096B7UL],
        ["duels"] = [0x0E5FB6218AF08719UL, 0x835A997D67A51B01UL, 0x516C9D033AB4BB29UL, 0x82CD46B930956C2FUL, 0xC96BDC69FAC5A341UL, 0x143473D855EDC086UL],
        ["wander-parts"] = [0x3543B26C51534781UL, 0x8B2D54DAA6E46871UL, 0x0A60CEAEB398B8A0UL, 0x147F1D52DE1D4B4AUL],
        ["town-parts"] = [0xA58DA00B93D12070UL, 0x739B1FD6E87FFD94UL, 0xE87D4C787A188EAFUL, 0x7E42A4B0CC31A189UL],
        ["first-aid"] = [0xAFAD7769C436AC9CUL, 0x38C43A917ECA7885UL, 0xB79D122CD38CDA96UL, 0x87DBD544E901E434UL, 0xD9ED984093B10D91UL, 0x015A015A09D30EABUL],
        ["aggro"] = [0x38FD3A8A1FB70FCBUL, 0x8AF6FA80A19968A0UL, 0xCD7CE3088D03FFFDUL, 0x9ABA600F19CF11E6UL, 0xA238B016E6FF2CA8UL],
        ["ranged"] = [0xE6F2B57F7E7B99CAUL, 0x8BC0F72FD287E978UL, 0x856E8266C7164587UL, 0xAFB178CBE4E44DEBUL, 0xA98780F180309899UL],
        ["carry"] = [0x9ADEC0334F6E42B7UL, 0x7E0F46F1EBB4A054UL, 0x9BA03986D1A040F5UL, 0x1356D433654BA57CUL, 0x0C1DF5975CAEAE06UL, 0xE6185E0403BA80CCUL, 0xD4BDC4C6685EBCAFUL],
        ["crime"] = [0x191996ABD7EF76EBUL, 0xCBAB35E145615D91UL, 0xBBDF44BD747BDC90UL, 0xF3E5728D86AC201EUL, 0xAB835F9372F80129UL, 0x0FD69AFBCC42F04AUL],
        ["build"] = [0x299A620252B4BAFBUL, 0x01776D574CAF2569UL, 0xBFED5FE65630655EUL, 0xBA48757A221E95BFUL, 0xA72F9C7F947459AEUL, 0x83F16E0876A3C4BEUL, 0x89D7501717E52C3EUL, 0x6AACCEFD7C873BA9UL],
        ["dialogue"] = [0x39C1AEDA2B813CF9UL, 0xD51DCAAE0CAB98E8UL, 0x4B6B44055EFA7350UL, 0x3CD242695748AF38UL, 0x30DF4DC5AF36801CUL, 0xED449BD0473174DAUL],
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
