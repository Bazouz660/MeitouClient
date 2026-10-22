using System.Globalization;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>DA_CHOOSE_SLAVES_SELLING, DA_CHOOSE_SLAVES_BUYING, DA_CHOOSE_PRISONER_BAIL.</summary>
    const int ChooseSlavesSelling = 41, ChooseSlavesBuying = 42, ChoosePrisonerBail = 43;

    /// <summary>The original's <c>CharacterTrading_*</c> windows the DA 41 to 43 lists stand in for.</summary>
    enum TradingKind { Bail, SlavesSelling, SlavesBuying }

    // The trading list that opens when the conversation ends (the desk or slaver, the talker, the window, the effect's val0 price override), and the list on show: its
    // characters with their prices (null: none), its window and override.
    (CharacterId Npc, CharacterId Player, TradingKind Kind, int Override)? tradingAfter;
    List<(CharacterId Who, int Price)>? tradingList;
    TradingKind tradingKind;
    int tradingOverride;

    /// <summary>
    /// Fires <paramref name="ev"/> at <paramref name="npc"/> now, with <paramref name="target"/> as its partner (<c>FUN_140684990</c>; the police's spotting, events 44 and 45): a line of
    /// its list for the event said as a speech bubble, its effects at once. Returns whether a line was said; false while the event lines are off (<see cref="DialogueSettings.AmbientEvents"/>).
    /// </summary>
    public bool FireEvent(World world, CharacterId npc, CharacterId target, int ev) => Settings.AmbientEvents && Fire(world, npc, target, ev);

    /// <summary>Whether <paramref name="npc"/> is saying a line or is in the open conversation.</summary>
    public bool Engages(CharacterId npc) => open?.Npc == npc || BubbleOf(npc);

    /// <summary>
    /// A trading list (the original's <c>CharacterTrading_*</c> windows, docs/game/economy.md 11.5 and 12): the conversation window again, the speaker saying the window's title, one
    /// choice per character offered with its price, and <c>[Leave]</c>. Engine stand-in for the trading window. The bail list (DA 43 <c>CHOOSE_PRISONER_BAIL</c>) offers
    /// <see cref="Crime.ArrestSystem.BailOffers"/>; the slave lists (DA 41, 42) <see cref="SellOffers"/> and <see cref="BuyOffers"/>.
    /// </summary>
    void OpenTrading(World world)
    {
        if (tradingAfter is not { } b) return;
        tradingAfter = null;
        var table = world.Characters;
        if (!table.TryResolveNext(b.Npc, out int n) || !Awake(table, n)) return;
        if (b.Kind == TradingKind.Bail && Arrests is null) return;
        var c = new Conversation { Npc = b.Npc, Player = b.Player, Event = DialogueEvents.PlayerTalkToMe, Speaker = b.Npc };
        (tradingKind, tradingOverride) = (b.Kind, b.Override);
        FillTrading(world, c);
        open = c;
        Hold(world, n);
    }

    void FillTrading(World world, Conversation c)
    {
        var offers = tradingKind switch
        {
            TradingKind.Bail => Arrests?.BailOffers(world, c.Npc) ?? [],
            TradingKind.SlavesSelling => SellOffers(world, c.Npc, c.Player, tradingOverride),
            _ => BuyOffers(world, c.Npc, tradingOverride),
        };
        var (title, none) = tradingKind switch
        {
            TradingKind.Bail => ("Pay Bounty", "Nobody here to bail out."),
            TradingKind.SlavesSelling => ("Selling Slaves", "Nobody here to sell."),
            _ => ("Buying Slaves", "No slaves for sale."),
        };
        c.Text = Loc.Tr(title);
        c.Choices.Clear();
        foreach (var (_, name, price) in offers) c.Choices.Add((null, $"{name} (c.{price.ToString("N0", CultureInfo.InvariantCulture)})"));
        c.Choices.Add((null, "[Leave]"));
        if (offers.Count == 0) world.Post(Loc.Tr(none));
        tradingList = [.. offers.Select(o => (o.Item1, o.Price))];
    }

    /// <summary>
    /// A click in a trading list: the bail paid (<see cref="Crime.ArrestSystem.PayBail(World, CharacterId, CharacterId)"/>) and the window closed; a slave sold or bought
    /// (<see cref="SellSlave"/>, <see cref="BuySlave"/>) and the list shown again without it (the original's window takes several at once).
    /// </summary>
    void ChooseTrading(World world, Conversation c, int index)
    {
        if (tradingList is not { } list || (uint)index >= (uint)list.Count) { c.Ended = true; return; }
        var (who, price) = list[index];
        switch (tradingKind)
        {
            case TradingKind.Bail:
                Arrests?.PayBail(world, who, c.Player);
                c.Ended = true;
                return;
            case TradingKind.SlavesSelling:
                SellSlave(world, c.Npc, who, price);
                break;
            default:
                if (!BuySlave(world, c.Npc, c.Player, who, price)) return;
                break;
        }
        FillTrading(world, c);
    }
}
