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

        // One squad of everything the start lists.
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
        var built = new BuiltSquad(plan, at, data.PlayerFaction, members);
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
    /// The <see cref="JoinPlayer"/> commands due (the inputs, and the paused interface commands): each joiner's start squad or rejoin (docs/simulation.md "Multiplayer
    /// (host-authoritative co-op)", Joining). Not handled yet: a join is a no-op.
    /// </summary>
    internal void ApplyJoins(World world, IReadOnlyList<SimCommand> commands)
    {
    }
}
