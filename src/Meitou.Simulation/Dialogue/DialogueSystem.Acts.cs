using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    // DA_FOLLOW_WHILE_TALKING: who follows whom while its conversation lasts (hashed).
    readonly SortedDictionary<CharacterId, CharacterId> followers = new(Comparer<CharacterId>.Create(CompareIds));
    // The tick the last "Alarm raised" was posted (DA_CRIME_ALARM; at most one per 30 s, FUN_1409335e0); hashed.
    long alarmPosted = long.MinValue;

    /// <summary>The original's <c>Task_Runaway</c> first run (task 280, DA 34): 2000 + 400 x random units away from the threat (as <see cref="FleeSystem.FirstRun"/>).</summary>
    const float RunAwayDistance = FleeSystem.FirstRun, RunAwaySpread = FleeSystem.RunSpread;

    /// <summary>
    /// The short-term tag a character's squad sets on another (<c>FUN_1406744a0</c>): TEMP_ENEMY is not set on a character of an allied faction or one the speaker regards as friendly.
    /// </summary>
    void FlagFrom(World world, CharacterCold mc, CharacterId me, CharacterId who, CharacterCold wc, int tag)
    {
        if (tag == MemoryTag.TempEnemy && (data.Relations.Contains(mc.Faction) && data.Relations.Contains(wc.Faction) && data.Relations.IsAlly(mc.Faction, wc.Faction)
            || RegardsFriendly(world, me, mc, who, wc))) return;
        Memory.Flag(world, mc.SquadId, who, tag);
    }

    /// <summary>
    /// "A regards B as friendly" (Character slot 126, <c>FUN_1407923d0</c>): the same faction; never when A's squad holds TEMP_ENEMY on B (or, for a player-faction A, B's squad on A);
    /// always when B's squad holds TEMP_ALLY on a player-faction A, A's squad TEMP_ALLY on B, or A loves B (DC 52); else A's faction is allied to B's. Engine: the uniform disguise
    /// the original can substitute for B's faction is not read.
    /// </summary>
    bool RegardsFriendly(World world, CharacterId a, CharacterCold ac, CharacterId b, CharacterCold bc)
    {
        if (ac.Faction == bc.Faction) return true;
        if (Memory.HasShort(world, ac.SquadId, b, MemoryTag.TempEnemy)) return false;
        if (ac.IsPlayer || ac.Faction == world.Player.Faction)
        {
            if (Memory.HasShort(world, bc.SquadId, a, MemoryTag.TempEnemy)) return false;
            if (Memory.HasShort(world, bc.SquadId, a, MemoryTag.TempAlly)) return true;
        }
        if (Memory.HasShort(world, ac.SquadId, b, MemoryTag.TempAlly) || Loves(world, ac, b)) return true;
        return data.Relations.Contains(ac.Faction) && data.Relations.Contains(bc.Faction) && data.Relations.IsAlly(ac.Faction, bc.Faction);
    }

    /// <summary>DC 52 I_LOVE_THIS_GUY (<c>FUN_140599590</c>): the squad's long-term tags on the character hold LIFESAVER or FREED_ME.</summary>
    bool Loves(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.LoveMask) is > 0;

    /// <summary>DC 53 I_HATE_THIS_GUY (<c>FUN_1405995d0</c>): STOLE_FROM_ME, MY_CAPTOR, DEFEATED_MY_SQUAD_ONCE or KILLED_MY_FRIEND.</summary>
    bool Hates(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.HateMask) is > 0;

    /// <summary>
    /// DC 54 I_SHOULD_SCREW_THIS_GUY_OVER (<c>FUN_14059a7b0</c>), each rule overriding the ones before: either is a bandit; not when both have the same NPC class; yes for a traitorous
    /// speaker; not when it regards S as friendly; not for an honourable speaker; yes when S's faction owns the speaker as a slave; not when it loves S; yes when it hates S; yes when S is dead.
    /// </summary>
    bool ScrewOver(World world, CharacterId me, CharacterCold mc, CharacterId s, CharacterCold sc)
    {
        bool r = mc.NpcClass == FundamentalType.Bandit || sc.NpcClass == FundamentalType.Bandit;
        if (mc.NpcClass == sc.NpcClass) r = false;
        int p = Personality.Of(world, data.Db, me, mc);
        if (p == Personality.Traitorous) r = true;
        if (RegardsFriendly(world, me, mc, s, sc)) r = false;
        if (p == Personality.Honorable) r = false;
        if (mc.SlaveOwner >= 0 && mc.SlaveOwner == sc.Faction) r = true;
        if (Loves(world, mc, s)) r = false;
        if (Hates(world, mc, s)) r = true;
        if (sc.Medical?.Dead == true) r = true;
        return r;
    }

    /// <summary>
    /// DC 55 I_SHOULD_HELP_THIS_GUY (<c>FUN_14059a670</c>), each rule overriding the ones before: the same NPC class; not for a traitorous or bandit speaker; yes when it regards S as
    /// friendly; yes when both are honourable; yes when it loves S; not when it hates S.
    /// </summary>
    bool Helps(World world, CharacterId me, CharacterCold mc, CharacterId s, CharacterCold sc)
    {
        bool r = mc.NpcClass == sc.NpcClass;
        int p = Personality.Of(world, data.Db, me, mc);
        if (p == Personality.Traitorous || mc.NpcClass == FundamentalType.Bandit) r = false;
        if (RegardsFriendly(world, me, mc, s, sc)) r = true;
        if (p == Personality.Honorable && Personality.Of(world, data.Db, s, sc) == Personality.Honorable) r = true;
        if (Loves(world, mc, s)) r = true;
        if (Hates(world, mc, s)) r = false;
        return r;
    }

    /// <summary>
    /// DA_RUN_AWAY (34): the speaker drops what it does and runs from the target (task 280, <c>Task_Runaway</c>'s first run; engine: one run at full speed, then its AI takes over), and its
    /// squad tags the target AGGRESSOR.
    /// </summary>
    void RunAway(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
        var table = world.Characters;
        if (mc.IsPlayer || !table.TryResolveNext(me, out int s) || Position(world, target) is not { } from) return;
        var here = table.Next[s].Position;
        var away = new Vector2(here.X - from.X, here.Z - from.Z);
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX;
        float r = Rng.Float(NextRoll(world, me));
        MovementSystem.GoTo(table, s, new Vector2(from.X, from.Z) + away * (RunAwayDistance + RunAwaySpread * r), SpeedMode.Free);
    }

    /// <summary>
    /// DA_GO_HOME (7): the speaker walks home (task 19; Meitou: its squad's home building, else its town, else its home centre, as the AI's going home) and its squad tags the target
    /// AGGRESSOR.
    /// </summary>
    void GoHome(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold? tc)
    {
        if (tc is not null) FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
        var table = world.Characters;
        if (mc.IsPlayer || !table.TryResolveNext(me, out int s) || world.Squads.Find(mc.SquadId) is not { } squad) return;
        Vector2 home = squad.Ai?.Home is { } h ? new(h.Position.X, h.Position.Z)
            : squad.Ai is { Town: >= 0 } board && board.Town < data.Sites.Count ? new(data.Sites[board.Town].Position.X, data.Sites[board.Town].Position.Z)
            : squad.HomeCentre;
        MovementSystem.GoTo(table, s, home, SpeedMode.Walk);
    }

    /// <summary>DA_SUMMON_MY_SQUAD (52): the speaker's squadmates run to it (task 67, "Running").</summary>
    void Summon(World world, CharacterId me)
    {
        var table = world.Characters;
        if (Position(world, me) is not { } at) return;
        foreach (var m in SquadOf(world, me))
            if (m != me && table.TryResolveNext(m, out int s) && Awake(table, s) && table.Cold(s) is { IsPlayer: false, InCombat: false })
                MovementSystem.GoTo(table, s, new Vector2(at.X, at.Z), SpeedMode.Free);
    }

    /// <summary>
    /// DA_SEPARATE_TO_MY_OWN_SQUAD (28, <c>FUN_1405ce4e0</c>): the speaker (the target when the speaker is of the player's faction) leaves for a new squad of its faction made where it
    /// stands, which takes a copy of the old squad's memory; nothing when it is alone already. Engine: the new NPC squad keeps the old one's template and home radius, no town, and the
    /// AI makes it a fresh mind.
    /// </summary>
    void Separate(World world, CharacterId who)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int s) || table.Cold(s) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { } old || old.Members.Count <= 1) return;
        var at = table.Next[s].Position;
        Squad squad;
        if (c.IsPlayer) squad = SquadManagement.NewSquad(world, data.Db, at, data.Relations.Contains(c.Faction) && data.Factions[c.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        else
        {
            squad = new Squad { Id = world.Squads.NextId(), TemplateId = old.TemplateId, Faction = old.Faction, Position = at, HomeCentre = new(at.X, at.Z), HomeRadius = old.HomeRadius };
            world.Squads.Add(squad);
            c.Ai = null;
            c.Role = (int)SquadRole.Leader;
        }
        Memory.Copy(old.Id, squad.Id);
        Leave(world, who, c);
        c.SquadId = squad.Id;
        squad.Members.Add(who);
        if (squad.Leader.IsNone) squad.Leader = who;
        if (!c.IsPlayer) MovementSystem.Stop(table, s);
    }

    /// <summary>
    /// DA_CRIME_ALARM (33, docs/game/dialogue.md "Effects"): the target's pending crime timer becomes 30 s and it is marked recently wanted; in a town whose faction is not hostile to the
    /// speaker, a target without a pending crime standing inside the speaker's squad's private home is caught trespassing, and a pending crime raises the alarm ("Alarm raised", at most
    /// every 30 s). Engine: the alarm has no responders yet, and the speaker's perception mark (<c>FUN_140858f40</c>) is not modelled.
    /// </summary>
    void CrimeAlarm(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (tc.Medical?.Dead == true) return;
        if (tc.Pending is { } p) p.Timer = 30;
        tc.RecentlyWanted = Crime.CrimeSystem.RecentlyWantedSeconds;
        if (Position(world, me) is not { } here || TownAt(here) is not (>= 0 and var town) || town >= data.SiteFactions.Count) return;
        int townFaction = data.SiteFactions[town];
        if (data.Relations.Contains(townFaction) && data.Relations.Contains(mc.Faction) && data.Relations.IsHostile(mc.Faction, townFaction)) return;
        if (tc.Pending is null && world.Squads.Find(mc.SquadId) is { } squad && (squad.Ai?.OwnHome ?? squad.HomeBuilding) is { } home && !(squad.Ai?.HomePublic ?? true)
            && world.Characters.TryResolveNext(target, out int t) && world.Sight.BuildingOf(t) == home.Key)
            Arrests?.Crimes.SetPending(world, t, Crime.CrimeCode.Trespassing, mc.Faction, me, 30);
        if (tc.Pending is null) return;
        if (alarmPosted != long.MinValue && (world.Tick - alarmPosted) * world.TickSeconds <= 30) return;
        alarmPosted = world.Tick;
        world.Post(Loc.Tr("Alarm raised"));
    }

    /// <summary>
    /// DA_MASSIVE_ALARM (56): when the speaker is in a town, every member of its faction's squads tags the target TEMP_ENEMY, fires event 66 at it and goes for it (task 29 to its
    /// position; Meitou: an attack, as its aggro does not read the tag). Engine: only loaded squads.
    /// </summary>
    void MassiveAlarm(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (Position(world, target) is null || Position(world, me) is not { } here || TownAt(here) < 0) return;
        var table = world.Characters;
        var fighters = new List<CharacterId>();
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction != mc.Faction) continue;
            foreach (var m in squad.Members)
            {
                if (!table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { IsPlayer: false } c) continue;
                FlagFrom(world, c, m, target, tc, MemoryTag.TempEnemy);
                world.Defer(new DialogueEvent(m, target, 66) { Tick = world.Tick + 1 });
                if (c.Fighter?.CanAttack == true) fighters.Add(m);
            }
        }
        if (fighters.Count > 0) world.Defer(new Combat.AttackOrder(fighters, target) { Tick = world.Tick + 1 });
    }

    /// <summary>DA_OPEN_NEAREST_GATE (54): the wall gate of the speaker's town nearest it starts opening (<c>FUN_140297040</c>, which tests no lock).</summary>
    void OpenNearestGate(World world, CharacterId me)
    {
        if (Position(world, me) is not { } at || TownAt(at) is not (>= 0 and var town)) return;
        Meitou.Data.World.TownBuilding? gate = null;
        float best = float.MaxValue;
        foreach (var b in data.Sites[town].Buildings)
        {
            float d = Vector2.DistanceSquared(new(b.Position.X, b.Position.Z), new(at.X, at.Z));
            if (b.IsGate && d < best) (gate, best) = (b, d);
        }
        if (gate is null) return;
        foreach (var door in world.Fixtures.OfBuilding(gate.Key)) Locks.FixtureRegistry.OpenDoor(door);
    }

    /// <summary>DA_FOLLOW_WHILE_TALKING (16, task 164): the speaker walks after the target while its conversation lasts (engine: re-aimed every 15 ticks when more than 40 units off).</summary>
    void StepFollowers(World world)
    {
        if (followers.Count == 0) return;
        var table = world.Characters;
        foreach (var (who, whom) in followers.ToArray())
        {
            bool talking = bubbles.Any(b => b.C.Npc == who) || open?.Npc == who;
            if (!talking || !table.TryResolveNext(who, out int s) || !Awake(table, s) || Position(world, whom) is not { } at) { followers.Remove(who); continue; }
            if (world.Tick % 15 != 0 || Vector3.DistanceSquared(table.Next[s].Position, at) <= 40 * 40) continue;
            MovementSystem.GoTo(table, s, new Vector2(at.X, at.Z), SpeedMode.Walk);
        }
    }

    void HashActs(ref StateHasher hasher)
    {
        foreach (var (who, whom) in followers) { hasher.Add(who); hasher.Add(whom); }
        if (alarmPosted != long.MinValue) hasher.Add(alarmPosted);
    }
}
