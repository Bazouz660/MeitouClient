using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// DA_JOIN_SQUAD_WITH_EDIT (3) / DA_JOIN_SQUAD_FAST (18) with the player character as the target (docs/game/dialogue.md "Recruiting"): the speaker leaves its squad and joins the
    /// talking player's active squad (the <see cref="Conversation.Owner"/>'s <see cref="PlayerSession.Squad"/>, else its selection's, else the talker's) as a player character; when that
    /// squad already holds CONSTANTS <c>max squad size</c> members, a new squad of the "blank squad" template, owned by that player, takes it. At the faction's head cap
    /// (<c>max faction size</c>) the conversation ends with "It looks like your squad is a little full." (to that player) and the line's remaining effects are skipped (false).
    /// DA 3 then opens the character editor on the recruit (engine: the conversation ends for it), and grants that player the recruit edit.
    /// </summary>
    bool Recruit(World world, Conversation c, CharacterId me, bool withEditor)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int slot) || table.Cold(slot) is not { IsPlayer: false } || Cold(world, c.Player) is not { IsPlayer: true } tc) return true;
        if (PlayerHeads(world) >= data.Bodies.Constants.MaxFactionSize)
        {
            world.Post(c.Owner, "It looks like your squad is a little full.");
            c.Ended = true;
            return false;
        }
        if (!JoinPlayer(world, slot, tc, c.Owner)) return true;
        if (withEditor)
        {
            c.Ended = true;
            TalkOf(c.Owner).Request = new DialogueRequest(++requestId, DialogueRequestKind.RecruitEditor, me, me);
            world.Player.Session(c.Owner).Grant = new EditGrant(Editing.EditKind.Recruit, me, CharacterId.None);
        }
        return true;
    }

    static int PlayerHeads(World world)
    {
        var table = world.Characters;
        int heads = 0;
        for (int i = 0; i < table.Next.Length; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 }) heads++;
        return heads;
    }

    /// <summary>
    /// The character at <paramref name="slot"/> leaves its squad and joins player <paramref name="owner"/>'s active squad (else its selection's, else the talker's; or a new blank
    /// squad of that player's when it is full) as a player character.
    /// </summary>
    bool JoinPlayer(World world, int slot, CharacterCold talker, int owner)
    {
        var table = world.Characters;
        var me = table.IdOf(slot);
        var mc = table.Cold(slot)!;
        var limits = data.Bodies.Constants;
        var session = world.Player.Session(owner);
        var selected = session.Selection.Select(id => Cold(world, id)).FirstOrDefault(s => s is { IsPlayer: true });
        if ((world.Squads.Find(session.Squad) ?? world.Squads.Find(selected?.SquadId ?? -1) ?? world.Squads.Find(talker.SquadId)) is not { } squad) return false;
        if (squad.Members.Count >= limits.MaxSquadSize)
            squad = SquadManagement.NewSquad(world, data.Db, table.Next[slot].Position, data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless", owner);

        Leave(world, me, mc);
        mc.Faction = world.Player.Faction;
        mc.SquadId = squad.Id;
        mc.Role = (int)SquadRole.Squad1;
        mc.FormationOffset = Vector2.Zero;
        mc.IsPlayer = true;
        mc.NpcClass = FundamentalType.Adventurer;   // every player-faction character's class
        mc.Orders = Orders.PlayerDefaults;
        mc.Ai = null;
        mc.Engaged = false;
        mc.Fleeing = false;
        mc.Pending = null;
        mc.Jobs.Clear();   // an NPC's list, if any, does not carry over
        mc.Save = null;   // written as a new member of the player's platoon; the save's old slot of it is removed
        table.Next[slot].Mode = SpeedMode.Free;   // the original's speed mode 2
        MovementSystem.Stop(table, slot);
        squad.Members.Add(me);
        if (squad.Leader.IsNone) squad.Leader = me;
        return true;
    }

    static void Leave(World world, CharacterId me, CharacterCold mc)
    {
        if (world.Squads.Find(mc.SquadId) is not { } old) return;
        old.Members.Remove(me);
        if (old.Leader == me) old.Leader = old.Members.Count > 0 ? old.Members[0] : CharacterId.None;
    }

    /// <summary>
    /// <c>DA_GIVE_TARGET_MY_SLAVES</c> (24, <c>FUN_140680560</c>): the slaves on the speaker's platoon's list are let out of their cages and join the target's squad. Engine reading of
    /// the list: the members of the speaker's squad in slave state (Meitou makes no slave squads yet, so it is often empty). They join the player (the conversation's owner, <see cref="Conversation.Owner"/>) as recruits do, keeping their
    /// slave state, now owned by the target's faction; a non-player target's squad takes them into its own.
    /// </summary>
    void GiveSlaves(World world, CharacterId me, CharacterCold mc, CharacterCold tc, int owner)
    {
        var table = world.Characters;
        if (mc.SquadId < 0 || world.Squads.Find(mc.SquadId) is not { } squad) return;
        foreach (var id in squad.Members.ToArray())
        {
            if (id == me || !table.TryResolveNext(id, out int s) || table.Cold(s) is not { SlaveState: not 0, Medical.Dead: false } slave) continue;
            byte state = slave.SlaveState;
            if (slave.IsImprisoned) world.Fixtures.Release(world, id);   // it clears the slave state; restored below
            if (tc.IsPlayer || tc.Faction == world.Player.Faction)
            {
                if (PlayerHeads(world) >= data.Bodies.Constants.MaxFactionSize || !JoinPlayer(world, s, tc, owner)) break;
            }
            else if (world.Squads.Find(tc.SquadId) is { } theirs)
            {
                Leave(world, id, slave);
                slave.SquadId = theirs.Id;
                theirs.Members.Add(id);
            }
            (slave.SlaveState, slave.SlaveOwner) = (state, tc.Faction);
        }
    }
}
