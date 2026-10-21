namespace Meitou.Net.Session;

/// <summary>
/// How a <see cref="NetHost"/> runs (docs/net.md, "Host"). <see cref="MaxPlayers"/> counts the host and is clamped to 2..<see cref="NetProtocol.MaxPlayers"/>;
/// <see cref="SendRate"/> is the snapshot rate per client while the game runs (clamped to 1..60), <see cref="PausedSendRate"/> while it is paused;
/// <see cref="MaxBytesPerSecond"/> is each client's snapshot budget; spatial events reach a client within <see cref="EventRadius"/> of its camera.
/// </summary>
public sealed record NetHostOptions
{
    public int Port { get; init; } = NetProtocol.DefaultPort;
    public int MaxPlayers { get; init; } = 4;
    public string? Password { get; init; }
    public string HostName { get; init; } = "Meitou";
    public ClockPolicy ClockPolicy { get; init; } = ClockPolicy.AnyPlayer;
    public int SendRate { get; init; } = 20;
    public int PausedSendRate { get; init; } = 5;
    public int MaxBytesPerSecond { get; init; } = 262_144;
    public float EventRadius { get; init; } = 10_000;
    /// <summary>Accept clients of another build (the data set and animation library must still match).</summary>
    public bool AllowBuildMismatch { get; init; }
    /// <summary>How long a connected client may take to send its <c>Hello</c>.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a client may take from <c>WorldReset</c> to <c>Ready</c>.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(180);
}

/// <summary>One peer's link figures for the stats line and the player list.</summary>
public readonly record struct NetPeerStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond, int LastSnapshotBytes);

/// <summary>One player as the host knows it (the host first, as player 0).</summary>
public sealed record NetPlayerInfo(int Number, Guid Id, string Name, NetPlayerState State, int Colour, NetPeerStats Stats);
