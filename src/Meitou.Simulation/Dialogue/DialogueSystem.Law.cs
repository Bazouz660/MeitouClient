using System.Globalization;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>DA_CHOOSE_PRISONER_BAIL.</summary>
    const int ChoosePrisonerBail = 43;

    /// <summary>
    /// Fires <paramref name="ev"/> at <paramref name="npc"/> now, with <paramref name="target"/> as its partner (<c>FUN_140684990</c>; the police's spotting, events 44 and 45): a line of
    /// its list for the event said as a speech bubble, its effects at once. Returns whether a line was said; false while the event lines are off (<see cref="DialogueSettings.AmbientEvents"/>).
    /// </summary>
    public bool FireEvent(World world, CharacterId npc, CharacterId target, int ev) => Settings.AmbientEvents && Fire(world, npc, target, ev);

    /// <summary>Whether <paramref name="npc"/> is saying a line or is in the open conversation.</summary>
    public bool Engages(CharacterId npc) => InConversation(npc) || BubbleOf(npc);

    /// <summary>Whether <paramref name="npc"/> is the NPC of some player's open conversation.</summary>
    bool InConversation(CharacterId npc)
    {
        foreach (var talk in talks.Values)
            if (talk.Open?.Npc == npc) return true;
        return false;
    }

    /// <summary>
    /// The bail list (DA 43 <c>CHOOSE_PRISONER_BAIL</c>, the original's <c>CharacterTrading_PrisonerBailout</c> window, docs/game/economy.md 11.5): the conversation window again, the speaker
    /// saying "Pay Bounty", one choice per prisoner it offers (<see cref="Crime.ArrestSystem.BailOffers"/>) with its price, and <c>[Leave]</c>. Engine stand-in for the trading window.
    /// It opens in the talk of the player whose conversation asked for it.
    /// </summary>
    void OpenBail(World world, PlayerTalk talk)
    {
        if (talk.BailAfter is not { } b || Arrests is null) return;
        talk.BailAfter = null;
        var table = world.Characters;
        if (!table.TryResolveNext(b.Npc, out int n) || !Awake(table, n)) return;
        var offers = Arrests.BailOffers(world, b.Npc);
        var c = new Conversation { Npc = b.Npc, Player = b.Player, Event = DialogueEvents.PlayerTalkToMe, Owner = talk.Player, Speaker = b.Npc, Text = Loc.Tr("Pay Bounty") };
        foreach (var (_, name, price) in offers) c.Choices.Add((null, $"{name} (c.{price.ToString("N0", CultureInfo.InvariantCulture)})"));
        c.Choices.Add((null, "[Leave]"));
        if (offers.Count == 0) world.Post(talk.Player, Loc.Tr("Nobody here to bail out."));
        c.BailList = [.. offers.Select(o => o.Prisoner)];
        talk.Open = c;
        Hold(world, n);
    }

    /// <summary>A click in the bail list: the prisoner's bail paid by the talker's faction (<see cref="Crime.ArrestSystem.PayBail(World, CharacterId, CharacterId)"/>), then the window closes.</summary>
    void ChooseBail(World world, Conversation c, int index)
    {
        if (c.BailList is { } list && (uint)index < (uint)list.Count) Arrests?.PayBail(world, list[index], c.Player);
        c.Ended = true;
    }
}
