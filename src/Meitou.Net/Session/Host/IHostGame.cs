using Meitou.Simulation;

namespace Meitou.Net.Session;

/// <summary>
/// What a <see cref="NetHost"/> asks of the game that hosts it (docs/net.md, "Host"): implemented by the game's host (and by test stubs), so the net layer
/// never references the game. Read on the frame thread only.
/// </summary>
public interface IHostGame
{
    /// <summary>How the command gate judges remote commands: <see cref="GateOptions.Cheats"/> (debug commands allowed) and the host's placement check.</summary>
    GateOptions Gate { get; }

    /// <summary>The simulation rules clients use in place of their own; compared by value every <see cref="NetHost.EndFrame"/>, a change goes out as <c>Rules</c>.</summary>
    SessionRules Rules { get; }

    /// <summary>The weather clients show; null: none. Sent in every <c>WorldReset</c>, when its <see cref="WeatherSync.Revision"/> moves and every 60 s.</summary>
    WeatherSync? Weather();
}
