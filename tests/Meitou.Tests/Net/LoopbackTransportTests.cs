using Meitou.Net;
using Meitou.Net.Transport;

namespace Meitou.Tests.Net;

/// <summary>The in-memory transport every session test runs on: connect, refusal, ordering, sequencing and drops (docs/net.md, "Transport").</summary>
public class LoopbackTransportTests
{
    const int Port = 40120;

    [Fact]
    public void Server_accepts_or_refuses_connect_requests_with_their_payload()
    {
        var clock = new ManualNetClock();
        var net = new LoopbackNetwork(new LoopbackOptions { LatencyMs = 10 }, clock);
        using var server = net.Server();
        server.Listen(Port);
        Assert.Equal(Port, server.Port);
        using var good = net.Client();
        using var bad = net.Client();
        using var lost = net.Client();
        var host = new TransportRecorder { Refuse = data => data[0] == 1 ? null : [9, 8] };
        var goodSide = new TransportRecorder();
        var badSide = new TransportRecorder();
        var lostSide = new TransportRecorder();

        var goodId = good.Connect("localhost", Port, [1, 2, 3]);
        var badId = bad.Connect("localhost", Port, [2]);
        var lostId = lost.Connect("localhost", Port + 1, [1]);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        server.Poll(host);
        Assert.Equal([(byte)1, 2, 3], host.Requests[0].Data);
        Assert.Equal([(byte)2], host.Requests[1].Data);
        Assert.NotEqual(host.Requests[0].Address, host.Requests[1].Address);
        var accepted = Assert.Single(host.Opened);
        Assert.Equal(host.Requests[0].Address, server.Address(accepted));

        good.Poll(goodSide);
        Assert.Empty(goodSide.Opened);   // the accept is still in flight
        clock.Advance(TimeSpan.FromMilliseconds(10));
        good.Poll(goodSide);
        bad.Poll(badSide);
        lost.Poll(lostSide);
        Assert.Equal([goodId], goodSide.Opened);
        Assert.Empty(badSide.Opened);
        var refused = Assert.Single(badSide.Drops);
        Assert.Equal((badId, NetDropReason.Rejected), (refused.Peer, refused.Reason));
        Assert.Equal([(byte)9, 8], refused.Data);
        var unanswered = Assert.Single(lostSide.Drops);
        Assert.Equal((lostId, NetDropReason.Timeout), (unanswered.Peer, unanswered.Reason));

        good.Send(goodId, NetChannel.Control, [42]);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        server.Poll(host);
        var hello = Assert.Single(host.Packets);
        Assert.Equal((accepted, NetChannel.Control), (hello.Peer, hello.Channel));
        Assert.Equal([(byte)42], hello.Data);
    }

    [Fact]
    public void Reliable_channels_arrive_complete_and_in_order_under_loss_and_jitter()
    {
        var clock = new ManualNetClock();
        var net = new LoopbackNetwork(new LoopbackOptions { LatencyMs = 20, JitterMs = 30, ReliableLoss = 0.3f, UnreliableLoss = 0.3f, Seed = 7 }, clock);
        var (server, client, hostId, clientId, host, guest) = Connected(net, clock);

        const int count = 300;
        for (int i = 0; i < count; i++)
        {
            server.Send(hostId, NetChannel.Control, [(byte)i, 0]);
            server.Send(hostId, NetChannel.Events, [(byte)i, 1]);
            server.Send(hostId, NetChannel.Views, [(byte)i, 2]);
            client.Send(clientId, NetChannel.Control, [(byte)i, 3]);
            if (i % 3 == 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                server.Poll(host);
                client.Poll(guest);
            }
        }
        for (int step = 0; step < 2000; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            server.Poll(host);
            client.Poll(guest);
        }

        foreach (var channel in new[] { NetChannel.Control, NetChannel.Events, NetChannel.Views })
        {
            var got = guest.Packets.Where(r => r.Channel == channel).Select(r => r.Data[0]).ToArray();
            Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), got);
        }
        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), host.Packets.Select(r => r.Data[0]));
    }

    [Fact]
    public void Snapshot_channel_drops_older_and_duplicate_packets()
    {
        var clock = new ManualNetClock();
        var net = new LoopbackNetwork(new LoopbackOptions { LatencyMs = 15, JitterMs = 40, UnreliableLoss = 0.1f, Duplicate = 0.5f, Mtu = 600, Seed = 3 }, clock);
        var (server, client, hostId, _, _, guest) = Connected(net, clock);

        const int count = 400;
        for (int i = 0; i < count; i++)
        {
            server.Send(hostId, NetChannel.Snapshots, BitConverter.GetBytes(i));
            clock.Advance(TimeSpan.FromMilliseconds(2));
            client.Poll(guest);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        client.Poll(guest);

        var got = guest.Packets.Select(r => BitConverter.ToInt32(r.Data)).ToArray();
        Assert.True(got.Length > count / 4, $"only {got.Length} of {count} arrived");
        Assert.True(got.Length < count * 9 / 10, $"{got.Length} of {count} arrived: jitter should have made some late");
        for (int i = 1; i < got.Length; i++)
            Assert.True(got[i] > got[i - 1], $"packet {got[i]} delivered after {got[i - 1]}");
        Assert.Equal(600, server.MaxUnreliablePayload(hostId));
        Assert.Throws<ArgumentException>(() => server.Send(hostId, NetChannel.Snapshots, new byte[601]));
    }

    [Fact]
    public void Drops_reach_both_sides_once_and_stop_traffic()
    {
        var clock = new ManualNetClock();
        var net = new LoopbackNetwork(new LoopbackOptions { LatencyMs = 10, JitterMs = 5 }, clock);
        var (server, client, hostId, clientId, host, guest) = Connected(net, clock);

        // A drop: the remote side gets the payload after the data sent before it; the dropping side an empty Closed at its next poll.
        server.Send(hostId, NetChannel.Control, [1]);
        server.Drop(hostId, [7, 7]);
        server.Send(hostId, NetChannel.Control, [2]);
        server.Drop(hostId, [8]);
        Assert.Empty(host.Drops);
        server.Poll(host);
        var local = Assert.Single(host.Drops);
        Assert.Equal((hostId, NetDropReason.Closed, 0), (local.Peer, local.Reason, local.Data.Length));
        client.Send(clientId, NetChannel.Control, [3]);
        Settle(clock, server, host, client, guest);
        Assert.Equal([(byte)1], Assert.Single(guest.Packets).Data);
        var remote = Assert.Single(guest.Drops);
        Assert.Equal((clientId, NetDropReason.Closed), (remote.Peer, remote.Reason));
        Assert.Equal([(byte)7, 7], remote.Data);
        Assert.Single(host.Drops);
        Assert.DoesNotContain(host.Packets, r => r.Data[0] == 3);
        Assert.Equal("", server.Address(hostId));
        Assert.Equal(0, server.MaxUnreliablePayload(hostId));

        // A severed link times out on both sides.
        using var second = net.Client();
        var secondSide = new TransportRecorder();
        var secondId = second.Connect("localhost", Port, [0]);
        Settle(clock, server, host, second, secondSide);
        var hostSecond = host.Opened[^1];
        net.Sever(secondId);
        server.Send(hostSecond, NetChannel.Control, [4]);
        Settle(clock, server, host, second, secondSide);
        Assert.Empty(secondSide.Packets);
        Assert.Equal((secondId, NetDropReason.Timeout), (secondSide.Drops.Single().Peer, secondSide.Drops.Single().Reason));
        Assert.Equal((hostSecond, NetDropReason.Timeout), (host.Drops[^1].Peer, host.Drops[^1].Reason));

        // A disposed client closes its link: the host sees Closed with no payload.
        var third = net.Client();
        var thirdSide = new TransportRecorder();
        third.Connect("localhost", Port, [0]);
        Settle(clock, server, host, third, thirdSide);
        var hostThird = host.Opened[^1];
        third.Dispose();
        Settle(clock, server, host, client, guest);
        Assert.Equal((hostThird, NetDropReason.Closed, 0), (host.Drops[^1].Peer, host.Drops[^1].Reason, host.Drops[^1].Data.Length));
        Assert.Equal(3, host.Drops.Count);
    }

    static (INetServerTransport Server, INetClientTransport Client, NetConnectionId HostId, NetConnectionId ClientId, TransportRecorder Host, TransportRecorder Guest)
        Connected(LoopbackNetwork net, ManualNetClock clock)
    {
        var server = net.Server();
        server.Listen(Port);
        var client = net.Client();
        var host = new TransportRecorder();
        var guest = new TransportRecorder();
        var clientId = client.Connect("localhost", Port, NetProtocol.Magic);
        Settle(clock, server, host, client, guest);
        Assert.Equal([clientId], guest.Opened);
        return (server, client, Assert.Single(host.Opened), clientId, host, guest);
    }

    static void Settle(ManualNetClock clock, INetTransport a, TransportRecorder aSide, INetTransport b, TransportRecorder bSide)
    {
        for (int i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            a.Poll(aSide);
            b.Poll(bSide);
        }
    }
}

/// <summary>Records every transport event, with copies of the data, for the transport tests.</summary>
sealed class TransportRecorder : INetListener
{
    public readonly record struct Packet(NetConnectionId Peer, NetChannel Channel, byte[] Data);
    public readonly record struct Drop(NetConnectionId Peer, NetDropReason Reason, byte[] Data);

    public Func<byte[], byte[]?> Refuse { get; init; } = _ => null;
    public List<(string Address, byte[] Data)> Requests { get; } = [];
    public List<NetConnectionId> Opened { get; } = [];
    public List<Packet> Packets { get; } = [];
    public List<Drop> Drops { get; } = [];

    public byte[]? Request(string address, ReadOnlySpan<byte> connectData)
    {
        Requests.Add((address, connectData.ToArray()));
        return Refuse(connectData.ToArray());
    }

    public void Connected(NetConnectionId peer) => Opened.Add(peer);

    public void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data) => Packets.Add(new(peer, channel, data.ToArray()));

    public void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data) => Drops.Add(new(peer, reason, data.ToArray()));
}
