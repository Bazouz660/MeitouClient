using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The squad-intact test (<c>FUN_14079acf0</c>, docs/game/perception.md "Fleeing", <b>Verified</b>), shared by the retreat package (<see cref="AiSystem"/>) and the
/// flee trigger (<see cref="FleeSystem"/>).
/// </summary>
static class SquadIntact
{
    /// <summary>From this many members the able share decides, below it the leader.</summary>
    public const int MinForRatio = 4;
    /// <summary>The share of the squad's size that must be able (<c>DAT_1416885d8</c>), and with the leader down (<c>DAT_141696c90</c>).</summary>
    public const float Ratio = 0.2f, LeaderDownRatio = 0.4f;

    /// <summary>
    /// A member in a cage or restraint, or carried by a character not allied with it (<c>FUN_14079ab40</c>, <b>Verified</b>): the squad stays intact, so it neither
    /// retreats nor breaks. A carrier that no longer resolves is ignored.
    /// </summary>
    public static bool Held(CharacterTable table, Squad squad, FactionRelations relations)
    {
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } c) continue;
            if (c.IsImprisoned) return true;
            if (!c.CarriedBy.IsNone && table.TryResolveNext(c.CarriedBy, out int k) && table.Cold(k) is { } carrier && carrier.Faction != c.Faction
                && !(relations.Contains(c.Faction) && relations.Contains(carrier.Faction) && relations.IsAlly(c.Faction, carrier.Faction))) return true;
        }
        return false;
    }

    /// <summary>
    /// The leader's down test (<c>FUN_1405c9e10</c>, docs/game/ui-input.md): gone, or <see cref="CharacterFacts.Down"/> (out cold, dead, carried); a crawling leader is up.
    /// </summary>
    public static bool Down(CharacterTable table, CharacterId id) =>
        !table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c || CharacterFacts.Down(c);

    /// <summary>
    /// The verdict once no member is <see cref="Held"/>: a squad without a leader or with no able member is not intact; under <see cref="MinForRatio"/> members a
    /// persistent squad with a home is intact while anyone is able, any other while its leader is up; from that many, while the able are at least
    /// <see cref="Ratio"/> of the size (<see cref="LeaderDownRatio"/> with the leader down). The original's able test also passes a down member whose
    /// <c>+0x504</c> is under 0.8 (persistent with a home) or 0.2 (meaning <b>Unknown</b>); Meitou counts the alive and not incapacitated.
    /// </summary>
    public static bool Decide(int able, int size, bool hasLeader, bool leaderDown, bool persistentHome)
    {
        if (!hasLeader || able == 0) return false;
        if (size < MinForRatio) return persistentHome ? able > 0 : !leaderDown;
        return able >= size * (leaderDown ? LeaderDownRatio : Ratio);
    }
}
