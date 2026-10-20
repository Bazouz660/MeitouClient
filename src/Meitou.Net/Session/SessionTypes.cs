using Meitou.Data.World;

namespace Meitou.Net.Session;

/// <summary>Why a connection was refused or ended; carried in <see cref="NetDisconnect"/> and the <c>Reject</c> message (docs/net.md, "Protocol").</summary>
public enum RejectCode : byte
{
    /// <summary>Another protocol version.</summary>
    Protocol,

    /// <summary>Another game build.</summary>
    Build,

    /// <summary>Another data set (mods, load order) or animation library.</summary>
    Data,

    /// <summary>Wrong or missing password.</summary>
    Password,

    /// <summary>No free player slot.</summary>
    Full,

    /// <summary>The address or player id is banned for this session.</summary>
    Banned,

    /// <summary>The player id is already connected, or equals the host's.</summary>
    Duplicate,

    /// <summary>Kicked by the host.</summary>
    Kicked,

    /// <summary>The host stopped hosting or closed the game.</summary>
    HostClosed,

    /// <summary>The connection timed out.</summary>
    Timeout,

    /// <summary>The peer sent bytes the protocol does not allow.</summary>
    ProtocolError,

    /// <summary>No player number is left to give.</summary>
    NoSlot,

    /// <summary>The client took too long to load the world.</summary>
    LoadTimeout,

    /// <summary>The client left on purpose (local only).</summary>
    Left,

    /// <summary>The host could not be reached (local only).</summary>
    Unreachable,
}

/// <summary>How a connection ended: the code picks the title, <see cref="Reason"/> is the text shown.</summary>
public sealed record NetDisconnect(RejectCode Code, string Reason);

/// <summary>Who may pause the game and change its speed.</summary>
public enum ClockPolicy : byte
{
    AnyPlayer,
    HostOnly,
}

/// <summary>The lobby rules every player sees: fixed when hosting starts, changed by the host (<c>Rules</c> message).</summary>
public sealed record NetRules(int SendRate, ClockPolicy ClockPolicy, int MaxPlayers, bool Cheats, bool HasPassword, int Port);

/// <summary>The simulation rules the host's settings decide; clients use these in place of their own (the <c>UserConfig</c> keys of the same names).</summary>
public sealed record SessionRules(int AttackSlots, bool LateDefences, bool DoorsCloseBehind, bool KeepRoamers, bool OffscreenFights,
    bool OffscreenHealing, bool CampaignAncestorsOnce, bool KeepWild, bool LuckyrenMode);

/// <summary>Where a player is in joining and playing; an <see cref="Away"/> player keeps the number, name and squads.</summary>
public enum NetPlayerState : byte
{
    Host,
    Loading,
    Playing,
    Away,
}

/// <summary>One line of the player list; <see cref="Colour"/> indexes the 8-colour palette.</summary>
public sealed record RosterEntry(int Player, string Name, NetPlayerState State, int RoundTripMs, int Colour, int Squads);

public enum ClockChangeKind : byte
{
    None,
    Paused,
    Resumed,
    Speed,
    TimeSet,
}

/// <summary>One change of the host clock and who made it (<paramref name="Player"/> -1: none, the host set the time).</summary>
public readonly record struct ClockChange(int Player, ClockChangeKind Kind, double Speed);

/// <summary>
/// The authoritative clock: <see cref="Speed"/> now (0 when paused), <see cref="LastSpeed"/> to resume at, <see cref="HostBusy"/> while the host loads,
/// <see cref="AchievedSpeed"/> it really ran at, and the game time <see cref="AnchorHours"/> at simulation tick <see cref="AnchorTick"/>.
/// </summary>
public readonly record struct ClockState(ushort Epoch, double Speed, double LastSpeed, bool HostBusy, double AchievedSpeed,
    long AnchorTick, double AnchorHours, ClockChange Change);

/// <summary>The weather state clients need to show the host's weather: <see cref="Revision"/> moves when it changes, <see cref="Forced"/> names a weather forced by option.</summary>
public sealed record WeatherSync(int Revision, string? Forced, int Day, double Hour, IReadOnlyList<WeatherRegionSnapshot> Regions);
