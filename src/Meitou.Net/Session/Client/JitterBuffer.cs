using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// What a client shows this frame: the two snapshots around the presentation point, <see cref="Alpha"/> between them (animation) and
/// <see cref="MotionAlpha"/> (positions; above 1 while extrapolating, at most 2), and the presentation point <see cref="Tick"/> in host ticks.
/// <see cref="Older"/> may be a copy of the older snapshot in which teleported characters already stand where the newer one has them.
/// </summary>
public readonly record struct Presentation(WorldSnapshot Older, WorldSnapshot Newer, float Alpha, float MotionAlpha, double Tick);

/// <summary>
/// The client's presentation clock (docs/net.md, "Client and jitter buffer"). It runs in host ticks: assembled snapshots go in keyed by their tick and
/// arrival time, and each frame <see cref="Advance"/> moves the presentation point at the host's rate (<see cref="SetRate"/>: 30 x achieved speed, 0 while
/// paused or busy), a playback delay behind the live estimate. The delay adapts to the arrival jitter (up at once, down slowly), a starved buffer
/// extrapolates at most <see cref="ExtrapolationCap"/> (and one snapshot interval) and then holds, a point too far from its target snaps, and a
/// character that moved faster than <see cref="TeleportSpeed"/> is not interpolated. Pure: every time is passed in, nothing reads a clock.
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>Playback delay at the start, its bounds, in seconds.</summary>
    public const double StartDelay = 0.10, MinDelay = 0.05, MaxDelay = 0.5;

    /// <summary>Longest extrapolation past the newest snapshot, in seconds of host time.</summary>
    public const double ExtrapolationCap = 0.1;

    /// <summary>A presentation point further than this (seconds of host time, at least 30 ticks' worth) from its target jumps to it.</summary>
    public const double SnapThreshold = 0.5;

    /// <summary>Most the presentation rate is sped up or slowed down to reach its target, as a share of the rate.</summary>
    public const double Correction = 0.1;

    /// <summary>Arrivals the live estimate looks at, in seconds; the lateness peak decays with this time constant in seconds.</summary>
    public const double SampleWindow = 1.0, JitterDecay = 2.0;

    /// <summary>Delay added once per starvation, and how fast the delay may fall, in seconds per real second.</summary>
    public const double StarveStep = 0.02, DelayFall = 0.005;

    /// <summary>A character whose position moves faster than this (units per game second) plus <see cref="TeleportSlack"/> units between two snapshots is shown at the newer position.</summary>
    public const float TeleportSpeed = 500, TeleportSlack = 20;

    /// <summary>Host ticks per game second.</summary>
    public const double TicksPerSecond = 30;

    readonly List<(long Tick, WorldSnapshot Snapshot)> frames = [];
    readonly List<(long Tick, long ArrivalUs)> samples = [];
    double rate = TicksPerSecond, runningRate = TicksPerSecond, jitter, delay = StartDelay, slowedFrom;
    int sendRate = 20;
    double? position;
    bool starving, holding;
    WorldSnapshot? cachedOlder, cachedNewer, cachedAdjusted;

    /// <summary>The playback delay now, in seconds and whole milliseconds.</summary>
    public double DelaySeconds => delay;
    public int DelayMs => (int)Math.Round(delay * 1000);

    /// <summary>How many times the buffer ran dry while the host's clock ran (one per episode).</summary>
    public int Starved { get; private set; }

    /// <summary>The presentation point of the last <see cref="Advance"/> in host ticks; null before the first or after <see cref="Clear"/>.</summary>
    public double? Position => position;

    /// <summary>The presentation rate in host ticks per real second.</summary>
    public double Rate => rate;

    /// <summary>Snapshots held.</summary>
    public int Count => frames.Count;

    /// <summary>
    /// Takes an assembled snapshot of host tick <paramref name="tick"/> that arrived at <paramref name="arrivalUs"/>. A snapshot of a tick already held replaces
    /// it (a rebuild with newer views); only a new newest tick counts as an arrival for the live estimate and the lateness.
    /// </summary>
    public void Add(long tick, long arrivalUs, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int i = frames.Count;
        while (i > 0 && frames[i - 1].Tick > tick) i--;
        if (i > 0 && frames[i - 1].Tick == tick)
        {
            frames[i - 1] = (tick, snapshot);
            return;
        }
        frames.Insert(i, (tick, snapshot));
        if (i != frames.Count - 1) return;
        Prune(arrivalUs);
        if (rate > 0 && samples.Count > 0) jitter = Math.Max(jitter, (Live(arrivalUs) - tick) / rate);
        samples.Add((tick, arrivalUs));
    }

    /// <summary>
    /// The host's rate in ticks per real second (0 while paused or busy) and its snapshot send rate. A change forgets the arrival samples (they were
    /// spaced at the old rate); a resume or speed-up holds the presentation point until the delay is built up at the new rate.
    /// </summary>
    public void SetRate(double ticksPerSecond, int sendRate)
    {
        if (!(ticksPerSecond >= 0) || double.IsInfinity(ticksPerSecond)) ticksPerSecond = 0;
        this.sendRate = Math.Max(1, sendRate);
        if (ticksPerSecond == rate) return;
        // A resume or a speed-up needs more ticks of delay than the point has: it holds until the target passes it (a starved buffer otherwise).
        if (ticksPerSecond > rate && position is not null) holding = true;
        // A slow-down leaves the point more ticks behind than the new delay wants: it catches up instead of snapping.
        if (ticksPerSecond < rate) slowedFrom = Math.Max(slowedFrom, rate);
        rate = ticksPerSecond;
        if (rate > 0) runningRate = rate;
        samples.Clear();
    }

    /// <summary>
    /// Moves the presentation point to <paramref name="nowUs"/> (<paramref name="realSeconds"/> since the last call) and returns the snapshots to show;
    /// null when none is held.
    /// </summary>
    public Presentation? Advance(long nowUs, double realSeconds)
    {
        if (!(realSeconds >= 0) || double.IsInfinity(realSeconds)) realSeconds = 0;
        if (frames.Count == 0) return null;
        Prune(nowUs);
        long newest = frames[^1].Tick;

        jitter *= Math.Exp(-realSeconds / JitterDecay);
        double wanted = Math.Clamp(1.25 / sendRate + jitter + 0.01, MinDelay, MaxDelay);
        delay = wanted >= delay ? wanted : Math.Max(wanted, delay - DelayFall * realSeconds);

        double target = Live(nowUs) - delay * rate;
        double p;
        if (position is double near && Math.Abs(target - near) < 1) slowedFrom = 0;
        if (position is not double at || Math.Abs(target - at) > SnapThreshold * Math.Max(Math.Max(rate, slowedFrom), TicksPerSecond))
        {
            p = target;
            holding = false;
        }
        else if (rate > 0)
        {
            p = at;
            if (!holding || p <= target)
            {
                holding = false;
                p += realSeconds * rate * (1 + Math.Clamp(0.5 * (target - p) / rate, -Correction, Correction));
            }
        }
        else p = at < newest ? Math.Min(at + realSeconds * runningRate, newest) : at;
        double gap = frames.Count > 1 ? newest - frames[^2].Tick : double.PositiveInfinity;
        p = Math.Min(p, newest + Math.Min(ExtrapolationCap * rate, gap));
        position = p;

        // Frames before the one at or below the point are not needed again.
        while (frames.Count > 2 && frames[1].Tick <= p) frames.RemoveAt(0);

        if (frames[0].Tick > p)
        {
            starving = false;
            var oldest = frames[0].Snapshot;
            return new Presentation(oldest, oldest, 0, 0, p);
        }
        int s0 = frames.Count > 1 && frames[1].Tick <= p ? 1 : 0;
        if (s0 + 1 < frames.Count)
        {
            starving = false;
            var (t0, older) = frames[s0];
            var (t1, newer) = frames[s0 + 1];
            float alpha = (float)((p - t0) / (t1 - t0));
            return new Presentation(Adjusted(older, newer, t1 - t0), newer, alpha, alpha, p);
        }

        // At or past the newest snapshot: show the last pair, extrapolating by the motion alpha.
        if (rate > 0 && p > newest)
        {
            if (!starving)
            {
                Starved++;
                delay = Math.Min(delay + StarveStep, MaxDelay);
            }
            starving = true;
        }
        else starving = false;
        var last = frames[^1].Snapshot;
        if (frames.Count == 1) return new Presentation(last, last, 1, 1, p);
        long before = frames[^2].Tick;
        float motion = (float)Math.Min(2, 1 + (p - newest) / (newest - before));
        return new Presentation(Adjusted(frames[^2].Snapshot, last, newest - before), last, 1, motion, p);
    }

    /// <summary>Forgets every snapshot, the arrivals and the presentation point (a new world epoch); the delay and the lateness peak stay (the link is the same).</summary>
    public void Clear()
    {
        frames.Clear();
        samples.Clear();
        position = null;
        starving = holding = false;
        slowedFrom = 0;
        cachedOlder = cachedNewer = cachedAdjusted = null;
    }

    /// <summary>The live estimate: the host tick now, from the least delayed arrival in the window; the newest tick when paused or without arrivals.</summary>
    double Live(long nowUs)
    {
        double live = frames.Count > 0 ? frames[^1].Tick : 0;
        if (rate <= 0 || samples.Count == 0) return live;
        live = double.NegativeInfinity;
        foreach (var (tick, arrival) in samples) live = Math.Max(live, tick + (nowUs - arrival) / 1e6 * rate);
        return live;
    }

    void Prune(long nowUs)
    {
        long oldest = nowUs - (long)(SampleWindow * 1e6);
        int stale = 0;
        while (stale < samples.Count && samples[stale].ArrivalUs < oldest) stale++;
        samples.RemoveRange(0, stale);
    }

    /// <summary>The older snapshot with every teleported character (moved faster than <see cref="TeleportSpeed"/>) replaced by its newer entry; cached per pair.</summary>
    WorldSnapshot Adjusted(WorldSnapshot older, WorldSnapshot newer, long ticks)
    {
        if (ReferenceEquals(older, cachedOlder) && ReferenceEquals(newer, cachedNewer)) return cachedAdjusted!;
        cachedOlder = older;
        cachedNewer = newer;
        cachedAdjusted = Snap(older, newer, ticks);
        return cachedAdjusted;
    }

    static WorldSnapshot Snap(WorldSnapshot older, WorldSnapshot newer, long ticks)
    {
        if (ReferenceEquals(older, newer) || older.Characters.Count == 0 || newer.Characters.Count == 0) return older;
        float limit = (float)(TeleportSpeed * Math.Max(1, ticks) / TicksPerSecond + TeleportSlack);
        var byId = new Dictionary<CharacterId, CharacterSnapshot>(newer.Characters.Count);
        foreach (var c in newer.Characters) byId[c.Id] = c;
        CharacterSnapshot[]? copy = null;
        for (int i = 0; i < older.Characters.Count; i++)
        {
            var c = older.Characters[i];
            if (!byId.TryGetValue(c.Id, out var next) || Vector3.DistanceSquared(c.Position, next.Position) <= limit * limit) continue;
            copy ??= [.. older.Characters];
            copy[i] = next;
        }
        return copy is null ? older : older with { Characters = copy };
    }
}
