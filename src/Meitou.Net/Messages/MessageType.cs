namespace Meitou.Net;

/// <summary>
/// The first byte of every packet: one table for all channels (docs/net.md, "Messages"). Values are append-only within a
/// <see cref="NetProtocol.Version"/>; a layout change bumps the version.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Control, host to client: the nonce for the password proof, the host's name.</summary>
    Challenge = 0x01,

    /// <summary>Control, client to host: version, build, player id, name, proof, data set, animation hash.</summary>
    Hello = 0x02,

    /// <summary>Control, host to client: the player number, the lobby rules and the roster.</summary>
    Welcome = 0x03,

    /// <summary>Control, host to client: a refusal or the reason a peer is dropped.</summary>
    Reject = 0x04,

    /// <summary>Control, client to host: the replica of this epoch is built and ready for snapshots.</summary>
    Ready = 0x05,

    /// <summary>Control, host to client: a new world (epoch): seed, focus, clock, rules, weather.</summary>
    WorldReset = 0x06,

    /// <summary>Control, host to client: the player list.</summary>
    Roster = 0x07,

    /// <summary>Control, client to host: pause, resume or change the game speed.</summary>
    ClockRequest = 0x08,

    /// <summary>Control, host to client: the authoritative clock.</summary>
    ClockState = 0x09,

    /// <summary>Control, host to client: lobby and session rules changed.</summary>
    Rules = 0x0A,

    /// <summary>Control, host to client: weather schedule state.</summary>
    Weather = 0x0B,

    /// <summary>Snapshots, host to client: a self-contained part of the world.</summary>
    SnapshotPart = 0x10,

    /// <summary>Snapshots, client to host: acks, the camera view and resync requests.</summary>
    ClientFrame = 0x11,

    /// <summary>Control, client to host: up to <see cref="NetLimits.MaxCommandBatch"/> simulation commands.</summary>
    CommandBatch = 0x20,

    /// <summary>Control, host to client: why a command was refused.</summary>
    CommandResult = 0x21,

    /// <summary>Control, client to host: a chat line.</summary>
    ChatRequest = 0x22,

    /// <summary>Views, host to client: a change to one replicated view.</summary>
    ViewUpdate = 0x30,

    /// <summary>Views, host to client: how to build one character look.</summary>
    LookDefinition = 0x31,

    /// <summary>Events, host to client: the events of one frame for this player.</summary>
    EventBatch = 0x40,
}
