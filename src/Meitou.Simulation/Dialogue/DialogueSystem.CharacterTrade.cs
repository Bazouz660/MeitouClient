using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// What the character trading window trades (the original's <c>CharacterTrading_*</c> handler classes). The original's window also serves bail (DA 43,
/// <c>CharacterTrading_PrisonerBailout</c>; Meitou shows bail as a conversation list, <see cref="DialogueSystem"/>) and animals (DA 48, <c>CharacterTrading_AnimalBuying</c>, not built).
/// </summary>
public enum CharacterTradeKind : byte { SlaveSelling, SlaveBuying }

/// <summary>One row of the window (<c>Kenshi_CharacterSelectionBox</c>): the character, its name, its price and the label/value lines of its two stats panels.</summary>
public sealed record CharacterTradeRow(CharacterId Character, string Name, long Price, IReadOnlyList<(string Label, string Value)> Panel1, IReadOnlyList<(string Label, string Value)> Panel2);

/// <summary>
/// The open character trading window as the UI shows it (published with the snapshot): its title and confirm caption, the buyer's money (the slaver's squad when selling, the
/// player's when buying) and the rows. <see cref="Id"/> is new for every opening.
/// </summary>
public sealed record CharacterTradeView(long Id, CharacterTradeKind Kind, CharacterId Slaver, CharacterId Player, string Title, string ConfirmCaption, long BuyerMoney,
    IReadOnlyList<CharacterTradeRow> Rows);

/// <summary>The window's confirm button with the characters of the ticked rows.</summary>
public sealed record ConfirmCharacterTrade(IReadOnlyList<CharacterId> Selected) : SimCommand;
/// <summary>Closes the character trading window without a trade (its cancel button, or the window closed).</summary>
public sealed record CloseCharacterTrade : SimCommand;

/// <summary>
/// DA 41 <c>CHOOSE_SLAVES_SELLING</c> and DA 42 <c>CHOOSE_SLAVES_BUYING</c> (<c>FUN_140680560</c>, docs/game/economy.md section 12): the character trading window
/// (<c>FUN_14072a880</c>), opened as the conversation closes, with the speaker as the slaver and the player character as the customer. One window at a time; it closes on
/// confirm, cancel, or when the slaver or the customer is no longer awake (engine choice).
/// </summary>
public sealed partial class DialogueSystem
{
    /// <summary>EV_BOUGHT_ME_FROM_SLAVERY.</summary>
    const int BoughtMeFromSlavery = 51;
    /// <summary>How near the slaver a member of the customer's squad must be to be offered for sale (DA 41, 300 units, 3D).</summary>
    const float SlaveSaleRange = 300;
    /// <summary>How far from the buyer a bought slave walks out of its cage to (engine choice).</summary>
    const float GetOutReach = 15;

    sealed record CharacterTrade(long Id, CharacterTradeKind Kind, CharacterId Slaver, CharacterId Player, IReadOnlyList<CharacterTradeRow> Rows);

    // The window to open when the conversation ends (val0 the price override), the open one and its view.
    (CharacterTradeKind Kind, CharacterId Slaver, CharacterId Player, int Price)? characterTradeAfter;
    CharacterTrade? characterTrade;
    CharacterTradeView? characterTradeView;
    long characterTradeId;

    public CharacterTradeView? PublishCharacterTrade() => characterTradeView;

    /// <summary>
    /// A slave's base price (<c>FUN_1406751f0</c>, economy.md section 12): clamp(trunc(strength x k + melee attack + dexterity x k) x RACE <c>buy value</c>, 1000, 20000), k the
    /// stats multiplier (1 for people). The stats are the raw values.
    /// </summary>
    public static int SlaveValue(CharacterCold c)
    {
        var stats = c.Stats;
        float k = stats?.Scale ?? 1;
        float sum = stats is null ? 0 : stats.Strength * k + stats[StatsEnumerated.MeleeAttack] + stats.Dexterity * k;
        return (int)Math.Clamp((int)sum * (c.Race?.BuyValue ?? 100), 1000, 20000);
    }

    /// <summary>The price the slaver pays for <paramref name="c"/> (<c>CharacterTrading_SlaveSelling</c>, <c>FUN_1406a6f50</c>): the override when above 0, else half the value within 200..2500.</summary>
    public static long SalePrice(CharacterCold c, int priceOverride) => priceOverride > 0 ? priceOverride : Math.Clamp(SlaveValue(c) / 2, 200, 2500);

    /// <summary>The price of buying <paramref name="c"/> free (<c>CharacterTrading_SlaveBuying</c>, <c>FUN_1406a7970</c>): the override when above 0, else its value; doubled for one of the player's faction.</summary>
    public static long PurchasePrice(World world, CharacterCold c, int priceOverride)
    {
        long price = priceOverride > 0 ? priceOverride : SlaveValue(c);
        return world.Player.Faction >= 0 && c.Faction == world.Player.Faction ? price * 2 : price;
    }

    /// <summary>
    /// Opens the window a DA 41 / 42 line asked for (as the conversation ends), when the customer is a player character. Selling lists the customer's squad members that are not dead and within 300 units of the slaver,
    /// the customer included; buying lists every character in slave state 1 owned by the slaver's faction that is not dead (engine reading of the original's per-owner slave set).
    /// </summary>
    void OpenCharacterTrade(World world)
    {
        if (characterTradeAfter is not { } t) return;
        characterTradeAfter = null;
        var table = world.Characters;
        if (!table.TryResolveNext(t.Slaver, out int s) || !table.TryResolveNext(t.Player, out int p) || table.Cold(s) is not { } sc || table.Cold(p) is not { IsPlayer: true } pc) return;
        var rows = new List<CharacterTradeRow>();
        if (t.Kind == CharacterTradeKind.SlaveSelling)
        {
            if (world.Squads.Find(pc.SquadId) is { } squad)
                foreach (var id in squad.Members)
                    if (table.TryResolveNext(id, out int m) && table.Cold(m) is { Medical.Dead: false } mc && Vector3.Distance(table.Next[m].Position, table.Next[s].Position) <= SlaveSaleRange)
                        rows.Add(new(id, mc.Name, SalePrice(mc, t.Price), [(Loc.Tr("Name:"), mc.Name), (Loc.Tr("WARNING: Character will become a slave!"), "")], []));
        }
        else if (sc.Faction >= 0)
        {
            for (int i = 0; i < table.HighWater; i++)
            {
                if (!table.Next[i].Alive || table.Cold(i) is not { SlaveState: 1, Medical.Dead: false } c || c.SlaveOwner != sc.Faction) continue;
                rows.Add(new(table.IdOf(i), c.Name, PurchasePrice(world, c, t.Price),
                    StatLines(c, StatsEnumerated.Strength, StatsEnumerated.Dexterity, StatsEnumerated.Toughness, StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence),
                    StatLines(c, StatsEnumerated.Labouring, StatsEnumerated.Farming, StatsEnumerated.Cooking, StatsEnumerated.Engineering, StatsEnumerated.Medic)));
            }
        }
        characterTrade = new(++characterTradeId, t.Kind, t.Slaver, t.Player, rows);
    }

    static (string, string)[] StatLines(CharacterCold c, params StatsEnumerated[] stats) =>
        [.. stats.Select(s => (Loc.Tr(StatName(s)), ((int)(c.Stats?[s] ?? 0)).ToString(CultureInfo.InvariantCulture)))];

    static string StatName(StatsEnumerated s) => s switch
    {
        StatsEnumerated.MeleeAttack => "Melee Attack", StatsEnumerated.MeleeDefence => "Melee Defence", _ => s.ToString(),
    };

    /// <summary>The buyer's money the window shows and gates the confirm by: the slaver's squad purse when selling (its shop's money for a trader), the player's when buying.</summary>
    long BuyerMoney(World world, CharacterTrade t) =>
        t.Kind == CharacterTradeKind.SlaveBuying ? world.Player.Money
        : world.Characters.TryResolveNext(t.Slaver, out int s) && world.Squads.Find(world.Characters.Cold(s)!.SquadId) is { } squad ? squad.Money : 0;

    /// <summary>Closes the window when the slaver or the customer is no longer awake (engine choice).</summary>
    void KeepCharacterTrade(World world)
    {
        if (characterTrade is not { } t) return;
        var table = world.Characters;
        if (!table.TryResolveNext(t.Slaver, out int s) || !table.TryResolveNext(t.Player, out int p) || !Awake(table, s) || !Awake(table, p)) characterTrade = null;
    }

    void RefreshCharacterTrade(World world)
    {
        if (characterTrade is not { } t) { characterTradeView = null; return; }
        bool buying = t.Kind == CharacterTradeKind.SlaveBuying;
        characterTradeView = new(t.Id, t.Kind, t.Slaver, t.Player, Loc.Tr(buying ? "Buying Slaves" : "Selling Slaves"), Loc.Tr(buying ? "Buy" : "Sell"), BuyerMoney(world, t), t.Rows);
    }

    /// <summary>
    /// The confirm (<c>FUN_1406add20</c> buying, <c>FUN_1406a7740</c> selling): the ticked rows whose characters are not dead are traded and the window closes. The window's gate
    /// (<c>FUN_1407281c0</c>: a row ticked, the total within the buyer's money) is checked again here, as the money may have moved since the click.
    /// </summary>
    void ConfirmTrade(World world, IReadOnlyList<CharacterId> selected)
    {
        if (characterTrade is not { } t) return;
        long buyerMoney = BuyerMoney(world, t);
        characterTrade = null;
        var table = world.Characters;
        if (!table.TryResolveNext(t.Slaver, out int s) || table.Cold(s) is not { } sc) return;
        var chosen = new List<(int Slot, CharacterCold Cold, long Price)>();
        foreach (var row in t.Rows)
            if (selected.Contains(row.Character) && table.TryResolveNext(row.Character, out int c) && table.Cold(c) is { Medical.Dead: false } cc) chosen.Add((c, cc, row.Price));
        long total = chosen.Sum(x => x.Price);
        if (chosen.Count == 0 || total > buyerMoney) return;
        foreach (var (slot, c, _) in chosen)
        {
            if (t.Kind == CharacterTradeKind.SlaveBuying) BuyFree(world, slot, c, sc, t.Player);
            else Sell(world, c, sc);
        }
        if (t.Kind == CharacterTradeKind.SlaveSelling) world.Player.Money += total;   // the slaver's purse is not touched
        else if (world.Player.Money >= total) world.Player.Money -= total;
    }

    /// <summary>
    /// One slave bought free (<c>FUN_1406add20</c>): no longer owned; out of a squad of others into a new squad of its own faction (it does not join the player); its cage unlocked
    /// and left; slave state 0; its AI reset; its worn shackles unlocked (still worn); event 51 at it with the player character as the target; relations toward the player's faction:
    /// the slaver's +1, its own +4. Engine choices: the new squad of a non-player faction is of the "Wandering Squad" template (the original's is <b>Unknown</b>); event 51 fires after it
    /// is out of its cage (the original fires it first; Meitou's events need a free speaker); the original's task 133 (GET_OUT_OF_CAGE_LEGIT) is a walk from the open cage to within
    /// a few units of the buyer (where the original's task takes it is <b>Unknown</b>).
    /// </summary>
    void BuyFree(World world, int slot, CharacterCold c, CharacterCold slaver, CharacterId player)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        if (world.Squads.Find(c.SquadId) is { Members.Count: > 1 }) SplitOff(world, slot, c);
        bool caged = c.IsImprisoned;
        if (caged) world.Fixtures.Release(world, id);
        Locks.FixtureRegistry.ClearSlave(world, c);
        c.Ai = null;
        c.Engaged = false;
        c.Fleeing = false;
        c.Pending = null;
        MovementSystem.Stop(table, slot);
        if (caged && table.TryResolveNext(player, out int buyer))
        {
            var from = table.Next[slot].Position;
            var to = table.Next[buyer].Position;
            var away = new Vector2(from.X - to.X, from.Z - to.Z);
            float gap = away.Length();
            // Beside the buyer, on the slave's side: out through the open cage door (the path finds it).
            var target = new Vector2(to.X, to.Z) + (gap > 1e-3f ? away / gap * Math.Min(GetOutReach, gap) : Vector2.Zero);
            MovementSystem.GoTo(table, slot, target, SpeedMode.Walk);
        }
        if (Locks.LockSystem.WornShackles(c)?.Lock is { } l) l.Locked = false;
        if (Settings.AmbientEvents) Fire(world, id, player, BoughtMeFromSlavery);
        ChangeTowardPlayer(world, slaver.Faction, 1);
        ChangeTowardPlayer(world, c.Faction, 4);
    }

    /// <summary>
    /// One character sold (<c>FUN_1406a7740</c>): it becomes the slaver faction's slave (state 1, "{1} has been enslaved" for a free player-faction character) and stays in its squad
    /// and faction; relations toward the player's faction: the slaver's +4, the sold one's own -4. Not built: the obedient-slave order and the shackles the original puts on one
    /// wearing no locked armour.
    /// </summary>
    void Sell(World world, CharacterCold c, CharacterCold slaver)
    {
        Locks.FixtureRegistry.Enslave(world, c, slaver.Faction);
        ChangeTowardPlayer(world, slaver.Faction, 4);
        ChangeTowardPlayer(world, c.Faction, -4);
    }

    void ChangeTowardPlayer(World world, int faction, int amount)
    {
        if (world.Player.Faction < 0 || faction == world.Player.Faction || !data.Relations.Contains(faction)) return;
        ChangeRelation(world, faction, world.Player.Faction, amount);
    }

    /// <summary>The character leaves its squad for a new one of its own faction: a player squad (<see cref="SquadManagement.NewSquad"/>), else a "Wandering Squad" it leads.</summary>
    void SplitOff(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var at = table.Next[slot].Position;
        Leave(world, id, c);
        Squad squad;
        if (world.Player.Faction >= 0 && c.Faction == world.Player.Faction)
            squad = SquadManagement.NewSquad(world, data.Db, at, data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        else
        {
            var template = data.Db.Find(SquadManagement.DismissTemplate);
            squad = new Squad
            {
                Id = world.Squads.NextId(),
                TemplateId = template?.StringId ?? SquadManagement.DismissTemplate,
                Name = template?.Name ?? "",
                Faction = c.Faction,
                Position = at,
                HomeCentre = new Vector2(at.X, at.Z),
                HomeRadius = 100,
                SpawnSize = 1,
            };
            world.Squads.Add(squad);
        }
        c.SquadId = squad.Id;
        c.Role = (int)SquadRole.Leader;
        squad.Members.Add(id);
        squad.Leader = id;
    }
}
