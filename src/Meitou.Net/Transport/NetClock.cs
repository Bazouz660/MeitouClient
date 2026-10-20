using System.Diagnostics;

namespace Meitou.Net.Transport;

/// <summary>Real time for the net layer in microseconds from an arbitrary origin; host and client loops and the loopback network read it.</summary>
public interface INetClock
{
    long NowMicroseconds { get; }
}

/// <summary>Monotonic wall time from <see cref="Stopwatch"/>, starting at 0 when constructed.</summary>
public sealed class StopwatchNetClock : INetClock
{
    readonly long origin = Stopwatch.GetTimestamp();

    public long NowMicroseconds => Stopwatch.GetElapsedTime(origin).Ticks / TimeSpan.TicksPerMicrosecond;
}

/// <summary>A clock tests move by hand, so loopback delivery and session timing are deterministic.</summary>
public sealed class ManualNetClock(long start = 0) : INetClock
{
    public long NowMicroseconds { get; set; } = start;

    public void Advance(TimeSpan by) => NowMicroseconds += by.Ticks / TimeSpan.TicksPerMicrosecond;
}
