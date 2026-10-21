namespace Meitou.Net.Transport.Steam;

/// <summary>Steam's old P2P send types (<c>EP2PSend</c>); docs/net.md "Steam transport".</summary>
public enum SteamSendType
{
    /// <summary>May be lost; at most about 1200 bytes.</summary>
    Unreliable = 0,
    /// <summary>As <see cref="Unreliable"/>, dropped when it cannot leave at once (no session yet, queue full).</summary>
    UnreliableNoDelay = 1,
    /// <summary>Delivered, ordered per channel, up to 1 MB.</summary>
    Reliable = 2,
    ReliableWithBuffering = 3,
}

/// <summary>What <see cref="ISteamP2P.SessionState"/> reports about the session with one user (<c>P2PSessionState_t</c>).</summary>
public readonly record struct SteamSessionState(bool Active, bool Connecting, byte Error, bool UsingRelay, int BytesQueued, int PacketsQueued);

/// <summary>
/// The part of Steam the multiplayer layer uses: P2P packets between Steam users (<c>ISteamNetworking</c>), the local user, friends and rich presence
/// (<c>ISteamFriends</c>). Implemented over Kenshi's own <c>steam_api64.dll</c> by <see cref="SteamNative"/> and in memory by the tests. Every call is made
/// from the frame thread; the events are raised only inside <see cref="RunCallbacks"/>, on the calling thread.
/// </summary>
public interface ISteamP2P
{
    /// <summary>The local user's 64-bit Steam id.</summary>
    ulong LocalId { get; }

    /// <summary>The local user's persona name.</summary>
    string PersonaName { get; }

    /// <summary>Dispatches Steam's queued callbacks (<see cref="SessionRequested"/>, <see cref="SessionFailed"/>, <see cref="JoinRequested"/>).</summary>
    void RunCallbacks();

    /// <summary>Sends one packet on P2P <paramref name="channel"/>; false when Steam refuses it (too big, no session for a no-delay send, bad id).</summary>
    bool Send(ulong remote, ReadOnlySpan<byte> data, SteamSendType type, int channel);

    /// <summary>Whether a packet waits on <paramref name="channel"/>, and its size.</summary>
    bool IsPacketAvailable(int channel, out int size);

    /// <summary>Reads the next packet of <paramref name="channel"/> into <paramref name="buffer"/> (at least the available size); false when none.</summary>
    bool ReadPacket(int channel, Span<byte> buffer, out int size, out ulong remote);

    /// <summary>Accepts the session <paramref name="remote"/> asked for (<see cref="SessionRequested"/>); without it Steam drops the user's packets.</summary>
    bool AcceptSession(ulong remote);

    /// <summary>Ends the session with <paramref name="remote"/> (the native one waits a moment first so packets already queued still leave).</summary>
    void CloseSession(ulong remote);

    /// <summary>The session with <paramref name="remote"/>; null when there is none.</summary>
    SteamSessionState? SessionState(ulong remote);

    /// <summary>A user sent the first packet of a new session (<c>P2PSessionRequest_t</c>, 1202).</summary>
    event Action<ulong>? SessionRequested;

    /// <summary>A session could not be made or broke (<c>P2PSessionConnectFail_t</c>, 1203): the user and <c>EP2PSessionError</c>.</summary>
    event Action<ulong, byte>? SessionFailed;

    /// <summary>The player accepted a friend's "Join game" in Steam (<c>GameRichPresenceJoinRequested_t</c>, 337): the friend and the connect string.</summary>
    event Action<ulong, string>? JoinRequested;

    /// <summary>The user's immediate friends (<c>k_EFriendFlagImmediate</c>).</summary>
    IReadOnlyList<ulong> Friends();

    string FriendName(ulong friend);

    /// <summary>The app id of the game <paramref name="friend"/> plays, 0 when none.</summary>
    uint FriendAppId(ulong friend);

    /// <summary>The friend's rich presence value for <paramref name="key"/>; "" when unset.</summary>
    string FriendRichPresence(ulong friend, string key);

    /// <summary>Asks Steam to fetch the friend's rich presence (it arrives later; friends in the same game get it pushed).</summary>
    void RequestFriendRichPresence(ulong friend);

    /// <summary>Sets (or with null or "" clears) one rich presence key of the local user.</summary>
    bool SetRichPresence(string key, string? value);

    void ClearRichPresence();
}
