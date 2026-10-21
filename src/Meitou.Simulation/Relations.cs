using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>
/// Relation changes caused by the player, with the game's threshold messages (docs/game/factions-squads-towns.md sections 3.3 and 3.4).
/// Serial phases only: the table is shared world state.
/// </summary>
public static class PlayerRelations
{
    /// <summary>
    /// Applies an event of <paramref name="kind"/> to <paramref name="faction"/>'s view of the player (the player's faction has no table, so a
    /// deed against a faction changes that faction's entry about the player) and posts the messages of a threshold it crosses. Does nothing
    /// without a player faction, for the player's own faction or for one outside the table.
    /// </summary>
    public static void Event(World world, FactionRelations relations, int faction, RelationEventKind kind, float x = 1)
    {
        int player = relations.PlayerFaction;
        if (player < 0 || faction == player || !relations.Contains(faction)) return;
        var (before, after) = relations.Event(faction, player, kind, x);
        if (Notice(relations.Factions[faction].Name, before, after) is { } text) world.Post(text);
    }

    /// <summary>
    /// The message of a change of a faction's relation to the player (<c>FUN_1406b28f0</c>, <b>Verified</b> thresholds): hostile at -30 or below,
    /// allies above 50 (the message's own test; <c>ally()</c> accepts 50). Null when no threshold is crossed.
    /// </summary>
    public static string? Notice(string factionName, float before, float after)
    {
        const float Hostile = FactionRelations.HostileThreshold, Ally = FactionRelations.AllyThreshold;
        if (before > Hostile && after <= Hostile) return Loc.Tr("{1} are now hostile towards you", factionName);
        if (before <= Hostile && after > Hostile) return Loc.Tr("{1} are no longer hostile towards you", factionName);
        if (before <= Ally && after > Ally) return Loc.Tr("{1} are now your allies", factionName);
        if (before > Ally && after <= Ally) return Loc.Tr("{1} are no longer your ally", factionName);
        return null;
    }
}
