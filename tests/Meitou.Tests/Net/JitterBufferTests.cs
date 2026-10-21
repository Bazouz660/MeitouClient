using System.Numerics;
using Meitou.Net.Session;
using Meitou.Simulation;

namespace Meitou.Tests.Net;

/// <summary>The client's presentation clock (docs/net.md, "Client and jitter buffer"): pairing and alphas under a steady stream, jitter, loss, pause and speed changes.</summary>
public class JitterBufferTests
{
    const double Frame = 1 / 60.0;

    /// <summary>A host sending 20 snapshots a second at <paramref name="rate"/> ticks per second, its arrivals delayed by 30 ms plus up to <paramref name="jitterMs"/>.</summary>
    sealed class Stream(JitterBuffer buffer, int seed, double jitterMs = 0, double rate = 30)
    {
        readonly Random random = new(seed);
        readonly SortedDictionary<long, (long Tick, WorldSnapshot Snapshot)> inFlight = [];
        long nextSendUs, sent;
        public long NowUs;
        public double Rate = rate;
        public double HostTick;
        public bool Silent;

        public List<Presentation> Shown { get; } = [];

        /// <summary>Runs <paramref name="seconds"/> of real time in 60 Hz client frames.</summary>
        public void Run(double seconds)
        {
            for (int i = 0; i < (int)Math.Round(seconds / Frame); i++)
            {
                NowUs += (long)(Frame * 1e6);
                HostTick += Frame * Rate;
                while (nextSendUs <= NowUs)
                {
                    if (!Silent)
                    {
                        long tick = (long)Math.Floor(HostTick);
                        long arrival = nextSendUs + 30_000 + (long)(random.NextDouble() * jitterMs * 1000);
                        inFlight[arrival * 1000 + sent++ % 1000] = (tick, new WorldSnapshot(tick, []));
                    }
                    nextSendUs += 50_000;
                }
                foreach (var key in inFlight.Keys.Where(k => k / 1000 <= NowUs).ToList())
                {
                    buffer.Add(inFlight[key].Tick, key / 1000, inFlight[key].Snapshot);
                    inFlight.Remove(key);
                }
                if (buffer.Advance(NowUs, Frame) is { } p) Shown.Add(p);
            }
        }
    }

    static void AssertSmooth(IReadOnlyList<Presentation> shown, double rate)
    {
        for (int i = 1; i < shown.Count; i++)
        {
            Assert.InRange(shown[i].Alpha, 0f, 1f);
            Assert.InRange(shown[i].MotionAlpha, 0f, 2f);
            double step = shown[i].Tick - shown[i - 1].Tick;
            Assert.InRange(step, 0, rate * Frame * 1.11 + 1e-9);
        }
    }

    [Fact]
    public void A_steady_stream_plays_back_smoothly_a_little_behind_the_host()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 1);
        stream.Run(1);
        stream.Shown.Clear();
        stream.Run(5);

        AssertSmooth(stream.Shown, 30);
        Assert.Equal(0, buffer.Starved);
        Assert.All(stream.Shown, p => Assert.True(p.Older.Tick <= p.Tick && p.Tick <= p.Newer.Tick, $"{p.Older.Tick} {p.Tick} {p.Newer.Tick}"));
        double lag = stream.HostTick - stream.Shown[^1].Tick;
        Assert.InRange(lag, 2, 6);   // 30 ms of travel and about 0.1 s of delay at 30 ticks a second
        Assert.InRange(buffer.DelaySeconds, JitterBuffer.MinDelay, 0.11);
    }

    [Fact]
    public void Jitter_grows_the_delay_until_playback_stops_starving()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 2, jitterMs: 150);
        stream.Run(4);
        Assert.True(buffer.DelaySeconds > 0.15, $"delay {buffer.DelaySeconds}");
        int starved = buffer.Starved;
        stream.Shown.Clear();
        stream.Run(6);

        AssertSmooth(stream.Shown, 30);
        Assert.Equal(starved, buffer.Starved);
        Assert.All(stream.Shown, p => Assert.Equal(p.Alpha, p.MotionAlpha));
    }

    [Fact]
    public void Loss_extrapolates_at_most_the_cap_then_holds_and_recovers()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 3);
        stream.Run(2);
        double delay = buffer.DelaySeconds;
        Assert.Equal(0, buffer.Starved);
        stream.Silent = true;
        stream.Shown.Clear();
        stream.Run(1);

        var newest = stream.Shown[^1].Newer.Tick;
        Assert.All(stream.Shown, p => Assert.True(p.Tick <= newest + JitterBuffer.ExtrapolationCap * 30 + 1e-9));
        Assert.Contains(stream.Shown, p => p.MotionAlpha > 1);
        Assert.All(stream.Shown, p => Assert.InRange(p.MotionAlpha, 0f, 2f));
        Assert.Equal(stream.Shown[^1].Tick, stream.Shown[^2].Tick);   // held
        Assert.Equal(1, buffer.Starved);
        Assert.InRange(buffer.DelaySeconds, delay + JitterBuffer.StarveStep - JitterBuffer.ShrinkPerSecond - 1e-9, delay + JitterBuffer.StarveStep);

        stream.Silent = false;
        stream.Run(2);
        stream.Shown.Clear();
        stream.Run(2);
        AssertSmooth(stream.Shown, 30);
        Assert.Equal(1, buffer.Starved);
    }

    [Fact]
    public void A_jump_in_host_ticks_snaps_the_playback_point()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 4);
        stream.Run(2);
        stream.HostTick += 3000;   // a long host hitch
        stream.Run(0.3);
        Assert.InRange(stream.HostTick - stream.Shown[^1].Tick, 0, 10);
    }

    [Fact]
    public void A_character_that_jumps_is_not_interpolated()
    {
        CharacterSnapshot At(int slot, float x) => new(new CharacterId(slot, 0), null, new Vector3(x, 0, 0), 0, []);
        var older = new WorldSnapshot(10, [At(0, 0), At(1, 0)]);
        var newer = new WorldSnapshot(12, [At(0, 30), At(1, 5000)]);   // 2 ticks allow 500 x 2 / 30 + 20 = 53 units
        var shown = JitterBuffer.Unteleport(older, newer);
        Assert.Same(older.Characters[0], shown.Characters[0]);
        Assert.Same(newer.Characters[1], shown.Characters[1]);
        Assert.Same(older, JitterBuffer.Unteleport(older, new WorldSnapshot(12, [At(0, 30), At(1, 40)])));
    }

    [Fact]
    public void A_pause_plays_out_what_arrived_then_holds_without_starving()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 5);
        stream.Run(2);
        stream.Rate = 0;
        buffer.SetRate(0, 20);
        stream.Run(0.5);
        long newest = buffer.NewestTick;
        stream.Shown.Clear();
        stream.Run(1);

        Assert.All(stream.Shown, p => Assert.Equal(newest, p.Tick));
        Assert.All(stream.Shown, p => Assert.True(p.MotionAlpha <= 1));
        Assert.Equal(0, buffer.Starved);

        stream.Rate = 30;
        buffer.SetRate(30, 20);
        stream.Run(1);
        stream.Shown.Clear();
        stream.Run(2);
        AssertSmooth(stream.Shown, 30);
        Assert.True(stream.Shown[^1].Tick > newest);
    }

    [Fact]
    public void A_speed_change_plays_back_at_the_new_rate()
    {
        var buffer = new JitterBuffer();
        buffer.SetRate(30, 20);
        var stream = new Stream(buffer, 6);
        stream.Run(2);
        stream.Rate = 150;
        buffer.SetRate(150, 20);
        stream.Run(2);
        stream.Shown.Clear();
        stream.Run(2);

        AssertSmooth(stream.Shown, 150);
        double rate = (stream.Shown[^1].Tick - stream.Shown[0].Tick) / ((stream.Shown.Count - 1) * Frame);
        Assert.InRange(rate, 140, 160);
        Assert.Equal(0, buffer.Starved);   // the larger delay in ticks is built up by slowing down, which is not counted as starving
    }
}
