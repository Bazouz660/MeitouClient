namespace Meitou.Net;

/// <summary>
/// The four transport channels (<c>ChannelsCount = 4</c>); reliable channels are ordered only within themselves, which is why each carries its own
/// string table (docs/net.md, "Channels").
/// </summary>
public enum NetChannel : byte
{
    /// <summary>Sequenced: <see cref="MessageType.SnapshotPart"/> host to client, <see cref="MessageType.ClientFrame"/> client to host. A late packet is dropped.</summary>
    Snapshots = 0,

    /// <summary>Reliable ordered, both ways: handshake, clock, rules, commands, chat requests.</summary>
    Control = 1,

    /// <summary>Reliable ordered, host to client: view updates and look definitions (kept apart so a large research view cannot hold back events).</summary>
    Views = 2,

    /// <summary>Reliable ordered, host to client: event batches.</summary>
    Events = 3,
}
