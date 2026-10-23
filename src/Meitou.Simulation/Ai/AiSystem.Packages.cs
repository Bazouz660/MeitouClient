using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The package classes with rules of their own beyond the patrol, bar and retreat (docs/game/ai.md "Package classes", decoded from their vtables; docs/simulation.md "AI",
/// Package classes): the wandering trader (16), the passing-by assault on a player town (29) and the occupation of it that follows (30). All run in the serial step.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The game hour each faction's last passing-by assault ended (the original's global map <c>DAT_14212dde0</c>, by faction, not saved): a squad of the faction may start
    /// one only while another of its squads assaults, or once its own wait has passed since then.
    /// </summary>
    readonly SortedDictionary<int, double> assaultEnds = [];

    /// <summary>The faction assault clocks once there is one (worlds without a passing-by assault hash as before).</summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (faction, end) in assaultEnds) { hasher.Add(faction); hasher.Add(end); }
    }

    /// <summary>A passing-by assault looks for the town faction's people within this of the town (<b>Verified</b>, 2500 in <c>FUN_140290450</c>).</summary>
    const float AssaultReach = 2500;
    /// <summary>The assault is not over before this many game hours (<b>Verified</b>, 0.25 in its finished test).</summary>
    const double AssaultMinHours = 0.25;
    /// <summary>The occupation ends after this long with no able member in the town (<b>Observed</b>: 4 of the timer the finished test adds to, read as game seconds).</summary>
    const float OccupyGiveUp = 4;
    /// <summary>Dialogue events: a leader coming into a town, each occupier as the occupation starts (the editor's <c>EventTriggerEnum</c>).</summary>
    const int EnterTown = 49, TakenOverPlayerTown = 73;

    /// <summary>Where the squad is: its leader's place, else its first living member's; null when nobody lives.</summary>
    static Vector3? LeaderPosition(World world, Squad squad)
    {
        var table = world.Characters;
        if (table.IsAlive(squad.Leader)) return table.Previous[squad.Leader.Slot].Position;
        foreach (var m in squad.Members)
            if (table.IsAlive(m)) return table.Previous[m.Slot].Position;
        return null;
    }

    /// <summary>
    /// The town a point is in (the original's <c>FUN_1402688d0</c>: the nearest town of any kind but a nest marker, when the point is within its radius, <c>FUN_140926d50</c>):
    /// placed towns by <see cref="TownData.Radius"/>, the player's by <see cref="PopulationData.PlayerTownExtent"/>.
    /// </summary>
    internal CampaignTown CurrentTown(World world, Vector3 p)
    {
        var at = Flat(p);
        var best = CampaignTown.None;
        float bestD = float.MaxValue, bestR = 0;
        foreach (var site in data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            float d = Vector2.DistanceSquared(at, Flat(site.Position));
            if (d < bestD) (best, bestD, bestR) = (CampaignTown.OfSite(site.Index), d, site.Town.Radius);
        }
        foreach (var town in world.Buildings.Towns)
        {
            float d = Vector2.DistanceSquared(at, Flat(town.Position));
            if (d < bestD) (best, bestD, bestR) = (CampaignTown.OfPlayer(town.Number), d, data.PlayerTownExtent);
        }
        return bestD <= bestR * bestR ? best : CampaignTown.None;
    }

    /// <summary>The centre of the running package's mission town (X/Z); null without one or when that player town is gone.</summary>
    Vector2? DestinationCentre(World world, Blackboard board)
    {
        var t = board.Destination;
        if (t.Site >= 0) return Flat(data.Sites[t.Site].Position);
        return t.PlayerTown > 0 && world.Buildings.FindTown(t.PlayerTown) is { } town ? Flat(town.Position) : null;
    }

    /// <summary>
    /// The town the squad is in, before the package choice (the original's Blackboard +0x138 and its arrival hour +0x140); coming into one, the leader fires ENTER_TOWN (49).
    /// </summary>
    void UpdateInTown(World world, Squad squad, Blackboard board)
    {
        if (LeaderPosition(world, squad) is not { } at) return;
        var town = CurrentTown(world, at);
        if (board.InTown.IsNone && !town.IsNone)
        {
            board.InTownSince = world.GameHours;
            if (world.Characters.IsAlive(squad.Leader)) world.Defer(new Dialogue.DialogueEvent(squad.Leader, CharacterId.None, EnterTown) { Tick = world.Tick + 1 });
        }
        board.InTown = town;
        if (town.Site >= 0 && !places.ContainsKey(town.Site)) RefreshPlaces(world);
    }

    /// <summary>
    /// The running package's update (the original's vtable +0x48, after the choice): the trader's arrival; under an assault or occupation the squad treats the town's faction
    /// as enemies (<see cref="Squad.War"/>; the original tags each one it sees with squad memory 4) and roams the town (engine choice: its wander centre).
    /// </summary>
    void StepPackage(World world, Squad squad, Blackboard board)
    {
        switch (board.CurrentClass)
        {
            case PackageClass.WanderingTrader:
                StepTrader(world, squad, board);
                break;
            case PackageClass.PassingAssault:
            case PackageClass.OccupyTown:
                var war = board.AssaultEnemy >= 0 ? new WarTargets(board.AssaultEnemy, false, default, 0) : WarTargets.None;
                if (squad.War != war) squad.War = war;
                if (DestinationCentre(world, board) is { } centre)
                {
                    squad.HomeCentre = centre;
                    squad.HomeRadius = MathF.Max(data.PlayerTownRadius * 0.5f, 40);
                }
                break;
            default:
                // An AI-run squad is never a campaign's (the campaign runs those), so nobody else sets its war targets.
                if (squad.War.IsSet) squad.War = WarTargets.None;
                break;
        }
    }

    // ---- the wandering trader (Package_WanderingTrader) ----

    /// <summary>
    /// Whether a trader may start (vtable +0x60): always while on its way or just arrived; once done in a town, when it is ready to leave (<see cref="ReadyToLeave"/>); in a player
    /// town at once. Engine choice: a town's own squad never starts it (Meitou unloads a town's squads with their town, so one away from home would vanish when it unloads).
    /// </summary>
    bool TraderCanStart(World world, Squad squad, Blackboard board) =>
        !squad.TownOwn && (board.InTown.PlayerTown > 0 || board.TraderState != 2 || ReadyToLeave(world, board));

    /// <summary>
    /// Ready to go on (<c>FUN_1402868c0</c>): in a player town that is not public (an outpost never is) at once; else after its stay (<see cref="Blackboard.TraderWait"/> hours since
    /// it came into the town) and only between 06:00 and 20:59.
    /// </summary>
    static bool ReadyToLeave(World world, Blackboard board) =>
        board.InTown.PlayerTown > 0 || board.TraderWait <= world.GameHours - board.InTownSince && world.Hour is > 5 and < 21;

    /// <summary>
    /// The trader's start (vtable +0x30): a stay of uniform [5, 8) hours, state 0 and the next town (<see cref="PopulationData.TraderTown"/> from where its leader is, not the town it
    /// is in); none found leaves it without one, so its next update tries again.
    /// </summary>
    void TraderStart(World world, Squad squad, Blackboard board)
    {
        ulong key = (ulong)squad.Id << 32 ^ (ulong)world.Tick;
        board.TraderWait = 5 + 3 * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiTraderWait));
        board.TraderState = 0;
        var at = LeaderPosition(world, squad) ?? squad.Position;
        int town = data.TraderTown(squad.Faction, Flat(at), board.InTown.Site, Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiTraderTown));
        board.Destination = town >= 0 ? CampaignTown.OfSite(town) : CampaignTown.None;
    }

    /// <summary>
    /// The trader's update (vtable +0x48): without a town it starts over; in its town the state goes 0 → 1 → 2 (2 is finished, so the next slot, its bar package, takes over),
    /// and when it is ready to leave there it drops the town (<c>FUN_140288bd0</c>) to draw a new one next time. The original also leaves 0.2 h after a flag of its leader is
    /// set (Character +0x648 → +0x70, meaning <b>Unknown</b>): not built.
    /// </summary>
    void StepTrader(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.IsNone)
        {
            TraderStart(world, squad, board);
            return;
        }
        if (board.InTown.IsNone || board.InTown != board.Destination) return;
        board.TraderState = board.TraderState == 0 ? 1 : 2;
        if (ReadyToLeave(world, board)) board.Destination = CampaignTown.None;
    }

    // ---- the passing-by assault (Package_PassingByTownAssault) and the occupation (Package_OccupyTown) ----

    /// <summary>Whether a character is alive and not down.</summary>
    static bool Up(World world, CharacterId id) =>
        world.Characters.IsAlive(id) && world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s)!.Medical is not { Incapacitated: true };

    int MembersUp(World world, Squad squad)
    {
        int n = 0;
        foreach (var m in squad.Members) if (Up(world, m)) n++;
        return n;
    }

    /// <summary>Whether someone of <paramref name="faction"/> (the player's characters for the player's faction) is up within <paramref name="range"/> of a point, or (range 0) in the board's mission town.</summary>
    bool FactionNear(World world, Blackboard board, int faction, Vector2 at, float range)
    {
        var table = world.Characters;
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        for (int i = 0; i < table.Previous.Length; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } c || c.Medical is { Incapacitated: true }) continue;
            if (c.Faction != faction && !(c.IsPlayer && faction == player)) continue;
            var p = table.Previous[i].Position;
            if (range > 0 ? Vector2.DistanceSquared(Flat(p), at) <= range * range : CurrentTown(world, p) == board.Destination) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a passing-by assault may start (vtable +0x60, <c>FUN_140290450</c>): attacks not set to Never; not spent; its leader in a player town whose faction it is not allied
    /// with (same or relation ≥ 50, <c>FUN_1406b2630</c>); the squad intact; its faction's wait passed (<c>FUN_140290020</c>: free while another squad of the faction assaults;
    /// a faction that never ended one is free and its clock starts now); and someone of the town's faction within 2500 of the town. The town test vtable +0x2f0 (Town +0x3b0,
    /// <b>Unknown</b>) is taken as passing.
    /// </summary>
    bool AssaultCanStart(World world, Squad squad, Blackboard board)
    {
        if (Settings.TownAttacks >= 5 || board.AssaultDone || !world.Characters.IsAlive(squad.Leader) || !board.Intact) return false;
        if (board.InTown.PlayerTown <= 0 || world.Buildings.FindTown(board.InTown.PlayerTown) is not { } town) return false;
        if (data.Relations.IsAlly(squad.Faction, town.Owner)) return false;
        if (!Assaulting(world, squad.Faction, squad))
        {
            if (assaultEnds.TryGetValue(squad.Faction, out double end))
            {
                if (world.GameHours - end <= board.AssaultCooldown) return false;
            }
            else assaultEnds[squad.Faction] = world.GameHours;
        }
        return FactionNear(world, board, town.Owner, Flat(town.Position), AssaultReach);
    }

    /// <summary>Whether another squad of the faction runs a passing-by assault now.</summary>
    static bool Assaulting(World world, int faction, Squad except)
    {
        foreach (var other in world.Squads.All)
            if (other != except && other.Faction == faction && other.Ai?.CurrentClass == PackageClass.PassingAssault) return true;
        return false;
    }

    /// <summary>
    /// The assault's start (vtable +0x30): its faction's wait for next time by <see cref="AiSettings.TownAttacks"/> (<c>FUN_140286a70</c>, game hours, uniform: Bombardment 0, High
    /// [6, 48), Normal [32, 96), Fewer [72, 144), Rare [130, 188)); the town its leader is in is its mission town, the town's faction its enemy, the start hour noted.
    /// </summary>
    void AssaultStarted(World world, Squad squad, Blackboard board)
    {
        var (min, max) = Settings.TownAttacks switch { 1 => (6f, 48f), 2 => (32f, 96f), 3 => (72f, 144f), 4 => (130f, 188f), _ => (0f, 0f) };
        board.AssaultCooldown = min + (max - min) * Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 32 ^ (ulong)world.Tick, RngPurpose.Think, SeedSalts.AiAssaultWait));
        board.Destination = board.InTown;
        board.AssaultStart = world.GameHours;
        board.AssaultEnemy = world.Buildings.FindTown(board.InTown.PlayerTown)?.Owner ?? -1;
        if (DestinationCentre(world, board) is { } centre) PostsInto(world, squad, centre, null);
    }

    /// <summary>
    /// Engine choice: the members' posts move into the town they assault or hold (a ring round its centre by member), so their idle and their node and bed goals, which have
    /// no building there, stay in it (the original's ransacking goal, <c>Task_PatrolTownViolent</c>, has a target finder Meitou lacks); with <paramref name="say"/> each says it.
    /// </summary>
    static void PostsInto(World world, Squad squad, Vector2 centre, int? say)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s)?.Ai is not { } mind) continue;
            float angle = mind.MemberIndex * 2.39996f, r = 20 + 6 * mind.MemberIndex;
            mind.Post = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
            // A stroll picked round the old post is dropped, so the next one is round the new.
            if (table.Next[s].Task == (byte)CharacterTask.Wander) Become(table, s, CharacterTask.Idle);
            if (say is { } e) world.Defer(new Dialogue.DialogueEvent(m, CharacterId.None, e) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>The assault gives way (vtable +0x38): the last of its faction's running ones to end starts the faction's wait.</summary>
    void AssaultEnded(World world, Squad squad)
    {
        if (!Assaulting(world, squad.Faction, squad)) assaultEnds[squad.Faction] = world.GameHours;
    }

    /// <summary>Someone of the attacked faction still stands in the town (engine reading of the original's target set: those within 2500 at the start and those seen since).</summary>
    bool DefenderStanding(World world, Blackboard board) =>
        board.AssaultEnemy >= 0 && FactionNear(world, board, board.AssaultEnemy, default, 0);

    /// <summary>
    /// Whether the assault is over (vtable +0x68, <c>FUN_14028eca0</c>): the squad no longer intact ends it for good; not before 0.25 h; while a defender stands it goes on; then it
    /// is over (for good) once at least 2 of the squad are up. A finished assault never starts again (+0xb4).
    /// </summary>
    bool AssaultFinished(World world, Squad squad, Blackboard board, AiPackage package)
    {
        bool done;
        if (!board.Intact) done = true;
        else if (world.GameHours - board.AssaultStart < AssaultMinHours || DefenderStanding(world, board)) return false;
        else done = MembersUp(world, squad) >= 2;
        if (done && board.Current == package) board.AssaultDone = true;
        return done;
    }

    /// <summary>Whether the assault hands over to its <c>Leads to</c>, the occupation (vtable +0x90, <c>FUN_14028ea90</c>): intact, no defender standing and at least 2 up.</summary>
    bool AssaultMayLead(World world, Squad squad, Blackboard board) =>
        board.Intact && !DefenderStanding(world, board) && MembersUp(world, squad) >= 2;

    /// <summary>
    /// The occupation's start (vtable +0x30, <c>FUN_140288fe0</c>): the town its leader is in is its mission town and that town's faction its enemy; every member says
    /// TAKEN_OVER_PLAYER_TOWN (73). The original also makes the platoon persistent while it lasts (Meitou keeps a wild squad only with <c>KeepWild</c>) and lists it among the
    /// town's hostile occupiers (Town +0x118, its count +0x128, which the campaigns read: <see cref="Occupied"/>). The posts move into the town (<see cref="PostsInto"/>).
    /// </summary>
    void OccupyStarted(World world, Squad squad, Blackboard board)
    {
        board.OccupyAway = 0;
        if (board.InTown.PlayerTown <= 0 || world.Buildings.FindTown(board.InTown.PlayerTown) is not { } town) return;
        board.Destination = board.InTown;
        board.AssaultEnemy = town.Owner;
        PostsInto(world, squad, Flat(town.Position), TakenOverPlayerTown);
    }

    /// <summary>
    /// Whether the occupation is over (vtable +0x68, <c>FUN_140289290</c>): the town gone; or no member alive and up in it (<c>FUN_140289180</c>) for more than 4 game seconds in
    /// a row (counted on the package cadence).
    /// </summary>
    bool OccupyFinished(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.PlayerTown <= 0 || world.Buildings.FindTown(board.Destination.PlayerTown) is null) return true;
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (Up(world, m) && CurrentTown(world, table.Previous[m.Slot].Position) == board.Destination)
            {
                board.OccupyAway = 0;
                return false;
            }
        board.OccupyAway += Settings.PackageEveryTicks * world.TickSeconds;
        return board.OccupyAway > OccupyGiveUp;
    }

    /// <summary>The occupation gives way (vtable +0x38, <c>FUN_140296150</c>): "{1} squad has been removed from {2}" (its faction, the town) while the town stands.</summary>
    void OccupyEnded(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.PlayerTown <= 0 || world.Buildings.FindTown(board.Destination.PlayerTown) is not { } town) return;
        string faction = squad.Faction >= 0 && squad.Faction < data.Factions.Count ? data.Factions[squad.Faction].Name : "";
        world.Post(Loc.Tr("{1} squad has been removed from {2}", faction, town.Name));
    }

    /// <summary>Whether a town has hostile occupiers (the original's Town +0x128, the count of its occupier set): a squad whose running occupation holds it.</summary>
    public static bool Occupied(World world, CampaignTown town)
    {
        foreach (var squad in world.Squads.All)
            if (squad.Ai is { CurrentClass: PackageClass.OccupyTown } board && board.Destination == town) return true;
        return false;
    }
}
