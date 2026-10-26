namespace Meitou.Data.Ui;

/// <summary>How a tutorial page moves on by itself (docs/game/ui-screens.md "Tutorials", advance tests); <see cref="None"/> waits for the player's button.</summary>
public enum TutorialAdvance
{
    None, CameraControls, CharSelection, CharMovement, CenterChar, SelectCharacter, Map, Signs, Medkiting, RecruitSelect, RecruitStats, SquadArrange,
    SelectUnconscious, Lockpicking, LockpickingSuccessful, CraftButton, CraftTab, ProductionSelection, ConstructionMaterials,
}

/// <summary>A tutorial page: its text's source line in <c>kenshi/TutorialsData.cpp</c>, whether its Next reads CONTINUE (else SKIP), and its own advance.</summary>
public readonly record struct TutorialPage(int Line, bool Continue, TutorialAdvance Advance = TutorialAdvance.None);

/// <summary>
/// The 34 tutorials' titles and pages (<b>Verified</b>, the item and page constructors; docs/game/ui-screens.md "Tutorials"). The texts are the installed English template's
/// msgids at those source lines, through the catalog; no game text is embedded.
/// </summary>
public sealed class TutorialCatalog
{
    const string Source = "kenshi/TutorialsData.cpp";

    static TutorialPage G(int line) => new(line, true);
    static TutorialPage S(int line, TutorialAdvance advance) => new(line, false, advance);
    static TutorialPage C(int line, TutorialAdvance advance = TutorialAdvance.None) => new(line, true, advance);

    /// <summary>Per id: the title's line and the pages in order.</summary>
    public static readonly (int Title, TutorialPage[] Pages)[] Items =
    [
        (220, [S(108, TutorialAdvance.CameraControls), S(156, TutorialAdvance.CharSelection), S(182, TutorialAdvance.CharMovement), S(203, TutorialAdvance.CenterChar), G(228)]),
        (406, [C(247, TutorialAdvance.SelectCharacter), C(262), C(289), C(309), C(329), C(349), C(369), C(389)]),
        (468, [G(473), S(440, TutorialAdvance.Map), G(475), G(476), G(477)]),
        (538, [G(542), S(511, TutorialAdvance.Signs), G(544)]),
        (598, [C(581), G(602)]),
        (785, [S(636, TutorialAdvance.Medkiting), C(681), G(790), C(704), C(733), C(761), G(794)]),
        (853, [G(856), G(857), G(858), C(832)]),
        (972, [S(895, TutorialAdvance.RecruitSelect), S(920, TutorialAdvance.RecruitStats), G(977), S(946, TutorialAdvance.SquadArrange)]),
        (1014, [G(1018), G(1019), G(1020), G(1021)]),
        (1096, [G(1100), S(1058, TutorialAdvance.SelectUnconscious), G(1102), G(1103), G(1104)]),
        (1142, [G(1146), G(1147), C(1130)]),
        (1290, [G(1294), S(1188, TutorialAdvance.Lockpicking), S(1261, TutorialAdvance.LockpickingSuccessful), G(1297)]),
        (1325, [G(1328), S(1188, TutorialAdvance.Lockpicking), S(1261, TutorialAdvance.LockpickingSuccessful), G(1331)]),
        (1354, [G(1358), G(1359), G(1360), G(1361), G(1362)]),
        (1387, [G(1391), G(1392), G(1393), G(1394)]),
        (1430, [C(1413), G(1435), G(1436), G(1437)]),
        (1469, [G(1473), G(1474)]),
        (1520, [C(1503), G(1525)]),
        (1592, [C(1555), G(1597), G(1598), C(1575)]),
        (1653, [G(1657), C(1636), G(1659), G(1660)]),
        (1707, [G(1711), C(1690), G(1713)]),
        (1787, [C(1770), G(1792)]),
        (2278, [C(2266), G(2282)]),
        (2166, [G(2170), S(2018, TutorialAdvance.ProductionSelection), C(2033), C(2053), C(2082), C(2111), G(2176), C(2140)]),
        (1828, [G(1832), G(1833), G(1834), G(1835)]),
        (1924, [S(1854, TutorialAdvance.CraftButton), S(1881, TutorialAdvance.CraftTab), G(1930), C(1907), G(1932), G(1933), G(1934)]),
        (1981, [C(1964), G(1986), G(1987)]),
        (2231, [C(2214), G(2236)]),
        (2319, [G(2323), G(2324), C(2302), G(2326)]),
        (2353, [G(2357), G(2358), G(2359)]),
        (2431, [G(2435), C(2405), G(2437), G(2438), G(2439), G(2440)]),
        (2554, [G(2558), S(2459, TutorialAdvance.ConstructionMaterials), C(2507)]),
        (2585, [G(2587), G(2588)]),
        (2601, [G(2602), G(2603)]),
    ];

    readonly Dictionary<int, string> texts = [];

    /// <summary>The English msgid at a source line ("" when the template lacks it).</summary>
    public string EnglishAt(int line) => texts.GetValueOrDefault(line, "");
    /// <summary>The text at a source line in the active language.</summary>
    public string TextAt(int line) => Meitou.Localisation.Loc.Tr(EnglishAt(line));
    public string Title(int id) => TextAt(Items[id].Title);
    /// <summary>The template had the tutorials' texts.</summary>
    public bool HasTexts => texts.Count > 0;

    public static TutorialCatalog From(IEnumerable<PotEntry> entries)
    {
        var result = new TutorialCatalog();
        foreach (var entry in entries)
            foreach (var (file, line) in entry.References)
                if (file == Source) result.texts[line] = entry.Id;
        return result;
    }
}
