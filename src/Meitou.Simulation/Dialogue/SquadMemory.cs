namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The tags a squad's memory holds about a character (docs/game/dialogue.md "Squad memory"): the long-term ones are the editor's <c>CharacterPerceptionTags_LongTerm</c>
/// (DC 23 HAS_TAG reads them), the short-term ones the exe's <c>CharacterPerceptionTags_ShortTerm</c> (named by its debug dump <c>FUN_140675680</c>). Bit = 1 &lt;&lt; tag.
/// </summary>
public static class MemoryTag
{
    public const int None = 0, Intruder = 1, Lifesaver = 2, FreedMe = 3, StoleFromMe = 4, Captor = 5, Friendly = 6, DefeatedMySquad = 7, SquadLostToMe = 8,
        KilledMyFriend = 9, ScrewedThisGuy = 10;
    public const int Aggressor = 2, TempAlly = 3, TempEnemy = 4, Criminal = 7;
    /// <summary>DC 52 I_LOVE_THIS_GUY's long-term mask (lifesaver, freed me) and DC 53 I_HATE_THIS_GUY's (stole from me, captor, defeated my squad, killed my friend).</summary>
    public const uint LoveMask = 0xc, HateMask = 0x2b0;
}

/// <summary>
/// The squads' memories of characters (the original's <c>platoon+0x98</c>, docs/game/dialogue.md "Squad memory"): per squad and character a time and two tag sets. A player-faction
/// character's long-term tags and time live in one entry the squad keeps for the whole player faction (<c>FUN_140671840</c>); its short-term tags stay per character, and the squad's
/// temporary ally flag for the player is one bool (<c>platoon memory +0x28</c>). Times are game hours. Hashed by the dialogue system; not saved yet (the original saves it on the platoon,
/// <c>taglong</c> / <c>tagshort</c> / <c>tagtime</c>).
/// </summary>
public sealed class SquadMemory
{
    public sealed class Entry
    {
        public double Time;
        public uint Long, Short;
    }

    static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    readonly SortedDictionary<(int Squad, CharacterId Who), Entry> entries = new(Comparer<(int, CharacterId)>.Create((a, b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));
    readonly SortedDictionary<int, Entry> player = [];
    readonly SortedSet<int> tempPlayerAlly = [];

    public bool IsEmpty => entries.Count == 0 && player.Count == 0 && tempPlayerAlly.Count == 0;

    static bool PlayerSide(World world, CharacterCold c) => c.IsPlayer || (world.Player.Faction >= 0 && c.Faction == world.Player.Faction);

    /// <summary>The entry a lookup finds (<c>FUN_140673a90</c>): the squad's player entry for a player-faction character, else the character's own; null: never remembered.</summary>
    public Entry? Find(World world, int squad, CharacterId who)
    {
        if (squad < 0 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return null;
        return PlayerSide(world, c) ? player.GetValueOrDefault(squad) : entries.GetValueOrDefault((squad, who));
    }

    /// <summary>
    /// A long-term tag and the time (<c>FUN_140673930</c>; tag 0 only remembers): what DA 19 REMEMBER_CHARACTER and the end of a player conversation (<c>FUN_140674830</c>) write.
    /// </summary>
    public void Remember(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        var e = PlayerSide(world, c) ? Get(player, squad) : Get(entries, (squad, who));
        e.Long |= 1u << tag;
        e.Time = world.GameHours;
    }

    /// <summary>
    /// A short-term tag on the character's own entry and the time (<c>FUN_140674300</c>): never TEMP_ENEMY on a character of the squad's own faction; for a player-faction character
    /// TEMP_ALLY also sets the squad's player flag, and the squad's player entry, when there is one, takes the time.
    /// </summary>
    public void Flag(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || (uint)tag > 31 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return;
        if (tag == MemoryTag.TempEnemy && world.Squads.Find(squad) is { } q && q.Faction == c.Faction) return;
        var e = Get(entries, (squad, who));
        e.Short |= 1u << tag;
        e.Time = world.GameHours;
        if (!PlayerSide(world, c)) return;
        if (tag == MemoryTag.TempAlly) tempPlayerAlly.Add(squad);
        if (player.TryGetValue(squad, out var p)) p.Time = world.GameHours;
    }

    /// <summary>Whether the squad holds short-term <paramref name="tag"/> on the character (<c>FUN_140678830</c>): TEMP_ALLY on a player-faction character is the squad's player flag.</summary>
    public bool HasShort(World world, int squad, CharacterId who, int tag)
    {
        if (squad < 0 || !world.Characters.TryResolveNext(who, out int s) || world.Characters.Cold(s) is not { } c) return false;
        if (tag == MemoryTag.TempAlly && PlayerSide(world, c)) return tempPlayerAlly.Contains(squad);
        return entries.TryGetValue((squad, who), out var e) && (e.Short >> tag & 1) != 0;
    }

    /// <summary>The hours since the squad last noted the character (<c>FUN_140673290</c>); 9999.99 when it never did.</summary>
    public double HoursSince(World world, int squad, CharacterId who) => Find(world, squad, who) is { } e ? world.GameHours - e.Time : 9999.99;

    /// <summary>
    /// Copies a squad's memory to another (<c>FUN_1405e85e0</c>, a character separating into a new squad, DA 28). Engine reading: the entries are copied as they are.
    /// </summary>
    public void Copy(int from, int to)
    {
        foreach (var ((squad, who), e) in entries.Where(kv => kv.Key.Squad == from).ToArray()) entries[(to, who)] = new Entry { Time = e.Time, Long = e.Long, Short = e.Short };
        if (player.TryGetValue(from, out var p)) player[to] = new Entry { Time = p.Time, Long = p.Long, Short = p.Short };
        if (tempPlayerAlly.Contains(from)) tempPlayerAlly.Add(to);
    }

    static Entry Get<TKey>(SortedDictionary<TKey, Entry> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var e)) map[key] = e = new Entry();
        return e;
    }

    public void Hash(ref StateHasher hasher)
    {
        if (IsEmpty) return;
        hasher.Add(0x4D656D6F);
        foreach (var ((squad, who), e) in entries) { hasher.Add(squad); hasher.Add(who); hasher.Add(e.Time); hasher.Add((ulong)e.Long); hasher.Add((ulong)e.Short); }
        foreach (var (squad, e) in player) { hasher.Add(squad); hasher.Add(e.Time); hasher.Add((ulong)e.Long); hasher.Add((ulong)e.Short); }
        foreach (int squad in tempPlayerAlly) hasher.Add(squad);
    }
}
