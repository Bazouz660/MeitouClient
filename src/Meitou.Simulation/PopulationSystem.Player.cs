using Meitou.Simulation.Items;
using Meitou.Simulation.Bodies;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>
    /// Starts the player (a new game, before the first tick): their faction, their money and their squad at the start town, made from the
    /// start-off's <c>squad</c> list (a SQUAD_TEMPLATE is planned by the squad factory, a CHARACTER is one member) as one squad of the
    /// player's faction, standing idle, selected for them. Where: <see cref="NewGameStart.ForceStartPos"/> gives the place itself,
    /// otherwise the first listed town that is placed in the world (the new-game map's own choice of start town is not modelled), at a
    /// seeded spot within a third of its radius. The start research is applied (<see cref="Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch"/>); faction relation overrides and <c>force race</c> are not applied yet (the first
    /// is <b>Unknown</b> in the original, docs/game/factions-squads-towns.md 3.2). The characters' items are what the generator rolls
    /// for their look; their inventory is built like everybody's (<see cref="CharacterAssembly"/>).
    /// </summary>
    public Squad StartPlayer(World world, NewGameStart start)
    {
        if (data.PlayerFaction < 0) throw new InvalidOperationException("the game data has no player faction (Nameless)");
        var centre = start.StartPosition;
        float radius = 100;
        TownSite? town = null;
        if (!start.ForceStartPos)
        {
            foreach (var link in start.Towns)
                if (data.Sites.FirstOrDefault(s => s.Town.Id == link.Id) is { } site) { town = site; break; }
            if (town is not null)
            {
                centre = new Vector2(town.Position.X, town.Position.Z);
                radius = town.Town.SizeRadius * town.Town.TownRadiusMult;
            }
        }
        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        var at = centre;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ulong roll = Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt);
            var p = Rng.PointInDisc(roll, centre, radius, 3);
            if (world.Walkability.IsWalkable(p.X, p.Y)) { at = p; break; }
        }
        var built = BuildStart(world, start, key, at);
        var squad = SpawnSquad(world, built, null, radius, player: true);
        // The first player squad reads like its platoon, the faction's name and 0 ("Nameless_0", docs/game/ui-screens.md "Squad tabs").
        squad.Name = $"{(data.Factions[data.PlayerFaction].Name is { Length: > 0 } f ? f : "Nameless")}_0";
        world.Player.Faction = data.PlayerFaction;
        world.Player.Money = start.Money;
        world.Research.Set(data.Db, Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch(data.Db, start));
        var local = world.Player.Local;
        local.Squad = squad.Id;
        local.CreationRaces = [.. start.ForceRace.Select(r => r.Id)];
        local.InCreation = true;   // the character editor may remake them until it is confirmed (docs/characters.md "Character editor")
        local.Selection.Clear();
        if (squad.Members.Count > 0) local.Selection.Add(squad.Members[0]);
        return squad;
    }

    /// <summary>
    /// The members of a start's one squad at <paramref name="at"/>: everything the start-off's <c>squad</c> list holds (a SQUAD_TEMPLATE is planned by the squad factory,
    /// a CHARACTER is one member) in creation order, the first leading when none does, with looks rolled from <paramref name="key"/>; of the player's faction.
    /// </summary>
    BuiltSquad BuildStart(World world, NewGameStart start, ulong key, Vector2 at)
    {
        var members = new List<BuiltMember>();
        var first = new List<MemberPlan>();
        SquadTemplate? template = null;
        foreach (var link in start.Squad)
        {
            var record = data.Db.Find(link.Id);
            if (record is null) continue;
            if (SquadFactory.IsTemplate(record))
            {
                var t = SquadTemplate.From(record);
                template ??= t;
                first.AddRange(SquadFactory.Plan(data.Db, t, settings.SquadSizeMultiplier, world.Seed, Rng.Mix(key ^ (uint)first.Count), States).Members);
            }
            else
            {
                first.Add(new MemberPlan(link.Id, SquadRole.Squad1, first.Count, default, false, 1));
            }
        }
        // Renumber in creation order (the layout counts every member) and make the first the leader when none is.
        bool hasLeader = first.Any(m => m.Role == SquadRole.Leader);
        for (int i = 0; i < first.Count; i++)
            first[i] = first[i] with { Index = i, Offset = SquadFactory.Offset(i), Role = !hasLeader && i == 0 ? SquadRole.Leader : first[i].Role };
        var factionId = data.Factions[data.PlayerFaction].Id;
        foreach (var m in first)
        {
            CharacterAppearance? look = null;
            if (data.Appearances is { } source)   // an animal gets its species' shared look (GeneratedAppearances)
                look = source.Create(m.RecordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerLook + (ulong)m.Index) & 0x7FFFFFFF));
            members.Add(new BuiltMember(m, look));
        }
        var plan = new SquadPlan(template ?? new SquadTemplate { Id = start.Id, Name = start.Name }, first, [], []);
        return new BuiltSquad(plan, at, data.PlayerFaction, members);
    }

    /// <summary>How far from the join point a joiner's start squad may stand (world units; engine choice).</summary>
    public const float JoinRadius = 150;

    /// <summary>
    /// Adds player <paramref name="player"/>'s start squad near <paramref name="near"/> (docs/simulation.md "Multiplayer (host-authoritative co-op)", Joining): the start's
    /// squad as <see cref="StartPlayer"/> builds it, owned by that player, at the first walkable seeded spot within <see cref="JoinRadius"/> (<see cref="NewGameStart.ForceStartPos"/>
    /// is ignored), named like a new squad and last in their order; their session gets it as the active squad with its first member selected and the character creation open.
    /// Faction, Money and Research are left alone: a joiner adds no money and the faction head cap is not checked (engine choices). A rejoin, when the player's squads still
    /// have a living member, makes nothing: their session keeps a squad of theirs active and selects its first living member when nothing is selected. Returns the squad.
    /// Before the first tick or in the serial Inputs phase (<see cref="JoinPlayer"/>); not for player 0 or before a game exists.
    /// </summary>
    public Squad AddPlayer(World world, int player, NewGameStart start, Vector3 near)
    {
        if (!world.Player.Exists) throw new InvalidOperationException("no game is running: start the player first");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(player);
        var session = world.Player.Session(player);
        var table = world.Characters;
        bool Living(CharacterId id) => table.TryResolveNext(id, out int slot) && table.Cold(slot) is { DiedTick: < 0 };
        var own = SquadManagement.PlayerSquads(world, player);
        if (own.Any(s => s.Members.Any(Living)))
        {
            SquadManagement.KeepActive(world);
            var active = own.Find(s => s.Id == session.Squad && s.Members.Any(Living)) ?? own.First(s => s.Members.Any(Living));
            session.Squad = active.Id;
            if (session.Selection.Count == 0) session.Selection.Add(active.Members.First(Living));
            return active;
        }

        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id) ^ Rng.Mix((ulong)player << 32 ^ (ulong)world.Tick));
        var centre = new Vector2(near.X, near.Z);
        var at = centre;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ulong roll = Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt);
            var p = Rng.PointInDisc(roll, centre, JoinRadius, 3);
            if (world.Walkability.IsWalkable(p.X, p.Y)) { at = p; break; }
        }
        // NewSquad's naming and order: the faction's name and a number unique among every player's squads, last in this player's order.
        var faction = world.Player.Faction < data.Factions.Count && data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless";
        var all = SquadManagement.PlayerSquads(world);
        int n = all.Count;
        while (all.Any(s => s.Name == $"{faction}_{n}")) n++;
        // Of the running player faction (a loaded save's may not be the data's "Nameless"), so it is one of the player squads.
        var squad = SpawnSquad(world, BuildStart(world, start, key, at) with { Faction = world.Player.Faction }, null, JoinRadius, player: true, owner: player);
        squad.Name = $"{faction}_{n}";
        squad.Index = own.Count == 0 ? 0 : own[^1].Index + 1;
        session.Squad = squad.Id;
        session.Selection.Clear();
        if (squad.Members.Count > 0) session.Selection.Add(squad.Members[0]);
        session.InCreation = true;
        session.CreationRaces = [.. start.ForceRace.Select(r => r.Id)];
        session.Focus = near;
        return squad;
    }

    /// <summary>
    /// The <see cref="JoinPlayer"/> commands due (the inputs, and the paused interface commands): each joiner's start squad or rejoin (<see cref="AddPlayer"/>). The start is
    /// the NEW_GAME_STARTOFF named by <see cref="JoinPlayer.StartId"/>, else the one named <see cref="NewGameStart.DefaultName"/>, else the first. A join before a game exists,
    /// for player 0 or without any start is refused with a message to the issuer.
    /// </summary>
    internal void ApplyJoins(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is not JoinPlayer join) continue;
            if (!world.Player.Exists || join.Player <= 0) { world.Post(Math.Max(join.Player, 0), "Cannot join: there is no game to join, or the player is the host."); continue; }
            var record = join.StartId is { } id && data.Db.Find(id) is { Type: FcsRecordType.NEW_GAME_STARTOFF } named ? named : null;
            record ??= data.Db.OfType(FcsRecordType.NEW_GAME_STARTOFF).FirstOrDefault(r => r.Name == NewGameStart.DefaultName)
                ?? data.Db.OfType(FcsRecordType.NEW_GAME_STARTOFF).FirstOrDefault();
            if (record is null) { world.Post(join.Player, "Cannot join: the game data has no start."); continue; }
            AddPlayer(world, join.Player, NewGameStart.From(record), join.Near);
        }
    }
}
