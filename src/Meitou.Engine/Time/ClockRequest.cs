namespace Meitou.Engine.Time;

/// <summary>What a clock request asks of the <see cref="SimulationClock"/>: the pause action, an explicit speed, or a step of the speed ladder.</summary>
public enum ClockRequestKind : byte { TogglePause, SetSpeed, Faster, Slower }

/// <summary>
/// One input to the world's clock (the pause and speed keys, the HUD speed buttons; docs/engine.md "Loop, input and camera"). Single-player applies it
/// to the <see cref="SimulationClock"/> at once; a net session hands it to its <see cref="IClockAuthority"/> instead (docs/net.md), which decides who may
/// change the clock and what the host's clock then does.
/// </summary>
public readonly record struct ClockRequest(ClockRequestKind Kind, double Speed = 0)
{
    /// <summary>The pause action: speed 0, or back to the last non-zero speed.</summary>
    public static ClockRequest TogglePause => new(ClockRequestKind.TogglePause);
    /// <summary>An explicit speed, one of 0, 1, 2 or 5 (<see cref="SimulationClock.SetSpeed"/>).</summary>
    public static ClockRequest Set(double speed) => new(ClockRequestKind.SetSpeed, speed);
    /// <summary>The next speed up (<see cref="SimulationClock.Faster"/>).</summary>
    public static ClockRequest Faster => new(ClockRequestKind.Faster);
    /// <summary>The next speed down (<see cref="SimulationClock.Slower"/>).</summary>
    public static ClockRequest Slower => new(ClockRequestKind.Slower);

    /// <summary>Does what the request asks to <paramref name="clock"/>, exactly as the pause and speed actions always did.</summary>
    public void ApplyTo(SimulationClock clock)
    {
        switch (Kind)
        {
            case ClockRequestKind.TogglePause: clock.TogglePause(); break;
            case ClockRequestKind.SetSpeed: clock.SetSpeed(Speed); break;
            case ClockRequestKind.Faster: clock.Faster(); break;
            case ClockRequestKind.Slower: clock.Slower(); break;
            default: throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "not a clock request");
        }
    }
}

/// <summary>Whoever decides the world's clock in a net session (the host, or the client that forwards to it); <see cref="WorldSession.RequestClock"/> sends every request here while one is set.</summary>
public interface IClockAuthority
{
    /// <summary>Handles the local player's request; the authority may refuse it, apply it to the host's clock, or forward it.</summary>
    void Request(ClockRequest request);
}
