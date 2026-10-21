namespace Meitou.Net.Session;

/// <summary>
/// How a <see cref="NetHost"/> runs (docs/net.md, "Host"). Out-of-range values are clamped by the host: <see cref="MaxPlayers"/> to 2..<see cref="NetProtocol.MaxPlayers"/>,
/// <see cref="SendRate"/> to 1..60, <see cref="PausedSendRate"/> to 1..<see cref="SendRate"/>.
/// </summary>
public sealed record NetHostOptions
{
    /// <summary>The UDP port to listen on (0: any free port; <see cref="NetRules.Port"/> then carries the bound one).</summary>
    public int Port { get; init; } = NetProtocol.DefaultPort;

    /// <summary>Most players connected at once, the host included.</summary>
    public int MaxPlayers { get; init; } = 4;

    /// <summary>Null or empty: no password.</summary>
    public string? Password { get; init; }

    /// <summary>The name a joining client sees in the <c>Challenge</c>.</summary>
    public string HostName { get; init; } = "Meitou";

    /// <summary>Who may pause and change the speed.</summary>
    public ClockPolicy ClockPolicy { get; init; } = ClockPolicy.AnyPlayer;

    /// <summary>Snapshot sends per second per client while the clock runs, and while it is paused.</summary>
    public int SendRate { get; init; } = 20;
    public int PausedSendRate { get; init; } = 5;

    /// <summary>The snapshot byte budget per client per second (<see cref="Replication.SnapshotEncoderOptions.BytesPerSecond"/>).</summary>
    public int MaxBytesPerSecond { get; init; } = 262_144;

    /// <summary>World units round a client's eye within which it gets sounds, blood and floaters (besides those naming a character it is sent; engine choice).</summary>
    public float EventRadius { get; init; } = 10_000;

    /// <summary>Accept clients of another build (the data set and animation library must still match).</summary>
    public bool AllowBuildMismatch { get; init; }

    /// <summary>How long a connection may take to answer the <c>Challenge</c>, and a joined client to send <c>Ready</c> after its <c>WorldReset</c>.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(180);
}

/// <summary>One client's link figures: round trip, unreliable loss, payload bytes per second each way (measured once a second) and the last snapshot send's bytes.</summary>
public readonly record struct NetPeerStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond, int LastSnapshotBytes);

/// <summary>One player as the host sees it: number, roster id, name, state, palette colour and link figures (default for the host and away players).</summary>
public sealed record NetPlayerInfo(int Number, Guid Id, string Name, NetPlayerState State, int Colour, NetPeerStats Stats);
