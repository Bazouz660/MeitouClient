using Meitou.Net.Events;
using Meitou.Net.Replication;
using Meitou.Net.Transport;

namespace Meitou.Net.Session;

/// <summary>Where a connection is in the host's handshake: <see cref="Challenged"/> until its <c>Hello</c> is accepted, then loading and playing.</summary>
enum HostPeerState : byte
{
    Challenged,
    Loading,
    Playing,
}

/// <summary>
/// One live connection on the host (docs/net.md, "Host"). Created at <see cref="INetListener.Connected"/>, forgotten when it drops; the player it carries
/// lives on in the roster (<see cref="HostPlayer"/>) as away. Frame thread only.
/// </summary>
sealed class HostPeer(NetConnectionId id, PeerSink sink, byte[] nonce, long sinceUs)
{
    public NetConnectionId Id { get; } = id;
    public PeerSink Sink { get; } = sink;
    public byte[] Nonce { get; } = nonce;
    public HostPeerState State { get; set; }

    /// <summary>When the current state's wait began (connected, or the last <c>WorldReset</c> sent): the hello and load timeouts count from it.</summary>
    public long SinceUs { get; set; } = sinceUs;

    /// <summary>Set by <c>Hello</c> (and a world swap for <see cref="Number"/>).</summary>
    public HostPlayer? Player { get; set; }
    public int Number => Player?.Number ?? -1;
    public bool Welcomed => State != HostPeerState.Challenged;

    /// <summary>A world swap happened: the <c>WorldReset</c> of the new epoch goes out at the next <see cref="NetHost.EndFrame"/>.</summary>
    public bool PendingReset { get; set; }

    /// <summary>The snapshot stream (one per connection: it owns the Snapshots and Views string tables of this connection).</summary>
    public ClientReplicator? Replicator { get; set; }
    public long NextSendUs { get; set; }

    /// <summary>The client's Control-channel string table (command batches).</summary>
    public StringTableReader ControlStrings { get; } = new();

    /// <summary>Non-spatial events for the next event batch (<see cref="NetHost.Send"/>, chat, notices).</summary>
    public List<NetEvent> Outbox { get; } = [];

    public RateLimit Commands = new(200, 400);
    public RateLimit Refusals = new(2, 2);
    public RateLimit OverflowNotices = new(1, 1);
    public RateLimit Chat = new(1, 5);
    public RateLimit ClockRequests = new(10, 10);

    // Link figures, measured once a second.
    public long LastBytesSent, LastBytesReceived;
    public int BytesInPerSecond, BytesOutPerSecond, LastSnapshotBytes;
}

/// <summary>A roster entry the host keeps for the whole hosting (number, roster id, name, colour); <see cref="Peer"/> null means away.</summary>
sealed class HostPlayer(int number, Guid id, string name, int colour)
{
    public int Number { get; set; } = number;
    public Guid Id { get; } = id;
    public string Name { get; set; } = name;
    public int Colour { get; set; } = colour;
    public HostPeer? Peer { get; set; }

    public NetPlayerState State => Peer is null ? NetPlayerState.Away : Peer.State == HostPeerState.Playing ? NetPlayerState.Playing : NetPlayerState.Loading;
}

/// <summary>A token bucket: <c>perSecond</c> tokens a second up to <c>burst</c>; each <see cref="Take"/> spends one. Times in microseconds of the host's clock.</summary>
struct RateLimit(double perSecond, double burst)
{
    readonly double rate = perSecond, capacity = burst;
    double tokens = burst;
    long last = long.MinValue;

    public bool Take(long nowUs)
    {
        if (last != long.MinValue && nowUs > last) tokens = Math.Min(capacity, tokens + (nowUs - last) / 1e6 * rate);
        if (last == long.MinValue || nowUs > last) last = nowUs;
        if (tokens < 1) return false;
        tokens -= 1;
        return true;
    }
}
