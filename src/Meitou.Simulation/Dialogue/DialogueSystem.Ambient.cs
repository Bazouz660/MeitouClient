using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>A line on show over a character's head (docs/game/dialogue.md "Ambient conversations"): <see cref="Large"/> for the events that use the large font.</summary>
public sealed record SpeechBubble(CharacterId Speaker, string Text, bool Large);

public sealed partial class DialogueSystem
{
    /// <summary>A speech-bubble conversation under way (docs/game/dialogue.md "Ambient conversations"): the line said next, by whom, and when.</summary>
    sealed record Bubble(Conversation C, DialogueNode Line, long DueTick, CharacterId Speaker);

    readonly List<Bubble> bubbles = [];
    // The lines on show: the text and the tick it goes (display only: follows from the bubbles, so not hashed).
    readonly SortedDictionary<CharacterId, (string Text, long Until, bool Large)> shown = new(Comparer<CharacterId>.Create(CompareIds));
    // When a squad last reacted to seeing a character (the cooldown of SeeOthers).
    readonly SortedDictionary<(int Squad, CharacterId Target), double> ambientAt = new(Comparer<(int, CharacterId)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));

    /// <summary>The event of a crowd trigger's lines (EV_CROWD_TRIGGERED), and of a contract's end talk (EV_CONTRACT_JOB_ENDED).</summary>
    const int CrowdTriggered = 74;
    public const int ContractJobEnded = 30;
    /// <summary>The AI-to-AI talking range (DC 16 TARGET_IN_TALKING_RANGE, 260 units): how near another squad's NPC must be to be greeted (engine choice).</summary>
    const float TalkingRange = 260;

    /// <summary>The events whose bubbles use the large font (<c>FUN_140725f30</c>, <b>Observed</b>); the others use the medium one.</summary>
    static bool IsLarge(int ev) => ev is 2 or 11 or 17 or 26 or 31 or 34 or 36 or 40 or 45 or 74;

    public IReadOnlyList<SpeechBubble> PublishSpeech() => [.. shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large))];

    /// <summary>
    /// A campaign's <c>dialog announcement</c> (docs/game/factions-squads-towns.md "Campaigns", announcement): <paramref name="dialogueId"/> fired as event 2 by
    /// <paramref name="npc"/> with no target. Its lines are said one after another as speech bubbles. Their effects run as said (an <c>interrupt</c> makes the next talk
    /// with the speaker that dialogue). Returns whether it started.
    /// </summary>
    public bool Announce(World world, CharacterId npc, string dialogueId) =>
        dialogue.Node(dialogueId) is { IsRoot: true } root && StartBubbles(world, npc, CharacterId.None, root, DialogueEvents.Announcement, now: false);

    /// <summary>
    /// Starts <paramref name="root"/> as a bubble conversation of <paramref name="npc"/> with <paramref name="whom"/> (<c>FUN_140683f90</c>): the best of its lines (speakers resolved as in
    /// any choice) is said, the chain after it. <paramref name="now"/>: said this tick, else on the next bubble step. Returns whether a line was found.
    /// </summary>
    bool StartBubbles(World world, CharacterId npc, CharacterId whom, DialogueNode root, int ev, bool now = true)
    {
        if (!world.Characters.TryResolveNext(npc, out int n) || !Awake(world.Characters, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev, Root = root };
        if (Pick(world, c, root.Children) is not { } first || Busy(first.Speaker, c)) return false;
        bubbles.Add(new Bubble(c, first.Line, world.Tick, first.Speaker));
        if (now) StepBubbles(world);
        return true;
    }

    /// <summary>
    /// <paramref name="ev"/> fired at <paramref name="npc"/> with <paramref name="whom"/> as its target outside a player conversation (<c>FUN_140684990</c>): a line is chosen from the
    /// NPC's list for the event and said as a speech bubble, the rest of its chain after it. Returns whether a line was said.
    /// </summary>
    bool Fire(World world, CharacterId npc, CharacterId whom, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || !Awake(table, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev };
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom || Busy(picked.Speaker, c)) return false;
        c.Root = picked.Root;
        bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        return true;
    }

    bool BubbleOf(CharacterId who) => shown.ContainsKey(who) || bubbles.Any(b => b.Speaker == who || b.C.Npc == who);

    /// <summary>Whether <paramref name="who"/> is saying a line of another conversation, or is in the open player conversation.</summary>
    bool Busy(CharacterId who, Conversation c) =>
        open is { } o && (o.Npc == who || o.Player == who) || bubbles.Any(b => b.C != c && (b.Speaker == who || b.C.Npc == who));

    /// <summary>
    /// Whom a loaded NPC reacts to by sight: the engine's stand-in for the perception that fires events 3, 66 and 67 (<c>FUN_140859680</c>). A player character within
    /// <see cref="DialogueSettings.AmbientRange"/> first (66 hostile, 67 ally, else 3); else the nearest awake NPC of another squad within <see cref="TalkingRange"/> (event 3, as the
    /// original fires it for a seen character outside the player's faction), only near a player character, where it can be heard.
    /// </summary>
    void SeeOthers(World world)
    {
        var table = world.Characters;
        var players = new List<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, Medical.Dead: false }) players.Add(i);
        if (players.Count == 0 || !data.Relations.Contains(world.Player.Faction)) return;
        float range2 = Settings.AmbientRange * Settings.AmbientRange;
        // The NPCs that can look or be greeted: loaded, awake, free, near a player character.
        var near = new List<int>();
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task == (byte)CharacterTask.Rest || table.Cold(i) is not { IsPlayer: false, SquadId: >= 0, Engaged: false, InCombat: false, Fleeing: false, IsImprisoned: false } nc) continue;
            if (nc.Fighter?.IsAnimal == true || nc.Appearance?.Animal is not null || !data.Relations.Contains(nc.Faction) || !Awake(table, i)) continue;
            foreach (int p in players)
                if (Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position) <= range2) { near.Add(i); break; }
        }
        int said = 0;
        foreach (int i in near)
        {
            if (said >= Settings.AmbientPerScan) break;
            var nc = table.Cold(i)!;
            int best = -1;
            float best2 = range2;
            foreach (int p in players)
            {
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position);
                if (d2 <= best2) (best, best2) = (p, d2);
            }
            int ev = -1;
            if (best >= 0 && !Recent(world, nc.SquadId, table.IdOf(best)))
                ev = data.Relations.IsHostile(nc.Faction, world.Player.Faction) ? 66 : data.Relations.IsAlly(nc.Faction, world.Player.Faction) ? 67 : 3;
            else
            {
                (best, best2) = (-1, TalkingRange * TalkingRange);
                foreach (int o in near)
                {
                    if (table.Cold(o)!.SquadId == nc.SquadId) continue;
                    float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[o].Position);
                    if (d2 <= best2 && !Recent(world, nc.SquadId, table.IdOf(o))) (best, best2) = (o, d2);
                }
                if (best >= 0) ev = 3;
            }
            if (ev < 0) continue;
            var target = table.IdOf(best);
            // Asked again only after the cooldown whether or not it has a line that passes (else every scan would roll again, and its squad would never turn to the NPCs).
            ambientAt[(nc.SquadId, target)] = world.GameHours;
            if (SetOf(world, i).For(ev).Count > 0 && Fire(world, table.IdOf(i), target, ev)) said++;
        }
    }

    // The characters that were down at the last look (the get-up events); hashed.
    readonly SortedSet<CharacterId> down = new(Comparer<CharacterId>.Create(CompareIds));

    /// <summary>EV_GET_UP_PEACE, EV_GET_UP_FIGHT.</summary>
    const int GetUpPeace = 23, GetUpFight = 24;

    /// <summary>
    /// Getting up (<c>Task_GetUp</c>, <c>FUN_14033ca10</c>): a character that was down at the last look and is awake now fires event 24 with no target when enemies are about,
    /// else 23. The original counts enemies on the character (its counters are not decoded): engine reading, fighting or a hostile awake character within sight. Its
    /// event 25 branch (an unnecessary fight) is not built.
    /// </summary>
    void SeeGettingUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical: { } medical } c) continue;
            var id = table.IdOf(i);
            if (medical.Dead) { down.Remove(id); continue; }
            if (medical.Incapacitated) { down.Add(id); continue; }
            if (!down.Remove(id)) continue;
            Fire(world, id, CharacterId.None, c.InCombat || EnemyNear(world, i, c) ? GetUpFight : GetUpPeace);
        }
        down.RemoveWhere(id => !table.TryResolveNext(id, out _));
    }

    bool EnemyNear(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        if (!data.Relations.Contains(c.Faction)) return false;
        float r = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (i == slot || !table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } o || !data.Relations.Contains(o.Faction)) continue;
            if (data.Relations.IsHostile(c.Faction, o.Faction) && Vector3.DistanceSquared(table.Next[i].Position, table.Next[slot].Position) <= r * r) return true;
        }
        return false;
    }

    bool Recent(World world, int squad, CharacterId target) => ambientAt.TryGetValue((squad, target), out double at) && world.GameHours - at < Settings.AmbientRepeatHours;

    /// <summary>Whether talking to <paramref name="npc"/> opens a pending <c>interrupt</c> dialogue (a campaign's negotiation).</summary>
    public bool HasInterrupt(CharacterId npc) => interrupts.ContainsKey(npc);

    /// <summary>
    /// The line said after <paramref name="line"/> (<c>FUN_140684720</c>): the best of its children with their speakers resolved (the owner, its target, an interjector), as in a choice;
    /// none when the root is not a monologue (a non-monologue root in bubble mode ends after its first line, <c>FUN_140685090</c>) or the speaker is busy elsewhere.
    /// </summary>
    Picked? NextBubble(World world, Conversation c, DialogueNode line)
    {
        if (line.Children.Count == 0 || c.Root is { Monologue: false }) return null;
        return Pick(world, c, line.Children) is { } next && !Busy(next.Speaker, c) ? next : null;
    }

    /// <summary>Says the bubbles that are due (serial): the text, its effects, then the next line after 2 + 0.12 × the text's length seconds, or DA 12's own wait when longer.</summary>
    void StepBubbles(World world)
    {
        for (int k = 0; k < bubbles.Count; k++)
        {
            var (c, line, due, speaker) = bubbles[k];
            if (world.Tick < due) continue;
            if (!world.Characters.TryResolveNext(speaker, out int n) || !Awake(world.Characters, n))
            {
                bubbles.RemoveAt(k--);
                continue;
            }
            MarkUsed(world, c, line);
            string text = Expand(world, c, line, PickText(world, speaker, line), speaker, speaker == c.Player ? c.Npc : c.Player);
            RunEffects(world, c, line, c.Npc);
            float seconds = 2 + 0.12f * text.Length;
            if (text.Length > 0) shown[speaker] = (text, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), IsLarge(c.Event));
            foreach (var e in line.Effects) if (e.Action == DialogueActions.BubbleWait) seconds = MathF.Max(seconds, e.Value);
            if (c.Ended || NextBubble(world, c, line) is not { } next)
            {
                EndBubbles(world, c);
                bubbles.RemoveAt(k--);
            }
            else bubbles[k] = new Bubble(c, next.Line, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), next.Speaker);
        }
    }

    /// <summary>
    /// A line's <c>crowd trigger</c> (<c>FUN_140680560</c>, unless the line is itself of event 74): the awake members of the conversation owner's squad other than itself and the
    /// speaker, not animals, start that DIALOGUE as event 74 at the target. Engine readings: the owner is left out (it is talking), and each member joins with the trigger's val0
    /// percent (the data's values are 100 but for two lines).
    /// </summary>
    void CrowdTrigger(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        if (c.Event == CrowdTriggered || line.CrowdTrigger is not { } id || dialogue.Node(id) is not { IsRoot: true } root) return;
        var table = world.Characters;
        foreach (var m in SquadOf(world, c.Npc))
        {
            if (m == c.Npc || m == speaker || !table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { IsPlayer: false } mc) continue;
            if (mc.Fighter?.IsAnimal == true || mc.Appearance?.Animal is not null || mc.Engaged) continue;
            if (line.CrowdChance < 100 && Rng.Float(NextRoll(world, m)) * 100 >= line.CrowdChance) continue;
            StartBubbles(world, m, c.Player, root, CrowdTriggered, now: false);
        }
    }

    /// <summary>A bubble conversation's end (<c>FUN_140674830</c>): the event's counter and time, and the speaker squad's memory of the target.</summary>
    void EndBubbles(World world, Conversation c)
    {
        var key = (c.Npc, c.Event);
        var (count, _) = counters.GetValueOrDefault(key);
        counters[key] = (count + 1, world.GameHours);
        if (!c.Player.IsNone && world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc) met[(nc.SquadId, c.Player)] = world.GameHours;
    }

    /// <summary>Drops the lines whose time is up or whose speaker is gone (serial, each tick).</summary>
    void ExpireSpeech(World world)
    {
        if (shown.Count == 0) return;
        foreach (var who in shown.Where(s => world.Tick >= s.Value.Until || !world.Characters.TryResolveNext(s.Key, out _)).Select(s => s.Key).ToArray()) shown.Remove(who);
    }
}
