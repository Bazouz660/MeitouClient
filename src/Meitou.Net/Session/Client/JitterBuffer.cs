using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>What a client shows this frame: the snapshots around the playback point, their alphas and the playback point in host ticks.</summary>
public readonly record struct Presentation(WorldSnapshot Older, WorldSnapshot Newer, float Alpha, float MotionAlpha, double Tick);

/// <summary>
/// The client's presentation clock (docs/net.md, "Client and jitter buffer"): assembled snapshots keyed by host tick, played back a little behind the newest
/// so the arrival jitter is hidden. The playback point runs in host ticks at the host's rate (30 x the achieved speed, 0 while the host is paused or busy),
/// steered gently toward <c>live estimate - delay</c>; it snaps on a large error, extrapolates at most <see cref="ExtrapolationCap"/> s past the newest
/// snapshot (motion alpha up to 2) and then holds. The delay grows with the measured lateness at once and shrinks slowly. Frame thread only.
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>Engine choices (seconds): the first delay, its bounds, how far playback may run past the newest snapshot, the error that snaps, the steering
    /// limits (fractions of the rate: speeding up, slowing down), the window of arrival samples, how much one starvation adds, how fast the delay shrinks per
    /// second, and the lateness decay. Slowing down may be stronger so a faster host speed (a larger delay in ticks) is reached within a fraction of a second.</summary>
    public const double StartDelay = 0.10, MinDelay = 0.05, MaxDelay = 0.5, ExtrapolationCap = 0.1, SnapThreshold = 0.5, Correction = 0.1, SlowDown = 0.5, Gain = 2,
        SampleWindow = 1.0, StarveStep = 0.02, ShrinkPerSecond = 0.005, LatenessDecay = 2.0;

    /// <summary>Most snapshots kept (older ones beyond the playback point are dropped anyway).</summary>
    public const int MaxFrames = 64;

    /// <summary>The host's tick rate at speed 1.</summary>
    public const double TicksPerSecond = 30;

    readonly List<(long Tick, WorldSnapshot Snapshot)> frames = [];
    readonly List<(long Tick, long ArrivalUs)> samples = [];
    double rate = TicksPerSecond, lastRunningRate = TicksPerSecond, lateness, delay = StartDelay;
    int sendRate = 20;
    double? point, settle;
    bool starving;

    /// <summary>The current playback delay in seconds.</summary>
    public double DelaySeconds => delay;

    /// <summary>How many times playback ran past the newest snapshot (one count per episode; not within <see cref="SampleWindow"/> after a rate change).</summary>
    public int Starved { get; private set; }

    /// <summary>Snapshots held.</summary>
    public int Count => frames.Count;

    /// <summary>The playback rate in host ticks per real second.</summary>
    public double Rate => rate;

    /// <summary>The newest snapshot's tick; -1 when empty.</summary>
    public long NewestTick => frames.Count == 0 ? -1 : frames[^1].Tick;

    /// <summary>Adds an assembled snapshot that arrived at <paramref name="arrivalUs"/>; one of the same tick replaces the older (its views may be newer).</summary>
    public void Add(long tick, long arrivalUs, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (rate > 0 && samples.Count > 0)
        {
            double live = Live(arrivalUs);
            lateness = Math.Max(lateness, Math.Max(0, (live - tick) / rate));
        }
        int at = frames.Count;
        while (at > 0 && frames[at - 1].Tick > tick) at--;
        if (at > 0 && frames[at - 1].Tick == tick) frames[at - 1] = (tick, snapshot);
        else frames.Insert(at, (tick, snapshot));
        if (frames.Count > MaxFrames) frames.RemoveAt(0);
        samples.Add((tick, arrivalUs));
        double target = Math.Clamp(1.25 / Math.Max(1, sendRate) + lateness + 0.01, MinDelay, MaxDelay);
        if (target > delay) delay = target;
    }

    /// <summary>
    /// The host's rate in ticks per real second (0 while it is paused or busy) and its snapshot rate. A change clears the arrival samples, so playback after a
    /// resume first waits about one delay.
    /// </summary>
    public void SetRate(double ticksPerSecond, int snapshotsPerSecond)
    {
        if (!double.IsFinite(ticksPerSecond) || ticksPerSecond < 0) ticksPerSecond = 0;
        sendRate = Math.Max(1, snapshotsPerSecond);
        if (ticksPerSecond == rate) return;
        rate = ticksPerSecond;
        if (rate > 0) lastRunningRate = rate;
        samples.Clear();
        settle = SampleWindow;
    }

    /// <summary>Moves the playback point by <paramref name="realSeconds"/> and pairs the snapshots around it; null when none is held.</summary>
    public Presentation? Advance(long nowUs, double realSeconds)
    {
        if (frames.Count == 0) return null;
        double dt = double.IsFinite(realSeconds) ? Math.Clamp(realSeconds, 0, 1) : 0;
        long newest = frames[^1].Tick;
        samples.RemoveAll(s => nowUs - s.ArrivalUs > SampleWindow * 1e6);

        // 1-2: the live estimate, the target and the playback point.
        double live = rate > 0 && samples.Count > 0 ? Live(nowUs) : newest;
        double target = live - delay * rate;
        if (point is not { } p || Math.Abs(target - p) > SnapThreshold * Math.Max(rate, TicksPerSecond)) p = target;
        else if (rate > 0) p += dt * rate * (1 + Math.Clamp(Gain * (target - p) / rate, -SlowDown, Correction));
        else if (p < newest) p = Math.Min(p + dt * lastRunningRate, newest);
        p = Math.Min(p, newest + ExtrapolationCap * rate);
        point = p;

        // 3: the pair around the point.
        int older = -1;
        for (int i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Tick <= p) { older = i; break; }
        Presentation presentation;
        if (older < 0)
        {
            starving = false;
            presentation = new Presentation(frames[0].Snapshot, frames[0].Snapshot, 0, 0, p);
        }
        else if (older + 1 < frames.Count)
        {
            starving = false;
            var (t0, s0) = frames[older];
            var (t1, s1) = frames[older + 1];
            float alpha = (float)((p - t0) / (t1 - t0));
            presentation = new Presentation(s0, s1, alpha, alpha, p);
        }
        else
        {
            var (tn, sn) = frames[^1];
            // Right after a rate change the buffer holds too little for the new rate: that is not jitter, so it costs no delay.
            if (rate > 0 && p > tn && !starving && settle is null)
            {
                starving = true;
                Starved++;
                delay = Math.Min(MaxDelay, delay + StarveStep);
            }
            if (frames.Count >= 2)
            {
                var (tm, sm) = frames[^2];
                float motion = (float)Math.Min(2, 1 + (p - tn) / (tn - tm));
                presentation = new Presentation(sm, sn, 1, motion, p);
            }
            else presentation = new Presentation(sn, sn, 1, 1, p);
            older = Math.Max(0, frames.Count - 2);
        }

        if (settle is { } left) settle = left > dt ? left - dt : null;

        // 4: the lateness peak decays; the delay follows it down slowly.
        lateness *= Math.Exp(-dt / LatenessDecay);
        double wanted = Math.Clamp(1.25 / sendRate + lateness + 0.01, MinDelay, MaxDelay);
        if (wanted < delay) delay = Math.Max(wanted, delay - ShrinkPerSecond * dt);

        // 5: frames before the older one are no longer needed.
        if (older > 0) frames.RemoveRange(0, older);
        return presentation;
    }

    /// <summary>Forgets every snapshot, sample and the playback point (a new epoch); the delay starts again.</summary>
    public void Clear()
    {
        frames.Clear();
        samples.Clear();
        point = null;
        settle = null;
        starving = false;
        lateness = 0;
        delay = StartDelay;
    }

    /// <summary>A character that moves farther than <c>TeleportSpeed x ticks / 30 + TeleportSlack</c> world units between two snapshots is not interpolated (about 110 mph).</summary>
    public const float TeleportSpeed = 500, TeleportSlack = 20;

    /// <summary>
    /// <paramref name="older"/> with every character that jumped too far by <paramref name="newer"/> (<see cref="TeleportSpeed"/>) replaced by its newer
    /// entry, so it is drawn at its new place rather than sliding there; <paramref name="older"/> itself when nothing jumped.
    /// </summary>
    public static WorldSnapshot Unteleport(WorldSnapshot older, WorldSnapshot newer)
    {
        if (ReferenceEquals(older, newer)) return older;
        float limit = TeleportSpeed * Math.Max(1, newer.Tick - older.Tick) / (float)TicksPerSecond + TeleportSlack;
        var byId = new Dictionary<CharacterId, CharacterSnapshot>(newer.Characters.Count);
        foreach (var n in newer.Characters) byId[n.Id] = n;
        CharacterSnapshot[]? list = null;
        for (int i = 0; i < older.Characters.Count; i++)
        {
            var o = older.Characters[i];
            if (!byId.TryGetValue(o.Id, out var n) || System.Numerics.Vector3.Distance(o.Position, n.Position) <= limit) continue;
            list ??= [.. older.Characters];
            list[i] = n;
        }
        return list is null ? older : older with { Characters = list };
    }

    /// <summary>The newest tick the host has probably reached by <paramref name="nowUs"/>: the least-delayed sample carried forward at the rate.</summary>
    double Live(long nowUs)
    {
        double best = double.MinValue;
        foreach (var (tick, arrival) in samples) best = Math.Max(best, tick + (nowUs - arrival) / 1e6 * rate);
        return best;
    }
}
