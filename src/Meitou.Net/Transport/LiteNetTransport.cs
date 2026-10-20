using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace Meitou.Net.Transport;

/// <summary>
/// The UDP transport over LiteNetLib 2.1.4, for both the host (<see cref="Listen"/>) and a client (<see cref="Connect"/>); the only file that touches
/// LiteNetLib types (docs/net.md, "Transport"). One <see cref="NetManager"/> with four channels: <see cref="NetChannel.Snapshots"/> is
/// <see cref="DeliveryMethod.Sequenced"/>, the others <see cref="DeliveryMethod.ReliableOrdered"/>. LiteNetLib's own logic thread keeps links alive
/// and resends; its events are queued (<c>UnsyncedEvents = false</c>) and reach the listener only in <see cref="Poll"/>, on the calling thread.
/// Connection ids are this class's own (LiteNetLib reuses peer ids), kept on <see cref="LiteNetPeer.Tag"/>.
/// </summary>
public sealed class LiteNetTransport : INetServerTransport, INetClientTransport
{
    /// <summary>Milliseconds without any packet (keep-alives included) before a link times out.</summary>
    public const int DisconnectTimeoutMs = 15000;

    const int ChannelCount = 4;

    /// <summary>Payload bytes per fragment of a reliable packet at LiteNetLib's initial MTU.</summary>
    static readonly int FragmentPayload = NetConstants.InitialMtu - NetConstants.FragmentedHeaderTotalSize;

    readonly NetManager manager;
    readonly Dictionary<int, Link> links = [];
    readonly List<(int Id, NetDropReason Reason)> pendingDrops = [];
    INetListener? current;
    int nextId;
    bool disposed;

    public LiteNetTransport()
    {
        manager = new NetManager(new Events(this))
        {
            ChannelsCount = ChannelCount,
            UnsyncedEvents = false,
            AutoRecycle = true,
            EnableStatistics = true,
            DisconnectTimeout = DisconnectTimeoutMs,
            IPv6Enabled = true,
            // Bounds what a peer can make us reassemble (and allocate per fragmented packet) to one NetLimits.MaxReliableMessage at the
            // initial MTU, which stays put because MTU discovery is off.
            MaxFragmentsCount = (ushort)((NetLimits.MaxReliableMessage + FragmentPayload - 1) / FragmentPayload),
        };
    }

    /// <summary>One connection: its peer and whether it still counts as connected for <see cref="Send"/>.</summary>
    sealed class Link(int id, NetPeer peer, string address)
    {
        public int Id { get; } = id;
        public NetPeer Peer { get; } = peer;
        public string Address { get; } = address;
        public bool Connected { get; set; }

        /// <summary>False once dropped here or reported dropped; LiteNetLib's later events for it are ignored.</summary>
        public bool Live { get; set; } = true;
    }

    public int Port => manager.IsRunning ? manager.LocalPort : 0;

    public void Listen(int port)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        if (manager.IsRunning)
            throw new InvalidOperationException("Already started.");
        if (!manager.Start(port))
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    public NetConnectionId Connect(string host, int port, ReadOnlySpan<byte> connectData)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        if (!manager.IsRunning && !manager.Start())
            throw new SocketException((int)SocketError.AddressNotAvailable);
        int id = ++nextId;
        NetPeer? peer = null;
        var target = Resolve(host);
        if (target is not null)
            peer = manager.Connect(new IPEndPoint(target, port), connectData);
        if (peer is null)
        {
            // Unknown host or a request to it already pending: report it as an error at the next poll.
            pendingDrops.Add((id, NetDropReason.Error));
            return new NetConnectionId(id);
        }
        if (peer.Tag is Link { Live: true } existing)
            return new NetConnectionId(existing.Id);
        var link = new Link(id, peer, AddressText(peer.Address));
        peer.Tag = link;
        links.Add(id, link);
        return new NetConnectionId(id);
    }

    /// <summary>The address <paramref name="host"/> names (a literal, else the first DNS answer, IPv4 first); null when it does not resolve.</summary>
    static IPAddress? Resolve(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return literal;
        try
        {
            var all = Dns.GetHostAddresses(host);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The IP address without the port (an IPv4-mapped IPv6 address as IPv4), so an address ban also matches the next connection.</summary>
    static string AddressText(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    public void Poll(INetListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (disposed)
            return;
        current = listener;
        try
        {
            if (pendingDrops.Count > 0)
            {
                var due = pendingDrops.ToArray();
                pendingDrops.Clear();
                foreach (var (id, reason) in due)
                {
                    if (disposed)
                        return;
                    listener.Dropped(new NetConnectionId(id), reason, []);
                    links.Remove(id);
                }
            }
            manager.PollEvents();
            // Wakes the logic thread so what was queued since the last poll (including by the callbacks) leaves without waiting for its next update.
            if (!disposed)
                manager.TriggerUpdate();
        }
        finally
        {
            current = null;
        }
    }

    public void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if ((byte)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not a transport channel.");
        if (!links.TryGetValue(peer.Value, out var link) || !link.Live || !link.Connected)
            return;
        var method = channel == NetChannel.Snapshots ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
        if (channel == NetChannel.Snapshots)
        {
            int max = link.Peer.GetMaxSinglePacketSize(method);
            if (data.Length > max)
                throw new ArgumentException($"A {data.Length}-byte snapshot packet exceeds the link's {max}-byte limit.", nameof(data));
        }
        else if (data.Length > NetLimits.MaxReliableMessage)
            throw new ArgumentException($"A {data.Length}-byte reliable packet exceeds {NetLimits.MaxReliableMessage} bytes.", nameof(data));
        link.Peer.Send(data, (byte)channel, method);
    }

    public void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload)
    {
        INetTransport.CheckDropPayload(payload.Length);
        if (!links.TryGetValue(peer.Value, out var link) || !link.Live)
            return;
        link.Live = false;
        link.Peer.Disconnect(payload.ToArray());
        pendingDrops.Add((link.Id, NetDropReason.Closed));
    }

    public int MaxUnreliablePayload(NetConnectionId peer) =>
        links.TryGetValue(peer.Value, out var link) && link.Live ? link.Peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) : 0;

    public NetLinkStats Stats(NetConnectionId peer)
    {
        if (!links.TryGetValue(peer.Value, out var link))
            return default;
        var s = link.Peer.Statistics;
        return new NetLinkStats(link.Peer.RoundTripTime, s.PacketLossPercent, s.BytesSent, s.BytesReceived);
    }

    public string Address(NetConnectionId peer) => links.TryGetValue(peer.Value, out var link) ? link.Address : "";

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        manager.Stop(true);
        links.Clear();
        pendingDrops.Clear();
    }

    static NetDropReason ReasonOf(DisconnectReason reason) => reason switch
    {
        DisconnectReason.RemoteConnectionClose or DisconnectReason.DisconnectPeerCalled => NetDropReason.Closed,
        DisconnectReason.Timeout or DisconnectReason.ConnectionFailed => NetDropReason.Timeout,
        DisconnectReason.ConnectionRejected => NetDropReason.Rejected,
        _ => NetDropReason.Error,
    };

    /// <summary>LiteNetLib's callbacks, run by <see cref="NetManager.PollEvents"/> inside <see cref="Poll"/>.</summary>
    sealed class Events(LiteNetTransport owner) : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest request)
        {
            var listener = owner.current;
            if (listener is null || owner.disposed)
            {
                request.Reject();
                return;
            }
            string address = AddressText(request.RemoteEndPoint.Address);
            var refusal = listener.Request(address, request.Data.GetRemainingBytesSpan());
            if (refusal is not null)
            {
                INetTransport.CheckDropPayload(refusal.Length);
                request.Reject(refusal);
                return;
            }
            var peer = request.Accept();
            if (peer is null)
                return;
            var link = new Link(++owner.nextId, peer, address);
            peer.Tag = link;
            owner.links.Add(link.Id, link);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            if (peer.Tag is not Link { Live: true } link || owner.current is not { } listener || owner.disposed)
                return;
            link.Connected = true;
            listener.Connected(new NetConnectionId(link.Id));
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer.Tag is not Link { Live: true } link || owner.current is not { } listener || owner.disposed)
                return;
            link.Live = false;
            var data = info.AdditionalData is { IsNull: false } extra ? extra.GetRemainingBytesSpan() : default;
            listener.Dropped(new NetConnectionId(link.Id), ReasonOf(info.Reason), data);
            owner.links.Remove(link.Id);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (peer.Tag is not Link { Live: true, Connected: true } link || owner.current is not { } listener || owner.disposed
                || channel >= ChannelCount)
                return;
            listener.Received(new NetConnectionId(link.Id), (NetChannel)channel, reader.GetRemainingBytesSpan());
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnMessageDelivered(NetPeer peer, object userData) { }

        public void OnNtpResponse(LiteNetLib.Utils.NtpPacket packet) { }

        public void OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress) { }
    }
}
