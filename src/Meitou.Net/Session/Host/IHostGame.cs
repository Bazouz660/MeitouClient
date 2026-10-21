using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// What a <see cref="NetHost"/> needs from the game that hosts it (docs/net.md, "Host"): declared here because the net layer never references the game,
/// implemented by the game host and by test stubs. Read on the frame thread only.
/// </summary>
public interface IHostGame
{
    /// <summary>The command gate's options: the lobby's cheats switch and the host's placement check. Read for every received command batch.</summary>
    GateOptions Gate { get; }

    /// <summary>The simulation rules clients use in place of their own; compared by value each <see cref="NetHost.EndFrame"/>, a change goes out as a <c>Rules</c> message.</summary>
    SessionRules Rules { get; }

    /// <summary>The weather state (null: none). Sent in <c>WorldReset</c>, when its <see cref="WeatherSync.Revision"/> changes and every 60 s; polled about once a second.</summary>
    WeatherSync? Weather();
}
