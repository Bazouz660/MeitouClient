using Meitou.Net;
using Meitou.Net.Session;
using Meitou.Net.Transport;
using Meitou.Net.Transport.Steam;

namespace Meitou.Tests.Net;

/// <summary>The Steam transport over in-memory Steam users (docs/net.md "Steam transport"): the same event contract as the UDP transport.</summary>
public class SteamTransportTests
{
    const ulong HostId = 76561198000000001, GuestId = 76561198000000002, OtherId = 76561198000000003;
    static readonly byte[] Hello = [.. NetProtocol.Magic, 2, 0];

    sealed class Pair : IDisposable
    {
        public readonly ManualNetClock Clock = new();
        public readonly FakeSteamNetwork Network;
        public readonly FakeSteam HostSteam, GuestSteam;
        public readonly SteamNetTransport Server, Client;
        public readonly TransportRecorder Host, Guest;

        public Pair(int latencyMs = 0, Func<byte[], byte[]?>? refuse = null, bool listen = true)
        {
            Network = new FakeSteamNetwork(Clock) { LatencyMs = latencyMs };
            HostSteam = Network.Add(HostId, "Hosty");
            GuestSteam = Network.Add(GuestId, "Kai");
            Server = new SteamNetTransport(HostSteam, Clock);
            Client = new SteamNetTransport(GuestSteam, Clock);
            Host = new TransportRecorder { Refuse = refuse ?? (_ => null) };
            Guest = new TransportRecorder();
            if (listen) Server.Listen(0);
        }

        /// <summary>Polls both sides <paramref name="times"/> times, the clock moving <paramref name="stepMs"/> before each round.</summary>
        public void Pump(int times = 3, int stepMs = 1)
        {
            for (int i = 0; i < times; i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(stepMs));
                Server.Poll(Host);
                Client.Poll(Guest);
            }
        }

        public (NetConnectionId Host, NetConnectionId Guest) Open()
        {
            var guestSide = Client.Connect(SteamPresence.Address(HostId), 0, Hello);
            int opened = Guest.Opened.Count;
            for (int i = 0; i < 500 && Guest.Opened.Count == opened; i++) Pump(times: 1);
            Assert.Equal(guestSide, Guest.Opened[^1]);
            return (Host.Opened[^1], guestSide);
        }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
        }
    }

    [Fact]
    public void A_listening_host_accepts_the_session_and_the_connect_payload_and_a_refusal_carries_its_payload()
    {
        using var pair = new Pair();
        var (hostSide, _) = pair.Open();
        Assert.Equal([GuestId], pair.HostSteam.Accepted);   // accepted in the session-request callback, before any packet was read
        var request = Assert.Single(pair.Host.Requests);
        Assert.Equal(SteamPresence.Address(GuestId), request.Address);
        Assert.Equal(Hello, request.Data);
        Assert.Equal(SteamPresence.Address(GuestId), pair.Server.Address(hostSide));
        Assert.Equal(SteamNetTransport.MaxUnreliable, pair.Server.MaxUnreliablePayload(hostSide));

        var other = pair.Network.Add(OtherId, "Other");
        using var refused = new SteamNetTransport(other, pair.Clock);
        var refusedRecorder = new TransportRecorder();
        var refusing = new TransportRecorder { Refuse = _ => [9, 8, 7] };
        var id = refused.Connect(HostId.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, Hello);
        for (int i = 0; i < 3; i++)
        {
            pair.Server.Poll(refusing);
            refused.Poll(refusedRecorder);
        }
        Assert.Empty(refusing.Opened);
        var drop = Assert.Single(refusedRecorder.Drops);
        Assert.Equal((id, NetDropReason.Rejected), (drop.Peer, drop.Reason));
        Assert.Equal([(byte)9, 8, 7], drop.Data);
    }

    [Fact]
    public void A_host_that_does_not_listen_leaves_the_session_unaccepted_and_the_connect_times_out()
    {
        using var pair = new Pair(listen: false);
        var id = pair.Client.Connect(SteamPresence.Address(HostId), 0, Hello);
        pair.Pump(times: 20, stepMs: 1000);
        Assert.Empty(pair.HostSteam.Accepted);
        Assert.Empty(pair.Host.Requests);
        var drop = Assert.Single(pair.Guest.Drops);
        Assert.Equal((id, NetDropReason.Timeout), (drop.Peer, drop.Reason));

        var bad = pair.Client.Connect("not a steam id", 0, Hello);
        pair.Pump(times: 1);
        Assert.Equal((bad, NetDropReason.Error), (pair.Guest.Drops[^1].Peer, pair.Guest.Drops[^1].Reason));
    }

    [Fact]
    public void Reliable_channels_deliver_every_packet_in_order_up_to_the_message_limit()
    {
        using var pair = new Pair(latencyMs: 20);
        var (hostSide, guestSide) = pair.Open();
        var sent = new List<(NetChannel, byte[])>();
        var random = new Random(3);
        for (int i = 0; i < 90; i++)
        {
            var channel = (NetChannel)(1 + i % 3);
            var data = new byte[i == 89 ? NetLimits.MaxReliableMessage : random.Next(1, 3000)];
            random.NextBytes(data);
            data[0] = (byte)i;
            pair.Server.Send(hostSide, channel, data);
            sent.Add((channel, data));
        }
        Assert.Throws<ArgumentException>(() => pair.Server.Send(hostSide, NetChannel.Events, new byte[NetLimits.MaxReliableMessage + 1]));
        pair.Pump(times: 30);
        for (int c = 1; c <= 3; c++)
        {
            var channel = (NetChannel)c;
            Assert.Equal(sent.Where(s => s.Item1 == channel).Select(s => s.Item2), pair.Guest.Packets.Where(p => p.Channel == channel).Select(p => p.Data));
        }
        Assert.All(pair.Guest.Packets, p => Assert.Equal(guestSide, p.Peer));
    }

    [Fact]
    public void Snapshots_pass_unreliably_and_an_older_packet_than_one_delivered_is_dropped()
    {
        using var pair = new Pair();
        var (hostSide, _) = pair.Open();
        pair.Server.Send(hostSide, NetChannel.Snapshots, new byte[SteamNetTransport.MaxUnreliable]);
        Assert.Throws<ArgumentException>(() => pair.Server.Send(hostSide, NetChannel.Snapshots, new byte[SteamNetTransport.MaxUnreliable + 1]));
        pair.Pump();
        Assert.Equal(SteamNetTransport.MaxUnreliable, Assert.Single(pair.Guest.Packets).Data.Length);

        pair.Network.HoldUnreliable = true;
        for (byte i = 1; i <= 3; i++) pair.Server.Send(hostSide, NetChannel.Snapshots, [i]);
        pair.GuestSteam.ReleaseUnreliable(reversed: true);
        pair.Network.HoldUnreliable = false;
        pair.Pump();
        Assert.Equal([(byte)3], pair.Guest.Packets[^1].Data);   // 2 and 1 came after 3
        Assert.Equal(2, pair.Guest.Packets.Count);
        Assert.Equal(NetChannel.Snapshots, pair.Guest.Packets[^1].Channel);
    }

    [Fact]
    public void A_drop_hands_its_payload_over_once_and_silence_times_both_sides_out()
    {
        using var pair = new Pair();
        var (hostSide, guestSide) = pair.Open();
        pair.Client.Drop(guestSide, [5, 6]);
        pair.Client.Send(guestSide, NetChannel.Control, [1]);   // ignored after the drop
        pair.Pump();
        var guestDrop = Assert.Single(pair.Guest.Drops);
        Assert.Equal((guestSide, NetDropReason.Closed, 0), (guestDrop.Peer, guestDrop.Reason, guestDrop.Data.Length));
        var hostDrop = Assert.Single(pair.Host.Drops);
        Assert.Equal((hostSide, NetDropReason.Closed), (hostDrop.Peer, hostDrop.Reason));
        Assert.Equal([(byte)5, 6], hostDrop.Data);
        Assert.Empty(pair.Host.Packets);

        var (hostAgain, guestAgain) = pair.Open();
        Assert.NotEqual(hostSide, hostAgain);   // ids are never reused
        pair.Network.Severed = true;
        pair.Pump(times: 16, stepMs: 1000);
        Assert.Equal((hostAgain, NetDropReason.Timeout), (pair.Host.Drops[^1].Peer, pair.Host.Drops[^1].Reason));
        Assert.Equal((guestAgain, NetDropReason.Timeout), (pair.Guest.Drops[^1].Peer, pair.Guest.Drops[^1].Reason));
        Assert.Equal((2, 2), (pair.Host.Drops.Count, pair.Guest.Drops.Count));
    }

    [Fact]
    public void Pings_keep_a_quiet_link_alive_and_measure_the_round_trip()
    {
        using var pair = new Pair(latencyMs: 40);
        var (hostSide, guestSide) = pair.Open();
        pair.Pump(times: 3000, stepMs: 10);   // 30 s with no data
        Assert.Empty(pair.Host.Drops);
        Assert.Empty(pair.Guest.Drops);
        Assert.InRange(pair.Server.Stats(hostSide).RoundTripMs, 80, 100);
        Assert.InRange(pair.Client.Stats(guestSide).RoundTripMs, 80, 100);
        Assert.Equal(0, pair.Server.Stats(hostSide).LossPercent);
    }

    [Fact]
    public void A_host_on_udp_and_steam_takes_a_guest_over_each()
    {
        FakeSteamNetwork? steam = null;
        using var h = SessionHarness.SyntheticTownHost(serverTransport: (loopback, clock) =>
        {
            steam = new FakeSteamNetwork(clock) { LatencyMs = 10 };
            return new CompositeServerTransport(loopback.Server(), new SteamNetTransport(steam.Add(HostId, "Hosty"), clock));
        });
        var udp = h.Join("Udo");
        var viaSteam = h.Join("Kai", transport: new SteamNetTransport(steam!.Add(GuestId, "Kai"), h.Clock), host: SteamPresence.Address(HostId));
        h.Run(10);
        Assert.Equal((NetClientState.Playing, NetClientState.Playing), (udp.Client.State, viaSteam.Client.State));
        Assert.NotEqual(udp.Player, viaSteam.Player);
        var roster = h.Host.Roster;
        Assert.Equal(["Hosty", "Udo", "Kai"], roster.Select(e => e.Name));
        Assert.Contains(h.Host.Players, p => p.Number == viaSteam.Player);
        Assert.Contains(viaSteam.Client.LastAssembled!.Characters, c => c.IsPlayer && c.Owner == viaSteam.Player);
        Assert.Contains(viaSteam.Client.LastAssembled!.Characters, c => c.IsPlayer && c.Owner == 0);
    }
}
