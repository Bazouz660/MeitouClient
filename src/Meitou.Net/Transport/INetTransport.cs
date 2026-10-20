namespace Meitou.Net.Transport;

/// <summary>
/// One connection as a transport knows it. Ids start at 1 and are never reused within one transport instance, so a late callback can never name
/// a newer peer (docs/net.md, "Transport").
/// </summary>
public readonly record struct NetConnectionId(int Value)
{
    public override string ToString() => $"#{Value}";
}

/// <summary>Why a connection ended, as <see cref="INetListener.Dropped"/> reports it.</summary>
public enum NetDropReason : byte
{
    /// <summary>Either side dropped it: the remote side gets the drop payload, the dropping side an empty one.</summary>
    Closed,

    /// <summary>Nothing arrived for the disconnect timeout, the host never answered the connect request, or the link was severed (loopback).</summary>
    Timeout,

    /// <summary>The server refused the connect request; the data is the refusal payload.</summary>
    Rejected,

    /// <summary>A socket or address error (unknown host, unreachable network).</summary>
    Error,
}

/// <summary>Link figures for the stats line: round trip, loss of the unreliable channel, and payload bytes so far.</summary>
public readonly record struct NetLinkStats(int RoundTripMs, float LossPercent, long BytesSent, long BytesReceived);

/// <summary>
/// Receives a transport's events, only from inside <see cref="INetTransport.Poll"/> and on the calling thread. Spans are valid only during the call.
/// A callback may call <see cref="INetTransport.Send"/> and <see cref="INetTransport.Drop"/>; nothing they cause is delivered within the same poll.
/// </summary>
public interface INetListener
{
    /// <summary>Server only: a connect request from <paramref name="address"/> (the IP address without the port, so it serves address bans) carrying
    /// <paramref name="connectData"/>. Null accepts it (<see cref="Connected"/> follows, in this poll or the next); a payload of at most
    /// <see cref="INetTransport.MaxDropPayload"/> bytes refuses it and the client gets <see cref="NetDropReason.Rejected"/> with it (a longer one throws
    /// <see cref="ArgumentException"/> out of <see cref="INetTransport.Poll"/>).</summary>
    byte[]? Request(string address, ReadOnlySpan<byte> connectData);

    /// <summary>The connection is open and <see cref="INetTransport.Send"/> to it works.</summary>
    void Connected(NetConnectionId peer);

    /// <summary>One packet, whole (reliable channels arrive in send order per channel; <see cref="NetChannel.Snapshots"/> drops late and duplicate packets).</summary>
    void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data);

    /// <summary>The connection ended; raised exactly once per accepted peer and per <see cref="INetClientTransport.Connect"/>, also after a local
    /// <see cref="INetTransport.Drop"/>, but never after the transport is disposed. <paramref name="data"/> is the remote side's drop or refusal payload, else empty.</summary>
    void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data);
}

/// <summary>
/// A packet transport with the four <see cref="NetChannel"/>s: <see cref="NetChannel.Snapshots"/> unreliable sequenced (a packet older than one already
/// delivered is dropped), the others reliable ordered and fragmented by the transport. Every call is made from the frame thread; events arrive only in
/// <see cref="Poll"/> (docs/net.md, "Transport").
/// </summary>
public interface INetTransport : IDisposable
{
    /// <summary>Largest <see cref="Drop"/> payload or connect refusal: LiteNetLib sends it in one datagram of its initial 1024-byte MTU and silently
    /// blanks a longer one, so both transports throw <see cref="ArgumentException"/> above it and writers must clamp to it (docs/net.md, "Transport").</summary>
    const int MaxDropPayload = 1000;

    internal static void CheckDropPayload(int length)
    {
        if (length > MaxDropPayload)
            throw new ArgumentException($"A {length}-byte drop or refusal payload exceeds {MaxDropPayload} bytes.");
    }

    /// <summary>Delivers every event due since the last poll to <paramref name="listener"/>, in order.</summary>
    void Poll(INetListener listener);

    /// <summary>Queues <paramref name="data"/> (copied before returning). Ignored for a peer that is not connected (pending, dropped or unknown).
    /// A <see cref="NetChannel.Snapshots"/> payload over <see cref="MaxUnreliablePayload"/>, or a reliable one over <see cref="NetLimits.MaxReliableMessage"/>,
    /// throws <see cref="ArgumentException"/>.</summary>
    void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data);

    /// <summary>Closes the connection, handing <paramref name="payload"/> (a <c>Reject</c>, at most <see cref="MaxDropPayload"/> bytes) to the remote
    /// side; this side gets <see cref="NetDropReason.Closed"/> at the next poll. Data sent to the peer and not yet delivered may be lost (LiteNetLib
    /// discards queued reliable packets), so whatever the remote side must learn goes in the payload. Ignored for an unknown or already dropped peer.</summary>
    void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload);

    /// <summary>Largest <see cref="NetChannel.Snapshots"/> payload that fits one datagram to <paramref name="peer"/> (from its MTU); 0 for an unknown peer.</summary>
    int MaxUnreliablePayload(NetConnectionId peer);

    /// <summary>Current link figures; default for an unknown peer.</summary>
    NetLinkStats Stats(NetConnectionId peer);

    /// <summary>The remote IP address as text, without the port (for logs and address bans); empty for an unknown peer.</summary>
    string Address(NetConnectionId peer);
}

/// <summary>The host side: accepts connections after <see cref="Listen"/>.</summary>
public interface INetServerTransport : INetTransport
{
    /// <summary>Starts accepting on <paramref name="port"/> (0: any free port). A port that cannot be bound throws <see cref="System.Net.Sockets.SocketException"/>.</summary>
    void Listen(int port);

    /// <summary>The bound port after <see cref="Listen"/> (the assigned one for port 0), else 0.</summary>
    int Port { get; }
}

/// <summary>The client side.</summary>
public interface INetClientTransport : INetTransport
{
    /// <summary>Starts connecting with <paramref name="connectData"/> (<see cref="NetProtocol.Magic"/> + version). Never throws for network
    /// failures: an unknown host or a refused or unanswered request ends in <see cref="INetListener.Dropped"/> for the returned id. <see cref="LiteNetTransport"/>
    /// returns the same id again for an endpoint still connecting or connected (LiteNetLib keeps one peer per endpoint).</summary>
    NetConnectionId Connect(string host, int port, ReadOnlySpan<byte> connectData);
}
