using System.Diagnostics;
using Meitou.Net;
using Meitou.Net.Transport;

namespace Meitou.Tests.Net;

/// <summary>A real-socket smoke run of the LiteNetLib transport on 127.0.0.1 (docs/net.md, "Transport").</summary>
public class LiteNetTransportTests
{
    [Fact]
    public void Connects_exchanges_on_every_channel_kind_and_drops_over_localhost_udp()
    {
        using var server = new LiteNetTransport();
        server.Listen(0);
        Assert.NotEqual(0, server.Port);
        using var client = new LiteNetTransport();
        var host = new TransportRecorder();
        var guest = new TransportRecorder();
        byte[] hello = [.. NetProtocol.Magic, 1, 0];

        var clientId = client.Connect("127.0.0.1", server.Port, hello);
        Pump(server, host, client, guest, () => host.Opened.Count == 1 && guest.Opened.Count == 1);
        Assert.Equal(hello, Assert.Single(host.Requests).Data);
        Assert.Equal([clientId], guest.Opened);
        var hostId = host.Opened[0];
        Assert.InRange(server.MaxUnreliablePayload(hostId), 400, 1500);

        var big = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        client.Send(clientId, NetChannel.Control, [1, 2, 3]);
        client.Send(clientId, NetChannel.Snapshots, [4]);
        server.Send(hostId, NetChannel.Events, big);   // several datagrams, reassembled by the transport
        Pump(server, host, client, guest, () => host.Packets.Count == 2 && guest.Packets.Count == 1);
        var control = Assert.Single(host.Packets, p => p.Channel == NetChannel.Control);
        Assert.Equal([(byte)1, 2, 3], control.Data);
        Assert.Equal([(byte)4], Assert.Single(host.Packets, p => p.Channel == NetChannel.Snapshots).Data);
        Assert.Equal(big, Assert.Single(guest.Packets, p => p.Channel == NetChannel.Events).Data);

        server.Drop(hostId, [7, 7]);
        Pump(server, host, client, guest, () => host.Drops.Count == 1 && guest.Drops.Count == 1);
        Assert.Equal((hostId, NetDropReason.Closed, 0), (host.Drops[0].Peer, host.Drops[0].Reason, host.Drops[0].Data.Length));
        Assert.Equal((clientId, NetDropReason.Closed), (guest.Drops[0].Peer, guest.Drops[0].Reason));
        Assert.Equal([(byte)7, 7], guest.Drops[0].Data);
    }

    static void Pump(INetTransport a, TransportRecorder aSide, INetTransport b, TransportRecorder bSide, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the transports did not deliver within 5 s");
            a.Poll(aSide);
            b.Poll(bSide);
            Thread.Sleep(1);
        }
    }
}
