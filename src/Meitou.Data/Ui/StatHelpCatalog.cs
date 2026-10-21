using Meitou.Content;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Ui;

/// <summary>Stat help read from the user's English gettext template, never embedded game text; shown in the active language when its catalog translates the paragraph.</summary>
public sealed class StatHelpCatalog
{
    sealed record Entry(int[] Lines, string Text, int EnglishLength);
    readonly List<Entry> entries = [];
    public sealed record Help(string Description, IReadOnlyList<string> Affected, IReadOnlyList<string> Training, string PhysicalEffect);

    public string TextAt(int line) => entries.Where(e => e.Lines.Contains(line)).MaxBy(e => e.EnglishLength)?.Text ?? "";

    public static StatHelpCatalog Read(string? path)
    {
        var result = new StatHelpCatalog();
        if (path is null) return result;
        path = GameFiles.Resolve(path);
        if (!File.Exists(path)) return result;
        var references = new List<int>();
        var text = new StringBuilder();
        bool reading = false;
        void Finish()
        {
            // The structure (which paragraph answers which stat) comes from the English template; the text shown is its translation when the language has one.
            if (references.Count > 0 && text.Length > 0) result.entries.Add(new(references.Distinct().ToArray(), Meitou.Localisation.Loc.Tr(text.ToString()), text.Length));
            references.Clear(); text.Clear(); reading = false;
        }
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) { Finish(); continue; }
            if (line.StartsWith("#:", StringComparison.Ordinal))
                foreach (Match match in Regex.Matches(line, @"kenshi/statswindow\.cpp:(\d+)"))
                    references.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            { reading = true; text.Append(JsonSerializer.Deserialize<string>(line[6..])); }
            else if (line.StartsWith("msgstr", StringComparison.Ordinal) || line.StartsWith("msgctxt", StringComparison.Ordinal)) reading = false;
            else if (reading && line.StartsWith('"')) text.Append(JsonSerializer.Deserialize<string>(line));
        }
        Finish();
        return result;
    }

    public Help For(StatsEnumerated stat)
    {
        // Source references in the installed 1.0.68 main.pot identify stat paragraphs and help groups.
        // These are catalog metadata, not a translation of the original program's logic.
        var (description, affected, training, end, physical) = stat switch
        {
            StatsEnumerated.Strength => (937, 499, 548, 553, 429),
            StatsEnumerated.Toughness => (942, 554, 568, 578, 433),
            StatsEnumerated.Dexterity => (945, 579, 587, 591, 422),
            StatsEnumerated.Perception => (947, 709, 714, 718, 0),
            StatsEnumerated.MeleeAttack => (959, 592, 597, 604, 0),
            StatsEnumerated.MeleeDefence => (960, 605, 609, 616, 0),
            StatsEnumerated.MartialArts => (961, 617, 625, 633, 426),
            StatsEnumerated.Dodge => (963, 634, 641, 646, 0),
            StatsEnumerated.Athletics => (973, 647, 654, 658, 0),
            StatsEnumerated.Swimming => (974, 659, 666, 670, 0),
            StatsEnumerated.Medic => (979, 671, 675, 680, 0),
            StatsEnumerated.Robotics => (984, 671, 675, 680, 0),
            StatsEnumerated.Turrets => (956, 689, 694, 698, 0),
            StatsEnumerated.Crossbows => (957, 699, 704, 708, 0),
            StatsEnumerated.FriendlyFire => (964, 719, 722, 726, 0),
            StatsEnumerated.Thieving => (968, 727, 732, 736, 0),
            StatsEnumerated.Lockpicking => (967, 737, 741, 746, 0),
            StatsEnumerated.Stealth => (966, 747, 759, 763, 0),
            StatsEnumerated.Assassination => (970, 764, 773, 778, 0),
            StatsEnumerated.Engineering => (980, 779, 784, 789, 0),
            StatsEnumerated.Science => (985, 790, 794, 800, 437),
            StatsEnumerated.Labouring => (987, 801, 806, 810, 0),
            StatsEnumerated.Farming => (988, 811, 817, 821, 0),
            StatsEnumerated.Cooking => (989, 822, 826, 831, 0),
            StatsEnumerated.SmithingWeapon => (981, 832, 839, 843, 0),
            StatsEnumerated.SmithingArmour => (982, 844, 853, 857, 0),
            StatsEnumerated.SmithingBow => (983, 858, 868, 870, 0),
            StatsEnumerated.Katanas => (950, 681, 684, 688, 0),
            StatsEnumerated.Sabres => (951, 681, 684, 688, 0),
            StatsEnumerated.Hackers => (952, 681, 684, 688, 0),
            StatsEnumerated.HeavyWeapons => (953, 681, 684, 688, 0),
            StatsEnumerated.Blunt => (954, 681, 684, 688, 0),
            StatsEnumerated.Polearms => (955, 681, 684, 688, 0),
            _ => (0, 0, 0, 0, 0),
        };
        string[] Between(int from, int to) => entries.SelectMany(e => e.Lines.Where(l => l > from && l < to).Select(l => (Line: l, e.Text)))
            .OrderBy(e => e.Line).Select(e => e.Text).Distinct().ToArray();
        return new(TextAt(description), Between(affected, training), Between(training, end + 1), TextAt(physical));
    }
}
