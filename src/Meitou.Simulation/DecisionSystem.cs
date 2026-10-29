using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation;

/// <summary>The steps of the original's chooser (<c>AITaskSytem::chooseGoal</c> <c>FUN_140510ef0</c>, docs/game/ai-tasks.md "Precedence"), in the order they are tried.</summary>
public enum DecisionStep : byte
{
    /// <summary>1: the front of the order queue (<c>checkOrders</c> <c>FUN_140510920</c>).</summary>
    Orders = 1,
    /// <summary>2: a committing current goal of priority above 2 keeps its plan.</summary>
    Commitment = 2,
    /// <summary>3: the urgent job pass (187 Obedient Slave, kind-2 jobs while enemies are sensed).</summary>
    UrgentJobs = 3,
    /// <summary>4: the priority-3 goals, one pick by desire x weight.</summary>
    Goals = 4,
    /// <summary>5: every job in list order, then the implicit jobs.</summary>
    Jobs = 5,
    /// <summary>6: priority-2 and priority-1 goals, the hold-position task.</summary>
    Fallback = 6,
}

/// <summary>
/// A source's answer for one character (<see cref="IDecisionSource.CanRun"/>, a pure read): it can run (<see cref="Can"/>; the original's desire x finder above 0), it runs the character's
/// task already (<see cref="Running"/>: the plan goes on, nothing is started), or why not (<see cref="Why"/>: an order's failure text, "no attacker in range"; null: none to show).
/// </summary>
public readonly record struct DecisionCheck(bool Can, bool Running = false, string? Why = null)
{
    public static DecisionCheck Run => new(true);
    public static DecisionCheck Runs => new(true, true);
    public static DecisionCheck No(string? why) => new(false, false, why);
}

/// <summary>
/// One source of the decision step (<see cref="DecisionSystem"/>): an order queue, a goal, a job. <see cref="CanRun"/> and <see cref="Desire"/> are pure reads of the world (serial phases), so the
/// same answer serves the step, the UI and a command gate; <see cref="Start"/> starts it through <see cref="TaskStart"/> (or a deferred fight). Sources are registered in an open list
/// (<see cref="DecisionSystem.Register"/>), sorted by the original's <see cref="Step"/> and, within it, its <see cref="Key"/>.
/// </summary>
public interface IDecisionSource
{
    /// <summary>A short name for the trace ("32 Self preservation").</summary>
    string Name { get; }
    DecisionStep Step { get; }
    /// <summary>The original's order within the step (the goal list's order, a job's list position); lower first, equal ones in registration order.</summary>
    int Key { get; }
    /// <summary>The goal's weight (step 4 scores desire x weight; the current goal x1.25).</summary>
    float Weight => 1;
    DecisionCheck CanRun(World world, int slot);
    /// <summary>How much it is wanted (above 0), asked only after <see cref="CanRun"/> said it can run.</summary>
    float Desire(World world, int slot) => 1;
    /// <summary>Starts it (serial phases only).</summary>
    void Start(World world, int slot);
    /// <summary>It could not run and the step went on (an order shows its failure text once).</summary>
    void Passed(World world, int slot, in DecisionCheck check) { }
}

/// <summary>A system that contributes decision sources registers them when the world is made (<see cref="World.Decisions"/>).</summary>
public interface IDecisionOwner
{
    void RegisterSources(DecisionSystem decisions);
}

/// <summary>
/// The last decision of a character, filled only while <see cref="DecisionSystem.Tracing"/> is on: the tick, the source that won (or what the step yielded to) and, per source asked,
/// its answer. Display only: not saved, not hashed.
/// </summary>
public sealed record DecisionTrace(long Tick, string Winner, IReadOnlyList<string> Lines);

/// <summary>
/// The single decision step for the player's characters (docs/simulation.md "Decision step"; the original's chooser <c>FUN_140510ef0</c>). Serial, in slot order. A character is decided
/// when its replan bit is set (<see cref="Request(World, int)"/>: a task ended, an order added or cleared, a stagger outside a fight, the melee alert with the fight off, the jobs changed,
/// a load), when its front order's task ended, and (<b>engine</b>) every <see cref="Production.ProductionSystem.JobCheckTicks"/> while it is idle or on a job's task. The sources are asked
/// in the original's order and the first that can run starts (step 4: the best score); a source already running ends the step. With nothing from steps 1 to 4 the step yields: the job passes
/// (production, first aid, carry, errands, attack on sight) and the standing tasks go on as before (M5b brings the jobs in as step-5 sources). NPCs keep <see cref="Ai.AiSystem"/>.
/// Run in the slow world after retaliation, aggro and flight (<see cref="World.RunTick"/>), and for orders only between ticks (<see cref="World.ApplyPausedInterfaceCommands"/>).
/// </summary>
public sealed class DecisionSystem
{
    readonly List<IDecisionSource> sources = [];
    long[] claimed = [];
    readonly List<string> lines = [];

    public DecisionSystem() => Register(new OrderSource());

    /// <summary>The sources in the order they are asked.</summary>
    public IReadOnlyList<IDecisionSource> Sources => sources;

    /// <summary>Record each decision in <see cref="CharacterCold.Decision"/> (a debug toggle; off by default, never hashed).</summary>
    public bool Tracing { get; set; }

    /// <summary>Adds a source after those of a lower step or key and after equal ones (an open list: a mod may add its own).</summary>
    public void Register(IDecisionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int at = sources.FindLastIndex(s => s.Step < source.Step || s.Step == source.Step && s.Key <= source.Key);
        sources.Insert(at + 1, source);
    }

    /// <summary>Asks for the character to be decided again (a replan trigger): any phase, its own slot or a serial one. Only the player's characters keep the bit.</summary>
    public static void Request(CharacterCold cold)
    {
        if (cold.IsPlayer) cold.Replan = true;
    }

    public static void Request(World world, int slot)
    {
        if (world.Characters.Cold(slot) is { } cold) Request(cold);
    }

    /// <summary>
    /// The engine started a task for the character this tick outside the step (a follow row's fight, a mate's defence, the attack-on-sight job): the step leaves it alone this tick.
    /// Serial phases only.
    /// </summary>
    public void Claim(World world, int slot)
    {
        if (claimed.Length <= slot) Array.Resize(ref claimed, Math.Max(slot + 1, claimed.Length * 2));
        claimed[slot] = world.Tick + 1;
    }

    bool Claimed(World world, int slot) => slot < claimed.Length && claimed[slot] == world.Tick + 1;

    /// <summary>
    /// The <b>engine</b> re-ask (design D4: no periodic replan was found in the original, but Meitou's idleness and job tasks do not end where the original's plans do): a character not fighting
    /// whose task is a job's, or that no start runs (idle, a standing task), is decided every <see cref="Production.ProductionSystem.JobCheckTicks"/>; so is one on an order whose row needs no
    /// melee threat (146, <see cref="PlayerOrders.NeedsNoThreat"/>), which a threat must take off its turret.
    /// </summary>
    public static bool ReAsked(World world, int slot, CharacterCold cold)
    {
        var task = (CharacterTask)world.Characters.Next[slot].Task;
        if (task == CharacterTask.Attack || cold.InCombat) return false;
        if (cold.Running is not { } run || !run.Runs(task)) return true;
        return run.Source == TaskSource.Job || run.Source == TaskSource.Order && cold.OrderQueue.Front is { Started: true } front && PlayerOrders.NeedsNoThreat(front.Row);
    }

    /// <summary>Decides every player character that is due, in slot order (serial). <paramref name="paused"/>: between ticks, orders only; the bit stays for the next tick's full step.</summary>
    internal void Run(World world, bool paused)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: true } cold) continue;
            bool front = cold.OrderQueue.Front is { } f && (!f.Tried || f.Started && !PlayerOrders.Runs(world, i, f));
            bool due = !paused && (world.Tick + i) % Production.ProductionSystem.JobCheckTicks == 0 && ReAsked(world, i, cold);
            if (!cold.Replan && !front && !due) continue;
            if (!paused && Claimed(world, i))
            {
                cold.Replan = false;
                continue;
            }
            Decide(world, i, paused);
        }
    }

    /// <summary>Decides one character now (serial phases): the order queue's own pops first (<see cref="PlayerOrders.Prepare"/>), then the sources in order.</summary>
    public void Decide(World world, int slot, bool paused = false)
    {
        var cold = world.Characters.Cold(slot)!;
        lines.Clear();
        PlayerOrders.Prepare(world, slot);
        string winner = paused ? "(paused: orders only)" : "(yield: jobs and standing tasks)";
        for (int k = 0; k < sources.Count;)
        {
            var step = sources[k].Step;
            int end = k;
            while (end < sources.Count && sources[end].Step == step) end++;
            if (paused && step != DecisionStep.Orders) break;
            if (step == DecisionStep.Goals ? PickGoal(world, slot, k, end, ref winner) : PickFirst(world, slot, k, end, ref winner)) break;
            k = end;
        }
        if (!paused) cold.Replan = false;   // what its own start ended asks nothing more
        if (Tracing) cold.Decision = new DecisionTrace(world.Tick, winner, lines.ToArray());
        else if (cold.Decision is not null) cold.Decision = null;
    }

    bool PickFirst(World world, int slot, int from, int end, ref string winner)
    {
        for (int k = from; k < end; k++)
        {
            var source = sources[k];
            var check = source.CanRun(world, slot);
            if (check.Can)
            {
                Note(source, check.Running ? "runs" : "starts");
                winner = source.Name;
                if (!check.Running) source.Start(world, slot);
                return true;
            }
            Note(source, check.Why ?? "cannot run");
            source.Passed(world, slot, check);
        }
        return false;
    }

    /// <summary>Step 4: the score is desire x weight, x1.25 for the goal running now; the best wins and a tie goes to the later-listed (<c>FUN_14050df10</c>'s raised keys).</summary>
    bool PickGoal(World world, int slot, int from, int end, ref string winner)
    {
        int best = -1;
        float bestScore = 0;
        bool bestRuns = false;
        for (int k = from; k < end; k++)
        {
            var source = sources[k];
            var check = source.CanRun(world, slot);
            if (!check.Can)
            {
                Note(source, check.Why ?? "cannot run");
                source.Passed(world, slot, check);
                continue;
            }
            float score = source.Desire(world, slot) * source.Weight * (check.Running ? 1.25f : 1);
            Note(source, score > 0 ? $"score {score:0.###}{(check.Running ? " (running)" : "")}" : "desire 0");
            if (score > 0 && score >= bestScore) (best, bestScore, bestRuns) = (k, score, check.Running);
        }
        if (best < 0) return false;
        winner = sources[best].Name;
        if (!bestRuns) sources[best].Start(world, slot);
        return true;
    }

    void Note(IDecisionSource source, string what)
    {
        if (Tracing) lines.Add($"{(int)source.Step} {source.Name}: {what}");
    }

    /// <summary>Source 1: the front of the order queue (<see cref="PlayerOrders"/>). One that cannot run shows its failure text once and stays queued; the step goes on.</summary>
    sealed class OrderSource : IDecisionSource
    {
        public string Name => "Orders";
        public DecisionStep Step => DecisionStep.Orders;
        public int Key => 0;

        public DecisionCheck CanRun(World world, int slot)
        {
            if (world.Characters.Cold(slot)!.OrderQueue.Front is not { } front) return DecisionCheck.No("no order");
            if (front.Started) return PlayerOrders.Runs(world, slot, front) ? DecisionCheck.Runs : DecisionCheck.No("its task ended");
            var check = PlayerOrders.CanRun(world, slot, front);
            return check.CanRun ? DecisionCheck.Run : DecisionCheck.No(check.Why ?? $"{front.Row} cannot run");
        }

        public void Start(World world, int slot) => PlayerOrders.StartFront(world, slot);

        public void Passed(World world, int slot, in DecisionCheck check) => PlayerOrders.Refused(world, slot);
    }
}
