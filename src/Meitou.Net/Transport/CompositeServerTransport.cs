namespace Meitou.Net.Transport;

/// <summary>
/// One host listening on several transports at once (UDP and Steam; docs/net.md "Transport"): each inner connection gets this transport's own id
/// (from 1, never reused), so the host sees one id space. <see cref="Listen"/> starts the first transport on the port (its failure propagates and
/// nothing is started), then the others; <see cref="Port"/> is the first one's. Events of each inner transport are forwarded with the ids mapped.
/// </summary>
public sealed class CompositeServerTransport : INetServerTransport
{
    readonly INetServerTransport[] inner;
    readonly Dictionary<(int Index, int Id), int> outer = [];
    readonly Dictionary<int, (int Index, NetConnectionId Id)> byOuter = [];
    int nextId;
    Mapper[]? mappers;
    INetListener? mappedListener;

    public CompositeServerTransport(params INetServerTransport[] transports)
    {
        ArgumentOutOfRangeException.ThrowIfZero(transports.Length);
        inner = transports;
    }

    /// <summary>The inner transports, first the one that owns the port.</summary>
    public IReadOnlyList<INetServerTransport> Transports => inner;

    public int Port => inner[0].Port;

    public void Listen(int port)
    {
        inner[0].Listen(port);
        for (int i = 1; i < inner.Length; i++) inner[i].Listen(port);
    }

    sealed class Mapper(CompositeServerTransport owner, int index, INetListener listener) : INetListener
    {
        public byte[]? Request(string address, ReadOnlySpan<byte> connectData) => listener.Request(address, connectData);

        public void Connected(NetConnectionId peer) => listener.Connected(owner.Map(index, peer));

        public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => listener.Received(owner.Map(index, peer), channel, data);

        public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data)
        {
            var id = owner.Map(index, peer);
            owner.outer.Remove((index, peer.Value));
            owner.byOuter.Remove(id.Value);
            listener.Dropped(id, reason, data);
        }
    }

    NetConnectionId Map(int index, NetConnectionId peer)
    {
        if (!outer.TryGetValue((index, peer.Value), out int id))
        {
            id = ++nextId;
            outer.Add((index, peer.Value), id);
            byOuter.Add(id, (index, peer));
        }
        return new NetConnectionId(id);
    }

    public void Poll(INetListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (mappers is null || mappedListener != listener)
        {
            mappers = [.. inner.Select((_, i) => new Mapper(this, i, listener))];
            mappedListener = listener;
        }
        for (int i = 0; i < inner.Length; i++) inner[i].Poll(mappers[i]);
    }

    public void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (byOuter.TryGetValue(peer.Value, out var to)) inner[to.Index].Send(to.Id, channel, data);
        else if ((byte)channel >= 4) throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not a transport channel.");
    }

    public void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload)
    {
        INetTransport.CheckDropPayload(payload.Length);
        if (byOuter.TryGetValue(peer.Value, out var to)) inner[to.Index].Drop(to.Id, payload);
    }

    public int MaxUnreliablePayload(NetConnectionId peer) => byOuter.TryGetValue(peer.Value, out var to) ? inner[to.Index].MaxUnreliablePayload(to.Id) : 0;

    public NetLinkStats Stats(NetConnectionId peer) => byOuter.TryGetValue(peer.Value, out var to) ? inner[to.Index].Stats(to.Id) : default;

    public string Address(NetConnectionId peer) => byOuter.TryGetValue(peer.Value, out var to) ? inner[to.Index].Address(to.Id) : "";

    public void Dispose()
    {
        foreach (var t in inner) t.Dispose();
        outer.Clear();
        byOuter.Clear();
    }
}
