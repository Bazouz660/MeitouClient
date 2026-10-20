using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Simulation;

/// <summary>A change to the player's squads (docs/game/ui-screens.md "Squads tab"), applied by <see cref="SquadManagement"/>.</summary>
public abstract record SquadCommand : SimCommand;

/// <summary>NEW SQUAD: an empty squad of the player's faction at <paramref name="At"/> (the camera's focus).</summary>
public sealed record NewSquadCommand(Vector3 At) : SquadCommand;

/// <summary>A squad box's name was edited (at most <see cref="SquadManagement.NameLength"/> characters).</summary>
public sealed record RenameSquadCommand(int Squad, string Name) : SquadCommand;

/// <summary>DELETE: removes an empty squad that is not the player's last.</summary>
public sealed record RemoveSquadCommand(int Squad) : SquadCommand;

/// <summary>
/// A portrait dropped on a squad box: the character goes into <paramref name="Squad"/> at <paramref name="Index"/> (-1: the end). With <paramref name="Swap"/> (Shift held) and a
/// member at <paramref name="Index"/>, the two change places instead.
/// </summary>
public sealed record MoveToSquadCommand(CharacterId Character, int Squad, int Index = -1, bool Swap = false) : SquadCommand;

/// <summary>A squad box dropped in the squads list: the squad takes place <paramref name="Index"/> in the order (-1: the end).</summary>
public sealed record ReorderSquadCommand(int Squad, int Index) : SquadCommand;

/// <summary>The squad bar shows <paramref name="Squad"/> (a squad tab); -1 is the original's change_squad (Tab): the next squad in order that has members.</summary>
public sealed record ActiveSquadCommand(int Squad) : SquadCommand;

/// <summary>A portrait dropped on DRAG HERE TO DISMISS and confirmed: the character leaves the player's faction.</summary>
public sealed record DismissCommand(CharacterId Character) : SquadCommand;

/// <summary>Host only (the command gate refuses it from clients): player squad <paramref name="Squad"/> passes to player <paramref name="Owner"/> (an absent player's squads, a reloaded save).</summary>
public sealed record TransferSquad(int Squad, int Owner) : SquadCommand;

/// <summary>
/// The player's squads (docs/game/ui-screens.md "Squads tab", from the original's <c>SquadManagementScreen</c>): their order, the active squad, and the squad screen's
/// commands with the original's limits and refusals (CONSTANTS <c>max squads</c>, <c>max squad size</c>, <c>max faction size</c>). A player squad's first member leads it.
/// Each human has squads of their own (<see cref="Squad.Owner"/>; docs/simulation.md "Multiplayer (host-authoritative co-op)"): a command acts on its issuer's squads, and
/// the order, the active squad and <c>max squads</c> are per owner; <c>max faction size</c> counts the whole faction.
/// </summary>
public static class SquadManagement
{
    /// <summary>The squad name box's limit (<c>setMaxTextLength(0x20)</c>, <b>Observed</b>).</summary>
    public const int NameLength = 32;
    /// <summary>The faction a dismissed character joins (<c>1233-gamedata.base</c>, Tech Hunters), else <c>nofac</c>; and the squad template of its new squad ("Wandering Squad").</summary>
    public const string DismissFaction = "1233-gamedata.base", DismissFallbackFaction = "nofac", DismissTemplate = "5006-squad.mod";

    /// <summary>Every player's squads (of the player's faction and not a platoon's active half), by owner, then in each owner's order.</summary>
    public static List<Squad> PlayerSquads(World world)
    {
        var list = new List<Squad>();
        if (!world.Player.Exists) return list;
        foreach (var s in world.Squads.All)
            if (IsPlayerSquad(world, s)) list.Add(s);
        list.Sort((a, b) => a.Owner != b.Owner ? a.Owner.CompareTo(b.Owner) : a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>Player <paramref name="owner"/>'s own squads in order (<see cref="Squad.Owner"/>).</summary>
    public static List<Squad> PlayerSquads(World world, int owner)
    {
        var list = new List<Squad>();
        if (!world.Player.Exists) return list;
        foreach (var s in world.Squads.All)
            if (IsPlayerSquad(world, s) && s.Owner == owner) list.Add(s);
        list.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>The player's characters alive (what CONSTANTS <c>max faction size</c> counts).</summary>
    public static int FactionHeads(World world)
    {
        var table = world.Characters;
        int heads = 0;
        for (int i = 0; i < table.Next.Length; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 }) heads++;
        return heads;
    }

    /// <summary>
    /// A new, empty squad of the player's faction for player <paramref name="owner"/> at <paramref name="at"/>, of the SQUAD_TEMPLATE "blank squad" (the original's
    /// <c>FUN_1407f4420</c> with no template), last in that player's order and named like the original's platoons, the faction's name and a number unique among every
    /// player's squads (<c>Nameless_1</c>).
    /// </summary>
    public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName, int owner = 0)
    {
        var template = db.OfType(FcsRecordType.SQUAD_TEMPLATE).FirstOrDefault(r => r.Name == "blank squad");
        var all = PlayerSquads(world);
        var own = PlayerSquads(world, owner);
        int n = all.Count;
        while (all.Any(s => s.Name == $"{factionName}_{n}")) n++;
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? "blank squad",
            Name = $"{factionName}_{n}",
            Index = own.Count == 0 ? 0 : own[^1].Index + 1,
            Owner = owner,
            Faction = world.Player.Faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
        };
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>Keeps each session's active squad one of its player's: when it is gone, the first in order with members, else the first, else none (-1).</summary>
    internal static void KeepActive(World world)
    {
        if (!world.Player.Exists) return;
        foreach (var session in world.Player.Sessions)
        {
            if (Own(world, session.Number, session.Squad) is not null) continue;
            var squads = PlayerSquads(world, session.Number);
            session.Squad = (squads.FirstOrDefault(q => q.Members.Count > 0) ?? squads.FirstOrDefault())?.Id ?? -1;
        }
    }

    internal static void Apply(World world, PopulationData? data, SquadCommand command)
    {
        if (!world.Player.Exists) return;
        var constants = data?.Bodies.Constants;
        int owner = command.Player;
        switch (command)
        {
            case NewSquadCommand create:
                if (data is null) return;
                // The original counts the squad boxes against `max squads` (FUN_140491b50); each human has a squads screen of their own (engine choice).
                if (PlayerSquads(world, owner).Count >= constants!.MaxSquads) { world.Post(owner, "Maximum number of squads reached."); return; }
                NewSquad(world, data.Db, create.At, FactionName(world, data), owner);
                break;
            case RenameSquadCommand rename:
                if (Own(world, owner, rename.Squad) is { } named) named.Name = rename.Name.Length > NameLength ? rename.Name[..NameLength] : rename.Name;
                break;
            case RemoveSquadCommand remove:
                if (Own(world, owner, remove.Squad) is not { } doomed) return;
                // FUN_140491d20: an empty squad only, and never the last.
                if (doomed.Members.Count > 0) { world.Post(owner, "The squad needs to be empty before it can be removed."); return; }
                if (PlayerSquads(world, owner).Count == 1) { world.Post(owner, "You must have at least one squad."); return; }
                world.Squads.Remove(doomed.Id);
                Renumber(PlayerSquads(world, owner));
                KeepActive(world);
                break;
            case MoveToSquadCommand move:
                Move(world, owner, constants?.MaxSquadSize ?? int.MaxValue, move);
                break;
            case ReorderSquadCommand reorder:
                if (Own(world, owner, reorder.Squad) is not { } moved) return;
                var order = PlayerSquads(world, owner);
                order.Remove(moved);
                order.Insert(reorder.Index < 0 || reorder.Index > order.Count ? order.Count : reorder.Index, moved);
                Renumber(order);
                break;
            case ActiveSquadCommand active:
                if (active.Squad >= 0) { if (Own(world, owner, active.Squad) is { } shown) world.SessionOf(active).Squad = shown.Id; }
                else NextSquad(world, world.SessionOf(active));
                break;
            case DismissCommand dismiss:
                if (data is not null) Dismiss(world, data, dismiss);
                break;
        }
    }

    /// <summary>A squad of the player's faction that is not a platoon's active half (whoever owns it).</summary>
    static bool IsPlayerSquad(World world, Squad s) => s.Faction == world.Player.Faction && s.PlatoonId < 0;

    /// <summary>Squad <paramref name="id"/> when it is one of player <paramref name="owner"/>'s, else null.</summary>
    static Squad? Own(World world, int owner, int id) => world.Squads.Find(id) is { } s && IsPlayerSquad(world, s) && s.Owner == owner ? s : null;

    static string FactionName(World world, PopulationData data) =>
        world.Player.Faction < data.Factions.Count && data.Factions[world.Player.Faction].Name is { Length: > 0 } name ? name : "Nameless";

    static void Renumber(List<Squad> order)
    {
        for (int i = 0; i < order.Count; i++) order[i].Index = i;
    }

    /// <summary>change_squad (<c>FUN_1407f6380</c>): with more than one squad, the session's next one after the active in order (wrapping) that has members; none: unchanged.</summary>
    static void NextSquad(World world, PlayerSession session)
    {
        var squads = PlayerSquads(world, session.Number);
        if (squads.Count < 2) return;
        int at = squads.FindIndex(s => s.Id == session.Squad);
        for (int k = 1; k < squads.Count; k++)
        {
            var next = squads[(at + k + squads.Count) % squads.Count];
            if (next.Members.Count > 0) { session.Squad = next.Id; return; }
        }
    }

    /// <summary>
    /// A portrait drop (<c>FUN_14049b050</c>): into another squad that already has <c>max squad size</c> members only with Shift; with Shift onto a member the two swap places
    /// (<c>FUN_140792e60</c> within a squad); else the character is put in at the place (<c>FUN_140796620</c>, the end when none). Both squads must be the issuer's.
    /// </summary>
    static void Move(World world, int owner, int maxSize, MoveToSquadCommand move)
    {
        var table = world.Characters;
        if (Own(world, owner, move.Squad) is not { } target || !table.TryResolveNext(move.Character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold
            || Own(world, owner, cold.SquadId) is not { } source) return;
        int from = source.Members.IndexOf(move.Character);
        if (from < 0) return;
        bool atMember = move.Index >= 0 && move.Index < target.Members.Count;
        if (!move.Swap && source != target && target.Members.Count >= maxSize) return;
        if (move.Swap && atMember)
        {
            var other = target.Members[move.Index];
            if (source == target) (source.Members[from], source.Members[move.Index]) = (other, move.Character);
            else
            {
                source.Members[from] = other;
                target.Members[move.Index] = move.Character;
                Join(table, other, source);
                Join(table, move.Character, target);
            }
        }
        else
        {
            source.Members.RemoveAt(from);
            int at = move.Index < 0 || move.Index > target.Members.Count ? target.Members.Count : move.Index;
            target.Members.Insert(at, move.Character);
            if (source != target) Join(table, move.Character, target);
        }
        Relead(table, source);
        if (source != target) Relead(table, target);
    }

    /// <summary>Makes a character a member of <paramref name="squad"/>: its squad, no formation place and no stop order left from the old one.</summary>
    static void Join(CharacterTable table, CharacterId id, Squad squad)
    {
        if (!table.TryResolveNext(id, out int slot)) return;
        var cold = table.Cold(slot)!;
        cold.SquadId = squad.Id;
        cold.FormationOffset = Vector2.Zero;
    }

    /// <summary>The first member leads (role 2), the one that led before goes back to role 0 (<c>FUN_140796620</c> / <c>FUN_140792e60</c>: index 0 is made the leader).</summary>
    static void Relead(CharacterTable table, Squad squad)
    {
        squad.Leader = squad.Members.Count > 0 ? squad.Members[0] : CharacterId.None;
        for (int i = 0; i < squad.Members.Count; i++)
        {
            if (!table.TryResolveNext(squad.Members[i], out int slot)) continue;
            var cold = table.Cold(slot)!;
            if (i == 0) cold.Role = (int)SquadRole.Leader;
            else if (cold.Role == (int)SquadRole.Leader) cold.Role = (int)SquadRole.Squad1;
        }
    }

    /// <summary>
    /// Dismiss (<c>FUN_14049b050</c>, the confirmation's <c>FUN_14048fa30</c>): refused for the faction's only character ("Cannot dismiss the only remaining character in the
    /// faction."). The character is deselected (by every player), joins Tech Hunters (else No Faction) and leaves in a new squad of its own of the "Wandering Squad"
    /// template, whose AI packages then run it.
    /// </summary>
    static void Dismiss(World world, PopulationData data, DismissCommand dismiss)
    {
        var table = world.Characters;
        var id = dismiss.Character;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) return;
        if (FactionHeads(world) <= 1) { world.Post(dismiss.Player, "Cannot dismiss the only remaining character in the faction."); return; }
        int faction = data.FactionIndex(DismissFaction);
        if (faction < 0) faction = data.FactionIndex(DismissFallbackFaction);
        if (faction < 0) return;
        if (world.Squads.Find(cold.SquadId) is { } old)
        {
            old.Members.Remove(id);
            if (IsPlayerSquad(world, old)) Relead(table, old);
            else if (old.Leader == id) old.Leader = old.Members.Count > 0 ? old.Members[0] : CharacterId.None;
        }
        foreach (var session in world.Player.Sessions) session.Selection.Remove(id);
        var at = table.Next[slot].Position;
        var template = data.Db.Find(DismissTemplate);
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? DismissTemplate,
            Name = template?.Name ?? "",
            Faction = faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
            SpawnSize = 1,
        };
        cold.Faction = faction;
        cold.SquadId = squad.Id;
        cold.Role = (int)SquadRole.Leader;
        cold.FormationOffset = Vector2.Zero;
        cold.IsPlayer = false;
        cold.Orders = CharacterOrders.None;
        cold.Ai = null;
        cold.Engaged = false;
        cold.Fleeing = false;
        cold.Pending = null;
        cold.Jobs.Clear();
        cold.Save = null;   // no longer in the player's platoon file
        table.Next[slot].Mode = SpeedMode.Walk;
        MovementSystem.Stop(table, slot);
        squad.Members.Add(id);
        squad.Leader = id;
        world.Squads.Add(squad);
        KeepActive(world);
    }
}
