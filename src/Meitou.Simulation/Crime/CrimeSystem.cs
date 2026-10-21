using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Crimes and bounties (docs/simulation.md "Crimes and bounties"; the original's rules in docs/game/economy.md section 11 and docs/game/carry-loot.md 4.3 and 4.6). Everything here runs in
/// serial phases: the deeds are reported by the systems that see them (combat's <c>Inputs</c> and commit, the carry system's <c>SlowWorld</c>), the witness sweep and the bounty expiry run in
/// this system's <c>SlowWorld</c>, in slot order. The state lives on the characters (<see cref="CharacterCold.Pending"/>, <see cref="CharacterCold.Bounties"/>) and is hashed only while set.
/// <list type="bullet">
/// <item><b>Pending</b> (<see cref="SetPending"/>, the game's <c>FUN_140852c80</c>): a deed first becomes a pending crime on the culprit, unless the culprit is police-like, the victim is
/// of the culprit's own faction or of a lawless one. An Assault does not replace another pending crime.</item>
/// <item><b>Witnesses</b> (<see cref="Sweep"/>, after <c>FUN_140854d10</c>): an awake NPC of another faction that sees the culprit and cares about the victim (by the crime) sets the timer
/// to 20; only a police-like witness of a faction that books bounties books it (<see cref="Book"/>, <c>FUN_140853ec0</c>): the crime table's amount into the ledger entry of the
/// <b>witness's</b> faction (<c>FUN_140852010</c>).</item>
/// <item><b>Police</b>: a hit between a police-like character and another books an Assault on the other at once (<c>FUN_140665fa0</c>); police-like NPCs spot, fine and arrest a character wanted by
/// their faction (<see cref="ArrestSystem"/>).</item>
/// <item><b>Expiry</b>: an entry lasts 0.04 hours per cat since its last raise, for ever from 10000 (<see cref="CrimeTable.LifetimeHours"/>).</item>
/// </list>
/// Only the player's characters are culprits (engine choice: the simulation's NPCs fight only enemies or in defence, loot nothing and carry only their own).
/// </summary>
public sealed class CrimeSystem(FactionRelations relations, CrimeSettings? settings = null) : ITickSystem
{
    /// <summary>The freshness a pending crime starts with at least (the setter's minimum, <b>Observed</b>), and the timers the game gives some deeds.</summary>
    public const float MinTimer = 3, WitnessTimer = 20, PoliceHitTimer = 30, KidnapTimer = 20;
    /// <summary>The timer of a murder: engine choice (the game's setter of code 4 was not found).</summary>
    public const float MurderTimer = 20;
    /// <summary>The "recently wanted" marker a booking or a witnessed crime sets (<see cref="CharacterCold.RecentlyWanted"/>; the game's 250.0, counted down here as game seconds: engine choice).</summary>
    public const float RecentlyWantedSeconds = 250;

    readonly List<Booking> bookings = [];
    long bookingsTick = -1;

    /// <summary>The bookings of this tick by a witness (the police-like NPC that booked, the culprit, the faction): the arrest system's witnessed-crime trigger. Not world state.</summary>
    public IReadOnlyList<Booking> BookingsOf(long tick) => bookingsTick == tick ? bookings : [];

    readonly List<CrimeEvent> log = [];

    public CrimeSettings Settings { get; } = settings ?? new CrimeSettings();
    public CrimeFactions Factions { get; } = new(relations);
    public FactionRelations Relations => Factions.Relations;

    /// <summary>What happened (with <see cref="CrimeSettings.RecordLog"/>).</summary>
    public IReadOnlyList<CrimeEvent> Log => log;

    // ------------------------------------------------------------------ who is who

    /// <summary>
    /// Police-like (the game's <c>FUN_140599600</c>, <b>Observed</b>): never of a BANDIT-type faction; <c>NPC class</c> LAW_ENFORCEMENT; or MILITARY when the character's own
    /// faction's object matches its squad's (read here as: a member of a resident squad of a town, of its own faction; roaming military are not police: engine reading). The
    /// player's characters never are (engine: their status object has no such link).
    /// </summary>
    public bool IsPoliceLike(World world, CharacterCold c)
    {
        if (c.IsPlayer || Factions.Bandit(c.Faction)) return false;
        if (c.NpcClass == FundamentalType.LawEnforcement) return true;
        return c.NpcClass == FundamentalType.Military && c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { TownOwn: true } squad && squad.Faction == c.Faction;
    }

    /// <summary>A faction regards another as friendly: the same, or allied (relation 50 or more), as the carry system reads the game's "regards as friendly".</summary>
    public bool Friendly(int a, int b) => a == b || (Relations.Contains(a) && Relations.Contains(b) && Relations.IsAlly(a, b));

    /// <summary>The character has an unclaimed bounty of <paramref name="faction"/> (what a police-like NPC of that faction attacks it for).</summary>
    public static bool IsWantedBy(CharacterCold c, int faction) => c.Bounties?.Of(faction) is { Amount: > 0, Claimed: false };

    // ------------------------------------------------------------------ deeds

    /// <summary>
    /// Sets a pending crime on the culprit (the game's setter <c>FUN_140852c80</c>, <b>Observed</b>): refused for a police-like culprit, a victim of the culprit's own faction and a lawless
    /// victim faction; an Assault does not replace another pending crime (it raises that one's timer to 20); the same code again keeps the booked flag; a new code clears it. The timer
    /// is at least <see cref="MinTimer"/>. True when the crime is now pending.
    /// </summary>
    public bool SetPending(World world, int culprit, CrimeCode code, int victimFaction, CharacterId victim, float timer)
    {
        var c = world.Characters.Cold(culprit);
        if (c is null || !c.IsPlayer || IsPoliceLike(world, c) || victimFaction == c.Faction || Factions.Lawless(victimFaction)) return false;
        timer = MathF.Max(timer, MinTimer);
        if (c.Pending is { } p && p.Code != code && code == CrimeCode.Assault)
        {
            p.Timer = MathF.Max(p.Timer, WitnessTimer);
            return false;
        }
        if (c.Pending is { } same && same.Code == code)
        {
            same.VictimFaction = victimFaction;
            same.Victim = victim;
            same.Timer = MathF.Max(same.Timer, timer);
            return true;
        }
        c.Pending = new PendingCrime { Code = code, VictimFaction = victimFaction, Victim = victim, Timer = timer };
        Note(world, CrimeEventKind.Pending, culprit, code, victimFaction, 0, CharacterId.None);
        return true;
    }

    /// <summary>
    /// An attack order on <paramref name="victim"/> (not a defensive one): an Assault (the game's <c>FUN_140334070</c>, <b>Observed</b>, run when an attack task starts) unless the victim is
    /// wanted by the attacker's faction, is a bandit (<c>NPC class</c> 8), has a pending crime of its own, or its faction is hostile to the attacker's (engine reading of the game's victim test
    /// <c>FUN_140436610</c>, not decoded).
    /// </summary>
    public void Assault(World world, int attacker, int victim)
    {
        var table = world.Characters;
        if (table.Cold(attacker) is not { IsPlayer: true } a || table.Cold(victim) is not { IsPlayer: false } v) return;
        if (IsWantedBy(v, a.Faction) || v.NpcClass == FundamentalType.Bandit || v.Pending is not null || AggroSystem.IsEnemy(Relations, v.Faction, a.Faction)) return;
        SetPending(world, attacker, CrimeCode.Assault, v.Faction, table.IdOf(victim), MinTimer);
    }

    /// <summary>A killing blow (engine choice: the game's pending Murder has no setter among the 25 callers found): a Murder, on the same terms as an assault.</summary>
    public void Murder(World world, int killer, int victim)
    {
        var table = world.Characters;
        if (table.Cold(killer) is not { IsPlayer: true } k || table.Cold(victim) is not { IsPlayer: false } v) return;
        if (v.NpcClass == FundamentalType.Bandit || AggroSystem.IsEnemy(Relations, v.Faction, k.Faction)) return;
        SetPending(world, killer, CrimeCode.Murder, v.Faction, table.IdOf(victim), MurderTimer);
    }

    /// <summary>
    /// Items taken from a downed character (the carry system's take-all): Looting (code 10, the right-click transfer's code for a downed owner, <b>Verified</b> call site) unless the game's
    /// permission test (<c>FUN_140793940</c>, <b>Observed</b>) allows it: the victim is a slave (here: <c>NPC class</c> SLAVE), a bandit, or has any bounty; or the taker is LAW_ENFORCEMENT,
    /// MILITARY or DIPLOMAT.
    /// </summary>
    public void Loot(World world, int looter, int victim)
    {
        var table = world.Characters;
        if (table.Cold(looter) is not { } l || table.Cold(victim) is not { } v || !IsLootTheft(l, v)) return;
        SetPending(world, looter, CrimeCode.Looting, v.Faction, table.IdOf(victim), MinTimer);
    }

    /// <summary>
    /// Whether the player's taking from a downed character is a theft: the permission test of <see cref="Loot"/> (<c>FUN_140793940</c>, <b>Observed</b>). The carry system marks what such a
    /// loot takes as stolen from the victim's faction (<see cref="Items.ItemInstance.StolenFrom"/>).
    /// </summary>
    public static bool IsLootTheft(CharacterCold looter, CharacterCold victim) =>
        looter.IsPlayer && !victim.IsPlayer
        && victim.NpcClass is not (FundamentalType.Slave or FundamentalType.Bandit) && victim.Bounties is not { Any: true }
        && looter.NpcClass is not (FundamentalType.LawEnforcement or FundamentalType.Military or FundamentalType.Diplomat);

    /// <summary>
    /// A landed hit between <paramref name="attacker"/> and <paramref name="defender"/> (the game's hit outcome <c>FUN_140665fa0</c>, <b>Observed</b>): when exactly one of them is
    /// police-like, the other gets a pending Assault (timer 30) against the police faction, and whatever crime it has pending is booked with that faction at once. The game's code reads
    /// as charging the non-police side whichever way the blow went (being hit by the police counts too).
    /// </summary>
    public void Hit(World world, int attacker, int defender)
    {
        var table = world.Characters;
        if (table.Cold(attacker) is not { } a || table.Cold(defender) is not { } d) return;
        bool pa = IsPoliceLike(world, a), pd = IsPoliceLike(world, d);
        if (pa == pd) return;
        int culprit = pd ? attacker : defender, police = pd ? defender : attacker;
        var c = table.Cold(culprit)!;
        var p = table.Cold(police)!;
        if (!c.IsPlayer) return;
        SetPending(world, culprit, CrimeCode.Assault, p.Faction, table.IdOf(police), PoliceHitTimer);
        if (c.Pending is not null) Book(world, culprit, p.Faction, table.IdOf(police));
    }

    // ------------------------------------------------------------------ booking

    /// <summary>
    /// Books the culprit's pending crime with <paramref name="faction"/> (the game's <c>FUN_140853ec0</c>, <b>Observed</b>): once per pending crime (the booked flag), not for a culprit of
    /// <c>NPC class</c> BANDIT, DIPLOMAT or LAW_ENFORCEMENT, and Enslaving adds nothing. The amount of the crime table is added to the faction's entry, the crime's bit set and the time of
    /// the last raise reset. The entry is the faction's own when it books bounties (<c>FUN_140852010</c>; its parent faction <c>+0x38</c> is <b>Unknown</b> and not used).
    /// </summary>
    public bool Book(World world, int culprit, int faction, CharacterId witness)
    {
        var c = world.Characters.Cold(culprit);
        if (c?.Pending is not { Booked: false } p || !Factions.Books(faction)) return false;
        if (c.NpcClass is FundamentalType.Bandit or FundamentalType.Diplomat or FundamentalType.LawEnforcement) return false;
        p.Booked = true;
        if (p.Code == CrimeCode.Enslaving) return false;
        var entry = (c.Bounties ??= new BountyLedger()).Get(faction, Factions.NameOf(faction));
        entry.Amount += CrimeTable.Amount(p.Code);
        entry.Crimes |= 1 << (int)p.Code;
        entry.RaisedTick = world.Tick;
        c.RecentlyWanted = RecentlyWantedSeconds;
        if (!witness.IsNone)
        {
            if (bookingsTick != world.Tick) { bookings.Clear(); bookingsTick = world.Tick; }
            bookings.Add(new Booking(witness, world.Characters.IdOf(culprit), faction));
        }
        Note(world, CrimeEventKind.Booked, culprit, p.Code, faction, entry.Amount, witness);
        if (c.IsPlayer) world.Post($"{c.Name} is wanted by {entry.FactionName} for {CrimeTable.Name(p.Code)}: bounty c.{entry.Amount}");
        return true;
    }

    // ------------------------------------------------------------------ the sweep

    public void SlowWorld(World world)
    {
        int every = Math.Max(1, Settings.SweepEveryTicks);
        if (world.Tick % every != 0) return;
        Sweep(world, every * world.TickSeconds);
    }

    void Sweep(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        double hoursPerTick = world.TickSeconds * 11.0 / 1200.0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c) continue;
            if (c.Bounties is { } ledger && !c.IsImprisoned) Expire(world, s, c, ledger, hoursPerTick);   // paused while imprisoned (economy.md 11.3)
            if (c.RecentlyWanted > 0) c.RecentlyWanted = MathF.Max(0, c.RecentlyWanted - seconds);
            if (c.Pending is { } p)
            {
                p.Timer -= seconds;
                if (p.Timer <= 0) c.Pending = null;
            }
            if (!c.IsPlayer || !c.CarriedBy.IsNone) continue;   // a carried suspect is not looked at (the game's witness function)
            bool kidnapping = !c.Carrying.IsNone && table.TryResolveNext(c.Carrying, out int body) && Innocent(c, table.Cold(body)!);
            if (c.Pending is null && !kidnapping) continue;
            Witnesses(world, s, c, kidnapping);
        }
    }

    /// <summary>
    /// A carried body whose carrying is a kidnapping when seen (carry-loot.md 4.6, the game's "innocent victim", <b>Observed</b>; its status flag <c>+0xe9</c> is <b>Unknown</b> and read
    /// here as: an NPC of another, real, non-bandit faction, not a bandit and with no bounty).
    /// </summary>
    bool Innocent(CharacterCold carrier, CharacterCold body) =>
        !body.IsPlayer && body.Faction != carrier.Faction && !Factions.Lawless(body.Faction) && body.NpcClass != FundamentalType.Bandit && body.Bounties is not { Any: true };

    /// <summary>
    /// Every awake NPC that sees the culprit, in slot order, reacts as the game's witness function does (<c>FUN_140854d10</c>, <b>Observed</b>): not of the culprit's faction, not carried,
    /// with no pending crime of its own; nothing when its faction is hostile to the victim's, when the victim's is lawless, when it books bounties and the victim's is a bandit faction, or
    /// when it regards the culprit as friendly and the victim is of another faction. Theft and Looting need it to care about the victim (same faction or allied); Looting also that it
    /// does not regard the culprit as friendly; Kidnapping a victim and that care; an Assault a victim that it does not regard as an enemy; a Murder nothing more. A witness then sets the
    /// timer to 20 and, police-like of a faction that books bounties, books the crime. With <paramref name="kidnapping"/>, a witness that cares about the carried body first sets a pending
    /// Kidnapping (timer 20) on the carrier (the carrying check that runs before the crime check).
    /// </summary>
    void Witnesses(World world, int culprit, CharacterCold c, bool kidnapping)
    {
        var table = world.Characters;
        var next = table.Next;
        var at = next[culprit].Position;
        CharacterCold? body = kidnapping && table.TryResolveNext(c.Carrying, out int b) ? table.Cold(b) : null;
        for (int w = 0; w < table.HighWater; w++)
        {
            if (w == culprit || !next[w].Alive) continue;
            if (table.Cold(w) is not { IsPlayer: false, Medical.Incapacitated: false } wc || !wc.CarriedBy.IsNone || wc.Pending is not null) continue;
            int wf = wc.Faction;
            if (wf == c.Faction || !Relations.Contains(wf)) continue;
            if (AggroSystem.Visibility(next[w].Position, next[w].Yaw, at, AggroSystem.RangeOf(wc, Settings.Sight)) <= 0) continue;
            if (body is not null && !Friendly(wf, c.Faction) && Friendly(wf, body.Faction))
                SetPending(world, culprit, CrimeCode.Kidnapping, body.Faction, c.Carrying, KidnapTimer);
            if (c.Pending is not { } p) continue;
            if (!Reacts(world, wc, c, p)) continue;
            p.Timer = WitnessTimer;
            c.RecentlyWanted = RecentlyWantedSeconds;
            var witness = table.IdOf(w);
            if (!p.Witnessed)
            {
                p.Witnessed = true;
                Note(world, CrimeEventKind.Witnessed, culprit, p.Code, wf, 0, witness);
                world.Defer(new Dialogue.DialogueEvent(witness, table.IdOf(culprit), WitnessEvent(world, wc, p)) { Tick = world.Tick + 1 });
                if (p.Code != CrimeCode.Looting) world.Post($"{c.Name} has been caught committing {CrimeTable.Name(p.Code)}!");   // the loot already says "was seen looting"
            }
            if (!p.Booked && IsPoliceLike(world, wc) && Factions.Books(wf)) Book(world, culprit, wf, witness);
            if (p.Booked) return;
        }
    }

    /// <summary>The dialogue event the witness says at the culprit (<c>FUN_140854d10</c>'s switch on the crime code, docs/game/dialogue.md "Event sources").</summary>
    static int WitnessEvent(World world, CharacterCold w, PendingCrime p) => p.Code switch
    {
        CrimeCode.Burglary or CrimeCode.Terrorism => 43,   // WITNESS_THIEF_OR_LOCKPICK
        CrimeCode.Theft or CrimeCode.Fencing or CrimeCode.Looting =>
            world.Characters.TryResolveNext(p.Victim, out int v) && world.Characters.Cold(v) is { SquadId: >= 0 } vc && vc.SquadId == w.SquadId ? 8 : 42,   // THIEF_CAUGHT_STEALING_FROM_ME, WITNESS_LOOTING_ALLY
        CrimeCode.Trespassing => 17,
        CrimeCode.CropTheft => 57,
        CrimeCode.Enslaving or CrimeCode.Kidnapping => 58,
        CrimeCode.UniformTheft => 34,
        _ => 41,   // WITNESS_GENERIC_ASSAULT
    };

    /// <summary>The witness function's tests for one witness and one pending crime (see <see cref="Witnesses"/>).</summary>
    bool Reacts(World world, CharacterCold w, CharacterCold culprit, PendingCrime p)
    {
        int wf = w.Faction, vf = p.VictimFaction;
        if (!Relations.Contains(vf) || Relations.IsHostile(wf, vf) || Factions.Lawless(vf)) return false;
        if (Factions.Books(wf) && Factions.Bandit(vf)) return false;
        bool friendlySuspect = Friendly(wf, culprit.Faction);
        if (friendlySuspect && vf != wf) return false;
        bool cares = Friendly(wf, vf);
        var victim = world.Characters.TryResolveNext(p.Victim, out int v) ? world.Characters.Cold(v) : null;
        return p.Code switch
        {
            CrimeCode.Theft or CrimeCode.Fencing => cares,
            CrimeCode.Looting => cares && !friendlySuspect,
            CrimeCode.Kidnapping => victim is not null && cares,
            CrimeCode.Assault => victim is not null && !AggroSystem.IsEnemy(Relations, wf, victim.Faction),
            _ => true,
        };
    }

    /// <summary>Removes entries whose lifetime since their last raise is over (the game removes one per tick in its per-character tick; all at once here).</summary>
    void Expire(World world, int slot, CharacterCold c, BountyLedger ledger, double hoursPerTick)
    {
        for (int k = ledger.Count - 1; k >= 0; k--)
        {
            var e = ledger.Entries[k];
            if (e.Amount <= 0) continue;
            double elapsed = (world.Tick - e.RaisedTick) * hoursPerTick;
            if (elapsed >= 0 && elapsed <= CrimeTable.LifetimeHours(e.Amount)) continue;
            ledger.Remove(e);
            Note(world, CrimeEventKind.Expired, slot, CrimeCode.None, e.Faction, e.Amount, CharacterId.None);
            if (c.IsPlayer) world.Post($"{c.Name} is no longer wanted by {e.FactionName}");
        }
        if (ledger.Count == 0) c.Bounties = null;
    }

    /// <summary>The bounty lines of a character for the HUD, with the hours left at <paramref name="tick"/>.</summary>
    public static IReadOnlyList<BountyLine> Lines(CharacterCold c, long tick, float tickSeconds)
    {
        if (c.Bounties is not { Count: > 0 } ledger) return [];
        double hoursPerTick = tickSeconds * 11.0 / 1200.0;
        var lines = new List<BountyLine>(ledger.Count);
        foreach (var e in ledger.Entries)
            if (e.Amount > 0)
                lines.Add(new BountyLine(e.FactionName, e.Amount, CrimeTable.Names(e.Crimes), CrimeTable.LifetimeHours(e.Amount) - (tick - e.RaisedTick) * hoursPerTick));
        return lines;
    }

    void Note(World world, CrimeEventKind kind, int culprit, CrimeCode code, int faction, int amount, CharacterId witness)
    {
        if (Settings.RecordLog) log.Add(new CrimeEvent(world.Tick, kind, world.Characters.IdOf(culprit), code, faction, amount, witness));
    }
}
