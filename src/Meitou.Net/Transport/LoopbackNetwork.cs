namespace Meitou.Net.Transport;

/// <summary>
/// Link behaviour of a <see cref="LoopbackNetwork"/> (docs/net.md, "Transport"). Every random draw comes from one <see cref="System.Random"/>
/// seeded with <see cref="Seed"/>, in send order, so a run repeats exactly for the same calls and clock.
/// </summary>
public sealed record LoopbackOptions
{
    /// <summary>One-way delay of every packet and of connect, accept, refusal and close messages.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Extra one-way delay of each data packet, uniform in [0, JitterMs]; it reorders unreliable packets.</summary>
    public int JitterMs { get; init; }

    /// <summary>Chance in [0, 1) that a <see cref="NetChannel.Snapshots"/> packet is lost.</summary>
    public float UnreliableLoss { get; init; }

    /// <summary>Chance in [0, 1) that one transmission of a reliable packet is lost; each loss delays it by one resend
    /// (2 × <see cref="LatencyMs"/> + <see cref="JitterMs"/>, at least 1 ms) and order holds it behind it, as a real resend would.</summary>
    public float ReliableLoss { get; init; }

    /// <summary>Chance in [0, 1] that a <see cref="NetChannel.Snapshots"/> packet arrives twice (the copy with its own jitter); sequencing drops the copy.</summary>
    public float Duplicate { get; init; }

    /// <summary>The <see cref="INetTransport.MaxUnreliablePayload"/> of every link; a larger snapshot packet throws, which catches part-size bugs.</summary>
    public int Mtu { get; init; } = 1200;

    public int Seed { get; init; } = 1;
}

/// <summary>
/// An in-memory network of one server and any number of clients for tests: deterministic delivery driven by an <see cref="INetClock"/> (usually a
/// <see cref="ManualNetClock"/>), with simulated latency, jitter, loss, reordering and duplication (docs/net.md, "Transport"). Each packet is due at
/// send time + latency (+ jitter for data); <see cref="INetTransport.Poll"/> delivers what is due, ordered by (due time, send order), and never
/// anything sent during the same poll. Reliable channels never lose and keep order per channel; <see cref="NetChannel.Snapshots"/> may lose,
/// reorder and duplicate, and the receiver drops a packet sent before the newest one it already delivered (LiteNetLib's <c>Sequenced</c>).
/// </summary>
public sealed class LoopbackNetwork
{
    const int ChannelCount = 4;
    const int AnyPort = 49152;

    readonly LoopbackOptions options;
    readonly INetClock clock;
    readonly Random random;
    long sequence;
    int clientCount;
    int nextClientId;
    ServerEnd? server;
    readonly List<ClientEnd> clients = [];

    public LoopbackNetwork(LoopbackOptions options, INetClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.LatencyMs < 0 || options.JitterMs < 0 || options.Mtu < 1)
            throw new ArgumentException("Latency and jitter must not be negative and the MTU must be positive.", nameof(options));
        if (options.UnreliableLoss is < 0 or >= 1 || options.ReliableLoss is < 0 or >= 1 || options.Duplicate is < 0 or > 1)
            throw new ArgumentException("Loss chances must lie in [0, 1) and the duplicate chance in [0, 1].", nameof(options));
        this.options = options;
        this.clock = clock;
        random = new Random(options.Seed);
    }

    /// <summary>The network's one server transport; a second call throws <see cref="InvalidOperationException"/>.</summary>
    public INetServerTransport Server()
    {
        if (server is not null)
            throw new InvalidOperationException("A loopback network has one server.");
        return server = new ServerEnd(this);
    }

    /// <summary>A new client transport. Client-side connection ids are unique across the network, so <see cref="Sever"/> needs only the id.</summary>
    public INetClientTransport Client() => new ClientEnd(this, ++clientCount);

    /// <summary>Cuts the link a client's <paramref name="clientSide"/> id names: both ends get <see cref="NetDropReason.Timeout"/> at their next poll
    /// and packets in flight are lost. Ignored for an unknown or closed link.</summary>
    public void Sever(NetConnectionId clientSide)
    {
        var end = FindClientEnd(clientSide);
        if (end is null)
            return;
        Sever(end);
        if (end.Remote is { } remote)
            Sever(remote);
    }

    void Sever(LinkEnd end)
    {
        if (end.Closed)
            return;
        end.Closed = true;
        Schedule(end.Owner, Now, new Delivery(DeliveryKind.LocalDrop, end, NetChannel.Control, [], NetDropReason.Timeout));
    }

    LinkEnd? FindClientEnd(NetConnectionId id)
    {
        foreach (var c in clients)
            if (c.Ends.TryGetValue(id.Value, out var end))
                return end;
        return null;
    }

    long Now => clock.NowMicroseconds;

    static long Ms(long ms) => ms * 1000;

    long Jitter() => options.JitterMs > 0 ? Ms(random.Next(options.JitterMs + 1)) : 0;

    long ResendDelay => Math.Max(Ms(1), Ms(2L * options.LatencyMs + options.JitterMs));

    void Schedule(Endpoint to, long at, Delivery delivery) => to.Inbox.Enqueue(delivery with { Seq = ++sequence }, (at, sequence));

    void ScheduleCopy(Endpoint to, long at, Delivery delivery) => to.Inbox.Enqueue(delivery, (at, delivery.Seq));

    enum DeliveryKind : byte { Request, Accepted, Rejected, Data, RemoteClose, LocalDrop }

    /// <summary>One scheduled event; <see cref="Target"/> is the end it is for (for <see cref="DeliveryKind.Request"/>, the requesting client end).</summary>
    sealed record Delivery(DeliveryKind Kind, LinkEnd Target, NetChannel Channel, byte[] Data, NetDropReason Reason = NetDropReason.Closed)
    {
        public long Seq { get; init; }
    }

    /// <summary>One side of a link.</summary>
    sealed class LinkEnd(Endpoint owner, int id, string remoteAddress)
    {
        public Endpoint Owner { get; } = owner;
        public int Id { get; } = id;
        public string RemoteAddress { get; } = remoteAddress;
        public LinkEnd? Remote { get; set; }
        public bool Open { get; set; }
        public bool Closed { get; set; }

        /// <summary>Earliest due time of data sent to this end (the client end: when the accept arrives), so data never overtakes the accept.</summary>
        public long OpenAt { get; set; }

        /// <summary>Due time of the last reliable packet sent from this end, per channel.</summary>
        public long[] LastReliableAt { get; } = new long[ChannelCount];

        /// <summary>Send order of the newest <see cref="NetChannel.Snapshots"/> packet delivered to this end.</summary>
        public long LastSequenced { get; set; }

        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
    }

    abstract class Endpoint(LoopbackNetwork network) : INetTransport
    {
        protected LoopbackNetwork Network { get; } = network;
        public PriorityQueue<Delivery, (long At, long Seq)> Inbox { get; } = new();
        public Dictionary<int, LinkEnd> Ends { get; } = [];
        protected bool Disposed { get; private set; }

        public void Poll(INetListener listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            if (Disposed)
                return;
            long now = Network.Now, last = Network.sequence;
            while (!Disposed && Inbox.TryPeek(out var d, out var due) && due.At <= now && due.Seq <= last)
            {
                Inbox.Dequeue();
                Deliver(d, listener);
            }
        }

        void Deliver(Delivery d, INetListener listener)
        {
            var end = d.Target;
            switch (d.Kind)
            {
                case DeliveryKind.Request:
                    ((ServerEnd)this).Accept(end, d.Data, listener);
                    break;
                case DeliveryKind.Accepted:
                    if (end.Closed)
                    {
                        // The client gave up before the accept arrived: tell the server side.
                        if (end.Remote is { } remote)
                            Network.Close(remote, []);
                        break;
                    }
                    end.Open = true;
                    listener.Connected(new NetConnectionId(end.Id));
                    break;
                case DeliveryKind.Rejected:
                    if (end.Closed)
                        break;
                    end.Closed = true;
                    listener.Dropped(new NetConnectionId(end.Id), NetDropReason.Rejected, d.Data);
                    Ends.Remove(end.Id);
                    break;
                case DeliveryKind.Data:
                    if (end.Closed || !end.Open)
                        break;
                    if (d.Channel == NetChannel.Snapshots)
                    {
                        if (d.Seq <= end.LastSequenced)
                            break;
                        end.LastSequenced = d.Seq;
                    }
                    end.BytesReceived += d.Data.Length;
                    listener.Received(new NetConnectionId(end.Id), d.Channel, d.Data);
                    break;
                case DeliveryKind.RemoteClose:
                    if (end.Closed)
                        break;
                    end.Closed = true;
                    listener.Dropped(new NetConnectionId(end.Id), NetDropReason.Closed, d.Data);
                    Ends.Remove(end.Id);
                    break;
                case DeliveryKind.LocalDrop:
                    listener.Dropped(new NetConnectionId(end.Id), d.Reason, []);
                    Ends.Remove(end.Id);
                    break;
            }
        }

        public void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data)
        {
            if ((byte)channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not a transport channel.");
            if (!Ends.TryGetValue(peer.Value, out var end) || end.Closed || !end.Open || end.Remote is not { } remote)
                return;
            if (channel == NetChannel.Snapshots && data.Length > Network.options.Mtu)
                throw new ArgumentException($"A {data.Length}-byte snapshot packet exceeds the {Network.options.Mtu}-byte MTU.", nameof(data));
            end.BytesSent += data.Length;
            Network.Transmit(end, remote, channel, data.ToArray());
        }

        public void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload)
        {
            if (!Ends.TryGetValue(peer.Value, out var end) || end.Closed)
                return;
            end.Closed = true;
            if (end.Remote is { } remote)
                Network.Close(remote, payload.ToArray(), end);
            Network.Schedule(this, Network.Now, new Delivery(DeliveryKind.LocalDrop, end, NetChannel.Control, [], NetDropReason.Closed));
        }

        public int MaxUnreliablePayload(NetConnectionId peer) => Ends.ContainsKey(peer.Value) ? Network.options.Mtu : 0;

        public NetLinkStats Stats(NetConnectionId peer) => Ends.TryGetValue(peer.Value, out var end)
            ? new NetLinkStats(2 * Network.options.LatencyMs, Network.options.UnreliableLoss * 100, end.BytesSent, end.BytesReceived)
            : default;

        public string Address(NetConnectionId peer) => Ends.TryGetValue(peer.Value, out var end) ? end.RemoteAddress : "";

        public virtual void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            foreach (var end in Ends.Values)
            {
                if (end.Closed)
                    continue;
                end.Closed = true;
                if (end.Remote is { } remote)
                    Network.Close(remote, [], end);
            }
            Ends.Clear();
            Inbox.Clear();
        }
    }

    /// <summary>Sends a close to <paramref name="remote"/>, due after the reliable data <paramref name="from"/> already sent.</summary>
    void Close(LinkEnd remote, byte[] payload, LinkEnd? from = null)
    {
        if (remote.Closed)
            return;
        long at = Now + Ms(options.LatencyMs);
        if (from is not null)
            foreach (long last in from.LastReliableAt)
                at = Math.Max(at, last);
        Schedule(remote.Owner, at, new Delivery(DeliveryKind.RemoteClose, remote, NetChannel.Control, payload));
    }

    void Transmit(LinkEnd from, LinkEnd to, NetChannel channel, byte[] data)
    {
        long at = Now + Ms(options.LatencyMs);
        if (channel == NetChannel.Snapshots)
        {
            if (options.UnreliableLoss > 0 && random.NextSingle() < options.UnreliableLoss)
            {
                ++sequence;   // a lost packet still takes its place in the send order
                return;
            }
            var delivery = new Delivery(DeliveryKind.Data, to, channel, data);
            long due = Math.Max(at + Jitter(), to.OpenAt);
            Schedule(to.Owner, due, delivery);
            if (options.Duplicate > 0 && random.NextSingle() < options.Duplicate)
                ScheduleCopy(to.Owner, Math.Max(at + Jitter(), to.OpenAt), delivery with { Seq = sequence });
            return;
        }
        if (options.ReliableLoss > 0)
            while (random.NextSingle() < options.ReliableLoss)
                at += ResendDelay;
        at = Math.Max(Math.Max(at + Jitter(), to.OpenAt), from.LastReliableAt[(int)channel]);
        from.LastReliableAt[(int)channel] = at;
        Schedule(to.Owner, at, new Delivery(DeliveryKind.Data, to, channel, data));
    }

    sealed class ServerEnd(LoopbackNetwork network) : Endpoint(network), INetServerTransport
    {
        int nextId;

        public int Port { get; private set; }

        public void Listen(int port)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(port);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
            if (Port != 0)
                throw new InvalidOperationException("Already listening.");
            Port = port == 0 ? AnyPort : port;
        }

        public bool Accepts(int port) => !Disposed && Port != 0 && Port == port;

        /// <summary>A connect request arrived: ask the listener, then open the link or send the refusal.</summary>
        public void Accept(LinkEnd client, byte[] connectData, INetListener listener)
        {
            if (client.Closed)
                return;
            string address = ((ClientEnd)client.Owner).SelfAddress;
            var refusal = listener.Request(address, connectData);
            if (refusal is not null)
            {
                Network.Schedule(client.Owner, Network.Now + Ms(Network.options.LatencyMs),
                    new Delivery(DeliveryKind.Rejected, client, NetChannel.Control, [.. refusal]));
                return;
            }
            var end = new LinkEnd(this, ++nextId, address)
            {
                Remote = client, Open = true,
            };
            Ends.Add(end.Id, end);
            client.Remote = end;
            client.OpenAt = Network.Now + Ms(Network.options.LatencyMs);
            Network.Schedule(client.Owner, client.OpenAt, new Delivery(DeliveryKind.Accepted, client, NetChannel.Control, []));
            listener.Connected(new NetConnectionId(end.Id));
        }
    }

    sealed class ClientEnd : Endpoint, INetClientTransport
    {
        public ClientEnd(LoopbackNetwork network, int index) : base(network)
        {
            SelfAddress = $"loopback-client:{index}";
            network.clients.Add(this);
        }

        /// <summary>This client's address as the server sees it.</summary>
        public string SelfAddress { get; }

        public NetConnectionId Connect(string host, int port, ReadOnlySpan<byte> connectData)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var end = new LinkEnd(this, ++Network.nextClientId, $"loopback-server:{port}");
            Ends.Add(end.Id, end);
            long now = Network.Now, latency = Ms(Network.options.LatencyMs);
            if (Network.server is { } server && server.Accepts(port))
                Network.Schedule(server, now + latency, new Delivery(DeliveryKind.Request, end, NetChannel.Control, connectData.ToArray()));
            else
            {
                // Nobody listens there: the attempt fails one round trip later.
                end.Closed = true;
                Network.Schedule(this, now + 2 * latency, new Delivery(DeliveryKind.LocalDrop, end, NetChannel.Control, [], NetDropReason.Timeout));
            }
            return new NetConnectionId(end.Id);
        }

        public override void Dispose()
        {
            base.Dispose();
            Network.clients.Remove(this);
        }
    }
}
