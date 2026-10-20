namespace Meitou.Net.Transport;

/// <summary>Adapts one peer of a transport to <see cref="IPacketSink"/>, so the replicator and message senders write without knowing the transport.</summary>
public sealed class PeerSink(INetTransport transport, NetConnectionId peer) : IPacketSink
{
    public NetConnectionId Peer => peer;

    public int MaxUnreliablePayload => transport.MaxUnreliablePayload(peer);

    public void Send(NetChannel channel, ReadOnlySpan<byte> packet) => transport.Send(peer, channel, packet);
}
