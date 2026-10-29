namespace Meitou.Simulation;

/// <summary>
/// What the context menu's pick-up verb (<c>FUN_140796f80</c>) reads about a target, asked between ticks (<see cref="World.VerbFactsOf"/>).
/// </summary>
/// <param name="RegardsLead">The target regards the lead as friendly (Character slot 126 on the lead).</param>
/// <param name="SquadTrader">The target's squad template is a trader's (<c>FUN_1405cff70</c>).</param>
public readonly record struct VerbFacts(bool RegardsLead, bool SquadTrader);

public sealed partial class World
{
    /// <summary>
    /// Whether <paramref name="who"/> regards <paramref name="of"/> as friendly (Character slot 126, <see cref="Dialogue.DialogueSystem.RegardsFriendly(World, int, int)"/>); the host asks between
    /// ticks. False without a dialogue system.
    /// </summary>
    public bool RegardsFriendly(CharacterId who, CharacterId of) =>
        Characters.Phase == TablePhase.Outside && Characters.TryResolveNext(who, out int a) && Characters.TryResolveNext(of, out int b)
        && SystemOf<Dialogue.DialogueSystem>() is { } dialogue && dialogue.RegardsFriendly(this, a, b);

    /// <summary>The facts the pick-up verb reads (<see cref="VerbFacts"/>), the host asking between ticks; null when the world has no population data or a character is gone.</summary>
    public VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target)
    {
        if (Characters.Phase != TablePhase.Outside || !Characters.TryResolveNext(lead, out int l) || !Characters.TryResolveNext(target, out int t)) return null;
        if (SystemOf<PopulationSystem>()?.Data is not { } data || SystemOf<Dialogue.DialogueSystem>() is not { } dialogue || Characters.Cold(t) is not { } tc) return null;
        bool squadTrader = tc.SquadId >= 0 && Squads.Find(tc.SquadId) is { } squad && data.Db.Find(squad.TemplateId)?.GetBool("is trader") == true;
        return new(dialogue.RegardsFriendly(this, t, l), squadTrader);
    }
}
