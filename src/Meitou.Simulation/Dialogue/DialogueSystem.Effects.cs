using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// A line's effects (<c>FUN_140680560</c>, docs/game/dialogue.md "Effects"), run as it is said or clicked, with <paramref name="me"/> the NPC-side speaker and the player character the
    /// target, in the original's order: locks, interrupt, relation changes, then the DA effects in data order. Not built yet: crowd triggers, items given, AI changes and
    /// contracts, arrests other than the bounty desk's hand-over, and the slavery effects other than DA 40. A Trade, an editor and a recruit serve the conversation's
    /// <see cref="Conversation.Owner"/>; relation and payment notices go to everyone (the purse is shared).
    /// </summary>
    void RunEffects(World world, Conversation c, DialogueNode line, CharacterId me)
    {
        var mc = Cold(world, me);
        if (mc is not null)
        {
            foreach (string d in line.Unlocks) Lock(mc, d, false);
            if (line.Unlocks.Count > 0 && c.Root is { } root) Lock(mc, root.StringId, true);
            foreach (string d in line.UnlockButKeepMe) Lock(mc, d, false);
            foreach (string d in line.Locks) Lock(mc, d, true);
        }
        if (line.Interrupt is { } interrupt) interrupts[me] = interrupt;
        foreach (var (factionId, amount) in line.ChangeRelations)
        {
            int f = data.Relations.IndexOf(factionId);
            if (f < 0 || amount == 0 || world.Player.Faction < 0) continue;
            ChangeRelation(world, f, world.Player.Faction, amount);
            world.Post(amount > 0 ? $"Relations with {data.Factions[f].Name} improved by {amount}" : $"Relations with {data.Factions[f].Name} decreased by {-amount}");
        }
        // Crowd triggers and items given are not built yet; then the campaign trigger.
        if (line.TriggerCampaigns.Count > 0) population?.TriggerFromDialogue(world, me, c.Player, line.TriggerCampaigns);
        var target = c.Player;
        var tc = Cold(world, target);
        bool takeOver = false, enslave = false;
        foreach (var effect in line.Effects)
        {
            switch (effect.Action)
            {
                case DialogueActions.Trade:
                    c.Ended = true;
                    if (mc is { SquadId: >= 0 } && world.Squads.Find(mc.SquadId)?.Shop is not null)
                        world.Defer(new Trading.OpenTrade(target, me) { Tick = world.Tick + 1, Player = c.Owner });
                    return;
                case DialogueActions.AffectRelations when mc is not null && tc is not null:
                    ChangeRelation(world, mc.Faction, tc.Faction, effect.Value);
                    break;
                case DialogueActions.AttackChaseForever or DialogueActions.AttackStayNearHome:
                    Attack(world, me, target);
                    break;
                case DialogueActions.AttackIfNoCoexist when mc is not null && tc is not null && !data.Relations.Coexists(mc.Faction, tc.Faction):
                    Attack(world, me, target);
                    break;
                case DialogueActions.TakeMoney when tc is not null:
                    Pay(world, tc, -effect.Value);
                    world.Post(effect.Value > 100000 ? $"Paid c.{effect.Value / 1000}k" : $"Paid c.{effect.Value.ToString("N0", CultureInfo.InvariantCulture)}");
                    break;
                case DialogueActions.GiveMoney when tc is not null:
                    Pay(world, tc, effect.Value);
                    break;
                case DialogueActions.CharacterEditor:
                    c.Ended = true;
                    TalkOf(c.Owner).Request = new DialogueRequest(++requestId, DialogueRequestKind.CharacterEditor, target, me);
                    // The paid surgery edit this editor sends is allowed for the conversation's player only (docs/simulation.md "Multiplayer (host-authoritative co-op)").
                    world.Player.Session(c.Owner).Grant = new EditGrant(Editing.EditKind.Surgery, target, me);
                    return;
                case DialogueActions.AssignBounty when tc is not null && mc is not null && effect.Value > 0 && data.Relations.Contains(mc.Faction):
                {
                    tc.Bounties ??= new Crime.BountyLedger();
                    tc.Bounties.Get(mc.Faction, data.Factions[mc.Faction].Name).Amount += effect.Value;
                    break;
                }
                case DialogueActions.PayBounty:
                    Arrests?.PayBounty(world, me, target);
                    break;
                case DialogueActions.ArrestTargetsCarriedPerson when tc is { Carrying.IsNone: false }:
                    takeOver = true;   // after the line's other effects: the original only gives the speaker the lift task, so DA_PAY_BOUNTY still finds the body
                    break;
                case DialogueActions.EnslaveTargetsCarriedPerson when tc is { Carrying.IsNone: false }:
                    takeOver = enslave = true;
                    break;
                case DialogueActions.ClearBounty when tc?.Bounties is { } ledger && mc is not null:
                    if (ledger.Of(mc.Faction) is { } entry) ledger.Remove(entry);
                    if (Position(world, target) is { } at && TownAt(at) is >= 0 and var town && town < data.SiteFactions.Count && ledger.Of(data.SiteFactions[town]) is { } local)
                        ledger.Remove(local);
                    break;
                case DialogueActions.LockThisDialog when mc is not null && c.Root is { } current:
                    Lock(mc, current.StringId, true);
                    break;
                case DialogueActions.AssaultPhase or DialogueActions.RetreatPhase:
                    population?.SetCampaignPhase(world, me, effect.Action == DialogueActions.AssaultPhase);
                    break;
                case DialogueActions.JoinSquadWithEdit or DialogueActions.JoinSquadFast:
                    if (!Recruit(world, c, me, effect.Action == DialogueActions.JoinSquadWithEdit)) return;
                    break;
            }
        }
        if (takeOver) Arrests?.TakeOver(world, me, target, enslave);
    }

    /// <summary>The arrest system the bounty desk's effects pay and hand over through (<c>DA_PAY_BOUNTY</c>, <c>DA_ARREST_TARGETS_CARRIED_PERSON</c>); null: they do nothing.</summary>
    public Crime.ArrestSystem? Arrests { get; set; }

    static void Lock(CharacterCold cold, string dialogue, bool locked) => (cold.DialogueLocks ??= new(StringComparer.Ordinal))[dialogue] = locked;

    void ChangeRelation(World world, int a, int b, int amount)
    {
        var (before, after) = data.Relations.Add(a, b, amount);
        int other = a == world.Player.Faction ? b : a;
        if (b == world.Player.Faction || a == world.Player.Faction)
            if (PlayerRelations.Notice(data.Factions[other].Name, before, after) is { } text) world.Post(text);
    }

    /// <summary>Money to (or from, when negative) the character's faction: the player's purse, else its squad's.</summary>
    static void Pay(World world, CharacterCold who, long amount)
    {
        if (who.IsPlayer || who.Faction == world.Player.Faction) world.Player.Money = Math.Max(0, world.Player.Money + amount);
        else if (who.SquadId >= 0 && world.Squads.Find(who.SquadId) is { } squad) squad.Money = Math.Max(0, squad.Money + amount);
    }

    /// <summary>The speaker and the awake members of its squad near the target attack it from the next tick (DA_ATTACK_CHASE_FOREVER's task 101 on the target's squad, engine reading).</summary>
    static void Attack(World world, CharacterId me, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(target, out int t)) return;
        var at = table.Next[t].Position;
        var fighters = new List<CharacterId>();
        foreach (var m in SquadOf(world, me))
        {
            if (!table.TryResolveNext(m, out int s) || !table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, Fighter.CanAttack: true } mc) continue;
            float r = 2 * AggroSystem.RangeOf(mc, AggroSystem.OutdoorSight);
            if (m == me || Vector3.DistanceSquared(table.Next[s].Position, at) <= r * r) fighters.Add(m);
        }
        if (fighters.Count > 0) world.Defer(new Combat.AttackOrder(fighters, target) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// The text tokens (docs/game/dialogue.md "Tokens"): <c>/NAME/</c> pairs until none is left; COST, MYNAME, YOURNAME, TARGETFACTION and PLAYERFACTION are built in, any other name
    /// is the WORD_SWAPS record of that name (its lines chosen like a line's children, one of their texts; none: empty). A token at the start or after ". ", "! ", "? " is capitalised.
    /// </summary>
    string Expand(World world, Conversation c, DialogueNode line, string text, CharacterId me, CharacterId other, int depth = 0)
    {
        if (depth > 4 || text.IndexOf('/') < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('/', i);
            int close = open < 0 ? -1 : text.IndexOf('/', open + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            string token = text[(open + 1)..close];
            string value = Token(world, c, line, token.Trim(), me, other, depth);
            bool capital = sb.Length == 0 || sb.ToString().EndsWith(". ", StringComparison.Ordinal) || sb.ToString().EndsWith("! ", StringComparison.Ordinal) || sb.ToString().EndsWith("? ", StringComparison.Ordinal);
            if (capital && value.Length > 0) value = char.ToUpperInvariant(value[0]) + value[1..];
            sb.Append(value);
            i = close + 1;
        }
        return sb.ToString();
    }

    string Token(World world, Conversation c, DialogueNode line, string token, CharacterId me, CharacterId other, int depth)
    {
        switch (token)
        {
            case "COST": return line.Cost is { } cost ? cost.ToString("N0", CultureInfo.InvariantCulture) : "";
            case "MYNAME": return NameOf(world, me);
            case "YOURNAME": return Cold(world, other)?.Name ?? "you";
            case "TARGETFACTION": return Cold(world, other) is { } o && data.Relations.Contains(o.Faction) ? data.Factions[o.Faction].Name : "you";
            case "PLAYERFACTION": return data.Relations.Contains(world.Player.Faction) ? data.Factions[world.Player.Faction].Name : "";
        }
        if (dialogue.WordSwap(token) is not { } swap) return "";
        var candidates = new List<DialogueNode>();
        int best = int.MinValue;
        foreach (var child in swap.Children)
        {
            if (child.Score < best) break;
            if (!Check(world, c, child, me, other)) continue;
            if (child.Score > best) { best = child.Score; candidates.Clear(); }
            candidates.Add(child);
        }
        if (candidates.Count == 0) return "";
        var pick = candidates.Count == 1 ? candidates[0] : candidates[Rng.Int(NextRoll(world, me), candidates.Count)];
        return Expand(world, c, pick, PickText(world, me, pick), me, other, depth + 1);
    }
}
