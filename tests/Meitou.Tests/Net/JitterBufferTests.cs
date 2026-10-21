using System.Numerics;
using Meitou.Net.Session;
using Meitou.Simulation;

namespace Meitou.Tests.Net;

/// <summary>The client's presentation clock (docs/net.md, "Client and jitter buffer"), driven by a simulated 20 Hz host stream and a 60 fps client.</summary>
public class JitterBufferTests
{
    /// <summary>A host ticking at <see cref="HostRate"/> and sending its tick every 50 ms, a link, and a client presenting every 1/60 s.</summary>
    sealed class Run(int seed = 1)
    {
        const long FrameUs = 16_667, SendUs = 50_000;
        public readonly JitterBuffer Buffer = new();
        public readonly List<(long NowUs, Presentation Shown)> Frames = [];
        readonly List<(long ArrivalUs, long Tick)> inFlight = [];
        readonly Random random = new(seed);
        long newestDelivered = -1, nextSendUs;
        double hostTicks;
        public long NowUs;
        public double HostRate = 30;
        public int LatencyUs = 40_000, JitterUs;
        public bool Outage;

        public long HostTick => (long)Math.Floor(hostTicks);

        public void Step(double seconds)
        {
            for (long end = NowUs + (long)(seconds * 1e6); NowUs < end;)
            {
                NowUs += FrameUs;
                while (nextSendUs <= NowUs)
                {
                    hostTicks += HostRate * SendUs / 1e6;
                    if (!Outage) inFlight.Add((nextSendUs + LatencyUs + random.Next(JitterUs + 1), HostTick));
                    nextSendUs += SendUs;
                }
                inFlight.Sort((a, b) => a.ArrivalUs.CompareTo(b.ArrivalUs));
                while (inFlight.Count > 0 && inFlight[0].ArrivalUs <= NowUs)
                {
                    long tick = inFlight[0].Tick;
                    inFlight.RemoveAt(0);
                    if (tick < newestDelivered) continue;   // the mirror assembles only its newest Seq
                    newestDelivered = tick;
                    Buffer.Add(tick, NowUs, new WorldSnapshot(tick, []));
                }
                if (Buffer.Advance(NowUs, FrameUs / 1e6) is { } shown) Frames.Add((NowUs, shown));
            }
        }

        public double TickAt(int frame) => Frames[frame].Shown.Tick;
    }

    /// <summary>One client frame, in seconds.</summary>
    const double Step = 0.016667;

    static void AssertMonotonic(Run run, int from)
    {
        for (int i = Math.Max(1, from); i < run.Frames.Count; i++)
            Assert.True(run.TickAt(i) >= run.TickAt(i - 1), $"the presentation went back at frame {i}: {run.TickAt(i - 1)} -> {run.TickAt(i)}");
    }

    [Fact]
    public void A_steady_stream_plays_at_the_host_rate_a_delay_behind_without_starving()
    {
        var run = new Run();
        run.Step(6);
        Assert.Equal(0, run.Buffer.Starved);
        AssertMonotonic(run, 0);
        int start = run.Frames.FindIndex(f => f.NowUs >= 3_000_000);
        double rate = (run.TickAt(run.Frames.Count - 1) - run.TickAt(start)) / ((run.Frames[^1].NowUs - run.Frames[start].NowUs) / 1e6);
        Assert.InRange(rate, 29, 31);
        Assert.InRange(run.Buffer.DelayMs, 70, 110);
        foreach (var (_, shown) in run.Frames.Skip(start))
        {
            Assert.InRange(shown.Alpha, 0f, 1f);
            Assert.Equal(shown.Alpha, shown.MotionAlpha);
            Assert.True(shown.Older.Tick <= shown.Tick && shown.Tick <= shown.Newer.Tick);
        }
        // Behind the host by about the delay plus the latency.
        double behind = run.HostTick - run.TickAt(run.Frames.Count - 1);
        Assert.InRange(behind, 2, 8);
    }

    [Fact]
    public void Jitter_grows_the_delay_at_once_and_calm_shrinks_it_slowly()
    {
        var run = new Run(seed: 7) { JitterUs = 120_000 };
        run.Step(2);
        int learning = run.Buffer.Starved;   // the delay starts below the jitter
        run.Step(4);
        int jittery = run.Buffer.DelayMs;
        Assert.True(jittery >= 140, $"delay {jittery} ms under 120 ms of jitter");
        Assert.True(run.Buffer.Starved - learning <= 2, $"starved {learning} times while learning, {run.Buffer.Starved - learning} after");
        AssertMonotonic(run, run.Frames.FindIndex(f => f.NowUs >= 500_000));

        run.JitterUs = 0;
        run.Step(2);
        int calm = run.Buffer.DelayMs;
        Assert.InRange(jittery - calm, 0, 2 * 5 + 1);   // falls at most 5 ms per second
        run.Step(30);
        Assert.InRange(run.Buffer.DelayMs, 50, 110);
        AssertMonotonic(run, run.Frames.FindIndex(f => f.NowUs >= 500_000));
    }

    [Fact]
    public void A_starved_buffer_extrapolates_to_the_cap_holds_and_recovers()
    {
        var run = new Run();
        run.Step(3);
        double delayBefore = run.Buffer.DelaySeconds;
        int before = run.Frames.Count;
        run.Outage = true;
        run.Step(0.6);
        long newest = run.Frames[^1].Shown.Newer.Tick;
        long previous = run.Frames[^1].Shown.Older.Tick;
        double cap = newest + Math.Min(JitterBuffer.ExtrapolationCap * 30, newest - previous);
        foreach (var (_, shown) in run.Frames.Skip(before))
        {
            Assert.InRange(shown.MotionAlpha, 0f, 2f);
            Assert.True(shown.Tick <= cap + 1e-9, $"extrapolated to {shown.Tick}, cap {cap}");
        }
        Assert.Equal(cap, run.TickAt(run.Frames.Count - 1), 6);
        Assert.Equal(run.TickAt(run.Frames.Count - 1), run.TickAt(run.Frames.Count - 10));   // held
        Assert.True(run.Frames[^1].Shown.MotionAlpha > 1);
        Assert.Equal(1, run.Buffer.Starved);   // one episode
        Assert.True(run.Buffer.DelaySeconds >= delayBefore + JitterBuffer.StarveStep - 0.006);

        run.Outage = false;
        run.Step(3);
        Assert.Equal(1, run.Buffer.Starved);
        var last = run.Frames[^1].Shown;
        Assert.True(last.MotionAlpha <= 1);
        Assert.InRange(run.HostTick - last.Tick, 2, 9);
        AssertMonotonic(run, run.Frames.Count - 60);
    }

    [Fact]
    public void A_teleport_is_not_interpolated_and_a_clear_forgets_everything()
    {
        var walker = new CharacterId(1, 1);
        var jumper = new CharacterId(2, 1);
        static CharacterSnapshot At(CharacterId id, float x) => new(id, null, new Vector3(x, 0, 0), 0, []);
        var older = new WorldSnapshot(10, [At(walker, 0), At(jumper, 0)]);
        var newer = new WorldSnapshot(12, [At(walker, 30), At(jumper, 5000)]);
        var buffer = new JitterBuffer();
        buffer.Add(10, 0, older);
        buffer.Add(12, 50_000, newer);
        // Find a frame between the two snapshots.
        Presentation between = default;
        for (long now = 50_000; now < 2_000_000 && between.Newer is null; now += 16_667)
            if (buffer.Advance(now, 0.016667) is { } p && p.Tick > 10 && p.Tick < 12) between = p;
        Assert.NotNull(between.Newer);
        Assert.Same(newer, between.Newer);
        Assert.NotSame(older, between.Older);
        Assert.Equal(new Vector3(0, 0, 0), between.Older.Characters[0].Position);      // walked 30 units in 2 ticks: interpolated
        Assert.Equal(new Vector3(5000, 0, 0), between.Older.Characters[1].Position);   // jumped: shown at the newer position
        Assert.Same(between.Older, buffer.Advance(1_000_000 + 1, 0)!.Value.Older);    // cached per pair

        buffer.Clear();
        Assert.Null(buffer.Position);
        Assert.Equal(0, buffer.Count);
        Assert.Null(buffer.Advance(3_000_000, 0.016));
    }

    [Fact]
    public void A_pause_finishes_the_buffered_motion_holds_and_resumes_without_starving()
    {
        var run = new Run();
        run.Step(3);
        run.HostRate = 0;
        run.Buffer.SetRate(0, 20);
        long pausedAt = run.HostTick;
        run.Step(2);
        var held = run.Frames[^1].Shown;
        Assert.Equal(pausedAt, held.Tick);
        Assert.Equal(1f, held.Alpha);
        Assert.Equal(1f, held.MotionAlpha);
        Assert.Equal(pausedAt, run.TickAt(run.Frames.Count - 30));

        run.HostRate = 30;
        run.Buffer.SetRate(30, 20);
        run.Step(3);
        Assert.Equal(0, run.Buffer.Starved);
        AssertMonotonic(run, 0);
        Assert.InRange(run.HostTick - run.TickAt(run.Frames.Count - 1), 2, 8);
    }

    [Fact]
    public void A_speed_change_moves_the_rate_without_a_jump_or_starving()
    {
        var run = new Run();
        run.Step(3);
        run.HostRate = 150;
        run.Buffer.SetRate(150, 20);
        int change = run.Frames.Count;
        run.Step(6);
        Assert.Equal(0, run.Buffer.Starved);
        AssertMonotonic(run, 0);
        for (int i = change; i < run.Frames.Count; i++)
            Assert.True(run.TickAt(i) - run.TickAt(i - 1) <= 150 * 1.1 * Step + 1e-6, $"jumped at frame {i}");
        int start = run.Frames.FindIndex(f => f.NowUs >= 7_000_000);
        double rate = (run.TickAt(run.Frames.Count - 1) - run.TickAt(start)) / ((run.Frames[^1].NowUs - run.Frames[start].NowUs) / 1e6);
        Assert.InRange(rate, 145, 155);

        run.HostRate = 30;
        run.Buffer.SetRate(30, 20);
        change = run.Frames.Count;
        run.Step(4);
        Assert.Equal(0, run.Buffer.Starved);
        for (int i = change; i < run.Frames.Count; i++)
            Assert.True(run.TickAt(i) >= run.TickAt(i - 1) && run.TickAt(i) - run.TickAt(i - 1) <= 30 * 1.1 * Step + 1e-6, $"jumped at frame {i}");
    }
}
