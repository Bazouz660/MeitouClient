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
    // When a squad last reacted to seeing a player character (the cooldown of SeePlayers).
    readonly SortedDictionary<(int Squad, CharacterId Target), double> ambientAt = new(Comparer<(int, CharacterId)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));

    /// <summary>The events whose bubbles use the large font (<c>FUN_140725f30</c>, <b>Observed</b>); the others use the medium one.</summary>
    static bool IsLarge(int ev) => ev is 2 or 11 or 17 or 26 or 31 or 34 or 36 or 40 or 45 or 74;

    public IReadOnlyList<SpeechBubble> PublishSpeech() => [.. shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large))];

    /// <summary>
    /// A campaign's <c>dialog announcement</c> (docs/game/factions-squads-towns.md "Campaigns", announcement): <paramref name="dialogueId"/> fired as event 2 by
    /// <paramref name="npc"/> with no target. Its lines are said one after another as speech bubbles. Their effects run as said (an <c>interrupt</c> makes the next talk
    /// with the speaker that dialogue). Returns whether it started.
    /// </summary>
    public bool Announce(World world, CharacterId npc, string dialogueId)
    {
        if (dialogue.Node(dialogueId) is not { IsRoot: true } root || !world.Characters.TryResolveNext(npc, out int n) || !Awake(world.Characters, n)) return false;
        var c = new Conversation { Npc = npc, Player = CharacterId.None, Event = DialogueEvents.Announcement, Root = root };
        if (NextBubble(world, c, root) is not { } first) return false;
        bubbles.Add(new Bubble(c, first, world.Tick, npc));
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
        var c = new Conversation { Npc = npc, Player = whom, Event = ev, Owner = Math.Max(0, world.OwnerOf(whom)) };
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom) return false;
        c.Root = picked.Root;
        bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        return true;
    }

    bool BubbleOf(CharacterId who) => shown.ContainsKey(who) || bubbles.Any(b => b.Speaker == who || b.C.Npc == who);

    /// <summary>Whom a loaded NPC reacts to by sight: the engine's stand-in for the perception that fires events 3, 66 and 67 (<c>FUN_140859680</c>).</summary>
    void SeePlayers(World world)
    {
        var table = world.Characters;
        var players = new List<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, Medical.Dead: false }) players.Add(i);
        if (players.Count == 0 || !data.Relations.Contains(world.Player.Faction)) return;
        float range2 = Settings.AmbientRange * Settings.AmbientRange;
        int said = 0;
        for (int i = 0; i < table.HighWater && said < Settings.AmbientPerScan; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task == (byte)CharacterTask.Rest || table.Cold(i) is not { IsPlayer: false, SquadId: >= 0, Engaged: false, InCombat: false, Fleeing: false, IsImprisoned: false } nc) continue;
            if (nc.Fighter?.IsAnimal == true || nc.Appearance?.Animal is not null || !data.Relations.Contains(nc.Faction) || !Awake(table, i)) continue;
            int best = -1;
            float best2 = range2;
            foreach (int p in players)
            {
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position);
                if (d2 <= best2) (best, best2) = (p, d2);
            }
            if (best < 0) continue;
            var target = table.IdOf(best);
            if (ambientAt.TryGetValue((nc.SquadId, target), out double at) && world.GameHours - at < Settings.AmbientRepeatHours) continue;
            int ev = data.Relations.IsHostile(nc.Faction, world.Player.Faction) ? 66 : data.Relations.IsAlly(nc.Faction, world.Player.Faction) ? 67 : 3;
            if (SetOf(world, i).For(ev).Count == 0) continue;
            // Asked again only after the cooldown whether or not a line passed (else every scan would roll again).
            ambientAt[(nc.SquadId, target)] = world.GameHours;
            if (Fire(world, table.IdOf(i), target, ev)) said++;
        }
    }

    /// <summary>Whether talking to <paramref name="npc"/> opens a pending <c>interrupt</c> dialogue (a campaign's negotiation).</summary>
    public bool HasInterrupt(CharacterId npc) => interrupts.ContainsKey(npc);

    /// <summary>The first child of <paramref name="line"/> the speaker says (its own lines; the bubble chain does not pass to interjectors), or null.</summary>
    DialogueNode? NextBubble(World world, Conversation c, DialogueNode line)
    {
        foreach (var child in line.Children)
            if (child.Speaker is Talker.Me or Talker.WholeSquad && Check(world, c, child, c.Npc, c.Player)) return child;
        return null;
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
            string text = Expand(world, c, line, PickText(world, speaker, line), speaker, c.Player);
            RunEffects(world, c, line, c.Npc);
            float seconds = 2 + 0.12f * text.Length;
            if (text.Length > 0) shown[speaker] = (text, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), IsLarge(c.Event));
            foreach (var e in line.Effects) if (e.Action == DialogueActions.BubbleWait) seconds = MathF.Max(seconds, e.Value);
            if (c.Ended || NextBubble(world, c, line) is not { } next)
            {
                EndBubbles(world, c);
                bubbles.RemoveAt(k--);
            }
            else bubbles[k] = new Bubble(c, next, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), c.Npc);
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
