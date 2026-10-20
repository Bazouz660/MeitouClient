using Meitou.Net;
using Meitou.Net.Session;

namespace Meitou.Game;

// The game ↔ interface contract of a multiplayer session (docs/meitou-ui.md "Multiplayer"): the game host implements IMultiplayerView and handles every
// MultiplayerRequest; the interface reads the view and raises the requests. In single-player PlayerInterface.Net is null and none of it runs.

/// <summary>Which side of a session this game is; <see cref="None"/> in single-player.</summary>
public enum NetRole { None, Host, Client }

/// <summary>A client's lifecycle (connect, handshake, world load, play, ended); a host is <see cref="Playing"/> from the moment it hosts.</summary>
public enum NetPhase { Idle, Connecting, Joining, Loading, Playing, Ended }

/// <summary>What a <see cref="MultiplayerEvent"/> reports: a player joining or leaving (left, timed out, kicked, banned), a clock change, a chat line.</summary>
public enum MultiplayerEventKind { Joined, Left, TimedOut, Kicked, Banned, Paused, Resumed, Speed, Chat }

/// <summary>
/// One thing that happened in the session, for the chat history and the banners. <paramref name="Name"/> is the actor's name at the time (a player who left is
/// gone from the roster's lists), <paramref name="Text"/> the chat line or the detail (a kick's reason), <paramref name="Speed"/> the new speed of a Speed event.
/// </summary>
public readonly record struct MultiplayerEvent(MultiplayerEventKind Kind, int Player, string Name, string Text = "", double Speed = 0);

/// <summary>The connection as a client sees it: round trip, age of the newest snapshot on show (starving above 0.5 s) and packet loss (0..1).</summary>
public readonly record struct NetQuality(int PingMs, double SnapshotAgeSeconds, float Loss);

/// <summary>The session as the interface reads it; implemented by the game host (<c>NetSessionView</c>), read once a frame on the frame thread.</summary>
public interface IMultiplayerView
{
    NetRole Role { get; }
    NetPhase Phase { get; }
    /// <summary>This game's player number: 0 for the host.</summary>
    int LocalPlayer { get; }
    /// <summary>The roster, host first; lists show the entries whose state is not <see cref="NetPlayerState.Away"/> (kept so tags can still name a player who left).</summary>
    IReadOnlyList<RosterEntry> Players { get; }
    NetRules Rules { get; }
    NetQuality Quality { get; }
    /// <summary>Why the session ended; null while it runs.</summary>
    NetDisconnect? Ended { get; }
    /// <summary>Who paused the shared clock; null when it runs or nobody is known.</summary>
    int? PausedBy { get; }
    /// <summary>Whether <paramref name="player"/> may pause and change the speed: <see cref="ClockPolicy.AnyPlayer"/>, or the host.</summary>
    bool CanControlTime(int player);
    /// <summary>The events since the last call; the interface drains it once a frame.</summary>
    IReadOnlyList<MultiplayerEvent> TakeEvents();
    void SendChat(string text);
    /// <summary>Host only (no-op on a client).</summary>
    void Kick(int player, string reason);
    /// <summary>Host only (no-op on a client): the player's id is refused for the rest of this hosting.</summary>
    void Ban(int player);
}

/// <summary>What the host chose in the Multiplayer window; <paramref name="Password"/> "" is none.</summary>
public sealed record HostSettings(int Port, int MaxPlayers, string Password, bool ClientsControlTime, bool AllowCheats);

/// <summary>What the interface asks the game host to do (<c>PlayerInterface.MultiplayerRequested</c>).</summary>
public abstract record MultiplayerRequest;
/// <summary>Host a new game from <paramref name="NewGameStart"/>, a save from <paramref name="SaveFolder"/>, or with both null the running game.</summary>
public sealed record HostGame(HostSettings Settings, string? NewGameStart, string? SaveFolder) : MultiplayerRequest;
public sealed record JoinGame(string Host, int Port, string Password) : MultiplayerRequest;
/// <summary>Stop a join still connecting or loading.</summary>
public sealed record CancelJoin() : MultiplayerRequest;
/// <summary>Disconnect every client; the host's game goes on single-player.</summary>
public sealed record StopHosting() : MultiplayerRequest;
/// <summary>Leave the host's game: back to the title, or with <paramref name="Quit"/> exit the application.</summary>
public sealed record LeaveGame(bool Quit) : MultiplayerRequest;

/// <summary>The interface's limits and defaults, from the protocol's (<see cref="NetProtocol"/>).</summary>
public static class MultiplayerDefaults
{
    public const int Port = NetProtocol.DefaultPort, MaxPlayers = 4, MaxPlayersLimit = NetProtocol.MaxPlayers, NameLength = NetProtocol.MaxNameLength,
        ChatLength = NetProtocol.MaxChatLength, PasswordLength = NetProtocol.MaxPasswordLength;
}
