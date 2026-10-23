using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A character's personality tag (<c>PersonalityTags</c>, docs/game/dialogue.md "Enums"), which DC_PERSONALITY_TAG tests. The original keeps one index per character (status
/// <c>+0xd8</c>) made from the CHARACTER's <c>personality</c> PERSONALITY record (<c>tags always*</c>, <c>tags common*</c>, <c>tags rare*</c>, <c>tags never*</c>); how it rolls is
/// <b>Unknown</b>. Engine roll: one of the <c>always</c> tags when there are any, else one of <c>common</c> (weight 3) and <c>rare</c> (weight 1), never a <c>never</c> tag; none: PT_NONE.
/// </summary>
public static class Personality
{
    public const int None = 0, Honorable = 1, Traitorous = 2, Normal = 13, Count = 15;

    /// <summary>The character's tag: its saved or earlier one, else rolled now (and kept on <paramref name="cold"/>).</summary>
    public static int Of(World world, GameDatabase db, CharacterId id, CharacterCold cold)
    {
        if (cold.Personality > 0) return cold.Personality;
        return cold.Personality = Roll(db, cold.RecordId, Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Dialogue, 0x9E75));
    }

    /// <summary>The engine roll for a CHARACTER record (see the type).</summary>
    public static int Roll(GameDatabase db, string characterId, ulong roll)
    {
        if (db.Find(characterId) is not { } character) return None;
        var always = new List<int>();
        var pool = new List<(int Tag, int Weight)>();
        var never = new HashSet<int>();
        foreach (var r in character.GetReferences("personality"))
        {
            if (db.Find(r.TargetStringId) is not { Type: FcsRecordType.PERSONALITY } p) continue;
            foreach (var (key, tag) in p.Ints)
            {
                if (tag is <= None or >= Count) continue;
                if (key.StartsWith("tags always", StringComparison.Ordinal)) always.Add(tag);
                else if (key.StartsWith("tags common", StringComparison.Ordinal)) pool.Add((tag, 3));
                else if (key.StartsWith("tags rare", StringComparison.Ordinal)) pool.Add((tag, 1));
                else if (key.StartsWith("tags never", StringComparison.Ordinal)) never.Add(tag);
            }
        }
        if (always.Count > 0)
        {
            always.Sort();
            return always[Rng.Int(roll, always.Count)];
        }
        pool.RemoveAll(e => never.Contains(e.Tag));
        if (pool.Count == 0) return None;
        pool.Sort((a, b) => a.Tag != b.Tag ? a.Tag.CompareTo(b.Tag) : a.Weight.CompareTo(b.Weight));
        int pick = Rng.Int(roll, pool.Sum(e => e.Weight));
        foreach (var (tag, weight) in pool)
        {
            if (pick < weight) return tag;
            pick -= weight;
        }
        return None;
    }
}
