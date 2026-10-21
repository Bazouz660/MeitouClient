using System.Buffers;
using System.Buffers.Binary;

namespace Meitou.Net.Transport.Steam;

/// <summary>
/// The Steam transport (docs/net.md "Steam transport"): <see cref="INetServerTransport"/> and <see cref="INetClientTransport"/> over Steam's P2P packets
/// (<see cref="ISteamP2P"/>), so friends connect by Steam id with no port forwarding. Steam has no connections of its own here, so this class keeps
/// them: a 3-byte header (kind, the connection's 16-bit tag chosen by the connecting side) on every packet, connect/accept/reject/disconnect on P2P
/// channel 1 (reliable, ordered with <see cref="NetChannel.Control"/>), pings for the round trip and liveness, a 15 s silence timeout.
/// <see cref="NetChannel.Snapshots"/> is no-delay unreliable with a 16-bit sequence (older and repeated packets dropped), the others reliable on P2P
/// channels of the same number. Events reach the listener only inside <see cref="Poll"/>, as with <see cref="LiteNetTransport"/>. The connect host
/// is a Steam id (<c>steam:&lt;id&gt;</c> or the digits); the port is ignored. Only one Steam transport may read a given <see cref="ISteamP2P"/> at a time.
/// </summary>
public sealed class SteamNetTransport : INetServerTransport, INetClientTransport
{
    public const int TimeoutMs = 15000, ConnectTimeoutMs = 15000, PingIntervalMs = 1000;
    /// <summary>Largest <see cref="NetChannel.Snapshots"/> payload: Steam's unreliable limit is about 1200 bytes, less the 5-byte header and a margin.</summary>
    public const int MaxUnreliable = 1150;
    const int HeaderSize = 3, SnapshotHeaderSize = 5, ControlChannel = 1, PingChannel = 0, PingRing = 16;

    enum Kind : byte { Connect = 1, Accept = 2, Reject = 3, Disconnect = 4, Ping = 5, Pong = 6, Data = 0x10 }

    sealed class Link(int id, ulong remote, ushort tag, bool outgoing, long now)
    {
        public int Id { get; } = id;
        public ulong Remote { get; } = remote;
        public ushort Tag { get; } = tag;
        public bool Outgoing { get; } = outgoing;
        public long Started { get; } = now;
        public bool Connected { get; set; }
        public bool Live { get; set; } = true;
        public long LastReceived { get; set; } = now;
        public long NextPing { get; set; }
        public ushort SendSeq { get; set; }
        public int RecvSeq { get; set; } = -1;
        public int RoundTripMs { get; set; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        /// <summary>Send times (ms) of the last pings and whether each was answered, for the loss figure.</summary>
        public (uint Stamp, bool Answered)[] Pings { get; } = new (uint, bool)[PingRing];
        public int PingCount { get; set; }
    }

    readonly ISteamP2P steam;
    readonly INetClock clock;
    readonly Dictionary<int, Link> links = [];
    readonly Dictionary<ulong, Link> byRemote = [];
    readonly List<(int Id, NetDropReason Reason)> pendingDrops = [];
    readonly List<(ulong Remote, byte Error)> failures = [];
    byte[] scratch = new byte[2048];
    bool listening, disposed;
    int nextId;

    public SteamNetTransport(ISteamP2P steam, INetClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(steam);
        this.steam = steam;
        this.clock = clock ?? new StopwatchNetClock();
        steam.SessionRequested += OnSessionRequested;
        steam.SessionFailed += OnSessionFailed;
    }

    long NowMs => clock.NowMicroseconds / 1000;

    /// <summary>Steam has no port: 0.</summary>
    public int Port => 0;

    /// <summary>Accepts connect requests from now on; <paramref name="port"/> is ignored.</summary>
    public void Listen(int port)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (listening) throw new InvalidOperationException("Already started.");
        listening = true;
    }

    public NetConnectionId Connect(string host, int port, ReadOnlySpan<byte> connectData)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string text = host.Trim();
        if (!SteamPresence.TryParseAddress(text, out ulong remote) && !SteamPresence.TryParseAddress(SteamPresence.AddressPrefix + text, out remote))
        {
            pendingDrops.Add((++nextId, NetDropReason.Error));
            return new NetConnectionId(nextId);
        }
        if (byRemote.TryGetValue(remote, out var existing) && existing.Live && existing.Outgoing) return new NetConnectionId(existing.Id);
        int id = ++nextId;
        ushort tag = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
        var link = new Link(id, remote, tag, outgoing: true, NowMs);
        links.Add(id, link);
        byRemote[remote] = link;
        if (!SendControl(link, Kind.Connect, connectData))
        {
            Forget(link);
            pendingDrops.Add((id, NetDropReason.Error));
        }
        return new NetConnectionId(id);
    }

    void OnSessionRequested(ulong remote)
    {
        if (disposed) return;
        if (listening || byRemote.TryGetValue(remote, out var link) && link.Outgoing && link.Live) steam.AcceptSession(remote);
    }

    void OnSessionFailed(ulong remote, byte error)
    {
        if (!disposed) failures.Add((remote, error));
    }

    public void Poll(INetListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (disposed) return;
        steam.RunCallbacks();
        if (pendingDrops.Count > 0)
        {
            var due = pendingDrops.ToArray();
            pendingDrops.Clear();
            foreach (var (id, reason) in due)
            {
                if (disposed) return;
                links.Remove(id);
                listener.Dropped(new NetConnectionId(id), reason, []);
            }
        }
        if (failures.Count > 0)
        {
            var due = failures.ToArray();
            failures.Clear();
            foreach (var (remote, _) in due)
            {
                if (disposed) return;
                if (byRemote.TryGetValue(remote, out var link) && link.Live)
                    Lose(listener, link, link.Connected ? NetDropReason.Timeout : NetDropReason.Error, []);
            }
        }
        for (int channel = 0; channel < 4 && !disposed; channel++)
            while (!disposed && steam.IsPacketAvailable(channel, out int size))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                try
                {
                    if (!steam.ReadPacket(channel, buffer, out int read, out ulong from)) break;
                    Handle(listener, from, buffer.AsSpan(0, read));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        if (disposed) return;
        long now = NowMs;
        foreach (var link in links.Values.ToArray())
        {
            if (disposed) return;
            if (!link.Live) continue;
            if (!link.Connected)
            {
                if (link.Outgoing && now - link.Started >= ConnectTimeoutMs) Lose(listener, link, NetDropReason.Timeout, []);
                continue;
            }
            if (now - link.LastReceived >= TimeoutMs)
            {
                Lose(listener, link, NetDropReason.Timeout, []);
                continue;
            }
            if (now >= link.NextPing) SendPing(link, now);
        }
    }

    void Handle(INetListener listener, ulong from, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return;
        var kind = (Kind)packet[0];
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(packet[1..]);
        var body = packet[HeaderSize..];
        byRemote.TryGetValue(from, out var link);
        if (link is not null && (!link.Live || link.Tag != tag)) link = kind == Kind.Connect ? link : null;
        if (kind == Kind.Connect)
        {
            OnConnect(listener, from, tag, body, link);
            return;
        }
        if (link is null) return;
        link.BytesReceived += packet.Length;
        switch (kind)
        {
            case Kind.Accept:
                if (!link.Outgoing || link.Connected) return;
                link.Connected = true;
                link.LastReceived = NowMs;
                link.NextPing = NowMs + PingIntervalMs;
                listener.Connected(new NetConnectionId(link.Id));
                return;
            case Kind.Reject:
                if (!link.Outgoing || link.Connected) return;
                Lose(listener, link, NetDropReason.Rejected, body);
                return;
            case Kind.Disconnect:
                Lose(listener, link, NetDropReason.Closed, body);
                return;
        }
        if (!link.Connected) return;
        link.LastReceived = NowMs;
        switch (kind)
        {
            case Kind.Ping when body.Length == 4:
                Span<byte> stamp = stackalloc byte[4];
                body.CopyTo(stamp);
                SendRaw(link, Kind.Pong, stamp, SteamSendType.UnreliableNoDelay, PingChannel);
                return;
            case Kind.Pong when body.Length == 4:
                OnPong(link, BinaryPrimitives.ReadUInt32LittleEndian(body));
                return;
        }
        int channel = (byte)kind - (byte)Kind.Data;
        if (channel is < 0 or > 3) return;
        if (channel == (int)NetChannel.Snapshots)
        {
            if (body.Length < 2) return;
            ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(body);
            if (link.RecvSeq >= 0 && (short)(seq - (ushort)link.RecvSeq) <= 0) return;   // older or repeated
            link.RecvSeq = seq;
            body = body[2..];
        }
        else if (body.Length > NetLimits.MaxReliableMessage) return;
        listener.Received(new NetConnectionId(link.Id), (NetChannel)channel, body);
    }

    void OnConnect(INetListener listener, ulong from, ushort tag, ReadOnlySpan<byte> connectData, Link? current)
    {
        if (!listening) return;
        if (current is { Live: true, Outgoing: false })
        {
            if (current.Tag == tag)
            {
                if (current.Connected) SendControl(current, Kind.Accept, []);   // a repeated request: answer again
                return;
            }
            // The same user connecting anew: the old connection is over.
            Lose(listener, current, NetDropReason.Closed, []);
            if (disposed) return;
        }
        else if (current is { Live: true, Outgoing: true }) return;
        var refusal = listener.Request(SteamPresence.Address(from), connectData);
        if (disposed) return;
        if (refusal is not null)
        {
            INetTransport.CheckDropPayload(refusal.Length);
            SendRaw(from, tag, Kind.Reject, refusal, SteamSendType.Reliable, ControlChannel);
            steam.CloseSession(from);
            return;
        }
        var link = new Link(++nextId, from, tag, outgoing: false, NowMs) { Connected = true, NextPing = NowMs + PingIntervalMs };
        link.BytesReceived += HeaderSize + connectData.Length;
        links.Add(link.Id, link);
        byRemote[from] = link;
        SendControl(link, Kind.Accept, []);
        listener.Connected(new NetConnectionId(link.Id));
    }

    /// <summary>The link ends here: reported to the listener once with <paramref name="reason"/> and the remote side's payload.</summary>
    void Lose(INetListener listener, Link link, NetDropReason reason, ReadOnlySpan<byte> data)
    {
        Forget(link);
        steam.CloseSession(link.Remote);
        listener.Dropped(new NetConnectionId(link.Id), reason, data);
    }

    void Forget(Link link)
    {
        link.Live = false;
        links.Remove(link.Id);
        if (byRemote.TryGetValue(link.Remote, out var known) && known == link) byRemote.Remove(link.Remote);
    }

    void SendPing(Link link, long now)
    {
        link.NextPing = now + PingIntervalMs;
        uint stamp = (uint)now;
        link.Pings[link.PingCount++ % PingRing] = (stamp, false);
        Span<byte> body = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, stamp);
        SendRaw(link, Kind.Ping, body, SteamSendType.UnreliableNoDelay, PingChannel);
    }

    void OnPong(Link link, uint stamp)
    {
        for (int i = 0; i < PingRing; i++)
            if (link.Pings[i].Stamp == stamp && !link.Pings[i].Answered && i < link.PingCount)
            {
                link.Pings[i].Answered = true;
                int rtt = (int)Math.Min((uint)NowMs - stamp, int.MaxValue);
                link.RoundTripMs = link.RoundTripMs == 0 ? rtt : (link.RoundTripMs * 7 + rtt) / 8;
                return;
            }
    }

    bool SendControl(Link link, Kind kind, ReadOnlySpan<byte> body) => SendRaw(link, kind, body, SteamSendType.Reliable, ControlChannel);

    bool SendRaw(Link link, Kind kind, ReadOnlySpan<byte> body, SteamSendType type, int channel)
    {
        link.BytesSent += HeaderSize + body.Length;
        return SendRaw(link.Remote, link.Tag, kind, body, type, channel);
    }

    bool SendRaw(ulong remote, ushort tag, Kind kind, ReadOnlySpan<byte> body, SteamSendType type, int channel)
    {
        int length = HeaderSize + body.Length;
        if (scratch.Length < length) scratch = new byte[Math.Max(length, scratch.Length * 2)];
        scratch[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(1), tag);
        body.CopyTo(scratch.AsSpan(HeaderSize));
        return steam.Send(remote, scratch.AsSpan(0, length), type, channel);
    }

    public void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if ((byte)channel >= 4) throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not a transport channel.");
        if (!links.TryGetValue(peer.Value, out var link) || !link.Live || !link.Connected) return;
        if (channel == NetChannel.Snapshots)
        {
            if (data.Length > MaxUnreliable)
                throw new ArgumentException($"A {data.Length}-byte snapshot packet exceeds the link's {MaxUnreliable}-byte limit.", nameof(data));
            int length = SnapshotHeaderSize + data.Length;
            if (scratch.Length < length) scratch = new byte[Math.Max(length, scratch.Length * 2)];
            scratch[0] = (byte)((byte)Kind.Data + (byte)channel);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(1), link.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(3), link.SendSeq++);
            data.CopyTo(scratch.AsSpan(SnapshotHeaderSize));
            link.BytesSent += length;
            steam.Send(link.Remote, scratch.AsSpan(0, length), SteamSendType.UnreliableNoDelay, (int)channel);
            return;
        }
        if (data.Length > NetLimits.MaxReliableMessage)
            throw new ArgumentException($"A {data.Length}-byte reliable packet exceeds {NetLimits.MaxReliableMessage} bytes.", nameof(data));
        SendRaw(link, (Kind)((byte)Kind.Data + (byte)channel), data, SteamSendType.Reliable, (int)channel);
    }

    public void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload)
    {
        INetTransport.CheckDropPayload(payload.Length);
        if (!links.TryGetValue(peer.Value, out var link) || !link.Live) return;
        SendControl(link, Kind.Disconnect, payload);
        Forget(link);
        steam.CloseSession(link.Remote);
        links[link.Id] = link;   // kept until the drop is reported, for Stats
        pendingDrops.Add((link.Id, NetDropReason.Closed));
    }

    public int MaxUnreliablePayload(NetConnectionId peer) => links.TryGetValue(peer.Value, out var link) && link.Live ? MaxUnreliable : 0;

    public NetLinkStats Stats(NetConnectionId peer)
    {
        if (!links.TryGetValue(peer.Value, out var link)) return default;
        long now = NowMs;
        int sent = 0, lost = 0;
        for (int i = 0; i < Math.Min(link.PingCount, PingRing); i++)
        {
            // A ping counts once it had three seconds to come back.
            if ((uint)now - link.Pings[i].Stamp < 3000) continue;
            sent++;
            if (!link.Pings[i].Answered) lost++;
        }
        return new NetLinkStats(link.RoundTripMs, sent == 0 ? 0 : 100f * lost / sent, link.BytesSent, link.BytesReceived);
    }

    public string Address(NetConnectionId peer) => links.TryGetValue(peer.Value, out var link) ? SteamPresence.Address(link.Remote) : "";

    /// <summary>Tells every live connection it is closed, ends the sessions and stops reading; no event follows.</summary>
    public void Dispose()
    {
        if (disposed) return;
        foreach (var link in links.Values)
            if (link.Live)
            {
                SendControl(link, Kind.Disconnect, []);
                steam.CloseSession(link.Remote);
            }
        disposed = true;
        steam.SessionRequested -= OnSessionRequested;
        steam.SessionFailed -= OnSessionFailed;
        links.Clear();
        byRemote.Clear();
        pendingDrops.Clear();
        failures.Clear();
    }
}
