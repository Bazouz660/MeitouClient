using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>The player's Talk order: <see cref="Talker"/> walks up to <see cref="Target"/> and the conversation opens in reach (the original's task 12, then event 1).</summary>
public sealed record TalkOrder(CharacterId Talker, CharacterId Target) : SimCommand;
/// <summary>Clicks choice <see cref="Index"/> of the open conversation.</summary>
public sealed record DialogueChoice(int Index) : SimCommand;
/// <summary>Leaves the open conversation (its window closed).</summary>
public sealed record EndConversation : SimCommand;

/// <summary>The open player conversation as the conversation window shows it (published with the snapshot).</summary>
public sealed record ConversationView(CharacterId Npc, CharacterId Player, CharacterId Speaker, string SpeakerName, string PlayerName, string Text,
    IReadOnlyList<string> Choices, IReadOnlyList<string> Log, long PlayerMoney);

/// <summary>
/// What a conversation asks the host to do once it has ended: DA_CHARACTER_EDITOR, the surgeon's editor on <see cref="DialogueRequest.Player"/>, already paid; DA_JOIN_SQUAD_WITH_EDIT,
/// the in-play editor on the recruit (<see cref="DialogueRequest.Player"/>).
/// </summary>
public enum DialogueRequestKind : byte { CharacterEditor, RecruitEditor }
public sealed record DialogueRequest(long Id, DialogueRequestKind Kind, CharacterId Player, CharacterId Npc);

/// <summary>A system with a conversation to publish (the dialogue system).</summary>
public interface IDialogueSource
{
    ConversationView? PublishConversation();
    DialogueRequest? PublishRequest();
    /// <summary>Player <paramref name="player"/>'s conversation and editor request (docs/simulation.md "Multiplayer (host-authoritative co-op)"); the parameterless ones are player 0's.</summary>
    ConversationView? PublishConversation(int player) => player == 0 ? PublishConversation() : null;
    DialogueRequest? PublishRequest(int player) => player == 0 ? PublishRequest() : null;
    /// <summary>The character answers the player's Talk (it has event-1 dialogue, and with <paramref name="talker"/> given, a line of it passes now).</summary>
    bool CanTalk(World world, int slot, CharacterId? talker = null);
    /// <summary>The lines on show over characters' heads (speech bubbles).</summary>
    IReadOnlyList<SpeechBubble> PublishSpeech() => [];
}

public sealed class DialogueSettings
{
    /// <summary>How near the talker must stand (the original walks there as task 12's precondition; its distance is <b>Unknown</b>; engine choice, the surgeon's reach).</summary>
    public float TalkRange { get; init; } = 60;
    /// <summary>Game seconds the talker has to reach the target before the order is dropped (engine choice).</summary>
    public float ApproachSeconds { get; init; } = 60;
    /// <summary>
    /// NPCs react to a player character they see with an ambient line (events 3 neutral, 66 enemy, 67 ally) as a speech bubble. Engine choice for the Unknown perception rules
    /// (docs/game/dialogue.md "Ambient conversations"): off by default (the pinned scenarios), the game turns it on.
    /// </summary>
    public bool AmbientEvents { get; init; }
    /// <summary>How near a player character must be to be seen, how often (ticks) the NPCs look, how many speak per look and the game hours before a squad reacts to the same character again (engine choices).</summary>
    public float AmbientRange { get; init; } = 600;
    public int AmbientEveryTicks { get; init; } = 30;
    public int AmbientPerScan { get; init; } = 2;
    public float AmbientRepeatHours { get; init; } = 0.5f;
}

/// <summary>
/// Dialogue (docs/game/dialogue.md; the engine design is its "Meitou design"): the player's conversations with NPCs. A <see cref="TalkOrder"/> walks the talker up and fires event 1
/// (PLAYER_TALK_TO_ME) at the NPC: its event-1 dialogue is chosen (score, line checks, a random pick at the top score), the NPC's line is said (its effects run at once) and the
/// passing replies become choices. The window pauses the game, so <see cref="DialogueChoice"/> and <see cref="EndConversation"/> apply between ticks (<see cref="World.ApplyPausedInterfaceCommands"/>);
/// effects that give AI orders start on the next tick.
/// <list type="bullet">
/// <item><b>Engine choices</b>: a childless NPC line is shown with a single <c>[Leave]</c> (the original ends at once and how it shows such a line is <b>Unknown</b>); a reply whose best
/// next line is another reply shows that reply as the only choice; conditions whose inputs Meitou lacks compute "no" (<see cref="Condition"/>).</item>
/// <item><b>State</b> (hashed): last use per line (global, as the original's), permanent chance rolls and event counters per character, the speaker squads' memory of whom they talked
/// to, pending interrupts, each player's approach and open conversation. The characters' dialogue locks are on <see cref="CharacterCold.DialogueLocks"/>.</item>
/// <item><b>Per player</b> (docs/simulation.md "Dialogue and trade per player"): each player number has its own talk; an NPC another player's window holds
/// (<see cref="World.HolderOf"/>) refuses a Talk with "{name} is busy with someone else.".</item>
/// </list>
/// </summary>
public sealed partial class DialogueSystem : ITickSystem, IStateHashed, IDialogueSource, INpcHolder
{
    readonly PopulationData data;
    readonly DialogueDatabase dialogue;
    readonly SortedDictionary<string, double> lastUse = new(StringComparer.Ordinal);
    readonly SortedDictionary<(CharacterId, string), bool> permanent = new(Comparer<(CharacterId, string)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
    readonly SortedDictionary<(CharacterId, int), (int Count, double Last)> counters = new(Comparer<(CharacterId, int)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : a.Item2.CompareTo(b.Item2)));
    readonly SortedDictionary<(int Squad, CharacterId Target), double> met = new(Comparer<(int, CharacterId)>.Create((a, b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));
    readonly SortedDictionary<CharacterId, string> interrupts = new(Comparer<CharacterId>.Create(CompareIds));
    ulong rolls;
    long requestId;

    // Each player's approach of a Talk order, open conversation and editor request (docs/simulation.md "Dialogue and trade per player"), by player number.
    readonly SortedDictionary<int, PlayerTalk> talks = [];

    readonly PopulationSystem? population;

    public DialogueSystem(PopulationData data, DialogueSettings? settings = null, PopulationSystem? population = null)
    {
        this.data = data;
        this.population = population;
        dialogue = data.Dialogue;
        Settings = settings ?? new DialogueSettings();
    }

    public DialogueSettings Settings { get; }

    static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    /// <summary>One player's Talk: the approach (<see cref="Talker"/> walking up to <see cref="Target"/>), the open conversation, its window and the last editor request.</summary>
    sealed class PlayerTalk
    {
        public required int Player;
        public CharacterId Talker = CharacterId.None, Target = CharacterId.None;
        public long ApproachUntil;
        public Conversation? Open;
        public ConversationView? View;
        public DialogueRequest? Request;
        public bool Empty => Talker.IsNone && Target.IsNone && Open is null;
    }

    PlayerTalk TalkOf(int player)
    {
        if (!talks.TryGetValue(player, out var talk)) talks[player] = talk = new PlayerTalk { Player = player };
        return talk;
    }

    /// <summary>One open player conversation.</summary>
    sealed class Conversation
    {
        public required CharacterId Npc, Player;
        public required int Event;
        /// <summary>The player whose window it is and whom its effects serve (a Trade, an editor, a recruit): the talk's player; a bubble's target's owner; 0 for an announcement.</summary>
        public int Owner;
        public DialogueNode? Root;
        /// <summary>The NPC-side line on screen and who said it.</summary>
        public DialogueNode? Line;
        public CharacterId Speaker;
        public string Text = "";
        public readonly CharacterId[] Interjectors = [CharacterId.None, CharacterId.None, CharacterId.None];
        /// <summary>The choices: a reply line, or <c>null</c> for <c>[...]</c> (continue <see cref="Line"/>) / <c>[Leave]</c> (when <see cref="Line"/> has no next).</summary>
        public readonly List<(DialogueNode? Reply, string Text)> Choices = [];
        public readonly List<string> Log = [];
        public bool Ended;
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case TalkOrder t:
                    // Another player's window holds the NPC (a conversation, its approach or a trade): the first holder keeps it.
                    if (world.HolderOf(t.Target) is >= 0 and var holder && holder != t.Player)
                    {
                        world.Post(t.Player, $"{NameOf(world, t.Target)} is busy with someone else.");
                        break;
                    }
                    var talk = TalkOf(t.Player);
                    End(world, talk);
                    StartApproach(world, talk, t);
                    break;
                case DialogueChoice c when talks.TryGetValue(c.Player, out var chosen) && chosen.Open is { } open:
                    Choose(world, open, c.Index);
                    if (open.Ended) End(world, chosen);
                    break;
                case EndConversation e when talks.TryGetValue(e.Player, out var ended):
                    End(world, ended);
                    CancelApproach(world, ended);
                    break;
            }
        }
        RefreshView(world);
    }

    public void SlowWorld(World world)
    {
        ExpireSpeech(world);
        if (bubbles.Count > 0) StepBubbles(world);
        if (Settings.AmbientEvents && world.Tick % Settings.AmbientEveryTicks == 0) SeePlayers(world);
        foreach (var talk in talks.Values)
        {
            if (talk.Talker.IsNone || talk.Open is not null) continue;
            var table = world.Characters;
            if (!table.TryResolveNext(talk.Talker, out int p) || !table.TryResolveNext(talk.Target, out int n) || !Awake(table, p) || !Awake(table, n)) CancelApproach(world, talk);
            else if (Vector3.Distance(table.Next[p].Position, table.Next[n].Position) <= Settings.TalkRange)
            {
                MovementSystem.Stop(table, p);
                var (who, whom) = (talk.Talker, talk.Target);
                CancelApproach(world, talk);
                Start(world, talk, whom, who, DialogueEvents.PlayerTalkToMe);
            }
            else if (world.Tick >= talk.ApproachUntil) CancelApproach(world, talk);
            else Hold(world, n);
        }
        RefreshView(world);
    }

    void StartApproach(World world, PlayerTalk talk, TalkOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Talker, out int p) || table.Cold(p) is not { IsPlayer: true } || !table.TryResolveNext(order.Target, out int n) || table.Cold(n) is not { IsPlayer: false } nc) return;
        if (!Awake(table, n)) return;
        // A campaign member with a pending interrupt (its negotiation) talks even when its faction is hostile (docs/game/factions-squads-towns.md "Campaigns", announcement).
        if (data.Relations.IsHostile(nc.Faction, world.Player.Faction) && !interrupts.ContainsKey(order.Target))
        {
            world.Defer(new Combat.AttackOrder([order.Talker], order.Target) { Tick = world.Tick + 1, Player = order.Player });
            return;
        }
        talk.Talker = order.Talker;
        talk.Target = order.Target;
        talk.ApproachUntil = world.Tick + (long)(Settings.ApproachSeconds / world.TickSeconds);
        Hold(world, n);
        if (Vector3.Distance(table.Next[p].Position, table.Next[n].Position) > Settings.TalkRange)
            world.Defer(new MoveOrder([talk.Talker], table.Next[n].Position) { Tick = world.Tick + 1, Player = order.Player });
    }

    void CancelApproach(World world, PlayerTalk talk)
    {
        if (!talk.Target.IsNone && talk.Open?.Npc != talk.Target) Release(world, talk.Target);
        talk.Talker = talk.Target = CharacterId.None;
    }

    static bool Awake(CharacterTable table, int slot) => table.Next[slot].Alive && table.Cold(slot) is { IsImprisoned: false } c && c.Medical?.Incapacitated != true;

    /// <summary>Keeps the NPC standing while the talker comes and talks (as the trader in a trade); released when it ends.</summary>
    static void Hold(World world, int slot)
    {
        var table = world.Characters;
        table.Cold(slot)!.Engaged = true;
        if (table.Next[slot].Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) MovementSystem.Stop(table, slot);
    }

    static void Release(World world, CharacterId id)
    {
        if (world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot) is { } c) c.Engaged = false;
    }

    /// <summary>Fires <paramref name="ev"/> at <paramref name="npc"/> with <paramref name="player"/> as its target and opens the conversation in <paramref name="talk"/> (docs/game/dialogue.md "Player conversation").</summary>
    void Start(World world, PlayerTalk talk, CharacterId npc, CharacterId player, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || table.Cold(n) is not { } nc) return;
        var c = new Conversation { Npc = npc, Player = player, Event = ev, Owner = talk.Player };
        IReadOnlyList<DialogueNode> roots = interrupts.Remove(npc, out var pending) && dialogue.Node(pending) is { IsRoot: true } interrupt ? [interrupt] : SetOf(world, n).For(ev);
        var picked = Pick(world, c, roots);
        if (picked is not { } p)
        {
            world.Post(c.Owner, $"{nc.Name} has nothing to say.");
            return;
        }
        talk.Open = c;
        Hold(world, n);
        c.Root = p.Root;
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else Offer(world, c, p.Root ?? p.Line);
        if (c.Ended) End(world, talk);
    }

    /// <summary>The character's dialogue (its record, its squad's template and its role, docs/game/dialogue.md "Who has which dialogue").</summary>
    internal DialogueSet SetOf(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool animal = cold.Fighter?.IsAnimal ?? cold.Appearance?.Animal is not null;
        string? playerFaction = data.Relations.Contains(world.Player.Faction) ? data.Factions[world.Player.Faction].Id : null;
        return dialogue.SetFor(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == world.Characters.IdOf(slot), cold.IsPlayer, animal, playerFaction);
    }

    /// <summary>
    /// The character offers Trade: its squad has a shop, and its record says <c>is trader</c> or its dialogue reaches a Trade line (docs/game/economy.md "Who trades"); the
    /// guards and other members of a trader squad do not.
    /// </summary>
    public bool Trades(World world, int slot) =>
        world.Characters.Cold(slot) is { IsPlayer: false, SquadId: >= 0 } cold && world.Squads.Find(cold.SquadId)?.Shop is not null
        && (data.Db.Find(cold.RecordId)?.GetBool("is trader") == true || SetOf(world, slot).Trades);

    public bool CanTalk(World world, int slot, CharacterId? talker = null)
    {
        if (world.Characters.Cold(slot) is not { IsPlayer: false }) return false;
        var npc = world.Characters.IdOf(slot);
        if (interrupts.ContainsKey(npc)) return true;
        var roots = SetOf(world, slot).For(DialogueEvents.PlayerTalkToMe);
        if (roots.Count == 0) return false;
        if (talker is not { } player) return true;
        // A dry run of Start's pick (it rolls dice and memoizes permanent chances: both put back), so the menu never offers a Talk that ends in "has nothing to say".
        var (savedRolls, known) = (rolls, permanent.Keys.ToList());
        bool says = Pick(world, new Conversation { Npc = npc, Player = player, Event = DialogueEvents.PlayerTalkToMe }, roots) is not null;
        rolls = savedRolls;
        foreach (var key in permanent.Keys.Except(known).ToList()) permanent.Remove(key);
        return says;
    }

    static bool IsNpcSide(Talker t) => t is Talker.Me or Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 or Talker.WholeSquad;

    /// <summary>The NPC side says <paramref name="line"/> (docs/game/dialogue.md "Saying a line"): a text, marked used, its effects at once, then the choices.</summary>
    void Say(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        c.Line = line;
        c.Speaker = speaker;
        MarkUsed(world, c, line);
        string text = Expand(world, c, line, PickText(world, speaker, line), speaker, c.Player);
        if (line.TakesMoney && line.Cost is { } cost) text += $" [c.{cost:N0}]";
        c.Text = text;
        c.Log.Add($"{NameOf(world, speaker)}: {text}");
        RunEffects(world, c, line, speaker);
        if (c.Ended) return;
        Offer(world, c, line);
    }

    /// <summary>The choices after <paramref name="line"/> (docs/game/dialogue.md "Choices"): the passing children in order; an NPC-side one makes a single <c>[...]</c>.</summary>
    void Offer(World world, Conversation c, DialogueNode line)
    {
        c.Choices.Clear();
        foreach (var child in line.Children)
        {
            if (!SpeakerOf(world, c, child, out var speaker)) continue;
            var me = IsNpcSide(child.Speaker) ? speaker : c.Npc;
            if (!Check(world, c, child, me, c.Player)) continue;
            if (IsNpcSide(child.Speaker))
            {
                c.Choices.Clear();
                c.Choices.Add((null, "[...]"));
                return;
            }
            c.Choices.Add((child, Expand(world, c, child, PickText(world, c.Player, child), c.Player, c.Npc)));
        }
        if (c.Choices.Count == 0) c.Choices.Add((null, "[Leave]"));
    }

    void Choose(World world, Conversation c, int index)
    {
        if ((uint)index >= (uint)c.Choices.Count) return;
        var (reply, text) = c.Choices[index];
        DialogueNode? from = c.Line;
        if (reply is not null)
        {
            c.Log.Add($"{NameOf(world, c.Player)}: {text}");
            MarkUsed(world, c, reply);
            RunEffects(world, c, reply, c.Npc);
            if (c.Ended) return;
            from = reply;
        }
        else if (text == "[Leave]" || from is null) { c.Ended = true; return; }
        var next = Pick(world, c, from.Children);
        if (next is not { } p) { c.Ended = true; return; }
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else
        {
            c.Choices.Clear();
            c.Choices.Add((p.Line, Expand(world, c, p.Line, PickText(world, c.Player, p.Line), c.Player, c.Npc)));
        }
    }

    /// <summary>Ends the talk's open conversation (<c>FUN_140674830</c>): the event counter and its time, the speaker squad's memory of the partner, the NPC released.</summary>
    void End(World world, PlayerTalk talk)
    {
        if (talk.Open is not { } c) return;
        talk.Open = null;
        talk.View = null;
        var key = (c.Npc, c.Event);
        var (count, _) = counters.GetValueOrDefault(key);
        counters[key] = (count + 1, world.GameHours);
        if (world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc) met[(nc.SquadId, c.Player)] = world.GameHours;
        Release(world, c.Npc);
    }

    void MarkUsed(World world, Conversation c, DialogueNode line)
    {
        lastUse[line.StringId] = world.GameHours;
        if (c.Root is { } root) lastUse[root.StringId] = world.GameHours;
    }

    ulong NextRoll(World world, CharacterId who) => Rng.Hash(world.Seed, Rng.Key(who), RngPurpose.Dialogue, rolls++);

    string PickText(World world, CharacterId who, DialogueNode line) =>
        line.Texts.Count switch { 0 => "", 1 => line.Texts[0], var n => line.Texts[Rng.Int(NextRoll(world, who), n)] };

    string NameOf(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { } c ? c.Name : "";

    internal void RefreshView(World world)
    {
        foreach (var talk in talks.Values)
            talk.View = talk.Open is { } c
                ? new ConversationView(c.Npc, c.Player, c.Speaker, NameOf(world, c.Speaker.IsNone ? c.Npc : c.Speaker), NameOf(world, c.Player), c.Text,
                    [.. c.Choices.Select(x => x.Text)], [.. c.Log], world.Player.Money)
                : null;
    }

    public ConversationView? PublishConversation() => PublishConversation(0);
    public DialogueRequest? PublishRequest() => PublishRequest(0);
    public ConversationView? PublishConversation(int player) => talks.TryGetValue(player, out var talk) ? talk.View : null;
    public DialogueRequest? PublishRequest(int player) => talks.TryGetValue(player, out var talk) ? talk.Request : null;

    /// <summary>The player whose talk holds <paramref name="npc"/> (its approach's target or its open conversation's NPC); -1 none.</summary>
    public int HolderOf(CharacterId npc)
    {
        if (npc.IsNone) return -1;   // an idle talk's Target is None
        foreach (var talk in talks.Values)
            if (talk.Target == npc || talk.Open?.Npc == npc) return talk.Player;
        return -1;
    }

    /// <summary>Some player's open conversation is <paramref name="root"/> with <paramref name="npc"/> (the <c>one at a time</c> test).</summary>
    bool Talking(CharacterId npc, DialogueNode root)
    {
        foreach (var talk in talks.Values)
            if (talk.Open is { } o && o.Npc == npc && o.Root == root) return true;
        return false;
    }

    bool NoTalks()
    {
        foreach (var talk in talks.Values)
            if (!talk.Empty) return false;
        return true;
    }

    /// <summary>
    /// Player 0's talk is hashed where the single-player approach and conversation always were (none: <see cref="CharacterId.None"/> twice); each other player's talk
    /// that is not empty follows with its number, so those terms appear only while a second player talks.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (lastUse.Count == 0 && permanent.Count == 0 && counters.Count == 0 && met.Count == 0 && interrupts.Count == 0 && bubbles.Count == 0 && ambientAt.Count == 0 && NoTalks()) return;
        hasher.Add(0x44696167);
        hasher.Add(rolls);
        foreach (var (line, hours) in lastUse) { hasher.Add(Rng.StableHash(line)); hasher.Add(hours); }
        foreach (var ((who, line), passed) in permanent) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); hasher.Add(passed); }
        foreach (var ((who, ev), (count, last)) in counters) { hasher.Add(who); hasher.Add(ev); hasher.Add(count); hasher.Add(last); }
        foreach (var ((squad, whom), hours) in met) { hasher.Add(squad); hasher.Add(whom); hasher.Add(hours); }
        foreach (var (who, line) in interrupts) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); }
        foreach (var b in bubbles) { hasher.Add(b.C.Npc); hasher.Add(Rng.StableHash(b.Line.StringId)); hasher.Add(b.DueTick); }
        foreach (var ((squad, whom), hours) in ambientAt) { hasher.Add(squad); hasher.Add(whom); hasher.Add(hours); }
        var local = talks.GetValueOrDefault(0);
        hasher.Add(local?.Talker ?? CharacterId.None);
        hasher.Add(local?.Target ?? CharacterId.None);
        if (local?.Open is { } c) HashOpen(ref hasher, c);
        foreach (var (player, talk) in talks)
        {
            if (player == 0 || talk.Empty) continue;
            hasher.Add(0x54616C00 + player);
            hasher.Add(talk.Talker);
            hasher.Add(talk.Target);
            if (talk.Open is { } o) HashOpen(ref hasher, o);
        }
    }

    static void HashOpen(ref StateHasher hasher, Conversation c)
    {
        hasher.Add(c.Npc);
        hasher.Add(c.Player);
        hasher.Add(c.Line is { } l ? Rng.StableHash(l.StringId) : 0UL);
        hasher.Add(c.Choices.Count);
    }
}
